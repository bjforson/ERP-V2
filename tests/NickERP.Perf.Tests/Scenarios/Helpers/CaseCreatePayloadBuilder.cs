using System.Text.Json;

namespace NickERP.Perf.Tests.Scenarios.Helpers;

/// <summary>
/// Sprint 55 — builds realistic case-create request payloads for the
/// CaseCreate perf scenario. Vendor-neutral (no Ghana / customs strings);
/// the <c>SubjectIdentifier</c> is an ISO 6346 container number with
/// valid check digit, the scanner / location ids are stable test
/// fixtures the operator pre-creates via <c>tools/perf-seed</c>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint shape is per <c>docs/perf/test-plan.md</c> §2.1 EP-001
/// (<c>POST /api/inspection/cases</c>). The actual handler does NOT yet
/// exist — case creation today flows through <c>NewCase.razor</c> +
/// <c>CaseWorkflowService.OpenCaseAsync</c>. The HTTP API will land
/// alongside Phase V kickoff; the scenario is shaped so it'll work as
/// soon as the endpoint exists. Until then, the scenario gracefully
/// reports 404 / auth failures as "endpoint not yet exposed".
/// </para>
/// </remarks>
public static class CaseCreatePayloadBuilder
{
    /// <summary>
    /// Build one case-create request body. Uses
    /// <see cref="ContainerNumberGenerator"/> for the
    /// <c>subjectIdentifier</c> and embeds a scanner-event reference +
    /// AnalysisService claim per the brief.
    /// </summary>
    /// <param name="rng">RNG for varying payloads across iterations.</param>
    /// <param name="locationId">Pre-seeded location guid (perf fixture).</param>
    /// <param name="analysisServiceCode">Vendor-neutral analysis service code (e.g. "default").</param>
    /// <returns>JSON string ready for an HTTP request body.</returns>
    public static string Build(Random rng, Guid locationId, string analysisServiceCode = "default")
    {
        ArgumentNullException.ThrowIfNull(rng);

        var containerNumber = ContainerNumberGenerator.Generate(rng);
        var scannerEventId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow.AddSeconds(-rng.Next(1, 60));

        var body = new
        {
            locationId,
            subjectType = "Container",
            subjectIdentifier = containerNumber,
            // Scanner-event reference per the brief — links to an upstream
            // scan capture. The id is generated per request so the test
            // exercises the case-create dedupe logic at most once per id.
            scannerEvent = new
            {
                id = scannerEventId,
                capturedAt = capturedAt.ToString("O")
            },
            // AnalysisService claim — which service should pick this case
            // up for review. Vendor-neutral default handles it; ASE /
            // FS6000 / etc. are adapter-specific overrides used elsewhere.
            analysisService = new
            {
                code = analysisServiceCode
            },
            // Idempotency key so a retried request collapses to one case.
            idempotencyKey = $"perf-{Guid.NewGuid():N}"
        };

        return JsonSerializer.Serialize(body, JsonOptions);
    }

    /// <summary>
    /// JSON options matching the platform's standard wire format
    /// (camelCase, ignore null). Public so unit tests can serialise
    /// with the same options.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
