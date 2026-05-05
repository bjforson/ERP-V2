using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Application.Detection;

/// <summary>
/// Sprint 31 / B5.2 — built-in cross-record-scan detector.
///
/// <para>
/// Vendor-neutral. Two detection signals fire today:
/// <list type="number">
///   <item><b>Multi-document fan-out</b>. When a case has N&gt;1
///   <see cref="AuthorityDocument"/>s whose
///   payload <c>containerNumber</c> values disagree, this is a
///   strong signal the scan event covers multiple subjects. We pull
///   each unique container number into a
///   <see cref="CrossRecordSubject"/>. Mirrors the v1
///   <c>MultiContainerValidationService.DetectAcrossDocumentsAsync</c>
///   path, vendor-neutralised.</item>
///   <item><b>Scan-metadata multi-token</b>. When a single
///   <see cref="ScanArtifact"/>'s
///   <c>MetadataJson</c> carries a <c>"containerNumbers"</c> array
///   with N&gt;1 distinct entries (FS6000 + ASE both expose this
///   when a multi-container truck rolls through), the detector flags
///   each token as a candidate subject.</item>
/// </list>
/// Either signal alone fires the row; both signals merge into one
/// descriptor. The detector is read-only — the caller (typically
/// <c>CrossRecordScanService.ScanAndPersistAsync</c>) decides whether
/// to upsert the <c>cross_record_detection</c> row.
/// </para>
///
/// <para>
/// <b>No coupling to the AuthorityDocumentMatcherWorker.</b> Sprint
/// 24's worker is finalised; this detector reads the
/// <c>authority_documents</c> table directly so no edit to the
/// matcher's worker file is required.
/// </para>
/// </summary>
public sealed class CrossRecordScanDetector : ICrossRecordScanDetector
{
    public string DetectorVersion => "v1";

    private readonly InspectionDbContext _db;
    private readonly ILogger<CrossRecordScanDetector> _logger;

    public CrossRecordScanDetector(
        InspectionDbContext db,
        ILogger<CrossRecordScanDetector> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CrossRecordDetectionDescriptor?> DetectAsync(Guid caseId, CancellationToken ct = default)
    {
        var @case = await _db.Cases.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == caseId, ct);
        if (@case is null)
        {
            _logger.LogDebug("CrossRecordScanDetector: case {CaseId} not found.", caseId);
            return null;
        }

        var subjects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The owning case's identifier — not a "second subject" if it's
        // the only one present. Used to filter out self-matches.
        var primarySubject = @case.SubjectIdentifier?.Trim() ?? string.Empty;

        // Signal 1: multi-document fan-out. Pull every authority
        // document for the case + extract a container number.
        var docs = await _db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.CaseId == caseId)
            .Select(d => new { d.PayloadJson })
            .ToListAsync(ct);
        foreach (var d in docs)
        {
            var containerNumber = ExtractContainerNumber(d.PayloadJson);
            if (string.IsNullOrEmpty(containerNumber)) continue;
            if (string.Equals(containerNumber, primarySubject, StringComparison.OrdinalIgnoreCase)) continue;
            if (!subjects.ContainsKey(containerNumber))
                subjects[containerNumber] = "Authority document references a different subject identifier.";
        }

        // Signal 2: scan-metadata multi-token. Walk every ScanArtifact
        // for the case + look for a "containerNumbers" array.
        var scanIds = await _db.Scans.AsNoTracking()
            .Where(s => s.CaseId == caseId)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (scanIds.Count > 0)
        {
            var artifacts = await _db.ScanArtifacts.AsNoTracking()
                .Where(a => scanIds.Contains(a.ScanId))
                .Select(a => new { a.MetadataJson })
                .ToListAsync(ct);
            foreach (var a in artifacts)
            {
                foreach (var token in ExtractContainerNumbers(a.MetadataJson))
                {
                    if (string.Equals(token, primarySubject, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!subjects.ContainsKey(token))
                        subjects[token] = "Scan metadata enumerates multiple subject tokens.";
                }
            }
        }

        if (subjects.Count == 0) return null;

        // Build the descriptor. The primary subject ALWAYS leads the
        // list so the analyst sees the original case as candidate-1
        // when reviewing.
        var ordered = new List<CrossRecordSubject>(capacity: subjects.Count + 1);
        if (!string.IsNullOrEmpty(primarySubject))
            ordered.Add(new CrossRecordSubject(
                primarySubject,
                "Original case subject identifier."));
        foreach (var (subj, ev) in subjects)
            ordered.Add(new CrossRecordSubject(subj, ev));

        var rationale = subjects.Count switch
        {
            1 => "Detector found 1 additional subject identifier on the case.",
            _ => $"Detector found {subjects.Count} additional subject identifiers on the case."
        };
        return new CrossRecordDetectionDescriptor(caseId, ordered, rationale);
    }

    /// <summary>
    /// Extract a container number from an authority document's
    /// payload. Mirrors the
    /// <c>AuthorityDocumentMatcherWorker.ExtractContainerNumber</c>
    /// shape so detector + matcher share the same hint key. The
    /// matcher itself is not edited (Sprint 24 territory).
    /// </summary>
    internal static string? ExtractContainerNumber(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            // Common keys (per v1 + Sprint 24 matcher convention).
            foreach (var key in new[] { "containerNumber", "container_number", "ContainerNumber" })
            {
                if (doc.RootElement.TryGetProperty(key, out var p)
                    && p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                }
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// <summary>
    /// Extract any "containerNumbers" array (or sibling keys) from a
    /// scan-artifact metadata payload. Returns an empty enumerable
    /// when the array is absent or malformed.
    /// </summary>
    internal static IEnumerable<string> ExtractContainerNumbers(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) yield break;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(metadataJson);
        }
        catch (JsonException)
        {
            yield break;
        }
        try
        {
            foreach (var key in new[] { "containerNumbers", "container_numbers", "ContainerNumbers" })
            {
                if (doc.RootElement.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var s = item.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) yield return s.Trim();
                    }
                }
            }
        }
        finally
        {
            doc.Dispose();
        }
    }
}
