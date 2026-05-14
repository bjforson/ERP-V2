using System.Net;

namespace NickERP.Perf.Tests.Runner;

/// <summary>
/// Sprint 58 — homegrown perf-scenario shape. Replaces the NBomber
/// <c>ScenarioProps</c> + <c>Scenario.Create</c> surface with a small
/// vendor-neutral type the in-tree <see cref="NickPerfRunner"/> drives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why homegrown.</b> Sprint 57's license audit revealed NBomber
/// 6.1.0 ships under a paid commercial subscription. Our usage is small
/// (rate-based scenarios, p50/p95/p99 stats, markdown reports). Owning
/// these primitives removes the licence risk entirely without giving up
/// any feature we actually use.
/// </para>
/// <para>
/// <b>Shape.</b> Each scenario carries a name, a per-step async delegate,
/// and a load profile (rate / interval / duration). The runner schedules
/// the step at the configured rate, captures latency + ok/fail per call,
/// and produces a stats snapshot at the end of the run.
/// </para>
/// <para>
/// <b>Step return.</b> The step returns a <see cref="NickPerfStepResult"/>
/// — ok flag, status code, optional fail-reason. The runner doesn't try
/// to introspect any HTTP shape; the step is responsible for translating
/// its outcome into ok/fail. The HTTP helpers in
/// <see cref="Http.NickPerfHttp"/> do this for the standard "2xx is ok,
/// non-2xx + transport error is fail" case.
/// </para>
/// </remarks>
public sealed class NickPerfScenario
{
    /// <summary>The scenario name. Used in reports + log lines.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Per-step async delegate. Invoked once per scheduled tick. The step
    /// is responsible for its own HTTP work and must return a
    /// <see cref="NickPerfStepResult"/> describing the outcome.
    /// </summary>
    public required Func<CancellationToken, Task<NickPerfStepResult>> RunStep { get; init; }

    /// <summary>
    /// Load profile: how many calls per interval, for how long. The
    /// runner converts this into a per-tick schedule via
    /// <see cref="PeriodicTimer"/>.
    /// </summary>
    public required NickPerfLoadProfile LoadProfile { get; init; }

    /// <summary>
    /// Optional concurrency cap. Defaults to 64 in-flight calls — enough
    /// for any pilot-shaped rate (we run at &lt; 10 RPS in steady state)
    /// and small enough to avoid swamping the runner machine.
    /// </summary>
    public int MaxConcurrent { get; init; } = 64;
}

/// <summary>
/// Sprint 58 — load profile. Rate-based: emit
/// <see cref="Rate"/> calls per <see cref="Interval"/> for
/// <see cref="Duration"/>. Mirrors NBomber's <c>Inject(rate, interval,
/// during)</c> semantics.
/// </summary>
public sealed record NickPerfLoadProfile
{
    /// <summary>Number of calls per interval.</summary>
    public required int Rate { get; init; }

    /// <summary>Interval over which <see cref="Rate"/> calls are spread.</summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>Total duration of the scenario (excluding warmup).</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Optional warmup window before the measured phase begins. Steps
    /// fired during the warmup window go through the same dispatch path
    /// (so JIT / connection pool / DNS caches are warmed) but their
    /// latency is excluded from the stats snapshot. Defaults to zero —
    /// existing scenarios are unaffected until they opt in. The first
    /// request typically pays for JIT compilation + cold HttpClient
    /// channel + DNS, so the p99 of a 30-sample scenario can be
    /// dominated by this single outlier; a 3–5 s warmup window keeps
    /// the measured tail meaningful.
    /// </summary>
    public TimeSpan Warmup { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Convenience: per-tick delay. With <c>Rate=21</c> and
    /// <c>Interval=1min</c>, ticks happen every ≈2.857 s.
    /// </summary>
    public TimeSpan TickDelay => Rate <= 0
        ? Interval
        : TimeSpan.FromTicks(Math.Max(1, Interval.Ticks / Rate));
}

/// <summary>
/// Sprint 58 — single step outcome. Replaces NBomber's
/// <c>Response&lt;T&gt;</c> for our reporting needs.
/// </summary>
public readonly record struct NickPerfStepResult
{
    /// <summary>True if the call succeeded; false otherwise.</summary>
    public bool Ok { get; init; }

    /// <summary>HTTP status code if relevant; 0 for transport errors.</summary>
    public int StatusCode { get; init; }

    /// <summary>Optional fail-reason for diagnostics.</summary>
    public string? FailReason { get; init; }

    public static NickPerfStepResult OkResult(int statusCode = (int)HttpStatusCode.OK)
        => new() { Ok = true, StatusCode = statusCode };

    public static NickPerfStepResult Fail(string reason, int statusCode = 0)
        => new() { Ok = false, StatusCode = statusCode, FailReason = reason };
}
