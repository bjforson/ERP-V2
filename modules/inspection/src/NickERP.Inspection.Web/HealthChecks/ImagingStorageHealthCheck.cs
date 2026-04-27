using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Imaging;

namespace NickERP.Inspection.Web.HealthChecks;

/// <summary>
/// Phase F5 — readiness probe verifying the image pipeline's
/// <see cref="ImagingOptions.StorageRoot"/> is configured + writable.
///
/// Writes a 0-byte sentinel under <c>{StorageRoot}/.healthcheck</c> and
/// deletes it; failure means the host can't persist source bytes or
/// rendered derivatives, which would silently break the analyst flow.
/// </summary>
public sealed class ImagingStorageHealthCheck : IHealthCheck
{
    private readonly IOptions<ImagingOptions> _opts;

    public ImagingStorageHealthCheck(IOptions<ImagingOptions> opts) => _opts = opts;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var root = _opts.Value.StorageRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "ImagingOptions.StorageRoot is not configured."));
        }

        try
        {
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".healthcheck");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy(
                $"StorageRoot '{root}' writable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                description: $"StorageRoot '{root}' not writable: {ex.GetType().Name}",
                exception: ex));
        }
    }
}
