namespace NickERP.Inspection.Web.Components.Shared;

/// <summary>
/// Sprint 33 / B7.1 — a single label/value row inside a
/// <c>ReportSummaryCard</c>. Used to render compact stat lines (e.g.
/// "Last 24h: 47", "Last 7d: 312") under a card's headline.
/// </summary>
/// <param name="Label">Left-hand label (e.g. "Last 24h").</param>
/// <param name="Value">Right-hand display value (e.g. "47", "Unhealthy").</param>
public sealed record ReportSummaryRow(string Label, string Value);
