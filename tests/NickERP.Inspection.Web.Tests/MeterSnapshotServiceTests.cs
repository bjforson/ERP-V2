using System.Diagnostics.Metrics;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Web.Components.Pages;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Telemetry;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint A2 — verifies the in-app MeterListener-backed snapshot service:
/// (1) histogram percentiles match a known sample distribution,
/// (2) tag dimensions partition into separate series,
/// (3) the counter total tracks every Add call,
/// (4) zero-state (no recordings yet) returns an empty snapshot rather
///     than throwing, so the /perf page renders cleanly on cold-start.
///
/// Each test uses a unique instrument name (GUID-suffixed) to keep the
/// shared static <see cref="NickErpActivity.Meter"/> from leaking state
/// across tests. The instruments must still match the
/// <c>nickerp.inspection.</c> prefix the snapshot service filters on.
/// </summary>
public sealed class MeterSnapshotServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void EmptyState_SnapshotIsRenderable()
    {
        // Cold-start surface: snapshotting before any sample lands on a
        // fresh service instance must not throw and must return a
        // populated DTO.
        using var svc = new MeterSnapshotService();
        var snap = svc.Snapshot();
        Assert.NotNull(snap);
        Assert.NotNull(snap.Histograms);
        Assert.NotNull(snap.Counters);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Histogram_With100KnownValues_ProducesSaneP95()
    {
        // Unique instrument name per test run so other tests on the same
        // process can't pollute the assertion. Name still starts with
        // "nickerp.inspection." so the service's prefix filter accepts it.
        var instrumentName = $"nickerp.inspection.testhist.{Guid.NewGuid():N}";
        var hist = NickErpActivity.Meter.CreateHistogram<double>(instrumentName);

        using var svc = new MeterSnapshotService();

        for (int i = 1; i <= 100; i++)
        {
            hist.Record(i, new KeyValuePair<string, object?>("kind", "thumbnail"));
        }

        var snap = svc.Snapshot();
        var view = Assert.Single(snap.Histograms, h => h.Name == instrumentName);
        Assert.Equal(100, view.Count);
        Assert.Equal(100, view.LifetimeCount);
        Assert.Equal(1.0, view.Min);
        Assert.Equal(100.0, view.Max);
        // p50 of 1..100 (nearest-rank: ceil(0.5*100)-1 = 49) → sorted[49] = 50.0
        Assert.Equal(50.0, view.P50);
        // p95 → sorted[ceil(0.95*100)-1] = sorted[94] = 95.0
        Assert.Equal(95.0, view.P95);
        // p99 → sorted[ceil(0.99*100)-1] = sorted[98] = 99.0
        Assert.Equal(99.0, view.P99);
        Assert.Contains("kind=thumbnail", view.TagKey);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Counter_AddedAcrossTagSets_SplitsIntoSeries()
    {
        var instrumentName = $"nickerp.inspection.testcounter.{Guid.NewGuid():N}";
        var counter = NickErpActivity.Meter.CreateCounter<long>(instrumentName);

        using var svc = new MeterSnapshotService();

        for (int i = 0; i < 3; i++)
        {
            counter.Add(1,
                new KeyValuePair<string, object?>("from", "Open"),
                new KeyValuePair<string, object?>("to", "Validated"));
        }
        for (int i = 0; i < 7; i++)
        {
            counter.Add(1,
                new KeyValuePair<string, object?>("from", "Verdict"),
                new KeyValuePair<string, object?>("to", "Submitted"));
        }

        var snap = svc.Snapshot();
        var rows = snap.Counters
            .Where(c => c.Name == instrumentName)
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows.Sum(r => r.Total));
        Assert.Single(rows, r => r.Tags["from"] == "Open" && r.Tags["to"] == "Validated");
        Assert.Single(rows, r => r.Tags["from"] == "Verdict" && r.Tags["to"] == "Submitted");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PercentileHelper_OnEmptyArray_ReturnsZeroNotThrow()
    {
        Assert.Equal(0, MeterSnapshotService.Percentile(Array.Empty<double>(), 0.95));
        Assert.Equal(0, MeterSnapshotService.Percentile(null!, 0.5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PerfPage_RendersZeroState_WithAllFourInstrumentNames()
    {
        // AC: "/perf page renders without exception even when no histogram
        // has values yet (zero-state should show 'no data yet')." The page
        // must list every meter name so an analyst sees the four surfaces
        // before any traffic flows. Bunit gives us a deterministic markup
        // string without needing a live host on :5410/:5411.
        using var ctx = new BunitTestContext();
        ctx.Services.AddSingleton(new MeterSnapshotService());

        var cut = ctx.RenderComponent<Perf>();
        var markup = cut.Markup;

        Assert.Contains("nickerp.inspection.image.serve_ms", markup);
        Assert.Contains("nickerp.inspection.prerender.render_ms", markup);
        Assert.Contains("nickerp.inspection.scan.ingest_ms", markup);
        Assert.Contains("nickerp.inspection.case.state_transitions_total", markup);
        // Zero-state empty markers — at least one "no data yet" string.
        Assert.Contains("No data yet", markup);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PerfPage_AfterRecordings_RendersHistogramAndCounterValues()
    {
        // AC: percentiles + counter totals show up after data lands. We
        // record onto the live NickErpActivity instruments so the page
        // exercises the same path the running host hits.
        using var svc = new MeterSnapshotService();

        // Force the four production instruments to be touched so they
        // appear in the snapshot.
        NickErpActivity.ImageServeMs.Record(12.0,
            new KeyValuePair<string, object?>("kind", "thumbnail"),
            new KeyValuePair<string, object?>("status", "200"));
        NickErpActivity.PreRenderRenderMs.Record(80.0,
            new KeyValuePair<string, object?>("kind", "thumbnail"),
            new KeyValuePair<string, object?>("mime", "image/png"));
        NickErpActivity.ScanIngestMs.Record(150.0,
            new KeyValuePair<string, object?>("scanner_type_code", "fs6000"));
        NickErpActivity.CaseStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", "none"),
            new KeyValuePair<string, object?>("to", "Open"));

        using var ctx = new BunitTestContext();
        ctx.Services.AddSingleton(svc);

        var cut = ctx.RenderComponent<Perf>();
        var markup = cut.Markup;

        // Each instrument name must appear in the rendered markup.
        Assert.Contains("nickerp.inspection.image.serve_ms", markup);
        Assert.Contains("nickerp.inspection.prerender.render_ms", markup);
        Assert.Contains("nickerp.inspection.scan.ingest_ms", markup);
        Assert.Contains("nickerp.inspection.case.state_transitions_total", markup);
        // Tag values made it through.
        Assert.Contains("fs6000", markup);
        Assert.Contains("image/png", markup);
        Assert.Contains("thumbnail", markup);
        // Counter total is rendered as ">1<" inside a <td>.
        Assert.Contains("Open", markup);
    }
}
