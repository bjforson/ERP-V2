using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Identity.Auth;

namespace NickERP.Inspection.Web.Endpoints;

/// <summary>
/// Sprint 9 / FU-icums-signing — admin REST surface for rotating
/// per-tenant IcumsGh signing keys.
///
/// <para>
/// Three endpoints, all gated by the <see cref="AdminScope"/> role
/// (mirrored from the <c>Inspection.Admin</c> scope by
/// <see cref="NickErpAuthenticationHandler"/>):
/// <list type="bullet">
///   <item><description><c>POST /api/icums/keys/rotate</c> — generate
///   a new (inactive) key for the calling tenant. Returns
///   <c>{ "keyId": "k2" }</c>.</description></item>
///   <item><description><c>POST /api/icums/keys/activate</c> — body
///   <c>{ "newKeyId": "k2", "verificationWindowDays": 7 }</c>.
///   Activates new, retires old, sets the verification overlap
///   window.</description></item>
///   <item><description><c>GET /api/icums/keys</c> — list keys for
///   the calling tenant. Metadata only — never the key
///   material.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Tenancy.</b> Tenant scope is taken from the
/// <see cref="NickErpClaims.TenantId"/> claim. RLS narrows DB
/// reads/writes to that tenant; the endpoint cannot rotate another
/// tenant's keys. v0 is per-tenant; a future cross-tenant rotation
/// would need a separate admin action that calls
/// <c>SetSystemContext()</c> + iterates <c>tenancy.tenants</c> — out
/// of scope here.
/// </para>
/// </summary>
public static class IcumsKeyRotationEndpoint
{
    /// <summary>Scope/role required to call any of the rotation endpoints.</summary>
    public const string AdminScope = "Inspection.Admin";

    /// <summary>
    /// Map the three rotation endpoints under <c>/api/icums/keys</c>.
    /// Wire after <c>UseAuthentication()</c> + <c>UseAuthorization()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapIcumsKeyRotationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/icums/keys")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(CfAccessAuthenticationOptions.SchemeName)
                                        .RequireRole(AdminScope))
            .WithTags("ICUMS Signing Keys");

        group.MapPost("/rotate", RotateAsync);
        group.MapPost("/activate", ActivateAsync);
        group.MapGet("/", ListAsync);

        return app;
    }

    /// <summary>Generate a fresh (inactive) signing key for the caller's tenant.</summary>
    public static async Task<IResult> RotateAsync(
        HttpContext http,
        IcumsKeyRotationService rotation,
        CancellationToken ct = default)
    {
        if (!TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var newKeyId = await rotation.GenerateAsync(tenantId, ct);
        return Results.Ok(new RotateResponse(newKeyId));
    }

    /// <summary>
    /// Activate a previously-generated key. Body shape:
    /// <c>{ "newKeyId": "k2", "verificationWindowDays": 7 }</c>.
    /// </summary>
    public static async Task<IResult> ActivateAsync(
        HttpContext http,
        IcumsKeyRotationService rotation,
        [Microsoft.AspNetCore.Mvc.FromBody] ActivateRequest body,
        CancellationToken ct = default)
    {
        if (!TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        if (body is null || string.IsNullOrWhiteSpace(body.NewKeyId))
            return Results.BadRequest(new { error = "newKeyId is required." });

        var window = body.VerificationWindowDays.HasValue
            ? TimeSpan.FromDays(Math.Max(0, body.VerificationWindowDays.Value))
            : IcumsKeyRotationService.DefaultVerificationWindow;

        try
        {
            var result = await rotation.ActivateAsync(tenantId, body.NewKeyId, window, ct);
            return Results.Ok(new ActivateResponse(
                ActivatedKeyId: result.ActivatedKeyId,
                ActivatedAt: result.ActivatedAt,
                RetiredKeyId: result.RetiredKeyId,
                VerificationOnlyUntil: result.VerificationOnlyUntil));
        }
        catch (InvalidOperationException ex)
        {
            // Wrong / already-retired / already-active keyId — surface
            // the reason so the operator can recover. The reason
            // strings are operator-facing (no PII).
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>List signing keys for the caller's tenant. Metadata only — no key material.</summary>
    public static async Task<IResult> ListAsync(
        HttpContext http,
        IcumsKeyRotationService rotation,
        CancellationToken ct = default)
    {
        if (!TryGetTenantId(http, out var tenantId))
            return Results.Unauthorized();

        var keys = await rotation.ListAsync(tenantId, ct);
        return Results.Ok(new ListResponse(keys.Select(k => new KeyView(
            KeyId: k.KeyId,
            CreatedAt: k.CreatedAt,
            ActivatedAt: k.ActivatedAt,
            RetiredAt: k.RetiredAt,
            VerificationOnlyUntil: k.VerificationOnlyUntil)).ToList()));
    }

    /// <summary>
    /// Pull the tenant id off the principal. Returns false for
    /// anonymous callers (which the endpoint policy already rejects;
    /// the handler-level check is defence-in-depth).
    /// </summary>
    internal static bool TryGetTenantId(HttpContext http, out long tenantId)
    {
        tenantId = 0;
        var raw = http.User.FindFirst(NickErpClaims.TenantId)?.Value;
        if (string.IsNullOrEmpty(raw))
            return false;
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) || t <= 0)
            return false;
        tenantId = t;
        return true;
    }
}

/// <summary>POST /api/icums/keys/rotate response.</summary>
public sealed record RotateResponse(string KeyId);

/// <summary>POST /api/icums/keys/activate request body.</summary>
public sealed record ActivateRequest(string NewKeyId, int? VerificationWindowDays);

/// <summary>POST /api/icums/keys/activate response.</summary>
public sealed record ActivateResponse(
    string ActivatedKeyId,
    DateTimeOffset ActivatedAt,
    string? RetiredKeyId,
    DateTimeOffset? VerificationOnlyUntil);

/// <summary>GET /api/icums/keys response shape.</summary>
public sealed record ListResponse(IReadOnlyList<KeyView> Keys);

/// <summary>One signing-key row in <see cref="ListResponse"/>. No key material.</summary>
public sealed record KeyView(
    string KeyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset? VerificationOnlyUntil);
