using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Per-tenant per-HS-6 commodity reference table (§6.10.2). Backs the
/// <c>inspection.hs_commodity_reference</c> table; consumed by §6.3
/// stage-3 <c>density_vs_declared</c> scorer to flag through-container
/// density / Z_eff distributions that fall outside the expected window
/// for the declared HS-6 code.
///
/// <para>
/// Composite primary key: <c>(TenantId, Hs6)</c>. Per-tenant by design
/// (§6.10.10 cross-tenant guard) — different tenants will see different
/// commodity mixes through different scanners and the calibration windows
/// must not bleed across them.
/// </para>
///
/// <para>
/// Confidence tiers (Authoritative / Curated / Inferred — see §6.10.6)
/// gate scoring weight and finding severity caps. Tier transitions are
/// one-way upgrades only; analyst explicitly downgrades on review.
/// </para>
///
/// <para>
/// <see cref="ScannerCalibrationVersionAtFitJson"/> ties this row's
/// numerical window to the specific <see cref="ScannerThresholdProfile"/>
/// versions in effect when the in-house seeding ran — when a new
/// threshold profile activates that affects normalization or Z
/// calibration, this row is enqueued for re-fit (§6.10.9).
/// </para>
/// </summary>
public sealed class HsCommodityReference : ITenantOwned
{
    /// <summary>Owning tenant. Part of the composite PK.</summary>
    public long TenantId { get; set; }

    /// <summary>HS-6 code, fixed-length 6 chars. Part of the composite PK.</summary>
    public string Hs6 { get; set; } = string.Empty;

    /// <summary>Robust lower bound on per-pixel Z_eff for this commodity.</summary>
    public decimal ZEffMin { get; set; }

    /// <summary>Robust central tendency on per-pixel Z_eff.</summary>
    public decimal ZEffMedian { get; set; }

    /// <summary>Robust upper bound on per-pixel Z_eff.</summary>
    public decimal ZEffMax { get; set; }

    /// <summary>
    /// Method used to fit the Z_eff window — one of
    /// <c>iqr_1.5x</c>, <c>p5_p95</c>, <c>public_source</c>,
    /// <c>inferred_sibling</c>.
    /// </summary>
    public string ZEffWindowMethod { get; set; } = string.Empty;

    /// <summary>
    /// Through-container apparent density (NOT lab density). Null
    /// permitted for items where density adds no signal — the scorer
    /// drops <c>density_vs_declared</c> for null rows and re-normalises.
    /// </summary>
    public decimal? ExpectedDensityKgPerM3 { get; set; }

    /// <summary>
    /// Half-open density-window range as a Postgres <c>numrange</c>.
    /// Stored as a string-formatted range literal (e.g. <c>"[300,500)"</c>);
    /// consumers parse on read. Null when density is not modelled for this
    /// row.
    /// </summary>
    public string? DensityWindowKgPerM3 { get; set; }

    /// <summary>
    /// Free-text packaging tags from the <c>Authorities.CustomsGh</c>
    /// packaging vocabulary. Stored as a Postgres <c>text[]</c>.
    /// </summary>
    public string[] TypicalPackaging { get; set; } = Array.Empty<string>();

    /// <summary>Confidence tier — see §6.10.6.</summary>
    public HsCommodityConfidence Confidence { get; set; } = HsCommodityConfidence.Inferred;

    /// <summary>
    /// Per-source provenance entries. Each source contributes one entry:
    /// <c>{ source_id, fetched_at, row_hash, fields_contributed, raw_url,
    /// licence, agreement_with_in_house }</c>.
    /// </summary>
    public string SourcesJson { get; set; } = "[]";

    /// <summary>In-house sample count contributing to this fit. Drives tier rules in §6.10.6.</summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Map of <c>scanner_device_instance_id → ScannerThresholdProfile.version</c>
    /// at fit time; ties this row's window to the §6.5 calibration that
    /// produced it. Null when no in-house samples backed the fit.
    /// </summary>
    public string? ScannerCalibrationVersionAtFitJson { get; set; }

    /// <summary>Last analyst review or automated re-fit timestamp.</summary>
    public DateTimeOffset LastValidatedAt { get; set; }

    /// <summary>The analyst who last validated. Null only for fully-automated tier-3 long-tail rows.</summary>
    public Guid? ValidatedByUserId { get; set; }

    /// <summary>Drives §6.10.8 cadence — daily sweeper enqueues rows past due date.</summary>
    public DateTimeOffset NextReviewDueAt { get; set; }

    /// <summary>Free-form analyst rationale.</summary>
    public string? Notes { get; set; }
}

/// <summary>Confidence tier — see §6.10.6 rules.</summary>
public enum HsCommodityConfidence
{
    /// <summary>≥ 100 in-house samples + analyst-validated + at least one public source agrees within 15 % on density. Default scorer weight.</summary>
    Authoritative = 0,

    /// <summary>≥ 30 in-house samples + analyst-validated, OR single high-quality public source covering all numeric fields. Default weight; severity capped at Medium.</summary>
    Curated = 10,

    /// <summary>&lt; 30 samples + weak/no public sources, OR HS-4 sibling inference. Severity capped at Low; Z_eff window widened ×1.5.</summary>
    Inferred = 20
}
