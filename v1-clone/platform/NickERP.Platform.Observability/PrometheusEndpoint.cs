using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

namespace NickERP.Platform.Observability;

/// <summary>
/// Maps the Prometheus scrape endpoint at <c>/metrics</c>, gated to loopback
/// by default. Other ranges can be opened via configuration without code change.
/// </summary>
public static class PrometheusEndpoint
{
    /// <summary>
    /// Maps the Prometheus scrape endpoint at <c>/metrics</c>. Gated to
    /// loopback by default — set <c>NickERP:Observability:Prometheus:AllowedNetworks</c>
    /// (CIDR list, comma-separated, e.g. <c>"10.0.0.0/8,192.168.1.0/24"</c>) to widen.
    /// </summary>
    public static IEndpointConventionBuilder MapNickErpMetrics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var rawCidrs = app.Configuration["NickERP:Observability:Prometheus:AllowedNetworks"] ?? string.Empty;
        var allowedNetworks = ParseNetworks(rawCidrs);

        // Wrap the Prometheus scraping middleware in an IP gate. The OTel
        // middleware itself responds at /metrics; we intercept the path
        // first, return 404 for disallowed callers (don't reveal the route),
        // and let the middleware handle the rest.
        app.UseWhen(
            ctx => ctx.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase),
            gated =>
            {
                gated.Use(async (ctx, next) =>
                {
                    var remote = ctx.Connection.RemoteIpAddress;
                    if (remote is null || !IsAllowed(remote, allowedNetworks))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }
                    await next();
                });
                gated.UseOpenTelemetryPrometheusScrapingEndpoint();
            });

        // The OTel exporter middleware responds inside the UseWhen branch
        // above; the conventional builder we hand back is a thin marker
        // so callers can chain conventions if they wish.
        return new NoopEndpointConventionBuilder();
    }

    private static IReadOnlyList<IPNetwork> ParseNetworks(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<IPNetwork>();
        }

        var list = new List<IPNetwork>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(token, out var net))
            {
                list.Add(net);
            }
        }
        return list;
    }

    private static bool IsAllowed(IPAddress remote, IReadOnlyList<IPNetwork> allowed)
    {
        // IPv4-mapped IPv6 (::ffff:127.0.0.1) needs unwrapping for IsLoopback
        // to behave correctly across Kestrel transports.
        var addr = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;

        if (IPAddress.IsLoopback(addr))
        {
            return true;
        }

        foreach (var net in allowed)
        {
            if (net.Contains(addr))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class NoopEndpointConventionBuilder : IEndpointConventionBuilder
    {
        public void Add(Action<EndpointBuilder> convention)
        {
            // No-op. The route is owned by the OpenTelemetry middleware.
        }
    }
}
