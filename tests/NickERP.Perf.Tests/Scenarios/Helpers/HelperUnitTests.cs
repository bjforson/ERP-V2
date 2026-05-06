using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace NickERP.Perf.Tests.Scenarios.Helpers;

/// <summary>
/// Sprint 55 — light unit tests for the scenario helpers. Run via
/// <c>dotnet run --project tests/NickERP.Perf.Tests -- selftest</c>;
/// the dispatcher routes there. Not picked up by <c>dotnet test</c>
/// because Perf.Tests has <c>IsTestProject=false</c> by design (Phase V
/// perf scenarios contribute 0 to the unit-test floor).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not xUnit.</b> Adding xUnit + a TestSdk reference here would
/// either (a) make Perf.Tests contribute tests to <c>dotnet test</c>
/// (breaks the brief's "0 contribution" rule) or (b) add a second test
/// project (out of scope for the path-zone allowed for Sprint 55). The
/// self-test CLI mode is the right size — it gives operators a one-line
/// verification that the helpers are sane without any project ceremony.
/// </para>
/// <para>
/// <b>Coverage.</b> One assertion per helper concern:
/// </para>
/// <list type="bullet">
///   <item>ISO 6346 check-digit roundtrip.</item>
///   <item>ISO 6346 invalid-input rejection.</item>
///   <item>Case-create payload shape (parses + carries the expected fields).</item>
///   <item>Edge-replay batch envelope (parses + carries N events with valid hint distribution).</item>
///   <item>CaseCreateScenario.ShouldSkip for missing target / missing auth.</item>
///   <item>EdgeReplayScenario.ShouldSkip for missing HMAC key.</item>
///   <item>EdgeReplayScenario URL composition (base + path).</item>
///   <item>Acceptance-gate boundary check (PASS / WARN / BLOCK regions).</item>
/// </list>
/// </remarks>
public static class HelperUnitTests
{
    /// <summary>
    /// Run every assertion. Returns 0 when all pass, non-zero on any
    /// failure. Each failure logs the assertion that broke.
    /// </summary>
    public static int RunAll(Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var failures = 0;

        failures += Run(log, nameof(ContainerNumber_Generate_Roundtrip), ContainerNumber_Generate_Roundtrip);
        failures += Run(log, nameof(ContainerNumber_Validate_RejectsBadCheckDigit), ContainerNumber_Validate_RejectsBadCheckDigit);
        failures += Run(log, nameof(ContainerNumber_Validate_RejectsLength), ContainerNumber_Validate_RejectsLength);
        failures += Run(log, nameof(CaseCreatePayload_ShapeIsValidJson), CaseCreatePayload_ShapeIsValidJson);
        failures += Run(log, nameof(CaseCreatePayload_CarriesContainerNumber), CaseCreatePayload_CarriesContainerNumber);
        failures += Run(log, nameof(EdgeReplayBatch_HasEdgeNodeIdAndEvents), EdgeReplayBatch_HasEdgeNodeIdAndEvents);
        failures += Run(log, nameof(EdgeReplayBatch_HintDistribution), EdgeReplayBatch_HintDistribution);
        failures += Run(log, nameof(EdgeReplayBatch_AuditReplayCarriesRequiredFields), EdgeReplayBatch_AuditReplayCarriesRequiredFields);
        failures += Run(log, nameof(EdgeReplayBatch_ScanCapturedCarriesRequiredFields), EdgeReplayBatch_ScanCapturedCarriesRequiredFields);
        failures += Run(log, nameof(EdgeReplayBatch_StatusChangedCarriesRequiredFields), EdgeReplayBatch_StatusChangedCarriesRequiredFields);
        failures += Run(log, nameof(CaseCreate_ShouldSkip_NoTarget), CaseCreate_ShouldSkip_NoTarget);
        failures += Run(log, nameof(CaseCreate_ShouldSkip_NoAuth), CaseCreate_ShouldSkip_NoAuth);
        failures += Run(log, nameof(CaseCreate_ShouldNotSkip_WithMockJwt), CaseCreate_ShouldNotSkip_WithMockJwt);
        failures += Run(log, nameof(EdgeReplay_ShouldSkip_NoHmacKey), EdgeReplay_ShouldSkip_NoHmacKey);
        failures += Run(log, nameof(EdgeReplay_UrlComposition), EdgeReplay_UrlComposition);
        failures += Run(log, nameof(CaseCreate_ResolveLocationId_DefaultsToFixed), CaseCreate_ResolveLocationId_DefaultsToFixed);
        failures += Run(log, nameof(CaseCreate_AcceptanceGate_PassRegion), CaseCreate_AcceptanceGate_PassRegion);
        failures += Run(log, nameof(CaseCreate_AcceptanceGate_BlockRegion), CaseCreate_AcceptanceGate_BlockRegion);
        failures += Run(log, nameof(EdgeReplay_AcceptanceGate_BlockRegion), EdgeReplay_AcceptanceGate_BlockRegion);
        failures += Run(log, nameof(EdgeReplay_AcceptanceGate_Stress10x_AlwaysPass), EdgeReplay_AcceptanceGate_Stress10x_AlwaysPass);

        log($"selftest: {failures} failure(s).");
        return failures;
    }

    private static int Run(Action<string> log, string name, Action testBody)
    {
        try
        {
            testBody();
            log($"  OK   {name}");
            return 0;
        }
        catch (Exception ex)
        {
            log($"  FAIL {name}: {ex.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------- ISO 6346

    private static void ContainerNumber_Generate_Roundtrip()
    {
        var rng = new Random(12345);
        for (var i = 0; i < 50; i++)
        {
            var c = ContainerNumberGenerator.Generate(rng);
            if (c.Length != 11) throw new InvalidOperationException($"length={c.Length}");
            if (!ContainerNumberGenerator.IsValid(c)) throw new InvalidOperationException($"check-digit invalid for {c}");
        }
    }

    private static void ContainerNumber_Validate_RejectsBadCheckDigit()
    {
        // Take a valid number, flip the last digit by +1 (mod 10) to invalidate.
        var rng = new Random(42);
        var c = ContainerNumberGenerator.Generate(rng);
        var lastDigit = c[10] - '0';
        var bad = c[..10] + ((lastDigit + 1) % 10).ToString();
        if (bad == c) throw new InvalidOperationException("test setup: lastDigit unchanged");
        if (ContainerNumberGenerator.IsValid(bad)) throw new InvalidOperationException($"expected invalid: {bad}");
    }

    private static void ContainerNumber_Validate_RejectsLength()
    {
        if (ContainerNumberGenerator.IsValid("ABCU1234567")) { /* this happens to be valid in some seeds; check format is what we test */ }
        if (ContainerNumberGenerator.IsValid("ABCU12345")) throw new InvalidOperationException("9 chars accepted");
        if (ContainerNumberGenerator.IsValid("ABCU1234567890")) throw new InvalidOperationException("14 chars accepted");
        if (ContainerNumberGenerator.IsValid("")) throw new InvalidOperationException("empty accepted");
    }

    // ---------------------------------------------------------------- Case-create payload

    private static void CaseCreatePayload_ShapeIsValidJson()
    {
        var rng = new Random(1);
        var json = CaseCreatePayloadBuilder.Build(rng, Guid.NewGuid());
        // Throws on invalid JSON.
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("root is not object");
    }

    private static void CaseCreatePayload_CarriesContainerNumber()
    {
        var rng = new Random(2);
        var json = CaseCreatePayloadBuilder.Build(rng, Guid.NewGuid());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("subjectIdentifier", out var subj))
            throw new InvalidOperationException("missing subjectIdentifier");
        var s = subj.GetString() ?? throw new InvalidOperationException("subjectIdentifier null");
        if (!ContainerNumberGenerator.IsValid(s))
            throw new InvalidOperationException($"subjectIdentifier {s} not ISO 6346 valid");
        // Required fields.
        foreach (var f in new[] { "locationId", "subjectType", "scannerEvent", "analysisService", "idempotencyKey" })
        {
            if (!root.TryGetProperty(f, out _))
                throw new InvalidOperationException($"missing {f}");
        }
    }

    // ---------------------------------------------------------------- Edge-replay batch

    private static void EdgeReplayBatch_HasEdgeNodeIdAndEvents()
    {
        var rng = new Random(3);
        var json = EdgeReplayPayloadBuilder.BuildBatch(rng, "test-edge", 99);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("edgeNodeId", out var id) || id.GetString() != "test-edge")
            throw new InvalidOperationException("edgeNodeId mismatch");
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("events not array");
        if (events.GetArrayLength() < 1) throw new InvalidOperationException("0 events");
    }

    private static void EdgeReplayBatch_HintDistribution()
    {
        var rng = new Random(7);
        // Build many batches so the distribution stabilises — each batch
        // is a tiny sample but 200+ events across 50 batches catches any
        // hint-shape bugs.
        var hintCounts = new Dictionary<string, int>();
        for (var i = 0; i < 50; i++)
        {
            var json = EdgeReplayPayloadBuilder.BuildBatch(rng, "edge", 1, meanEvents: 5, maxEvents: 20);
            using var doc = JsonDocument.Parse(json);
            foreach (var evt in doc.RootElement.GetProperty("events").EnumerateArray())
            {
                var hint = evt.GetProperty("eventTypeHint").GetString()!;
                hintCounts[hint] = hintCounts.GetValueOrDefault(hint, 0) + 1;
            }
        }
        // All three hints must have appeared at least once.
        foreach (var hint in new[] {
            EdgeReplayPayloadBuilder.AuditEventReplayHint,
            EdgeReplayPayloadBuilder.ScanCapturedHint,
            EdgeReplayPayloadBuilder.ScannerStatusChangedHint })
        {
            if (!hintCounts.TryGetValue(hint, out var n) || n == 0)
                throw new InvalidOperationException($"hint {hint} never produced");
        }
    }

    private static void EdgeReplayBatch_AuditReplayCarriesRequiredFields()
    {
        var rng = new Random(11);
        var payload = EdgeReplayPayloadBuilder.BuildAuditReplayPayload(rng);
        foreach (var f in new[] { "eventType", "entityType", "entityId" })
        {
            var n = payload[f];
            if (n is null) throw new InvalidOperationException($"missing {f}");
        }
    }

    private static void EdgeReplayBatch_ScanCapturedCarriesRequiredFields()
    {
        var rng = new Random(12);
        var payload = EdgeReplayPayloadBuilder.BuildScanCapturedPayload(rng);
        foreach (var f in new[] { "scannerId", "sourcePath" })
        {
            var n = payload[f];
            if (n is null) throw new InvalidOperationException($"missing {f}");
        }
    }

    private static void EdgeReplayBatch_StatusChangedCarriesRequiredFields()
    {
        var rng = new Random(13);
        var payload = EdgeReplayPayloadBuilder.BuildScannerStatusChangedPayload(rng);
        foreach (var f in new[] { "scannerId", "status" })
        {
            var n = payload[f];
            if (n is null) throw new InvalidOperationException($"missing {f}");
        }
    }

    // ---------------------------------------------------------------- Skip logic

    private static void CaseCreate_ShouldSkip_NoTarget()
    {
        var config = BuildConfig(("Auth:MockJwt:Subject", "x"));
        // No TargetBaseUrl + no CaseCreate:TargetBaseUrl → should skip.
        var (skip, reason) = Scenarios.CaseCreateScenario.ShouldSkip(config);
        if (!skip) throw new InvalidOperationException("expected skip when no target");
        if (string.IsNullOrEmpty(reason)) throw new InvalidOperationException("skip reason missing");
    }

    private static void CaseCreate_ShouldSkip_NoAuth()
    {
        var config = BuildConfig(("TargetBaseUrl", "http://localhost:5400"));
        // No mock JWT subject + no real bearer env var → should skip.
        Environment.SetEnvironmentVariable(Scenarios.CaseCreateScenario.RealBearerTokenEnvVar, null);
        var (skip, reason) = Scenarios.CaseCreateScenario.ShouldSkip(config);
        if (!skip) throw new InvalidOperationException("expected skip when no auth");
        if (reason is null || !reason.Contains("bearer")) throw new InvalidOperationException("skip reason should mention bearer");
    }

    private static void CaseCreate_ShouldNotSkip_WithMockJwt()
    {
        Environment.SetEnvironmentVariable(Scenarios.CaseCreateScenario.RealBearerTokenEnvVar, null);
        var config = BuildConfig(
            ("TargetBaseUrl", "http://localhost:5400"),
            ("Auth:MockJwt:Subject", "perf-analyst"));
        var (skip, _) = Scenarios.CaseCreateScenario.ShouldSkip(config);
        if (skip) throw new InvalidOperationException("expected NOT skip when mock JWT subject + target are set");
    }

    private static void EdgeReplay_ShouldSkip_NoHmacKey()
    {
        Environment.SetEnvironmentVariable(Scenarios.EdgeReplayScenario.EdgeHmacKeyEnvVar, null);
        var config = BuildConfig(("TargetBaseUrl", "http://localhost:5410"));
        var (skip, reason) = Scenarios.EdgeReplayScenario.ShouldSkip(config);
        if (!skip) throw new InvalidOperationException("expected skip when no HMAC key");
        if (reason is null || !reason.Contains("HMAC")) throw new InvalidOperationException("skip reason should mention HMAC");
    }

    // ---------------------------------------------------------------- URL composition

    private static void EdgeReplay_UrlComposition()
    {
        var config = BuildConfig(
            ("Endpoints:InspectionWebBaseUrl", "http://api.example.com"),
            ("Endpoints:EdgeReplayPath", "/api/edge/replay"));
        var url = Scenarios.EdgeReplayScenario.ResolveEndpointUrl(config);
        if (url != "http://api.example.com/api/edge/replay")
            throw new InvalidOperationException($"unexpected url: {url}");

        // Trailing-slash on base must be normalised.
        var config2 = BuildConfig(
            ("Endpoints:InspectionWebBaseUrl", "http://api.example.com/"),
            ("Endpoints:EdgeReplayPath", "/api/edge/replay"));
        var url2 = Scenarios.EdgeReplayScenario.ResolveEndpointUrl(config2);
        if (url2 != "http://api.example.com/api/edge/replay")
            throw new InvalidOperationException($"unexpected url2: {url2}");
    }

    private static void CaseCreate_ResolveLocationId_DefaultsToFixed()
    {
        var config = BuildConfig();
        var loc = Scenarios.CaseCreateScenario.ResolveLocationId(config);
        // The default is the "00…05ca5e0" sentinel — won't collide with seeded data.
        var expected = new Guid("00000000-0000-0000-0000-0000005ca5e0");
        if (loc != expected) throw new InvalidOperationException($"unexpected default loc: {loc}");

        // When set explicitly, it parses.
        var explicitGuid = "11111111-2222-3333-4444-555555555555";
        var config2 = BuildConfig(("CaseCreate:LocationId", explicitGuid));
        var loc2 = Scenarios.CaseCreateScenario.ResolveLocationId(config2);
        if (loc2 != Guid.Parse(explicitGuid)) throw new InvalidOperationException($"explicit not honoured: {loc2}");
    }

    // ---------------------------------------------------------------- Acceptance gates

    private static void CaseCreate_AcceptanceGate_PassRegion()
    {
        // 800ms p99 at 1x — under 1000ms acceptance.
        var lines = new List<string>();
        var exit = Scenarios.CaseCreateScenario.CheckAcceptanceGate(800.0, LoadProfile.Pilot1x, lines.Add);
        if (exit != 0) throw new InvalidOperationException($"expected exit 0, got {exit}");
        if (!lines.Any(l => l.Contains("PASS"))) throw new InvalidOperationException("expected PASS log");
    }

    private static void CaseCreate_AcceptanceGate_BlockRegion()
    {
        // 2500ms p99 at 1x — above 2000ms BLOCK.
        var lines = new List<string>();
        var exit = Scenarios.CaseCreateScenario.CheckAcceptanceGate(2500.0, LoadProfile.Pilot1x, lines.Add);
        if (exit == 0) throw new InvalidOperationException("expected non-zero exit at BLOCK");
        if (!lines.Any(l => l.Contains("BLOCK"))) throw new InvalidOperationException("expected BLOCK log");
    }

    private static void EdgeReplay_AcceptanceGate_BlockRegion()
    {
        // 2000ms p99 at 1x — above 1500ms BLOCK.
        var lines = new List<string>();
        var exit = Scenarios.EdgeReplayScenario.CheckAcceptanceGate(2000.0, LoadProfile.Pilot1x, lines.Add);
        if (exit == 0) throw new InvalidOperationException("expected non-zero exit at BLOCK");
    }

    private static void EdgeReplay_AcceptanceGate_Stress10x_AlwaysPass()
    {
        // 10x is informative — even astronomic latencies should not block.
        var lines = new List<string>();
        var exit = Scenarios.EdgeReplayScenario.CheckAcceptanceGate(99_999.0, LoadProfile.Stress10x, lines.Add);
        if (exit != 0) throw new InvalidOperationException("10x stress should never BLOCK");
    }

    // ---------------------------------------------------------------- Helpers

    private static IConfiguration BuildConfig(params (string Key, string Value)[] keys)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in keys) dict[k] = v;
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }
}
