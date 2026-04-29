using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One row in the in-house threat library provenance table (§6.9.4).
/// Backs the <c>inspection.threat_library_provenance</c> table referenced
/// by §6.6.2's TIP renderer; each row is a single isolated threat capture
/// (firearm, currency bundle, narcotic mass, contraband, or
/// deliberately-captured benign look-alike) with full chain-of-custody
/// back to the seizure that produced it.
///
/// <para>
/// Per-tenant by design — RLS-enforced. Storage path includes
/// <c>tenant_id</c> (§6.9.7); cross-tenant share requires an explicit
/// grant entity (Q-L7, deferred).
/// </para>
///
/// <para>
/// Image artifacts (HE, LE, material Z_eff, alpha mask) are
/// content-addressed by sha256 in object storage at
/// <c>&lt;storage_root&gt;/threat-library/&lt;tenant_id&gt;/&lt;sha[:2]&gt;/&lt;sha&gt;/</c>.
/// This entity stores the <em>paths</em>, not the bytes — the database
/// row is the auditable record, the blobs live on disk / S3.
/// </para>
/// </summary>
public sealed class ThreatLibraryEntry : ITenantOwned
{
    /// <summary>The threat-library row id; cited from §6.6.2 as <c>threat_id</c>.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public long TenantId { get; set; }

    /// <summary>Capturing port.</summary>
    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>Top-level taxonomy class — see §6.9.4 table.</summary>
    public ThreatClass ThreatClass { get; set; } = ThreatClass.ContrabandOther;

    /// <summary>
    /// Open-vocabulary subclass (e.g. <c>"Pistol"</c>, <c>"Banknote_Bundle"</c>,
    /// <c>"Powder_Compressed"</c>). Free-form per Q-L1.
    /// </summary>
    public string? ThreatSubclass { get; set; }

    /// <summary>The seizure case that produced this capture. FK → <see cref="InspectionCase"/>.</summary>
    public Guid SourceSeizureCaseId { get; set; }

    /// <summary>The <c>decision = Seize</c> verdict that triggered the capture. FK → <see cref="Verdict"/>.</summary>
    public Guid SourceVerdictId { get; set; }

    /// <summary>
    /// Internal capture event modelled as a degenerate <see cref="InspectionCase"/>
    /// of subject-type <c>ThreatCapture</c> (taxonomy extension pending —
    /// §6.9 calls for a new <see cref="CaseSubjectType"/> value, not yet
    /// added). Optional — not all captures use the case-machine framing
    /// yet.
    /// </summary>
    public Guid? CaptureCaseId { get; set; }

    /// <summary>When the calibration-belt scan ran.</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Certified operator who performed the capture.</summary>
    public Guid CapturedByUserId { get; set; }

    /// <summary>The scanner that produced the capture imagery. FK → <see cref="ScannerDeviceInstance"/>.</summary>
    public Guid SourceScannerInstanceId { get; set; }
    public ScannerDeviceInstance? SourceScannerInstance { get; set; }

    /// <summary>
    /// Denormalised scanner type code for §6.6.4 bias-query speed —
    /// joining through <see cref="SourceScannerInstance"/> on every TIP
    /// sample selection would dominate cost.
    /// </summary>
    public string SourceScannerTypeCode { get; set; } = string.Empty;

    /// <summary>Content-addressed path to the HE channel float32 npy blob.</summary>
    public string HePath { get; set; } = string.Empty;

    /// <summary>Content-addressed path to the LE channel float32 npy blob.</summary>
    public string LePath { get; set; } = string.Empty;

    /// <summary>Content-addressed path to the per-pixel material Z_eff float32 npy blob.</summary>
    public string MaterialZeffPath { get; set; } = string.Empty;

    /// <summary>Content-addressed path to the SAM 2 soft-segmentation alpha mask npy blob.</summary>
    public string AlphaMaskPath { get; set; } = string.Empty;

    /// <summary>Pose canonicalization metadata: <c>{ orientation, scanner_view, anchors[] }</c> per the threat-class taxonomy.</summary>
    public string PoseCanonicalJson { get; set; } = "{}";

    /// <summary>
    /// Tags: <c>zeff_range</c>, <c>mass_estimate_g</c>,
    /// <c>with_packaging</c>, <c>pii_class</c>, <c>subclass_confidence</c>,
    /// <c>operator_notes</c>, <c>linked_threats</c>.
    /// </summary>
    public string TagsJson { get; set; } = "{}";

    /// <summary>SAM 2 model version stamped per ingest.</summary>
    public string Sam2ModelVersion { get; set; } = string.Empty;

    /// <summary>Operator-confirmed segmentation quality, 0.0–1.0.</summary>
    public decimal? SegmentationQualityScore { get; set; }

    /// <summary>Redaction flags: <c>{ stamp, license_plate, pii, manual_review }</c>.</summary>
    public string RedactionFlagsJson { get; set; } = "{}";

    /// <summary>Legal-hold lifecycle.</summary>
    public ThreatLibraryLegalHoldStatus LegalHoldStatus { get; set; } = ThreatLibraryLegalHoldStatus.None;

    /// <summary>Row lifecycle.</summary>
    public ThreatLibraryEntryStatus Status { get; set; } = ThreatLibraryEntryStatus.PendingRedaction;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Top-level threat taxonomy — locked 2026-04-28; see §6.9.4.</summary>
public enum ThreatClass
{
    /// <summary>Pistol, rifle, shotgun, components, ammunition.</summary>
    Firearm = 0,

    /// <summary>Banknote bundles, loose notes, coin bulk.</summary>
    Currency = 10,

    /// <summary>Powder compressed/loose, pill mass, plant material, liquid concealed.</summary>
    Narcotic = 20,

    /// <summary>Counterfeit goods, wildlife product, untaxed tobacco, chemical precursors.</summary>
    ContrabandOther = 30,

    /// <summary>Deliberately-captured benign object resembling a threat — trains §6.2 not to fire on look-alikes.</summary>
    BenignBaseline = 40
}

/// <summary>Legal-hold lifecycle for retention sweeper.</summary>
public enum ThreatLibraryLegalHoldStatus
{
    /// <summary>No hold; subject to normal retention rules.</summary>
    None = 0,

    /// <summary>Held — must not be deleted; retention sweeper skips.</summary>
    Held = 10,

    /// <summary>Hold released; subject to normal retention again.</summary>
    Released = 20
}

/// <summary>Threat-library row lifecycle.</summary>
public enum ThreatLibraryEntryStatus
{
    /// <summary>Captured but redaction review not yet complete; not eligible for §6.6 sampling.</summary>
    PendingRedaction = 0,

    /// <summary>Live — eligible for §6.6 TIP sampling and §6.6.4 bias queries.</summary>
    Active = 10,

    /// <summary>Quarantined — pulled from sampling pending investigation.</summary>
    Quarantined = 20,

    /// <summary>End-of-life — no longer used; preserved for audit.</summary>
    Retired = 30
}
