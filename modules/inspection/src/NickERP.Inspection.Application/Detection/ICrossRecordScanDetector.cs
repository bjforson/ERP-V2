using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.Detection;

/// <summary>
/// Sprint 31 / B5.2 — vendor-neutral cross-record-scan detector.
///
/// <para>
/// v1 NSCIM 2.15.4 introduced multi-container scan splitting. When a
/// single scan event maps to N cargo containers (e.g. multi-container
/// truck), the FS6000 file metadata or matched ICUMS BOE rows let the
/// system flag the case for split. v1's
/// <c>MultiContainerValidationService</c> +
/// <c>CrossRecordScansController</c> made this work.
/// </para>
///
/// <para>
/// v2 keeps the same shape but vendor-neutral: a detector examines a
/// case and emits a <see cref="CrossRecordDetection"/> row when it
/// suspects multi-subject content. The detector is purely
/// observational — splits are analyst-confirmed via
/// <c>CrossRecordScanService</c>; this contract just decides "should
/// we ask?". Multiple detectors can coexist (one for FS6000 file-name
/// patterns, one for matched-document N-way pairings, etc.) and each
/// owns a stable <see cref="DetectorVersion"/> string used to
/// idempotency-guard the detection table.
/// </para>
/// </summary>
public interface ICrossRecordScanDetector
{
    /// <summary>
    /// Stable version code — bumped whenever the detection algorithm
    /// changes. Persisted on every <see cref="CrossRecordDetection"/>
    /// row so dashboards can attribute findings to a specific rule
    /// revision.
    /// </summary>
    string DetectorVersion { get; }

    /// <summary>
    /// Examine the supplied case and decide whether it is a
    /// cross-record-scan candidate. Returns null when the case is
    /// unambiguously single-subject; returns a populated descriptor
    /// when the detector wants the analyst to review.
    /// </summary>
    Task<CrossRecordDetectionDescriptor?> DetectAsync(Guid caseId, CancellationToken ct = default);
}

/// <summary>
/// Sprint 31 / B5.2 — descriptor for a detected multi-subject case.
///
/// <para>
/// Returned by <see cref="ICrossRecordScanDetector.DetectAsync"/> when
/// the detector wants to flag a case. Carries the per-subject
/// breakdown that <c>CrossRecordScanService</c> persists into the
/// <c>cross_record_detection</c> table's
/// <c>DetectedSubjectsJson</c> column.
/// </para>
/// </summary>
public sealed record CrossRecordDetectionDescriptor(
    Guid CaseId,
    IReadOnlyList<CrossRecordSubject> Subjects,
    string Rationale);

/// <summary>
/// Sprint 31 / B5.2 — one detected child-subject candidate.
/// Vendor-neutral. The
/// <see cref="SubjectIdentifier"/> matches what
/// <c>InspectionCase.SubjectIdentifier</c> would carry on the split
/// child case (e.g. a container number); <see cref="Evidence"/> is a
/// short human description (e.g. "second container number found in
/// scan metadata").
/// </summary>
public sealed record CrossRecordSubject(string SubjectIdentifier, string Evidence);
