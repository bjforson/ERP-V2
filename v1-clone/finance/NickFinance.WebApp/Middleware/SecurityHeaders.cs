namespace NickFinance.WebApp.Middleware;

/// <summary>
/// Response-header middleware. Sits in the pipeline immediately after
/// <c>UseHsts</c> so every response (including static assets) carries
/// the same security posture.
/// </summary>
/// <remarks>
/// CSP allows <c>'unsafe-inline'</c> on style-src because Blazor Server
/// emits inline error styles via <c>blazor.web.js</c> when the SignalR
/// circuit drops. Tightening that requires a nonce that the framework
/// doesn't currently produce.
/// </remarks>
public static class SecurityHeadersExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            h["Content-Security-Policy"] =
                "default-src 'self'; " +
                "script-src 'self' 'wasm-unsafe-eval'; " +
                "style-src 'self' 'unsafe-inline' fonts.googleapis.com; " +
                "font-src 'self' fonts.gstatic.com; " +
                "img-src 'self' data:; " +
                "connect-src 'self' wss:; " +
                "frame-ancestors 'none';";
            await next();
        });
    }
}
