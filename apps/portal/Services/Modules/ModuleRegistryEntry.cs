namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — one record in the portal launcher's module catalogue. The
/// registry returns these per request (per tenant); the launcher renders
/// one tile per entry where <see cref="Enabled"/> is true.
/// </summary>
/// <param name="Id">Stable, lowercase id matching the module-side
/// <c>NickErpSharedChromeOptions.ModuleId</c> (e.g. <c>"inspection"</c>,
/// <c>"nickfinance"</c>, <c>"nickhr"</c>).</param>
/// <param name="DisplayName">Human-readable name shown on the tile and
/// in the shared header.</param>
/// <param name="BaseUrl">Absolute URL where the module is hosted (read
/// from <c>Portal:Modules:{Id}:BaseUrl</c>). The launcher tile links to
/// this URL.</param>
/// <param name="IconHint">Single-char hint for the launcher tile mark
/// (mirrors the <c>AppCard.Initial</c> shape).</param>
/// <param name="Description">One-line tile subtitle.</param>
/// <param name="Enabled">True ⇒ render the tile for this tenant. False
/// ⇒ tile suppressed (registry filters out before returning, but the
/// flag is exposed so admin tooling can show disabled modules too).</param>
/// <param name="Color">Tile accent color (CSS color string). Mirrors the
/// existing <c>AppCard</c> palette so the launcher visual is consistent
/// with the legacy apps grid.</param>
public sealed record ModuleRegistryEntry(
    string Id,
    string DisplayName,
    string BaseUrl,
    string IconHint,
    string Description,
    bool Enabled,
    string Color);
