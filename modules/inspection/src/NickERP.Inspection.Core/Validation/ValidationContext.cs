using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Core.Validation;

/// <summary>
/// Sprint 28 — read-only snapshot of an <see cref="InspectionCase"/> + the
/// scaffolding rules need to evaluate it.
///
/// <para>
/// The engine builds one <see cref="ValidationContext"/> per case-evaluation
/// pass and hands the same instance to every registered <see cref="IValidationRule"/>.
/// Rules MUST treat the context as immutable — mutations are not propagated
/// to the database, and concurrent rules will see a torn snapshot if any
/// rule writes.
/// </para>
///
/// <para>
/// The context lives in <c>Core/Validation</c> rather than the
/// authority-rules contract (in <c>Authorities.Abstractions</c>) on
/// purpose: the new validation engine is vendor-neutral and runs at
/// case-life-cycle hooks (e.g. <c>MarkValidatedAsync</c>), independently
/// of the per-authority <c>IAuthorityRulesProvider</c>
/// pipeline that runs after document fetch. The two systems coexist;
/// neither replaces the other.
/// </para>
/// </summary>
public sealed record ValidationContext(
    InspectionCase Case,
    IReadOnlyList<Scan> Scans,
    IReadOnlyList<AuthorityDocument> Documents,
    IReadOnlyList<ScannerDeviceInstance> ScannerDevices,
    IReadOnlyList<ScanArtifact> ScanArtifacts,
    string LocationCode,
    long TenantId)
{
    /// <summary>
    /// Convenience accessor — returns the most recently captured scan in
    /// the context (or null when the case has no scans yet). Many rules
    /// only care about the latest scan; centralising the helper avoids
    /// each rule re-implementing the OrderByDescending sort.
    /// </summary>
    public Scan? LatestScan =>
        Scans.Count == 0
            ? null
            : Scans.OrderByDescending(s => s.CapturedAt).First();

    /// <summary>
    /// Find the <see cref="ScannerDeviceInstance"/> that produced a given
    /// scan, or null when the device row is missing from the snapshot
    /// (e.g. soft-deleted device, or the engine builder didn't load it).
    /// Rules that need the device's TypeCode (FS6000, ASE, mock) read it
    /// through this helper.
    /// </summary>
    public ScannerDeviceInstance? DeviceFor(Scan scan)
        => ScannerDevices.FirstOrDefault(d => d.Id == scan.ScannerDeviceInstanceId);

    /// <summary>
    /// Latest scan device convenience — when a rule wants
    /// <c>device.TypeCode</c> for the most recent scan it can call this
    /// rather than chaining <see cref="LatestScan"/> + <see cref="DeviceFor"/>.
    /// </summary>
    public ScannerDeviceInstance? LatestScanDevice
    {
        get
        {
            var s = LatestScan;
            return s is null ? null : DeviceFor(s);
        }
    }

    /// <summary>
    /// Documents-by-type lookup; case-insensitive on the document type
    /// (BOE / CMR / IM are the v1 conventions). Returns an empty list
    /// when the type is absent — rules can iterate without null-checking.
    /// </summary>
    public IReadOnlyList<AuthorityDocument> DocumentsOfType(string documentType)
    {
        if (string.IsNullOrEmpty(documentType)) return Array.Empty<AuthorityDocument>();
        return Documents
            .Where(d => string.Equals(d.DocumentType, documentType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
