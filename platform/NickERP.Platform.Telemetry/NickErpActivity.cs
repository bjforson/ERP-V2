using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NickERP.Platform.Telemetry;

/// <summary>
/// Static handles to the canonical NickERP <see cref="ActivitySource"/> and
/// <see cref="Meter"/>. Use these for manual spans / counters anywhere inside
/// the platform or modules — keeps span names + metric names under one
/// consistent root namespace, which keeps Seq queries simple.
/// </summary>
/// <example>
/// <code>
/// using var span = NickErpActivity.Source.StartActivity("inspection.case.review");
/// span?.SetTag("case.id", caseId);
/// // ... do work ...
/// NickErpActivity.CasesReviewed.Add(1, new KeyValuePair&lt;string, object?&gt;("verdict", verdict));
/// </code>
/// </example>
public static class NickErpActivity
{
    /// <summary>Single shared <see cref="ActivitySource"/>. Name matches <see cref="NickErpTelemetryExtensions.DefaultActivitySource"/>.</summary>
    public static readonly ActivitySource Source = new(
        NickErpTelemetryExtensions.DefaultActivitySource,
        version: typeof(NickErpActivity).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    /// <summary>Single shared <see cref="Meter"/>. Name matches <see cref="NickErpTelemetryExtensions.DefaultMeter"/>.</summary>
    public static readonly Meter Meter = new(
        NickErpTelemetryExtensions.DefaultMeter,
        version: typeof(NickErpActivity).Assembly.GetName().Version?.ToString() ?? "0.0.0");

    // -------------------------------------------------------------------
    // Sprint A2 — Acceptance-bar instruments. Centralised here so any
    // module can record against the canonical NickERP Meter (which the
    // OTel pipeline already subscribes to) and the in-app
    // MeterSnapshotService picks them up by instrument name.
    //
    // Names follow the `nickerp.<bounded-context>.<surface>.<unit>`
    // convention so a Seq / Prometheus query can filter by prefix.
    // -------------------------------------------------------------------

    /// <summary>
    /// Image endpoint response time in milliseconds. Recorded by the
    /// /api/images/{id}/{kind} lambda on every return path.
    /// Tags: <c>kind</c> (thumbnail|preview), <c>status</c> (200|304|404|400).
    /// Acceptance bars per ARCHITECTURE §7.7 — thumbs ≤ 50ms p95, previews ≤ 80ms p95.
    /// </summary>
    public static readonly Histogram<double> ImageServeMs = Meter.CreateHistogram<double>(
        name: "nickerp.inspection.image.serve_ms",
        unit: "ms",
        description: "Image endpoint response time, tagged by kind + status.");

    /// <summary>
    /// Pre-render worker per-derivative duration in milliseconds.
    /// Recorded only on success (failures are tracked separately via
    /// scan_render_attempts). Tags: <c>kind</c>, <c>mime</c>.
    /// </summary>
    public static readonly Histogram<double> PreRenderRenderMs = Meter.CreateHistogram<double>(
        name: "nickerp.inspection.prerender.render_ms",
        unit: "ms",
        description: "PreRenderWorker render+persist duration per derivative, success only.");

    /// <summary>
    /// Scan ingestion duration in milliseconds — covers parse + hash +
    /// SaveSourceAsync + DB row inserts. Tag: <c>scanner_type_code</c>
    /// (e.g. fs6000, mock-scanner).
    /// </summary>
    public static readonly Histogram<double> ScanIngestMs = Meter.CreateHistogram<double>(
        name: "nickerp.inspection.scan.ingest_ms",
        unit: "ms",
        description: "CaseWorkflowService.IngestArtifactAsync duration, tagged by scanner type.");

    /// <summary>
    /// Counter of case-state transitions. Tags: <c>from</c>, <c>to</c>
    /// (string state names). Incremented next to each EmitAsync call in
    /// CaseWorkflowService that announces a workflow state change.
    /// </summary>
    public static readonly Counter<long> CaseStateTransitions = Meter.CreateCounter<long>(
        name: "nickerp.inspection.case.state_transitions_total",
        unit: "transitions",
        description: "Case workflow state transitions, tagged by from→to.");
}
