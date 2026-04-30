using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.PostHocOutcomes;

/// <summary>
/// Persists a post-hoc outcome row into <c>authority_documents</c> + (when
/// the rollout phase opts in) updates the originating case's
/// <see cref="AnalystReview.PostHocOutcomeJson"/>.
///
/// <para>
/// Single-method contract intentionally — the worker pulls a batch from
/// the adapter then calls <see cref="WriteAsync"/> once per outcome.
/// Implementations are scoped (depend on
/// <c>InspectionDbContext</c>) and the DbContext save is committed by
/// the worker per call (small transactions; idempotency makes replay
/// safe).
/// </para>
///
/// <para>
/// Idempotency contract (§6.11.7): same idempotency key arriving twice
/// is a silent no-op, returning <see cref="OutcomeWriteOutcome.Deduplicated"/>.
/// The key is derived from the record by
/// <see cref="PostHocOutcomeWriter.ComputeIdempotencyKey"/>.
/// Supersession (a row carrying
/// <see cref="PostHocOutcomeRecord.SupersedesDecisionReference"/>)
/// appends a new <c>AuthorityDocument</c> row and threads the prior
/// document id into the new payload's <c>supersedes_chain</c>; the
/// historical row is preserved unchanged.
/// </para>
/// </summary>
public interface IPostHocOutcomeWriter
{
    /// <summary>
    /// Materialise the outcome into <c>authority_documents</c> and
    /// (conditionally on the phase) update the originating case's
    /// <see cref="AnalystReview.PostHocOutcomeJson"/>.
    /// </summary>
    /// <param name="record">Validated outcome record from the adapter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What happened — Inserted / Deduplicated / Superseded / NoMatchingCase.</returns>
    Task<OutcomeWriteOutcome> WriteAsync(PostHocOutcomeRecord record, CancellationToken ct);
}

/// <summary>
/// Validated post-hoc outcome record handed to <see cref="IPostHocOutcomeWriter"/>.
/// Authority-neutral at the contract level — the typed payload is opaque
/// JSON (the adapter validated it against its plugin-shipped JSON Schema
/// per §6.11.5); this record captures the orchestrator-level fields that
/// drive case lookup, idempotency, and phase observation.
/// </summary>
public sealed record PostHocOutcomeRecord(
    long TenantId,
    Guid ExternalSystemInstanceId,
    string AuthorityCode,
    string DeclarationNumber,
    string? ContainerNumber,
    DateTimeOffset DecidedAt,
    string DecisionReference,
    string? SupersedesDecisionReference,
    string PayloadJson,
    PostHocRolloutPhaseValue Phase,
    string EntryMethod);

/// <summary>What <see cref="IPostHocOutcomeWriter"/> did with one record.</summary>
public enum OutcomeWriteOutcome
{
    /// <summary>New <c>AuthorityDocument</c> row inserted.</summary>
    Inserted,

    /// <summary>Idempotency key already on file — silent no-op.</summary>
    Deduplicated,

    /// <summary>Inserted as a supersession — new row appended, prior row preserved.</summary>
    Superseded,

    /// <summary>No <c>InspectionCase</c> matched the outcome's declaration / container; row not inserted.</summary>
    NoMatchingCase
}
