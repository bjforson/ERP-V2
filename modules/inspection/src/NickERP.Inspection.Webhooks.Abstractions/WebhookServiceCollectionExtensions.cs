using Microsoft.Extensions.DependencyInjection;

namespace NickERP.Inspection.Webhooks.Abstractions;

/// <summary>
/// Sprint 47 / Phase A — DI extension for the webhook adapter
/// abstractions. Today this is a no-op: registers no
/// <see cref="IOutboundWebhookAdapter"/> implementations because the
/// pilot ships zero adapter projects. The host's
/// <c>WebhookDispatchWorker</c> discovers any adapter contributed by
/// a plugin assembly at runtime via
/// <see cref="NickERP.Platform.Plugins.IPluginRegistry.GetContributedTypes"/>.
///
/// <para>
/// <b>Why have the extension at all?</b> The host's
/// <c>Program.cs</c> calls <see cref="AddNickErpInspectionWebhooks"/>
/// to declare intent — "this host participates in webhook dispatch"
/// — even though no adapters are wired today. Post-pilot, when adapter
/// projects ship, they get a parallel
/// <c>AddNickErpInspectionWebhooksXyzAdapter</c> extension that
/// registers their concrete <see cref="IOutboundWebhookAdapter"/>
/// alongside this one.
/// </para>
/// </summary>
public static class WebhookServiceCollectionExtensions
{
    /// <summary>
    /// Register the webhook abstractions. No-op today (no adapters
    /// ship with v2). Returns the <paramref name="services"/>
    /// unchanged so it composes with the rest of <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddNickErpInspectionWebhooks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Intentionally empty — adapter discovery is deferred to the
        // dispatch worker via IPluginRegistry.GetContributedTypes.
        // Adapter projects shipping post-pilot register their concrete
        // IOutboundWebhookAdapter implementation through their own
        // plugin DI extension; the dispatcher resolves it through DI
        // after the registry exposes the contributed type.
        return services;
    }
}
