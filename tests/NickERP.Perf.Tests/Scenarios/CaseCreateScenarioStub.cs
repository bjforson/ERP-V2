using NickERP.Perf.Tests.Auth;

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
///   <item>A test tenant + analyst user provisioned in the perf-test DB
///         (Sprint 52 / Phase C — <c>tools/perf-seed</c> handles this).</item>
///   <item>A bearer token for the test analyst — produced via
///         <see cref="GetBearerToken"/> below; the mode (mock vs real)
///         is decided by env var per test-plan §11.</item>
///   <item>NBomber HTTP client wired with the bearer header.</item>
///   <item>Realistic case payload fixtures (container number,
///         scanner-event reference, AnalysisService claim).</item>
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
/// route <c>case-create</c> to the live implementation. The auth seam is
/// already wired (see <see cref="GetBearerToken"/>) so the Phase V work
/// is just the request-shaping + assertion plumbing.
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

    /// <summary>
    /// Sprint 52 / FU-perf-auth-mocking-decision — env var the operator
    /// sets to a real CF Access JWT for the spot-check scenario. When
    /// set, <see cref="GetBearerToken"/> returns it verbatim; the
    /// scenario hits the real CF Access JWKS path. When unset, the mock
    /// signer at <see cref="MockJwtBearerHandler"/> produces a fresh
    /// signed token per run. The decision is documented in
    /// <c>docs/perf/test-plan.md §11</c>.
    /// </summary>
    public const string RealBearerTokenEnvVar = "NICKERP_PERF_BEARER_TOKEN";

    /// <summary>
    /// Resolve the bearer token for this scenario invocation. Mode is
    /// chosen by the <see cref="RealBearerTokenEnvVar"/> env var:
    /// </summary>
    /// <list type="bullet">
    ///   <item>Set → spot-check path; the env-var value is returned as-is.</item>
    ///   <item>Unset → rep-volume path; <paramref name="mockHandler"/>
    ///         signs a fresh CF-Access-shaped token. The handler must be
    ///         a long-lived instance per run (signing key rotates per
    ///         instance).</item>
    /// </list>
    /// <param name="mockHandler">The per-run signer; required when env-var unset.</param>
    /// <param name="subject">CF Access <c>sub</c> claim — analyst user id.</param>
    /// <param name="email">CF Access <c>email</c> claim.</param>
    /// <param name="tenantId">Custom <c>tenant_id</c> claim — must match the seeded perf tenant.</param>
    public static string GetBearerToken(
        MockJwtBearerHandler mockHandler,
        string subject,
        string email,
        long tenantId)
    {
        var realToken = Environment.GetEnvironmentVariable(RealBearerTokenEnvVar);
        if (!string.IsNullOrWhiteSpace(realToken))
        {
            return realToken;
        }

        ArgumentNullException.ThrowIfNull(mockHandler);
        return mockHandler.ProduceBearerToken(subject, email, tenantId);
    }
}
