using System.Diagnostics;

namespace NickERP.Perf.Tests.Runner;

/// <summary>
/// Sprint 58 — collects per-step latency + ok/fail counts and computes
/// p50 / p95 / p99 percentiles via <see cref="Array.Sort{T}(T[])"/> +
/// index lookup. Replaces NBomber's <c>NodeStats</c> /
/// <c>ScenarioStats</c> / <c>LatencyCount</c> shape with the minimum we
/// need for the report.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="List{T}"/> not a streaming-sketch.</b> Pilot-scale
/// runs are short (60s) and small (≤300 RPM). The full latency buffer
/// fits in a few thousand doubles; a streaming sketch (HdrHistogram /
/// t-digest) would buy nothing here and add complexity. If we ever load-
/// test at 100k RPS for an hour we'll revisit.
/// </para>
/// <para>
/// <b>Thread safety.</b> The runner dispatches steps concurrently per
/// <see cref="NickPerfScenario.MaxConcurrent"/>. <see cref="Record"/> is
/// <see langword="lock"/>-protected so concurrent step results are merged
/// safely. Snapshot is taken after the run is complete.
/// </para>
/// </remarks>
public sealed class NickPerfStats
{
    private readonly object _lock = new();
    private readonly List<double> _latencyMs = new(capacity: 1024);
    private long _ok;
    private long _fail;
    private DateTime? _firstStartUtc;
    private DateTime? _lastEndUtc;

    /// <summary>
    /// Record one step's outcome. Safe to call concurrently from
    /// scenario step delegates.
    /// </summary>
    public void Record(NickPerfStepResult result, double latencyMs, DateTime stepStartUtc, DateTime stepEndUtc)
    {
        if (latencyMs < 0) latencyMs = 0;
        lock (_lock)
        {
            _latencyMs.Add(latencyMs);
            if (result.Ok) _ok++;
            else _fail++;
            if (_firstStartUtc is null || stepStartUtc < _firstStartUtc) _firstStartUtc = stepStartUtc;
            if (_lastEndUtc is null || stepEndUtc > _lastEndUtc) _lastEndUtc = stepEndUtc;
        }
    }

    /// <summary>
    /// Snapshot the current stats. Returns a copy — safe to use after
    /// the runner exits without further locking.
    /// </summary>
    public NickPerfStatsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var arr = _latencyMs.ToArray();
            Array.Sort(arr);
            var elapsed = (_lastEndUtc - _firstStartUtc) ?? TimeSpan.Zero;
            return new NickPerfStatsSnapshot(
                ok: _ok,
                fail: _fail,
                p50: Percentile(arr, 50),
                p75: Percentile(arr, 75),
                p95: Percentile(arr, 95),
                p99: Percentile(arr, 99),
                min: arr.Length == 0 ? 0 : arr[0],
                max: arr.Length == 0 ? 0 : arr[^1],
                mean: arr.Length == 0 ? 0 : arr.Sum() / arr.Length,
                elapsed: elapsed);
        }
    }

    /// <summary>
    /// Compute the percentile <paramref name="p"/> (0–100) from a sorted
    /// array. Uses the nearest-rank method (per ISO 5479 / NIST §1.3.6.7
    /// "linear interpolation between closest ranks") which is the same
    /// shape NBomber's <c>HdrHistogram</c> reports — close enough for
    /// our gate-checks.
    /// </summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (p <= 0) return sorted[0];
        if (p >= 100) return sorted[^1];
        // Nearest-rank index, 1-based per NIST: rank = ceil(p/100 * N).
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        var idx = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }
}

/// <summary>
/// Sprint 58 — immutable snapshot of one scenario's stats. Drives the
/// markdown report + the dispatcher's exit-code gating.
/// </summary>
/// <param name="ok">Successful step count.</param>
/// <param name="fail">Failed step count.</param>
/// <param name="p50">50th-percentile latency in ms.</param>
/// <param name="p75">75th-percentile latency in ms.</param>
/// <param name="p95">95th-percentile latency in ms.</param>
/// <param name="p99">99th-percentile latency in ms.</param>
/// <param name="min">Minimum observed latency in ms.</param>
/// <param name="max">Maximum observed latency in ms.</param>
/// <param name="mean">Mean latency in ms.</param>
/// <param name="elapsed">Wall-clock duration of the run.</param>
public sealed record NickPerfStatsSnapshot(
    long ok,
    long fail,
    double p50,
    double p75,
    double p95,
    double p99,
    double min,
    double max,
    double mean,
    TimeSpan elapsed)
{
    /// <summary>Total step count (ok + fail).</summary>
    public long Total => ok + fail;

    /// <summary>
    /// Throughput in requests-per-second. Returns 0 when the run was
    /// instantaneous (no observations).
    /// </summary>
    public double Rps => elapsed.TotalSeconds <= 0 ? 0 : Total / elapsed.TotalSeconds;
}

/// <summary>
/// Sprint 58 — high-resolution stopwatch helpers. <see cref="Stopwatch"/>
/// is used per-step to measure latency in milliseconds.
/// </summary>
public static class NickPerfClock
{
    /// <summary>Start a stopwatch.</summary>
    public static Stopwatch Start() => Stopwatch.StartNew();

    /// <summary>Stop and return elapsed milliseconds.</summary>
    public static double StopElapsedMs(Stopwatch sw)
    {
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
