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
}
