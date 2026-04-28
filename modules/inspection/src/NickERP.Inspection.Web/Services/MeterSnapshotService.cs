using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using NickERP.Platform.Telemetry;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint A2 — In-process subscriber for the four acceptance-bar
/// instruments declared on <see cref="NickErpActivity.Meter"/>. Powers
/// the <c>/perf</c> admin page; runs alongside (not in place of) the OTel
/// pipeline that ships the same meters to Seq / OTLP. The snapshot is
/// purely a development-time read-out — for production observability the
/// canonical source is whatever scrapes the OTLP exporter.
///
/// Design notes:
/// <list type="bullet">
///   <item><description>Subscribes via <see cref="MeterListener"/> at construction; unsubscribes on disposal. Singleton lifetime so the listener spans the whole host.</description></item>
///   <item><description>Histograms keep a per-tag-key sliding-window of the last <see cref="WindowSeconds"/> seconds (default 300s). Older samples are pruned lazily on each Record + on every <see cref="Snapshot"/> call.</description></item>
///   <item><description>Counters keep a running total per tag-key. No window — totals are monotonic across the host's lifetime, which matches how Prometheus / OTLP sees them.</description></item>
///   <item><description>Per-series sample cap (<see cref="MaxSamplesPerSeries"/>, default 5000) bounds memory if a series gets hammered.</description></item>
///   <item><description>Series key = instrument name + sorted "k=v|k=v" tag tuple. Stable across recordings so percentiles aggregate per dimension.</description></item>
/// </list>
/// </summary>
public sealed class MeterSnapshotService : IDisposable
{
    /// <summary>Sliding window for histograms, in seconds. Older samples are dropped.</summary>
    public const int WindowSeconds = 300;

    /// <summary>Per-series sample cap to bound memory when a histogram is recorded at very high volume.</summary>
    public const int MaxSamplesPerSeries = 5000;

    /// <summary>Instrument-name prefix this service subscribes to. Anything outside the prefix is ignored.</summary>
    public const string InstrumentPrefix = "nickerp.inspection.";

    private readonly MeterListener _listener;
    private readonly Func<DateTimeOffset> _clock;

    // Per-series state. Keyed by "instrumentName||sortedTagKey".
    private readonly ConcurrentDictionary<string, HistogramSeries> _histograms = new();
    private readonly ConcurrentDictionary<string, CounterSeries> _counters = new();

    public MeterSnapshotService() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>
    /// Test seam — caller-provided clock so unit tests can drive samples
    /// at deterministic times and assert window-pruning behaviour.
    /// </summary>
    public MeterSnapshotService(Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                // Only attach to NickERP-meter instruments matching our
                // prefix. The listener is shared with the host's other
                // meters (runtime, ASP.NET) which we don't want polluting
                // the /perf table.
                if (instrument.Meter.Name == NickErpTelemetryExtensions.DefaultMeter
                    && instrument.Name.StartsWith(InstrumentPrefix, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
        _listener.Start();
    }

    private void OnDoubleMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        // Histograms in this service are double-typed; counters are long-typed.
        // If a future histogram emits long, route it through Convert.ToDouble.
        if (instrument is Histogram<double>)
        {
            RecordHistogram(instrument.Name, BuildTagKey(tags), measurement);
        }
        else if (instrument is Counter<double>)
        {
            RecordCounter(instrument.Name, BuildTagKey(tags), tags, (long)Math.Round(measurement));
        }
    }

    private void OnLongMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument is Counter<long>)
        {
            RecordCounter(instrument.Name, BuildTagKey(tags), tags, measurement);
        }
        else if (instrument is Histogram<long>)
        {
            RecordHistogram(instrument.Name, BuildTagKey(tags), measurement);
        }
    }

    private void RecordHistogram(string instrumentName, string tagKey, double value)
    {
        var seriesKey = instrumentName + "||" + tagKey;
        var series = _histograms.GetOrAdd(seriesKey, _ => new HistogramSeries(instrumentName, tagKey));
        var now = _clock();
        lock (series.Lock)
        {
            // Lazy prune: drop anything older than the window. Capped at
            // MaxSamplesPerSeries by evicting oldest first when full.
            var cutoff = now.AddSeconds(-WindowSeconds);
            while (series.Samples.Count > 0 && series.Samples.First!.Value.At < cutoff)
            {
                series.Samples.RemoveFirst();
            }
            while (series.Samples.Count >= MaxSamplesPerSeries)
            {
                series.Samples.RemoveFirst();
            }
            series.Samples.AddLast(new Sample(now, value));
            series.LifetimeCount++;
        }
    }

    private void RecordCounter(
        string instrumentName,
        string tagKey,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        long delta)
    {
        var seriesKey = instrumentName + "||" + tagKey;
        // Materialise the tag dict up-front — ref-struct (Span) can't be
        // captured by the GetOrAdd factory lambda. Costs an allocation
        // per Record, but counters fire infrequently relative to image
        // serves so it's a non-issue.
        var tagDict = BuildTagDict(tags);
        var series = _counters.GetOrAdd(seriesKey, _ => new CounterSeries(instrumentName, tagKey, tagDict));
        // Single Interlocked.Add — no need for the per-series lock on a
        // pure additive counter. The tag dictionary is created once with
        // the first sample for this key.
        Interlocked.Add(ref series.Total, delta);
    }

    /// <summary>
    /// Build a stable string key from a tag set (sorted by key) so the
    /// same tag combination always hashes to the same series. Tag values
    /// are stringified via <c>ToString</c>; null becomes the literal "null".
    /// </summary>
    private static string BuildTagKey(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return string.Empty;
        // For typical NickERP tag-counts (1-3) sorting by hand is cheap.
        // Materialise to array, sort, then concat.
        var pairs = new KeyValuePair<string, object?>[tags.Length];
        for (var i = 0; i < tags.Length; i++) pairs[i] = tags[i];
        Array.Sort(pairs, (a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < pairs.Length; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(pairs[i].Key);
            sb.Append('=');
            sb.Append(pairs[i].Value?.ToString() ?? "null");
        }
        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> BuildTagDict(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0) return new Dictionary<string, string>();
        var dict = new Dictionary<string, string>(tags.Length, StringComparer.Ordinal);
        foreach (var kv in tags) dict[kv.Key] = kv.Value?.ToString() ?? "null";
        return dict;
    }

    /// <summary>
    /// Capture the current state of every observed series. Cheap enough to
    /// call per render — copies samples under each series's lock and runs
    /// a Quickselect-style percentile pass on the snapshot.
    /// </summary>
    public PerfSnapshot Snapshot()
    {
        var now = _clock();
        var cutoff = now.AddSeconds(-WindowSeconds);

        var histograms = new List<HistogramView>(_histograms.Count);
        foreach (var (_, series) in _histograms)
        {
            double[] valuesInWindow;
            long lifetime;
            lock (series.Lock)
            {
                // Prune in place so memory stays bounded even if Snapshot
                // is the only thing keeping the GC honest.
                while (series.Samples.Count > 0 && series.Samples.First!.Value.At < cutoff)
                {
                    series.Samples.RemoveFirst();
                }
                valuesInWindow = new double[series.Samples.Count];
                var i = 0;
                foreach (var s in series.Samples) valuesInWindow[i++] = s.Value;
                lifetime = series.LifetimeCount;
            }

            histograms.Add(new HistogramView(
                Name: series.InstrumentName,
                TagKey: series.TagKey,
                Count: valuesInWindow.Length,
                LifetimeCount: lifetime,
                P50: Percentile(valuesInWindow, 0.50),
                P95: Percentile(valuesInWindow, 0.95),
                P99: Percentile(valuesInWindow, 0.99),
                Min: valuesInWindow.Length == 0 ? 0 : valuesInWindow.Min(),
                Max: valuesInWindow.Length == 0 ? 0 : valuesInWindow.Max(),
                WindowSeconds: WindowSeconds));
        }

        var counters = new List<CounterView>(_counters.Count);
        foreach (var (_, series) in _counters)
        {
            counters.Add(new CounterView(
                Name: series.InstrumentName,
                TagKey: series.TagKey,
                Tags: series.Tags,
                Total: Interlocked.Read(ref series.Total)));
        }

        return new PerfSnapshot(
            CapturedAt: now,
            Histograms: histograms,
            Counters: counters);
    }

    /// <summary>
    /// Percentile via nearest-rank on a sorted copy. O(n log n) per call —
    /// fine at 5k samples × ~handful of series, Snapshot is render-time.
    /// Returns 0 for empty input rather than throwing.
    /// </summary>
    public static double Percentile(double[] values, double p)
    {
        if (values is null || values.Length == 0) return 0;
        if (values.Length == 1) return values[0];
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        if (rank < 0) rank = 0;
        if (rank >= sorted.Length) rank = sorted.Length - 1;
        return sorted[rank];
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    // ---------------------------------------------------------------------
    // Internal series state
    // ---------------------------------------------------------------------

    private sealed class HistogramSeries
    {
        public readonly string InstrumentName;
        public readonly string TagKey;
        public readonly LinkedList<Sample> Samples = new();
        public readonly object Lock = new();
        public long LifetimeCount;

        public HistogramSeries(string instrumentName, string tagKey)
        {
            InstrumentName = instrumentName;
            TagKey = tagKey;
        }
    }

    private sealed class CounterSeries
    {
        public readonly string InstrumentName;
        public readonly string TagKey;
        public readonly IReadOnlyDictionary<string, string> Tags;
        public long Total;

        public CounterSeries(string instrumentName, string tagKey, IReadOnlyDictionary<string, string> tags)
        {
            InstrumentName = instrumentName;
            TagKey = tagKey;
            Tags = tags;
        }
    }

    private readonly record struct Sample(DateTimeOffset At, double Value);
}

/// <summary>Snapshot DTO returned to the /perf page.</summary>
public sealed record PerfSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<HistogramView> Histograms,
    IReadOnlyList<CounterView> Counters);

/// <summary>One histogram series — values aggregated over the window.</summary>
public sealed record HistogramView(
    string Name,
    string TagKey,
    int Count,
    long LifetimeCount,
    double P50,
    double P95,
    double P99,
    double Min,
    double Max,
    int WindowSeconds);

/// <summary>One counter series — running total + the originating tag dict.</summary>
public sealed record CounterView(
    string Name,
    string TagKey,
    IReadOnlyDictionary<string, string> Tags,
    long Total);
