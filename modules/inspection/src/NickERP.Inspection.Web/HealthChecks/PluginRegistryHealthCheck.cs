using Microsoft.Extensions.Diagnostics.HealthChecks;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Web.HealthChecks;

/// <summary>
/// Phase F5 — readiness probe asserting that at least one plugin loaded.
/// An empty <see cref="IPluginRegistry"/> usually means the
/// <c>plugins/</c> staging step was skipped or the plugin DLLs failed
/// to load, and the inspection admin is effectively non-functional.
/// </summary>
public sealed class PluginRegistryHealthCheck : IHealthCheck
{
    private readonly IPluginRegistry _registry;

    public PluginRegistryHealthCheck(IPluginRegistry registry) => _registry = registry;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var count = _registry.All.Count;
        if (count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No plugins loaded — check the host's plugins/ folder."));
        }
        return Task.FromResult(HealthCheckResult.Healthy(
            $"{count} plugin(s) loaded",
            data: new Dictionary<string, object> { ["pluginCount"] = count }));
    }
}
