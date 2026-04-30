using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Application.PostHocOutcomes;

/// <summary>
/// Default <see cref="IPostHocOutcomeWriter"/>. Materialises the record
/// into <c>authority_documents</c> with the §6.11.6 mapping rules and
/// updates the originating case's <see cref="AnalystReview.PostHocOutcomeJson"/>
/// when the phase opts in. Scoped — captures the request-scoped
/// <see cref="InspectionDbContext"/>.
///
/// <para>
/// Storage decisions (locked here to match §6.11):
/// <list type="bullet">
/// <item>The outcome is stored as an <see cref="AuthorityDocument"/> row
/// with <c>DocumentType = "PostHocOutcome"</c>. The
/// <c>AuthorityDocument</c> entity does not yet have an
/// <c>idempotency_key</c> column; v0 packs the key into
/// <c>PayloadJson.idempotency_key</c> and dedups via a LINQ
/// <c>Any()</c> over that JSON path. A column-and-unique-index migration
/// lands in commit 4 to lift this from application-level to DB-level
/// dedup.</item>
/// <item>Case lookup follows §6.11.6 step 1: try
/// <c>(ExternalSystemInstanceId, declaration_number)</c> via prior
/// <c>AuthorityDocument</c>s; fallback to
/// <c>(scanner_serial, captured_at_window ± 4h, container_number)</c>
/// is deferred to a follow-up (no scan-side data model yet here).</item>
/// <item>On phase &gt;= <see cref="PostHocRolloutPhaseValue.PrimaryPlus5PctAudit"/>,
/// the analyst review's <c>PostHocOutcomeJson</c> is updated to the
/// normalised <c>{ outcome, decided_at, decision_reference,
/// document_id, supersedes_chain[] }</c> shape.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PostHocOutcomeWriter : IPostHocOutcomeWriter
{
    private readonly InspectionDbContext _db;
    private readonly ILogger<PostHocOutcomeWriter> _logger;
    private readonly TimeProvider _clock;

    public PostHocOutcomeWriter(
        InspectionDbContext db,
        ILogger<PostHocOutcomeWriter> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<OutcomeWriteOutcome> WriteAsync(
        PostHocOutcomeRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // 1. Idempotency check — does this (instance, key) already exist?
        //    Application-level dedup until commit 4's migration adds the
        //    DB-level unique index.
        var idempotencyKey = ComputeIdempotencyKey(record);
        var existing = await FindByIdempotencyKeyAsync(record.ExternalSystemInstanceId, idempotencyKey, ct);
        if (existing is not null)
        {
            _logger.LogDebug(
                "Post-hoc outcome dedup HIT instance={InstanceId} key={Key} existingDocId={DocId}",
                record.ExternalSystemInstanceId, idempotencyKey, existing.Id);
            return OutcomeWriteOutcome.Deduplicated;
        }

        // 2. Case lookup — §6.11.6 step 1. v0: declaration-number match
        //    against any prior AuthorityDocument under the same external
        //    system instance. Container-number fallback is a follow-up.
        var matchedCase = await TryFindCaseAsync(record, ct);
        if (matchedCase is null)
        {
            _logger.LogWarning(
                "Post-hoc outcome could not match any case (declaration={Declaration} container={Container}); skipping persistence.",
                record.DeclarationNumber, record.ContainerNumber);
            return OutcomeWriteOutcome.NoMatchingCase;
        }

        // 3. Resolve supersession — if the record references a prior
        //    decision, find that AuthorityDocument so we can stitch the
        //    supersedes_chain into the new payload.
        Guid? supersedesDocumentId = null;
        IReadOnlyList<Guid> supersedesChain = Array.Empty<Guid>();
        if (!string.IsNullOrEmpty(record.SupersedesDecisionReference))
        {
            var prior = await FindByReferenceAsync(record.ExternalSystemInstanceId, record.SupersedesDecisionReference!, ct);
            if (prior is not null)
            {
                supersedesDocumentId = prior.Id;
                supersedesChain = ExtractSupersedesChain(prior.PayloadJson)
                    .Concat(new[] { prior.Id })
                    .ToList();
            }
            else
            {
                _logger.LogWarning(
                    "Post-hoc outcome references unknown supersedes_decision_reference={Ref} on instance={InstanceId}; appending without back-reference.",
                    record.SupersedesDecisionReference, record.ExternalSystemInstanceId);
            }
        }

        // 4. Build the persisted PayloadJson — adapter's typed payload
        //    plus orchestrator-stamped metadata (idempotency_key, phase
        //    label, supersedes_chain, entry_method).
        var enrichedPayload = EnrichPayload(record, idempotencyKey, supersedesChain, supersedesDocumentId);

        var docId = Guid.NewGuid();
        var doc = new AuthorityDocument
        {
            Id = docId,
            CaseId = matchedCase.Id,
            ExternalSystemInstanceId = record.ExternalSystemInstanceId,
            DocumentType = AuthorityDocumentTypes.PostHocOutcome,
            ReferenceNumber = record.DecisionReference,
            PayloadJson = enrichedPayload,
            ReceivedAt = _clock.GetUtcNow(),
            TenantId = record.TenantId
        };
        _db.AuthorityDocuments.Add(doc);

        // 5. If phase opts in, update the originating case's analyst-review
        //    PostHocOutcomeJson. Shadow phase persists the row but skips
        //    this step so the §6.4 priority extractor doesn't pick it up
        //    until link is enabled.
        if (RolloutPhasePolicy.ShouldEmitTrainingSignal(record.Phase))
        {
            await UpdateAnalystReviewPostHocAsync(matchedCase.Id, doc, supersedesChain, ct);
        }

        await _db.SaveChangesAsync(ct);

        var outcome = supersedesDocumentId is not null
            ? OutcomeWriteOutcome.Superseded
            : OutcomeWriteOutcome.Inserted;

        _logger.LogInformation(
            "Post-hoc outcome {Outcome} caseId={CaseId} docId={DocId} key={Key} phase={Phase} entryMethod={EntryMethod}.",
            outcome, matchedCase.Id, docId, idempotencyKey, record.Phase, record.EntryMethod);

        return outcome;
    }

    /// <summary>
    /// §6.11.7 idempotency key:
    /// <c>sha256(authority_code || ":" || declaration_number || ":" ||
    /// decided_at_iso8601 || ":" || decision_reference)</c>. Lower-case
    /// hex.
    /// </summary>
    public static string ComputeIdempotencyKey(PostHocOutcomeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var input = string.Concat(
            record.AuthorityCode, ":",
            record.DeclarationNumber, ":",
            record.DecidedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture), ":",
            record.DecisionReference);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private async Task<AuthorityDocument?> FindByIdempotencyKeyAsync(
        Guid instanceId, string idempotencyKey, CancellationToken ct)
    {
        // EF Core can't translate System.Text.Json access on jsonb into
        // a Postgres operator without an Npgsql json-path expression and
        // a configured value converter. Apply the path filter via raw
        // SQL — the index on (TenantId, CaseId) keeps it cheap, and the
        // commit-4 migration upgrades this to a unique-indexed lookup.
        var match = await _db.AuthorityDocuments
            .FromSqlInterpolated($@"
SELECT * FROM inspection.authority_documents
WHERE ""ExternalSystemInstanceId"" = {instanceId}
  AND ""DocumentType"" = {AuthorityDocumentTypes.PostHocOutcome}
  AND ""PayloadJson""->>'idempotency_key' = {idempotencyKey}
LIMIT 1
")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        return match;
    }

    private async Task<AuthorityDocument?> FindByReferenceAsync(
        Guid instanceId, string referenceNumber, CancellationToken ct)
    {
        return await _db.AuthorityDocuments
            .AsNoTracking()
            .Where(d => d.ExternalSystemInstanceId == instanceId
                        && d.DocumentType == AuthorityDocumentTypes.PostHocOutcome
                        && d.ReferenceNumber == referenceNumber)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Look up the originating case for an outcome. v0 — declaration-
    /// number lookup via any prior <c>AuthorityDocument</c> under the
    /// same external-system instance. The §6.11.6 fallback paths
    /// (container number + capture-window match) are a follow-up; the
    /// declaration-number path covers the steady-state ICUMS Ghana
    /// flow described in §6.11.5.
    /// </summary>
    private async Task<InspectionCase?> TryFindCaseAsync(
        PostHocOutcomeRecord record, CancellationToken ct)
    {
        // Path 1 — find a prior Declaration document under this instance
        // whose ReferenceNumber matches the new outcome's declaration_number.
        var priorDoc = await _db.AuthorityDocuments
            .AsNoTracking()
            .Where(d => d.ExternalSystemInstanceId == record.ExternalSystemInstanceId
                        && d.ReferenceNumber == record.DeclarationNumber
                        && d.DocumentType == AuthorityDocumentTypes.Declaration)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefaultAsync(ct);

        if (priorDoc is not null)
        {
            return await _db.Cases
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == priorDoc.CaseId, ct);
        }

        // Path 2 — direct case lookup by SubjectIdentifier =
        // declaration_number. v1 stamped the declaration as the case
        // subject for declaration-driven workflows; in v2 the same path
        // remains until the proper authority-document join lands.
        return await _db.Cases
            .AsNoTracking()
            .Where(c => c.SubjectIdentifier == record.DeclarationNumber)
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Locate the latest <see cref="AnalystReview"/> for the case and
    /// rewrite <see cref="AnalystReview.PostHocOutcomeJson"/> to the
    /// normalised <c>{ outcome, decided_at, decision_reference,
    /// document_id, supersedes_chain[] }</c> shape (§6.11.6 step 4).
    /// No-op (with a debug log) if the case has no analyst review yet —
    /// the §6.11.14 "outcome arrives for a case in Pending review state"
    /// failure mode.
    /// </summary>
    private async Task UpdateAnalystReviewPostHocAsync(
        Guid caseId,
        AuthorityDocument newDoc,
        IReadOnlyList<Guid> supersedesChain,
        CancellationToken ct)
    {
        var review = await (
            from rs in _db.ReviewSessions.AsNoTracking()
            join rev in _db.AnalystReviews on rs.Id equals rev.ReviewSessionId
            where rs.CaseId == caseId
            orderby rs.StartedAt descending
            select rev).FirstOrDefaultAsync(ct);

        if (review is null)
        {
            _logger.LogDebug(
                "Post-hoc outcome arrived but no AnalystReview yet for caseId={CaseId}; PostHocOutcomeJson update deferred.",
                caseId);
            return;
        }

        // Pull the outcome value out of the new document's payload so
        // the analyst-review JSON carries it without re-parsing the full
        // adapter payload downstream.
        var outcomeValue = ExtractOutcomeValue(newDoc.PayloadJson);
        var decidedAt = ExtractDecidedAt(newDoc.PayloadJson);

        var normalised = new JsonObject
        {
            ["outcome"] = outcomeValue,
            ["decided_at"] = decidedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["decision_reference"] = newDoc.ReferenceNumber,
            ["document_id"] = newDoc.Id.ToString(),
            ["supersedes_chain"] = new JsonArray(supersedesChain.Select(id => (JsonNode?)id.ToString()).ToArray())
        };

        // Re-attach a tracked instance and update the column. The lookup
        // above used AsNoTracking so we only update the one field.
        _db.AnalystReviews.Attach(review);
        review.PostHocOutcomeJson = normalised.ToJsonString();
        _db.Entry(review).Property(r => r.PostHocOutcomeJson).IsModified = true;
    }

    private static string EnrichPayload(
        PostHocOutcomeRecord record,
        string idempotencyKey,
        IReadOnlyList<Guid> supersedesChain,
        Guid? supersedesDocumentId)
    {
        JsonNode? root = null;
        if (!string.IsNullOrWhiteSpace(record.PayloadJson))
        {
            try { root = JsonNode.Parse(record.PayloadJson); }
            catch (JsonException) { root = null; }
        }
        var obj = root as JsonObject ?? new JsonObject();

        // Stamp orchestrator-level metadata. We don't blindly overwrite
        // entries that already exist (e.g. an adapter that pre-computes
        // its own idempotency_key gets to keep that one).
        if (!obj.ContainsKey("idempotency_key"))
            obj["idempotency_key"] = idempotencyKey;
        if (!obj.ContainsKey("posthoc_phase"))
            obj["posthoc_phase"] = RolloutPhasePolicy.PhaseLabel(record.Phase);
        if (!obj.ContainsKey("entry_method"))
            obj["entry_method"] = record.EntryMethod;
        if (supersedesDocumentId is not null && !obj.ContainsKey("supersedes_document_id"))
            obj["supersedes_document_id"] = supersedesDocumentId.Value.ToString();
        if (supersedesChain.Count > 0 && !obj.ContainsKey("supersedes_chain"))
            obj["supersedes_chain"] = new JsonArray(supersedesChain.Select(id => (JsonNode?)id.ToString()).ToArray());

        return obj.ToJsonString();
    }

    private static IReadOnlyList<Guid> ExtractSupersedesChain(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return Array.Empty<Guid>();
        try
        {
            var node = JsonNode.Parse(payloadJson) as JsonObject;
            if (node?["supersedes_chain"] is not JsonArray arr) return Array.Empty<Guid>();
            var result = new List<Guid>(arr.Count);
            foreach (var entry in arr)
            {
                if (entry is null) continue;
                if (Guid.TryParse(entry.GetValue<string>(), out var g)) result.Add(g);
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }

    private static string? ExtractOutcomeValue(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var node = JsonNode.Parse(payloadJson) as JsonObject;
            return node?["outcome"]?.GetValue<string>();
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? ExtractDecidedAt(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var node = JsonNode.Parse(payloadJson) as JsonObject;
            var raw = node?["decided_at"]?.GetValue<string>();
            if (string.IsNullOrEmpty(raw)) return null;
            return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Canonical <c>AuthorityDocument.DocumentType</c> values used by the
/// post-hoc outcome adapter and its consumers. Free-form string in the
/// schema (per <c>AuthorityDocument</c>) but kept as compile-time
/// constants here so the worker, writer, manual-entry service, and
/// downstream §6.4 extractor agree on the wire.
/// </summary>
public static class AuthorityDocumentTypes
{
    /// <summary>Inbound post-hoc outcome — §6.11.6 mapping target.</summary>
    public const string PostHocOutcome = "PostHocOutcome";

    /// <summary>Authority-side declaration document — used for case-lookup join in §6.11.6 step 1.</summary>
    public const string Declaration = "Declaration";
}
