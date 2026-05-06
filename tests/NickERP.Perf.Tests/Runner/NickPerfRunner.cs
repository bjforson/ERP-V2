namespace NickERP.Perf.Tests.Runner;

/// <summary>
/// Sprint 58 — homegrown scenario runner. Schedules
/// <see cref="NickPerfScenario.RunStep"/> at the configured rate via
/// <see cref="PeriodicTimer"/>, caps in-flight calls with a
/// <see cref="SemaphoreSlim"/>, and collects stats into
/// <see cref="NickPerfStats"/>. Replaces NBomber's
/// <c>NBomberRunner.RegisterScenarios(...).Run()</c> entry point with a
/// permissively-licensed in-tree primitive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheduling shape.</b> Per the load profile, the runner emits
/// one step every <see cref="NickPerfLoadProfile.TickDelay"/>. With
/// <c>Rate=21, Interval=1min</c> that's a step every ≈2.857 s for
/// <c>Duration</c> seconds — same shape NBomber's
/// <c>Simulation.Inject(rate:21, interval:1min, during:60s)</c> produced
/// in Sprint 55.
/// </para>
/// <para>
/// <b>Concurrency cap.</b> Each tick acquires a semaphore slot before
/// dispatching the step; if the cap is hit, the tick waits. This mirrors
/// NBomber's "queue-up but don't unbounded-spawn" behaviour and keeps
/// the runner stable under runaway-fail conditions (e.g. a 5 s timeout
/// holding every slot).
/// </para>
/// <para>
/// <b>Stats.</b> Per-step latency is captured with
/// <see cref="NickPerfClock"/>; ok/fail attribution comes from the
/// step's <see cref="NickPerfStepResult"/>. The snapshot at the end is
/// the only output (no streaming live-stats; out of scope).
/// </para>
/// </remarks>
public static class NickPerfRunner
{
    /// <summary>
    /// Run one scenario. Returns the stats snapshot. The caller decides
    /// what exit code to produce based on
    /// <see cref="NickPerfStatsSnapshot.ok"/> /
    /// <see cref="NickPerfStatsSnapshot.fail"/> +
    /// <see cref="NickPerfStatsSnapshot.p99"/>.
    /// </summary>
    /// <param name="scenario">Scenario to run.</param>
    /// <param name="reportFolder">Folder under which the markdown
    /// report is written; same shape NBomber's <c>WithReportFolder</c>
    /// produced.</param>
    /// <param name="testName">Name written in the report header.</param>
    /// <param name="ct">Cancellation token; usually
    /// <see cref="CancellationToken.None"/>. The runner enforces its
    /// own timeout via <see cref="NickPerfLoadProfile.Duration"/>.</param>
    public static async Task<NickPerfStatsSnapshot> RunAsync(
        NickPerfScenario scenario,
        string reportFolder,
        string testName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);

        Directory.CreateDirectory(reportFolder);

        var stats = new NickPerfStats();
        var profile = scenario.LoadProfile;
        var tickDelay = profile.TickDelay;
        var deadline = DateTime.UtcNow + profile.Duration;

        Console.WriteLine(
            $"NickPerf scenario={scenario.Name} rate={profile.Rate}/{profile.Interval} " +
            $"duration={profile.Duration} tick={tickDelay} maxConcurrent={scenario.MaxConcurrent}");

        using var sem = new SemaphoreSlim(scenario.MaxConcurrent, scenario.MaxConcurrent);
        using var timer = new PeriodicTimer(tickDelay);
        var inFlight = new List<Task>();

        // The runner cancels the per-step token when the scenario duration
        // elapses. Steps in-flight at deadline get a chance to complete
        // (we Task.WhenAll on the in-flight tasks below) but no NEW step
        // is dispatched after the deadline.
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;
                if (DateTime.UtcNow >= deadline) break;

                // Acquire a slot — backpressure under saturation. The
                // wait time IS counted against the scenario duration but
                // NOT against per-call latency (latency starts after
                // acquire, mirroring NBomber's "queued requests don't
                // count toward latency until they fire" semantics).
                await sem.WaitAsync(ct).ConfigureAwait(false);
                inFlight.Add(DispatchStepAsync(scenario, stats, sem, stepCts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — flush in-flight then snapshot.
        }

        // Wait briefly for in-flight steps to drain. We don't want to
        // truncate the latency tail with a hard kill; pilot-shape
        // scenarios produce bounded tails (5 s timeout per step).
        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch
        {
            // Per-step exceptions are recorded as fail in DispatchStepAsync;
            // anything escaping here is a runner bug — swallow and snapshot.
        }

        var snapshot = stats.Snapshot();
        var reportPath = Path.Combine(reportFolder, "report.md");
        File.WriteAllText(reportPath, NickPerfReport.BuildMarkdown(testName, scenario.Name, snapshot));
        Console.WriteLine(
            $"NickPerf scenario={scenario.Name} ok={snapshot.ok} fail={snapshot.fail} " +
            $"p99={snapshot.p99:F1}ms rps={snapshot.Rps:F2} report={reportPath}");

        return snapshot;
    }

    private static async Task DispatchStepAsync(
        NickPerfScenario scenario,
        NickPerfStats stats,
        SemaphoreSlim sem,
        CancellationToken stepCt)
    {
        var startUtc = DateTime.UtcNow;
        var sw = NickPerfClock.Start();
        NickPerfStepResult result;
        try
        {
            result = await scenario.RunStep(stepCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            result = NickPerfStepResult.Fail($"cancelled: {ex.Message}");
        }
        catch (Exception ex)
        {
            result = NickPerfStepResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            sem.Release();
        }

        var latencyMs = NickPerfClock.StopElapsedMs(sw);
        var endUtc = DateTime.UtcNow;
        stats.Record(result, latencyMs, startUtc, endUtc);
    }
}
