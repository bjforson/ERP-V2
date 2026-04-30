using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NickERP.Platform.Identity;
using NickFinance.WebApp.Services;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// HTTP-aware default implementation of
/// <see cref="ISecurityAuditService"/>. Resolves the actor and tenant
/// from the current HTTP context + the cached <see cref="CurrentUser"/>.
/// Inserts directly into <see cref="IdentityDbContext.SecurityAuditEvents"/>;
/// the bootstrap CLI does NOT register this service — its mutations
/// are pre-auth and should not appear here.
/// </summary>
/// <remarks>
/// Failure mode: if the DB write itself fails, we swallow the exception
/// and log a warning. The premise: a degraded audit log shouldn't
/// degrade the user-facing flow it's recording. Operators noticing
/// missing rows can correlate with the warning logs.
/// </remarks>
public sealed class SecurityAuditService : ISecurityAuditService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IdentityDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly IServiceProvider _sp;
    private readonly ILogger<SecurityAuditService> _log;
    private readonly TimeProvider _clock;

    public SecurityAuditService(
        IdentityDbContext db,
        IHttpContextAccessor http,
        IServiceProvider sp,
        ILogger<SecurityAuditService> log,
        TimeProvider? clock = null)
    {
        _db = db;
        _http = http;
        _sp = sp;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task RecordAsync(
        SecurityAuditAction action,
        string? targetType = null,
        string? targetId = null,
        SecurityAuditResult result = SecurityAuditResult.Allowed,
        object? details = null,
        CancellationToken ct = default)
    {
        try
        {
            var ctx = _http.HttpContext;
            // Resolve current user lazily — auth-failure paths may not yet have one.
            Guid? userId = null;
            long tenantId = 1;
            try
            {
                var current = _sp.GetService(typeof(CurrentUser)) as CurrentUser;
                if (current is not null)
                {
                    userId = current.UserId;
                    tenantId = current.TenantId;
                }
            }
            catch
            {
                // CurrentUser not resolvable in this scope — fine, audit row goes anonymous.
            }

            var ip = ctx?.Connection?.RemoteIpAddress?.ToString();
            var userAgent = ctx?.Request?.Headers?["User-Agent"].ToString();

            // Capture which auth scheme produced this principal so audit
            // can distinguish CF-Access-OTP traffic from LAN-trust and
            // cookie-login traffic.
            //
            // The "nickerp.via" claim is stamped by:
            //   • LanTrustAuthHandler  → value "lan-trust" + "nickerp.via.source-ip"
            //   • AuthEndpoints.LoginSubmitAsync (cookie login) → value "cookie"
            //
            // CF-Access-issued principals don't carry the claim at all,
            // so absence implies "cf-access". W4 (2026-04-29) added the
            // "cookie" path; the JSON details payload is unchanged.
            string via = "cf-access";
            string? viaSourceIp = null;
            try
            {
                var principal = ctx?.User;
                if (principal?.Identity?.IsAuthenticated == true)
                {
                    var viaClaim = principal.FindFirst(LanTrustAuthHandler.ViaClaimType);
                    if (viaClaim is not null)
                    {
                        via = viaClaim.Value;
                        viaSourceIp = principal.FindFirst(LanTrustAuthHandler.ViaClaimType + ".source-ip")?.Value;
                    }
                }
            }
            catch { /* don't let auth-introspection break audit */ }

            string? detailsJson = null;
            try
            {
                // Fold details (caller-supplied, may be null) plus via tag
                // into a single JSON object so the audit row carries
                // origin-of-trust without needing a schema column.
                var enriched = new Dictionary<string, object?>
                {
                    ["via"] = via,
                };
                if (viaSourceIp is not null) enriched["via.source-ip"] = viaSourceIp;
                if (details is not null) enriched["details"] = details;
                detailsJson = JsonSerializer.Serialize(enriched, JsonOpts);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Audit details serialise failed; storing null.");
            }

            _db.SecurityAuditEvents.Add(new SecurityAuditEvent
            {
                UserId = userId,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Ip = string.IsNullOrEmpty(ip) ? null : ip,
                UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent,
                OccurredAt = _clock.GetUtcNow(),
                DetailsJson = detailsJson,
                Result = result,
                TenantId = tenantId
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Swallow — audit failure must never break the user-facing flow.
            _log.LogError(ex, "Security audit write failed for action {Action}", action);
        }
    }
}
