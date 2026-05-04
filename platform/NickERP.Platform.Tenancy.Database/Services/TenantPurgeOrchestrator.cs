using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Tenancy.Entities;
using Npgsql;

namespace NickERP.Platform.Tenancy.Database.Services;

/// <summary>
/// Sprint 18 — orchestrates the cross-DB cascade behind a hard-purge.
/// Tenant business data lives in <c>nickerp_inspection</c> (and other
/// per-module DBs); audit data lives in <c>nickerp_platform</c>'s
/// <c>audit</c> schema; identity data lives in <c>nickerp_platform</c>'s
/// <c>identity</c> schema; the tenant row itself is in
/// <c>nickerp_platform</c>'s <c>tenancy</c> schema.
/// </summary>
/// <remarks>
/// <para>
/// Postgres does not natively offer two-phase commit (2PC) across multiple
/// databases without an external coordinator. The orchestrator therefore
/// runs sequentially: it opens one connection per database, wraps each
/// DB's deletes in its own transaction, and writes a
/// <see cref="TenantPurgeLog"/> row at the end recording the per-table
/// counts and an outcome of <c>"completed"</c> or <c>"partial"</c>. If a
/// step fails, the log row records the partial state and the surviving
/// data can be re-purged on the next operator-initiated retry once the
/// tenant is back in <see cref="TenantState.PendingHardPurge"/>.
/// </para>
/// <para>
/// Order of deletes (intra-DB FK ON DELETE CASCADE handles intra-table
/// fan-out where present; the orchestrator only enumerates the tables a
/// query actually has to touch):
/// </para>
/// <list type="number">
/// <item><description><c>nickerp_inspection</c>: every <c>ITenantOwned</c>
///   table, walked by `TRUNCATE` against the schema's tables WHERE TenantId.
///   For Sprint 18 we only execute SELECT-DELETE on the schema's *root*
///   tables; FK CASCADE handles the rest. Schema = <c>inspection</c>.</description></item>
/// <item><description><c>nickerp_nickfinance</c>: same shape. Schema =
///   <c>nickfinance</c>.</description></item>
/// <item><description><c>nickerp_platform</c> identity: <c>user_scopes</c>
///   then <c>users</c> WHERE TenantId.</description></item>
/// <item><description><c>nickerp_platform</c> audit: <c>events</c>,
///   <c>notifications</c>, <c>edge_node_authorizations</c>,
///   <c>edge_node_api_keys</c>, <c>edge_node_replay_log</c>
///   WHERE TenantId.</description></item>
/// <item><description><c>nickerp_platform</c> tenancy:
///   <c>tenant_purge_log</c> INSERT (row counts), then
///   <c>tenants</c> WHERE Id.</description></item>
/// </list>
/// <para>
/// System context: the deletes against the inspection / nickfinance /
/// audit schemas need to bypass per-row RLS for the tenant being purged.
/// We use <see cref="ITenantContext.SetSystemContext"/> on the tenant
/// context that the EF interceptor pushes to Postgres. That sets
/// <c>app.tenant_id = '-1'</c>; tables that have opted in via the
/// <c>OR app.tenant_id = '-1'</c> clause admit the cross-tenant DELETE.
/// </para>
/// <para>
/// Tables that have opted in: <c>audit.events</c>,
/// <c>audit.notifications</c>, <c>audit.edge_node_api_keys</c>,
/// <c>nickfinance.fx_rate</c>. Tables that haven't include every
/// inspection.* and nickfinance.* business table — those continue to
/// filter on the per-row TenantId. Since the DELETE we issue includes
/// <c>WHERE "TenantId" = @tenantId</c>, the policy's USING clause needs
/// to admit reads of THAT tenant's rows for the system-context session
/// to see them. The policies were written with
/// <c>COALESCE(current_setting('app.tenant_id', true), '0') = "TenantId"</c>
/// — under system context current_setting returns '-1' so this fails.
/// We therefore use a raw connection (NOT through EF) and explicitly
/// SET app.tenant_id = @tenantId for the duration of the DELETE. This is
/// a deliberate, narrowly-scoped bypass of the system-context flag —
/// Postgres-level audit captures who issued it, and the
/// <see cref="TenantPurgeLog"/> row carries the operator id.
/// </para>
/// </remarks>
public interface ITenantPurgeOrchestrator
{
    /// <summary>
    /// Run the cross-DB cascade. Returns a result with per-table row
    /// counts and the id of the persisted <see cref="TenantPurgeLog"/>
    /// row. Throws only on catastrophic failure — partial outcomes are
    /// captured in the result.
    /// </summary>
    Task<TenantPurgeResult> PurgeAsync(TenantPurgeContext context, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ITenantPurgeOrchestrator"/>. Reads downstream
/// connection strings from configuration (env vars or
/// IConfiguration-backed values via the
/// <see cref="TenantPurgeOrchestratorOptions"/> binding).
/// </summary>
public sealed class TenantPurgeOrchestrator : ITenantPurgeOrchestrator
{
    private readonly TenancyDbContext _tenancy;
    private readonly TenantPurgeOrchestratorOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantPurgeOrchestrator> _logger;

    /// <summary>
    /// Tables on <c>nickerp_inspection</c> that own per-tenant rows. Walked
    /// in order so a stable plan emerges in the audit log. FK ON DELETE
    /// CASCADE handles dependent rows (cases -> reviews -> attempts etc.)
    /// so we only enumerate the root tables here.
    /// </summary>
    /// <remarks>
    /// The list is intentionally conservative — every entry must exist in
    /// every NickERP deployment. New ITenantOwned tables added in future
    /// sprints get added here OR rely on FK cascade from one of the listed
    /// roots. A test-only orchestrator override can be passed via
    /// <see cref="TenantPurgeOrchestratorOptions.InspectionTables"/> for
    /// integration tests with truncated schemas.
    /// </remarks>
    public static readonly IReadOnlyList<string> InspectionTablesDefault = new[]
    {
        "inspection.cases",
        "inspection.locations",
        "inspection.scanner_device_instances",
        "inspection.external_system_instances",
        "inspection.analysis_services",
        "inspection.location_assignments",
    };

    /// <summary>Tables on <c>nickerp_nickfinance</c> that own per-tenant rows.</summary>
    public static readonly IReadOnlyList<string> NickFinanceTablesDefault = new[]
    {
        "nickfinance.voucher",
        "nickfinance.petty_cash_box",
        "nickfinance.period",
        // fx_rate is suite-wide (TenantId nullable); skip — per-tenant
        // hard-purge does not delete suite-wide reference data.
    };

    /// <summary>Tables on <c>nickerp_platform</c> audit schema.</summary>
    public static readonly IReadOnlyList<string> AuditTablesDefault = new[]
    {
        "audit.notifications",
        "audit.edge_node_authorizations",
        "audit.edge_node_api_keys",
        "audit.edge_node_replay_log",
        "audit.events",
    };

    /// <summary>Tables on <c>nickerp_platform</c> identity schema.</summary>
    public static readonly IReadOnlyList<string> IdentityTablesDefault = new[]
    {
        "identity.user_scopes",
        "identity.identity_users",
        "identity.app_scopes",
        "identity.service_token_scopes",
        "identity.service_tokens",
    };

    public TenantPurgeOrchestrator(
        TenancyDbContext tenancy,
        TenantPurgeOrchestratorOptions options,
        ILogger<TenantPurgeOrchestrator> logger,
        TimeProvider? clock = null)
    {
        _tenancy = tenancy;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<TenantPurgeResult> PurgeAsync(TenantPurgeContext context, CancellationToken ct = default)
    {
        var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        string? failureNote = null;
        var outcome = "completed";

        // Each block is best-effort: a failure logs and proceeds. The
        // operator can re-run a partial purge after the underlying issue
        // is resolved (the tenant remains in PendingHardPurge until the
        // tenants row itself is deleted in the final block).
        var inspectionNote = await TryPurgeDbAsync(
            label: "inspection",
            connectionString: _options.InspectionConnectionString,
            tables: _options.InspectionTables ?? InspectionTablesDefault,
            tenantId: context.TenantId,
            rowCounts: rowCounts,
            ct: ct);
        if (inspectionNote is not null)
        {
            failureNote = inspectionNote;
            outcome = "partial";
        }

        var financeNote = await TryPurgeDbAsync(
            label: "nickfinance",
            connectionString: _options.NickFinanceConnectionString,
            tables: _options.NickFinanceTables ?? NickFinanceTablesDefault,
            tenantId: context.TenantId,
            rowCounts: rowCounts,
            ct: ct);
        if (financeNote is not null)
        {
            failureNote = failureNote is null ? financeNote : failureNote + "; " + financeNote;
            outcome = "partial";
        }

        // Platform DB blocks share a connection. Audit + identity + tenancy
        // all live in nickerp_platform.
        var platformNote = await TryPurgePlatformAsync(
            connectionString: _options.PlatformConnectionString,
            tenantId: context.TenantId,
            auditTables: _options.AuditTables ?? AuditTablesDefault,
            identityTables: _options.IdentityTables ?? IdentityTablesDefault,
            rowCounts: rowCounts,
            ct: ct);
        if (platformNote is not null)
        {
            failureNote = failureNote is null ? platformNote : failureNote + "; " + platformNote;
            outcome = "partial";
        }
        if (failureNote is not null && failureNote.Length > 1000)
        {
            failureNote = failureNote[..997] + "...";
        }

        // Persist the log row + delete the tenants row in a final
        // platform-DB step. Even on a partial outcome we write the log so
        // the operator can see what was purged and what remains.
        var purgeLogId = Guid.NewGuid();
        await PersistLogAndDeleteTenantAsync(
            purgeLogId, context, rowCounts, outcome, failureNote, ct);

        return new TenantPurgeResult(purgeLogId, outcome, rowCounts, failureNote);
    }

    /// <summary>
    /// Per-DB best-effort cascade. Skipped silently when
    /// <paramref name="connectionString"/> is null/empty (test scenarios
    /// where a module DB isn't deployed). Returns a failure-note string
    /// on partial failure, null on clean success or skip.
    /// </summary>
    private async Task<string?> TryPurgeDbAsync(
        string label,
        string? connectionString,
        IReadOnlyList<string> tables,
        long tenantId,
        Dictionary<string, long> rowCounts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogInformation(
                "TenantPurge[{Label}] skipped — no connection string configured.",
                label);
            return null;
        }

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            // Push the tenant id explicitly so per-table RLS USING clauses
            // (which compare against current_setting('app.tenant_id'))
            // admit reads/deletes of this tenant's rows. NOT system-context
            // — system-context (-1) won't satisfy the per-row TenantId
            // comparison.
            await using (var setCmd = conn.CreateCommand())
            {
                setCmd.CommandText = $"SET app.tenant_id = '{tenantId}'; SET app.user_id = '00000000-0000-0000-0000-000000000000';";
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            await using var tx = await conn.BeginTransactionAsync(ct);

            foreach (var table in tables)
            {
                ct.ThrowIfCancellationRequested();
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $@"DELETE FROM {table} WHERE ""TenantId"" = @tid;";
                var p = cmd.CreateParameter();
                p.ParameterName = "tid";
                p.Value = tenantId;
                cmd.Parameters.Add(p);
                var deleted = await cmd.ExecuteNonQueryAsync(ct);
                rowCounts[table] = deleted;
            }

            await tx.CommitAsync(ct);
            _logger.LogInformation(
                "TenantPurge[{Label}] completed for tenant {TenantId} — {Counts}.",
                label, tenantId, string.Join(", ", tables.Select(t => $"{t}={rowCounts.GetValueOrDefault(t, 0)}")));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "TenantPurge[{Label}] FAILED for tenant {TenantId} — partial state, see tenant_purge_log.",
                label, tenantId);
            var msg = $"{label}: {ex.GetType().Name}: {ex.Message}";
            return msg.Length <= 500 ? msg : $"{label}: {ex.GetType().Name} (truncated)";
        }
    }

    /// <summary>
    /// Platform-DB cascade — audit, identity, then ready for the final
    /// tenants-row delete. One connection / one transaction so an audit
    /// failure rolls back the identity delete (consistent platform-side
    /// state). Returns a failure-note string on partial failure, null on
    /// clean success or skip.
    /// </summary>
    private async Task<string?> TryPurgePlatformAsync(
        string? connectionString,
        long tenantId,
        IReadOnlyList<string> auditTables,
        IReadOnlyList<string> identityTables,
        Dictionary<string, long> rowCounts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "TenantPurge[platform] skipped — no platform connection string. "
                + "tenant_purge_log will not be written; tenants row will not be deleted.");
            return null;
        }

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using (var setCmd = conn.CreateCommand())
            {
                // Audit + identity have a mix of opt-in (audit.events,
                // audit.notifications, audit.edge_node_api_keys) and
                // strict-tenant tables. Setting both system-context AND
                // tenant-id won't work; we set tenant id and rely on
                // per-table USING clauses to admit the row reads. For
                // opt-in tables, the USING/WITH-CHECK clauses are
                // additive — `OR app.tenant_id = '-1'` is one branch and
                // the standard tenant comparison is the other; setting
                // app.tenant_id = @tenantId hits the standard branch.
                setCmd.CommandText = $"SET app.tenant_id = '{tenantId}'; SET app.user_id = '00000000-0000-0000-0000-000000000000';";
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            await using var tx = await conn.BeginTransactionAsync(ct);

            // Identity first — user_scopes references identity_users.
            // Service tokens / scopes can fan out either way; the order
            // here matches IdentityTablesDefault.
            foreach (var table in identityTables)
            {
                ct.ThrowIfCancellationRequested();
                rowCounts[table] = await ExecDeleteAsync(conn, tx, table, tenantId, ct);
            }
            foreach (var table in auditTables)
            {
                ct.ThrowIfCancellationRequested();
                rowCounts[table] = await ExecDeleteAsync(conn, tx, table, tenantId, ct);
            }

            await tx.CommitAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "TenantPurge[platform] FAILED for tenant {TenantId} — partial state.",
                tenantId);
            var msg = $"platform: {ex.GetType().Name}: {ex.Message}";
            return msg.Length <= 500 ? msg : $"platform: {ex.GetType().Name} (truncated)";
        }
    }

    private async Task PersistLogAndDeleteTenantAsync(
        Guid purgeLogId,
        TenantPurgeContext context,
        Dictionary<string, long> rowCounts,
        string outcome,
        string? failureNote,
        CancellationToken ct)
    {
        var rowCountsJson = JsonSerializer.SerializeToElement(rowCounts);
        var entity = new TenantPurgeLog
        {
            Id = purgeLogId,
            TenantId = context.TenantId,
            TenantCode = context.TenantCode,
            TenantName = context.TenantName,
            PurgedAt = _clock.GetUtcNow(),
            PurgedByUserId = context.ConfirmingUserId,
            DeletionReason = context.DeletionReason,
            SoftDeletedAt = context.SoftDeletedAt,
            RowCounts = rowCountsJson,
            Outcome = outcome,
            FailureNote = failureNote,
        };
        _tenancy.TenantPurgeLog.Add(entity);

        // Final delete on the tenants row itself. The Tenant entity is
        // tracked by the TenantLifecycleService load, so we delete via
        // raw SQL to avoid contention with that tracker — the lifecycle
        // service may have the row attached as Modified (state =
        // PendingHardPurge), and rebuilding tracking state mid-orchestration
        // would be brittle. Use IgnoreQueryFilters semantics in raw SQL.
        await _tenancy.SaveChangesAsync(ct);

        // After the tenant_purge_log row is committed, delete the tenants
        // row. Done in a separate SaveChanges so a deletion failure
        // (e.g. FK reference we missed) doesn't undo the log.
        try
        {
            var deleted = await _tenancy.Database.ExecuteSqlRawAsync(
                "DELETE FROM tenancy.tenants WHERE \"Id\" = {0};",
                new object[] { context.TenantId },
                ct);
            rowCounts["tenancy.tenants"] = deleted;
            // Update the log row's row counts now that we know the final tally.
            entity.RowCounts = JsonSerializer.SerializeToElement(rowCounts);
            await _tenancy.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "TenantPurge[tenancy] FAILED to delete tenants row for tenant {TenantId}; "
                + "tenant_purge_log entry persisted with outcome={Outcome}.",
                context.TenantId, outcome);
            entity.Outcome = "partial";
            entity.FailureNote = $"tenancy: {ex.GetType().Name}: {ex.Message}";
            try { await _tenancy.SaveChangesAsync(ct); }
            catch { /* best-effort */ }
        }
    }

    private static async Task<long> ExecDeleteAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, long tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"DELETE FROM {table} WHERE ""TenantId"" = @tid;";
        var p = cmd.CreateParameter();
        p.ParameterName = "tid";
        p.Value = tenantId;
        cmd.Parameters.Add(p);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Configuration for <see cref="TenantPurgeOrchestrator"/>. Connection
/// strings can come from <c>NICKERP_*</c> env vars at startup; the table
/// lists default to the canonical sets if null.
/// </summary>
public sealed class TenantPurgeOrchestratorOptions
{
    /// <summary>Connection string for <c>nickerp_platform</c>.</summary>
    public string? PlatformConnectionString { get; set; }

    /// <summary>Connection string for <c>nickerp_inspection</c>. Null = skip inspection block.</summary>
    public string? InspectionConnectionString { get; set; }

    /// <summary>Connection string for <c>nickerp_nickfinance</c>. Null = skip nickfinance block.</summary>
    public string? NickFinanceConnectionString { get; set; }

    /// <summary>Override the inspection-tables list (test fixtures).</summary>
    public IReadOnlyList<string>? InspectionTables { get; set; }

    /// <summary>Override the nickfinance-tables list (test fixtures).</summary>
    public IReadOnlyList<string>? NickFinanceTables { get; set; }

    /// <summary>Override the audit-tables list.</summary>
    public IReadOnlyList<string>? AuditTables { get; set; }

    /// <summary>Override the identity-tables list.</summary>
    public IReadOnlyList<string>? IdentityTables { get; set; }
}
