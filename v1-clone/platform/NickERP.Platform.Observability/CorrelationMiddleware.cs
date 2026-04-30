using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Serilog.Context;

namespace NickERP.Platform.Observability;

/// <summary>
/// Correlation-id middleware. One id per request — propagated through the
/// active <see cref="Activity"/>, the Serilog <see cref="LogContext"/>, and
/// echoed on the response so callers can stitch across services.
/// </summary>
public static class CorrelationMiddleware
{
    /// <summary>The header name used to read and emit the correlation id.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>The Activity tag name.</summary>
    public const string ActivityTagName = "nickerp.correlation_id";

    /// <summary>The Serilog log property name.</summary>
    public const string LogPropertyName = "CorrelationId";

    /// <summary>
    /// Adds a correlation-id middleware: reads <c>X-Correlation-Id</c> header
    /// from the request (creates a new GUID if absent), stamps the active
    /// <see cref="Activity"/> with tag <c>nickerp.correlation_id</c>, pushes
    /// to Serilog's <see cref="LogContext"/> as property <c>CorrelationId</c>
    /// so all log lines carry it, echoes it back on the response.
    /// </summary>
    public static IApplicationBuilder UseNickErpCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (ctx, next) =>
        {
            var id = ResolveOrCreate(ctx.Request.Headers[HeaderName]);

            // Stamp the current Activity. Activity.Current may be null when
            // OTel is disabled — that's fine, the other channels still work.
            Activity.Current?.SetTag(ActivityTagName, id);

            // Echo back as soon as headers are about to be sent. Using
            // OnStarting avoids a "headers already sent" exception when
            // downstream middleware writes the body before our scope unwinds.
            ctx.Response.OnStarting(() =>
            {
                if (!ctx.Response.Headers.ContainsKey(HeaderName))
                {
                    ctx.Response.Headers[HeaderName] = id;
                }
                return Task.CompletedTask;
            });

            // Make the id visible to Serilog for the duration of the request.
            using (LogContext.PushProperty(LogPropertyName, id))
            {
                ctx.Items[LogPropertyName] = id;
                await next();
            }
        });

        return app;
    }

    private static string ResolveOrCreate(StringValues incoming)
    {
        var raw = incoming.ToString();
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw.Trim();
        }
        return Guid.NewGuid().ToString("N");
    }
}
