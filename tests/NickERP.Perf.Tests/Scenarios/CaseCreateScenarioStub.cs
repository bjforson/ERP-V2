namespace NickERP.Perf.Tests.Scenarios;

/// <summary>
/// STUB — case-create scenario for the inspection module's hot path
/// (<c>POST /api/inspection/cases</c> per test-plan §2.1 EP-001).
/// </summary>
/// <remarks>
/// <para>
/// Not yet executable. Phase V execution requires:
/// </para>
/// <list type="number">
///   <item>A test tenant + analyst user provisioned in the perf-test DB</item>
///   <item>A CF Access JWT bearer for the test analyst (or mocked JWKS validation per test-plan §11)</item>
///   <item>NBomber HTTP client wired with the bearer header</item>
///   <item>Realistic case payload fixtures (container number, scanner-event reference, AnalysisService claim)</item>
/// </list>
/// <para>
/// Profile RPS (per test-plan §3.1):
/// </para>
/// <list type="bullet">
///   <item>1x pilot — 0.35 RPS, p99 acceptance gate 1000 ms (BLOCK at 2000 ms)</item>
///   <item>5x — 1.75 RPS, p99 1500 ms acceptance gate (relaxed 50%)</item>
///   <item>10x — 3.5 RPS, informative only</item>
/// </list>
/// <para>
/// When Phase V kicks off, this stub gets fleshed out with a Build method
/// that returns a real ScenarioProps. The Program.cs dispatcher will then
/// route <c>case-create</c> to the live implementation.
/// </para>
/// </remarks>
public static class CaseCreateScenarioStub
{
    public const string ScenarioName = "case-create";

    /// <summary>Endpoint under test, per docs/perf/test-plan.md §2.1.</summary>
    public const string EndpointPath = "/api/inspection/cases";

    /// <summary>Acceptance-gate latency in ms at 1x pilot peak. p99 must be ≤ this.</summary>
    public const int Pilot1xP99AcceptanceMs = 1000;

    /// <summary>BLOCK-pilot latency in ms at 1x pilot peak. p99 above this fails Phase V.</summary>
    public const int Pilot1xP99BlockMs = 2000;
}
