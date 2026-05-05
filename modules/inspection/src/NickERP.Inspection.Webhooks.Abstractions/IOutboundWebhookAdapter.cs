namespace NickERP.Inspection.Webhooks.Abstractions;

/// <summary>
/// Sprint 47 / Phase A — plugin contract for outbound webhook
/// adapters. Each implementation forwards <see cref="WebhookEvent"/>s
/// to one downstream subscriber (an internal SIEM, an external risk-
/// scoring partner, etc.).
///
/// <para>
/// <b>Discovery.</b> Adapters ship as separate plugin assemblies
/// loaded by <see cref="NickERP.Platform.Plugins.IPluginRegistry"/>.
/// The host's <c>WebhookDispatchWorker</c> enumerates them via
/// <see cref="NickERP.Platform.Plugins.IPluginRegistry.GetContributedTypes(System.Type)"/>
/// at startup and resolves one instance per cycle.
/// </para>
///
/// <para>
/// <b>No public webhook endpoint for the pilot.</b> The contract +
/// dispatcher + cursor mechanism are ready, but no adapter projects
/// ship with v2 — the inspection module's deploy unit is internal-
/// only until a post-pilot follow-up.
/// </para>
///
/// <para>
/// <b>Idempotency.</b> Two webhook adapters wired for the same tenant
/// advance independently — the dispatcher tracks per-(TenantId,
/// AdapterName) cursors so one adapter's outage doesn't replay an
/// hour's worth of events to its peer when it recovers. Adapters
/// themselves should also dedupe via
/// <see cref="WebhookEvent.IdempotencyKey"/> — downstream subscribers
/// can replay-safely.
/// </para>
///
/// <para>
/// <b>Per-adapter exception isolation.</b> The dispatcher catches every
/// exception thrown from <see cref="DispatchAsync"/> and continues
/// with the next registered adapter on the same tick. One bad
/// adapter must not poison dispatch for the others.
/// </para>
///
/// <para>
/// <b>Vendor-neutral.</b> No country, customs-authority, or specific-
/// vendor concepts surface in the adapter contract. Vendor-shaped
/// webhook adapters (e.g. an authority-specific hook for a Ghana-only
/// regulator) live in country/authority module plugins, not here.
/// </para>
/// </summary>
public interface IOutboundWebhookAdapter
{
    /// <summary>
    /// Stable, unique identifier for this adapter. Used as the
    /// <c>AdapterName</c> on <c>WebhookCursor</c> rows so the
    /// dispatcher can advance per-(TenantId, AdapterName) cursors
    /// independently. Convention: kebab-case (e.g. <c>siem-internal</c>,
    /// <c>partner-risk-scoring</c>).
    /// </summary>
    string AdapterName { get; }

    /// <summary>
    /// Dispatch a single <paramref name="evt"/> to the downstream
    /// subscriber. The dispatcher invokes this method per event per
    /// adapter; adapters that batch internally do so behind their own
    /// queue.
    ///
    /// <para>
    /// <b>Throws on transient failure.</b> The dispatcher treats any
    /// exception as a per-event failure: the cursor stays at the prior
    /// successful event, the failure is audited via
    /// <c>nickerp.webhooks.dispatch_failed</c>, and the next tick
    /// retries from the cursor. Adapters that exhaust their own retry
    /// budget should still throw so the dispatcher logs + audits the
    /// outcome consistently.
    /// </para>
    /// </summary>
    /// <param name="evt">Standard-shape webhook event to forward.</param>
    /// <param name="ct">Cooperative cancellation — usually the worker's stopping token.</param>
    Task DispatchAsync(WebhookEvent evt, CancellationToken ct);
}
