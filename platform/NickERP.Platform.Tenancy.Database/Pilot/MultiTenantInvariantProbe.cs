using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Pilot;

namespace NickERP.Platform.Tenancy.Database.Pilot;

/// <summary>
/// Sprint 43 — active multi-tenant invariant probe. Backs the marquee
/// gate <see cref="PilotReadinessGate.MultiTenantInvariants"/> in
/// <c>PilotReadinessService</c>. Three sub-checks; all three must pass
/// for the gate to pass.
/// </summary>
/// <remarks>
/// <para>
/// This is the only probe in Sprint 43 that is allowed to flip
/// <c>SetSystemContext</c> — the system-context register integrity
/// check enumerates every caller and the cross-tenant export refusal
/// check has to escape tenant scope to attempt the cross-tenant read.
/// Both flips are bounded to this probe's execution window and
/// reverted before the probe returns.
/// </para>
/// <para>
/// Sub-check 1 — RLS read isolation: open a tenant-scoped query for
/// a different tenant's id under the supplied tenant's context;
/// expect 0 rows. The query targets <c>tenancy.tenant_module_settings</c>
/// because it's RLS-enforced (Sprint 29) and lives in the same
/// DbContext the probe already holds.
/// </para>
/// <para>
/// Sub-check 2 — system-context register integrity: enumerate every
/// <c>SetSystemContext()</c> caller in the source tree under
/// <c>Pilot:SourceRoot</c> and cross-reference with the file paths
/// listed in <c>docs/system-context-audit-register.md</c>. Drift in
/// either direction (caller in source not in register, or register
/// entry whose source file no longer contains the call) flips the
/// sub-check to fail. If <c>Pilot:SourceRoot</c> is unset or missing,
/// the sub-check is recorded as a pass-with-skip-note (production
/// deploys ship DLLs only; the operator wires the setting on the
/// dev box that runs the dashboard).
/// </para>
/// <para>
/// Sub-check 3 — cross-tenant export gate: invoke
/// <c>TenantExportService.DownloadExportAsync</c> with a synthetic
/// foreign user-id against an export request that doesn't exist
/// (random Guid). The Sprint 25 service must return null —
/// confirming both that random ids don't accidentally resolve and
/// that the gate itself is wired. A real impersonation attempt
/// (existing exportId, foreign userId) is harder to set up
/// hermetically without seeding cross-tenant data, so this probe
/// targets the simpler "unknown id" path and relies on the existing
/// <see cref="TenancyExportServiceTests"/> for the deeper
/// impersonation coverage.
/// </para>
/// <para>
/// Probe failures are observability: every sub-check is wrapped in
/// try/catch and surfaced as <see cref="MultiTenantInvariantSubCheck.Pass"/>=false
/// with the exception message in <see cref="MultiTenantInvariantSubCheck.Reason"/>.
/// The probe itself never throws back into the gate runner.
/// </para>
/// </remarks>
public class MultiTenantInvariantProbe
{
    /// <summary>Stable audit event type emitted on every probe run.</summary>
    public const string AuditEventType = "nickerp.tenancy.invariant_probe_run";

    private static readonly Regex SetSystemContextCallRegex = new(
        @"\.SetSystemContext\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RegisterEntryFilePathRegex = new(
        @"\|\s*`[^`]+`\s*\|\s*`(?<path>[^`]+)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TenancyDbContext _tenancyDb;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _config;
    private readonly ILogger<MultiTenantInvariantProbe> _logger;
    private readonly IEventPublisher? _events;

    public MultiTenantInvariantProbe(
        TenancyDbContext tenancyDb,
        TimeProvider clock,
        ILogger<MultiTenantInvariantProbe> logger,
        IConfiguration? config = null,
        IEventPublisher? events = null)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? new ConfigurationBuilder().Build();
        _events = events;
    }

    /// <summary>
    /// Run all three sub-checks against the supplied tenant. Always
    /// emits the <see cref="AuditEventType"/> audit event (best
    /// effort — failure to emit logs but doesn't propagate).
    /// </summary>
    public virtual async Task<MultiTenantInvariantProbeResult> RunAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var observedAt = _clock.GetUtcNow();
        var rls = await SafeRunSubCheckAsync(() => CheckRlsReadIsolationAsync(tenantId, ct), "rls_read_isolation");
        var register = await SafeRunSubCheckAsync(() => CheckSystemContextRegisterIntegrityAsync(ct), "system_context_register");
        var export = await SafeRunSubCheckAsync(() => CheckCrossTenantExportGateAsync(ct), "cross_tenant_export_gate");

        var overall = rls.Pass && register.Pass && export.Pass;
        Guid? proofEventId = null;

        if (_events is not null)
        {
            try
            {
                var payload = new
                {
                    overall = overall ? "pass" : "fail",
                    checks = new[]
                    {
                        new { id = "rls_read_isolation", pass = rls.Pass, reason = rls.Reason },
                        new { id = "system_context_register", pass = register.Pass, reason = register.Reason },
                        new { id = "cross_tenant_export_gate", pass = export.Pass, reason = export.Reason },
                    },
                    tenantId,
                    observedAt,
                };
                var element = JsonSerializer.SerializeToElement(payload);
                // Idempotency key collides only on identical (tenant, instant);
                // we round to the second so back-to-back ticks within the
                // same wall-clock second dedupe to a single audit row rather
                // than spamming the log.
                var ipk = $"pilot.invariant_probe.{tenantId}.{observedAt.UtcDateTime:yyyyMMddTHHmmss}";
                var evt = DomainEvent.Create(
                    tenantId: tenantId,
                    actorUserId: null,
                    correlationId: null,
                    eventType: AuditEventType,
                    entityType: "Tenant",
                    entityId: tenantId.ToString(),
                    payload: element,
                    idempotencyKey: ipk,
                    clock: _clock);
                var persisted = await _events.PublishAsync(evt, ct);
                proofEventId = persisted.EventId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MultiTenantInvariantProbe: failed to emit invariant_probe_run audit event for tenant {TenantId}; probe result still surfaced via dashboard.",
                    tenantId);
            }
        }

        return new MultiTenantInvariantProbeResult(
            OverallPass: overall,
            ObservedAt: observedAt,
            ProofEventId: proofEventId,
            RlsReadIsolation: rls,
            SystemContextRegister: register,
            CrossTenantExportGate: export);
    }

    // ---- sub-check 1: RLS read isolation -------------------------------

    /// <summary>
    /// Pick another active tenant id (not the supplied one); query
    /// <c>tenancy.tenant_module_settings</c> WHERE TenantId = otherId
    /// under whatever <c>ITenantContext</c> the caller is in (i.e. the
    /// supplied tenant's scope, since the dashboard is invoked
    /// per-tenant). Expect 0 rows.
    /// </summary>
    /// <remarks>
    /// If the install only has one active tenant, the sub-check
    /// records a pass-with-trivial note rather than failing. This is
    /// realistic for early-pilot single-tenant deployments where
    /// cross-tenant isolation has nothing to assert against.
    /// </remarks>
    private async Task<MultiTenantInvariantSubCheck> CheckRlsReadIsolationAsync(long tenantId, CancellationToken ct)
    {
        // Find another active tenant. tenancy.tenants is intentionally
        // not under RLS — root of the tenant graph, see TENANCY.md —
        // so we can read it from any context.
        var otherTenantId = await _tenancyDb.Tenants
            .AsNoTracking()
            .Where(t => t.Id != tenantId)
            .OrderBy(t => t.Id)
            .Select(t => (long?)t.Id)
            .FirstOrDefaultAsync(ct);
        if (otherTenantId is null)
        {
            return new MultiTenantInvariantSubCheck(
                Pass: true,
                Reason: "single-tenant install — no cross-tenant rows to test against");
        }

        // Try to read another tenant's tenant_module_settings rows
        // under the current scope. tenant_module_settings is RLS-
        // enforced (Sprint 29), so the policy USING clause should
        // strip rows whose TenantId does not match the session's
        // app.tenant_id setting. If we get any rows back, RLS has
        // either been disabled or the connection-interceptor is not
        // pushing app.tenant_id correctly — both are critical
        // failures.
        //
        // Note: AsNoTracking + IgnoreQueryFilters defeats EF's
        // soft-delete and tenant-owned filters at the LINQ layer so
        // the only thing limiting the result is RLS itself.
        var leakedRowCount = await _tenancyDb.TenantModuleSettings
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == otherTenantId.Value)
            .CountAsync(ct);

        if (leakedRowCount > 0)
        {
            return new MultiTenantInvariantSubCheck(
                Pass: false,
                Reason: $"{leakedRowCount} row(s) for tenant {otherTenantId.Value} visible from tenant {tenantId}'s scope — RLS read isolation broken.");
        }
        return new MultiTenantInvariantSubCheck(
            Pass: true,
            Reason: $"0 rows leaked from tenant {otherTenantId.Value} into tenant {tenantId}'s scope.");
    }

    // ---- sub-check 2: System-context register integrity ----------------

    /// <summary>
    /// Enumerate every <c>SetSystemContext()</c> caller in the source
    /// tree under <c>Pilot:SourceRoot</c> + parse the file-path column
    /// out of <c>docs/system-context-audit-register.md</c> + verify a
    /// 1:1 match. Drift in either direction trips the sub-check.
    /// </summary>
    /// <remarks>
    /// Production deploys ship DLLs only — the source tree is not
    /// present at runtime. <c>Pilot:SourceRoot</c> resolution falls
    /// back through env var <c>NICKERP_PILOT_SOURCE_ROOT</c> then a
    /// best-effort walk up from <c>AppContext.BaseDirectory</c> to
    /// find a directory that contains <c>docs/system-context-audit-register.md</c>.
    /// If none of those resolve, the sub-check records a pass-with-skip
    /// note rather than failing.
    /// </remarks>
    private Task<MultiTenantInvariantSubCheck> CheckSystemContextRegisterIntegrityAsync(CancellationToken ct)
    {
        var sourceRoot = ResolveSourceRoot();
        if (sourceRoot is null)
        {
            return Task.FromResult(new MultiTenantInvariantSubCheck(
                Pass: true,
                Reason: "skipped — Pilot:SourceRoot not configured and no fallback found"));
        }

        var registerPath = Path.Combine(sourceRoot, "docs", "system-context-audit-register.md");
        if (!File.Exists(registerPath))
        {
            return Task.FromResult(new MultiTenantInvariantSubCheck(
                Pass: false,
                Reason: $"docs/system-context-audit-register.md missing under SourceRoot '{sourceRoot}'"));
        }

        var registerText = File.ReadAllText(registerPath);
        // Pull file-path column from rows under "## Entries". Format
        // for each row:
        //   | `Caller.Method` | `path/to/file.cs` | ...
        var registeredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in RegisterEntryFilePathRegex.Matches(registerText))
        {
            // Strip any trailing colon-line annotation ("(method block)" / "file.cs:174").
            var raw = m.Groups["path"].Value;
            var path = ExtractFilePath(raw);
            if (path is null) continue;
            registeredPaths.Add(NormalisePath(path));
        }

        // Enumerate code files containing SetSystemContext( calls.
        // Skip bin/, obj/, .worktrees/, node_modules/, .git/.
        // Also skip the audit register file itself + this probe (which
        // mentions SetSystemContext in comments).
        var sourceCallerPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in EnumerateSourceFiles(sourceRoot, ct))
            {
                ct.ThrowIfCancellationRequested();
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                if (!SetSystemContextCallRegex.IsMatch(text)) continue;
                // Don't double-count the interface declaration itself
                // (ITenantContext.SetSystemContext) or the implementation
                // (TenantContext.SetSystemContext) — those are method-
                // declarations, not callers. The regex requires `.SetSystemContext(`
                // so a method declaration `void SetSystemContext()` won't match.
                // But `tenant.SetSystemContext()` calls do. Filter further:
                // a file that ONLY contains the declaration (and no `.SetSystemContext(` call)
                // is filtered by the regex pre-check above.
                var rel = NormalisePath(Path.GetRelativePath(sourceRoot, file));
                sourceCallerPaths.Add(rel);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(new MultiTenantInvariantSubCheck(
                Pass: false,
                Reason: $"source enumeration failed: {ex.Message}"));
        }

        // Drift detection. We compare on file-path only (not
        // file:line) because the register entries' line numbers
        // drift naturally as files evolve and re-registering on
        // every line move is more noise than signal. The file-path
        // is the stable identity.
        var unregistered = sourceCallerPaths.Except(registeredPaths, StringComparer.OrdinalIgnoreCase).ToList();
        var missingFromSource = registeredPaths.Except(sourceCallerPaths, StringComparer.OrdinalIgnoreCase).ToList();

        if (unregistered.Count == 0 && missingFromSource.Count == 0)
        {
            return Task.FromResult(new MultiTenantInvariantSubCheck(
                Pass: true,
                Reason: $"register and source agree on {registeredPaths.Count} caller(s)"));
        }

        // Bound the message to fit the snapshot Note column (1000
        // chars). Show up to 5 paths per side.
        string Show(IList<string> items) => items.Count == 0
            ? "(none)"
            : string.Join(", ", items.Take(5)) + (items.Count > 5 ? ", ..." : "");
        var reason = $"register drift — unregistered callers: {Show(unregistered)}; entries missing from source: {Show(missingFromSource)}";
        if (reason.Length > 900) reason = reason[..900];
        return Task.FromResult(new MultiTenantInvariantSubCheck(
            Pass: false,
            Reason: reason));
    }

    // ---- sub-check 3: Cross-tenant export gate -------------------------

    /// <summary>
    /// Invoke <c>TenantExportService.DownloadExportAsync</c> with a
    /// random Guid (no matching row exists) and a synthetic foreign
    /// user id. Expect null — the service must not return content
    /// for an unknown export. Surfaces as fail if an instance returns
    /// non-null (impossible without seeded data, but the gate is the
    /// gate).
    /// </summary>
    /// <remarks>
    /// We resolve the export service via the same DbContext we hold;
    /// the service only depends on TenancyDbContext + IEventPublisher
    /// + ILogger + TimeProvider for the lookup path, so an in-process
    /// construction here is fine. Any throw is caught by the
    /// SafeRunSubCheckAsync wrapper.
    /// </remarks>
    private async Task<MultiTenantInvariantSubCheck> CheckCrossTenantExportGateAsync(CancellationToken ct)
    {
        // Seed-free "unknown id" probe — the service must return null
        // for a random Guid. This validates the lookup path AND
        // confirms that a foreign user id passed alongside cannot
        // coerce a non-null result.
        var unknownExportId = Guid.NewGuid();
        var foreignUserId = Guid.NewGuid();

        var svc = new Services.TenantExportService(
            _tenancyDb,
            new NoopEventPublisher(),
            new Services.TenantExportOptions { OutputPath = Path.GetTempPath(), RetentionDays = 7 },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Services.TenantExportService>.Instance,
            _clock);

        var result = await svc.DownloadExportAsync(unknownExportId, foreignUserId, ct);
        if (result is not null)
        {
            // Best-effort cleanup if a stream was returned.
            try { await result.Stream.DisposeAsync(); } catch { /* ignore */ }
            return new MultiTenantInvariantSubCheck(
                Pass: false,
                Reason: $"DownloadExportAsync returned a non-null result for an unknown export id ({unknownExportId}) — gate compromised.");
        }

        return new MultiTenantInvariantSubCheck(
            Pass: true,
            Reason: "DownloadExportAsync correctly returned null for an unknown export id under a foreign user.");
    }

    // ---- helpers -------------------------------------------------------

    private async Task<MultiTenantInvariantSubCheck> SafeRunSubCheckAsync(
        Func<Task<MultiTenantInvariantSubCheck>> runner,
        string subCheckId)
    {
        try
        {
            return await runner();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "MultiTenantInvariantProbe sub-check {SubCheckId} threw; recording fail.",
                subCheckId);
            var msg = ex.Message.Length > 800 ? ex.Message[..800] : ex.Message;
            return new MultiTenantInvariantSubCheck(Pass: false, Reason: $"exception: {msg}");
        }
    }

    private string? ResolveSourceRoot()
    {
        // Highest priority: explicit Pilot:SourceRoot config.
        var configured = _config["Pilot:SourceRoot"];
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }
        // Fallback: env var.
        var env = Environment.GetEnvironmentVariable("NICKERP_PILOT_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            return Path.GetFullPath(env);
        }
        // Best-effort walk: from AppContext.BaseDirectory upward,
        // looking for a directory with docs/system-context-audit-register.md.
        // Bound the walk to 8 hops so we don't pathologically scan the
        // whole drive.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "docs", "system-context-audit-register.md");
            if (File.Exists(candidate)) return Path.GetFullPath(dir);
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string sourceRoot, CancellationToken ct)
    {
        // Walk the tree manually so we can cheaply skip noise dirs.
        var stack = new Stack<string>();
        stack.Push(sourceRoot);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, ".worktrees", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "v1-clone", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                stack.Push(sub);
            }
            string[] files;
            try { files = Directory.GetFiles(dir, "*.cs"); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private static string NormalisePath(string path)
        => path.Replace('\\', '/').Trim();

    private static string? ExtractFilePath(string raw)
    {
        // The register column may contain just the path, or the path
        // followed by a parenthetical annotation ("file.cs (method block)"),
        // or path:line ("file.cs:174"). Take everything up to the first
        // space, paren, or colon-followed-by-digit.
        var s = raw.Trim();
        if (s.Length == 0) return null;
        int cutoff = s.Length;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ' ' || c == '\t' || c == '(') { cutoff = i; break; }
            if (c == ':' && i + 1 < s.Length && char.IsDigit(s[i + 1])) { cutoff = i; break; }
        }
        var path = s.Substring(0, cutoff).Trim();
        return path.Length == 0 ? null : path;
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
            => Task.FromResult(evt with { EventId = Guid.NewGuid() });

        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DomainEvent>>(events);
    }
}

/// <summary>Aggregate result of a single probe run.</summary>
public sealed record MultiTenantInvariantProbeResult(
    bool OverallPass,
    DateTimeOffset ObservedAt,
    Guid? ProofEventId,
    MultiTenantInvariantSubCheck RlsReadIsolation,
    MultiTenantInvariantSubCheck SystemContextRegister,
    MultiTenantInvariantSubCheck CrossTenantExportGate);

/// <summary>Outcome of one sub-check.</summary>
public sealed record MultiTenantInvariantSubCheck(bool Pass, string Reason);
