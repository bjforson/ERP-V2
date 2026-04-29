namespace NickERP.Inspection.ExternalSystems.Abstractions;

/// <summary>
/// Derived contract for external-system adapters that ingest <b>post-hoc
/// outcomes</b> — authority decisions delivered after a case is closed
/// (supersession chains, late verdicts, retroactive holds). Adapters
/// declare support by implementing this interface AND setting
/// <see cref="ExternalSystemCapabilities.SupportsOutcomePull"/> and/or
/// <see cref="ExternalSystemCapabilities.SupportsOutcomePush"/>.
/// <para>
/// Adapters that only outbound-submit verdicts (existing
/// <see cref="IExternalSystemAdapter"/>) compile unchanged — this surface
/// is purely additive.
/// </para>
/// <para>
/// Driven by <c>NickERP.Inspection.Application.PostHocOutcomes
/// .OutcomeIngestionOrchestrator</c> — a hosted background service, not an
/// adapter. See IMAGE-ANALYSIS-MODERNIZATION.md §6.11 (closes Q-G1).
/// </para>
/// </summary>
public interface IInboundOutcomeAdapter : IExternalSystemAdapter
{
    /// <summary>
    /// Bulk-fetch post-hoc outcomes within a time window. Invoked by the
    /// orchestrator on a schedule (default 4× daily for pure-pull mode,
    /// 1× daily for hybrid-mode reconciliation). MUST be idempotent — the
    /// orchestrator may replay the same window after a transient failure.
    /// Implementations honour <paramref name="window"/>.<see cref="OutcomeWindow.Kind"/>
    /// when interpreting <c>Since</c> / <c>Until</c>.
    /// </summary>
    /// <param name="cfg">Per-instance config; tenant-partitioned.</param>
    /// <param name="window">Time window + window-kind interpretation.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<AuthorityDocumentDto>> FetchOutcomesAsync(
        ExternalSystemConfig cfg,
        OutcomeWindow window,
        CancellationToken ct);

    /// <summary>
    /// Accept a webhook delivery from the authority — the orchestrator's
    /// HTTP receiver dispatches the verified envelope here. Adapter is
    /// responsible for parsing, signature verification (using credentials
    /// in <paramref name="cfg"/>) and translating the payload into
    /// <see cref="AuthorityDocument"/> rows.
    /// <para>
    /// MUST be idempotent — webhooks may be replayed by the authority on
    /// network errors. The orchestrator's caller-side store dedupes on
    /// supersession-chain key, but the adapter should also tolerate
    /// repeat invocations cleanly.
    /// </para>
    /// </summary>
    /// <param name="cfg">Per-instance config; tenant-partitioned.</param>
    /// <param name="envelope">
    /// Raw webhook envelope: bytes, headers, optional signature, source IP.
    /// Receiver pre-validates transport-level concerns; the adapter validates
    /// payload-level signatures.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<AuthorityDocumentDto>> ReceiveOutcomeWebhookAsync(
        ExternalSystemConfig cfg,
        InboundWebhookEnvelope envelope,
        CancellationToken ct);
}

/// <summary>
/// Time window for <see cref="IInboundOutcomeAdapter.FetchOutcomesAsync"/>.
/// Authorities differ on which timestamp they expose for windowing
/// (decided-at vs received-at vs last-modified-at); the orchestrator picks
/// the kind matching the authority's API and the adapter routes
/// accordingly. See IMAGE-ANALYSIS-MODERNIZATION.md §6.11.2 / Q-N3.
/// </summary>
/// <param name="Since">Inclusive lower bound.</param>
/// <param name="Until">Exclusive upper bound.</param>
/// <param name="Kind">Which authority-side timestamp the bounds refer to.</param>
public sealed record OutcomeWindow(
    DateTimeOffset Since,
    DateTimeOffset Until,
    OutcomeWindowKind Kind);

/// <summary>
/// Which authority-side timestamp <see cref="OutcomeWindow.Since"/> /
/// <see cref="OutcomeWindow.Until"/> refer to. Q-N3.
/// </summary>
public enum OutcomeWindowKind
{
    /// <summary>Authority's recorded decision time (when the verdict was rendered).</summary>
    DecidedAt,

    /// <summary>Authority's received time (when the case landed on their desk).</summary>
    ReceivedAt,

    /// <summary>Authority's last-modified time (latest mutation, including supersessions).</summary>
    LastModifiedAt,
}

/// <summary>
/// Raw webhook envelope handed to
/// <see cref="IInboundOutcomeAdapter.ReceiveOutcomeWebhookAsync"/>.
/// Transport details are preserved so the adapter can verify
/// payload-level signatures (HMAC over <see cref="RawPayload"/>) and
/// honour authority-specific replay/IP-allowlist policies.
/// </summary>
/// <param name="RawPayload">Verbatim request body — do not pre-parse.</param>
/// <param name="Headers">Case-insensitive header map.</param>
/// <param name="Signature">
/// Pre-extracted signature header value (e.g. <c>X-Signature</c>) when the
/// receiver knows which header carries it; null when the adapter must dig
/// it out of <see cref="Headers"/> itself.
/// </param>
/// <param name="SourceIp">
/// Remote IP after trusted-proxy unwrap, for authority-side IP allowlists.
/// Null when the receiver was not configured with proxy-trust rules.
/// </param>
public sealed record InboundWebhookEnvelope(
    ReadOnlyMemory<byte> RawPayload,
    IReadOnlyDictionary<string, string> Headers,
    string? Signature,
    string? SourceIp);
