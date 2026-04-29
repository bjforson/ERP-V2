using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Web.Services;
using NickERP.Platform.Identity.Auth;

namespace NickERP.NickFinance.Web.Endpoints;

/// <summary>
/// G2 — minimal-API endpoints for petty-cash voucher transitions and
/// FX rate publish. Mirrors the inspection module's
/// <c>NotificationsEndpoints</c> pattern: handler methods are exposed
/// as <c>public static</c> so the Web.Tests project can drive them
/// directly with a hand-built <see cref="HttpContext"/> and an
/// in-memory DbContext.
///
/// <para>
/// All endpoints require auth via the host's default policy; role
/// requirements (publish-fx, manage-periods) are checked in-handler
/// because the role lookup today is claim-based and not yet wired into
/// ASP.NET's policy infrastructure.
/// </para>
/// </summary>
public static class PettyCashEndpoints
{
    /// <summary>Map every petty-cash endpoint onto <paramref name="app"/>.</summary>
    public static IEndpointRouteBuilder MapPettyCashEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/nickfinance").RequireAuthorization();

        // Voucher transitions.
        group.MapPost("/vouchers/{id:guid}/approve", ApproveVoucherAsync);
        group.MapPost("/vouchers/{id:guid}/reject", RejectVoucherAsync);
        group.MapPost("/vouchers/{id:guid}/cancel", CancelVoucherAsync);
        group.MapPost("/vouchers/{id:guid}/disburse", DisburseVoucherAsync);
        group.MapPost("/vouchers/{id:guid}/reconcile", ReconcileVoucherAsync);

        // FX rates publish.
        group.MapPost("/fx-rates", PublishFxRatesAsync);

        // Periods admin.
        group.MapPost("/periods/{ym}/close", ClosePeriodAsync);
        group.MapPost("/periods/{ym}/reopen", ReopenPeriodAsync);

        return app;
    }

    // ---------------------------------------------------------------------
    // Voucher transition endpoints — thin wrappers around the workflow service.
    // ---------------------------------------------------------------------

    /// <summary>POST <c>/api/nickfinance/vouchers/{id}/approve</c>.</summary>
    public static async Task<IResult> ApproveVoucherAsync(
        Guid id,
        HttpContext http,
        VoucherWorkflowService workflow,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out _)) return Results.Unauthorized();
        return await RunTransitionAsync(() => workflow.ApproveAsync(id, ct));
    }

    /// <summary>POST <c>/api/nickfinance/vouchers/{id}/reject</c>; body = <see cref="RejectRequest"/>.</summary>
    public static async Task<IResult> RejectVoucherAsync(
        Guid id,
        HttpContext http,
        VoucherWorkflowService workflow,
        RejectRequest body,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out _)) return Results.Unauthorized();
        return await RunTransitionAsync(() => workflow.RejectAsync(id, body?.Reason ?? string.Empty, ct));
    }

    /// <summary>POST <c>/api/nickfinance/vouchers/{id}/cancel</c>.</summary>
    public static async Task<IResult> CancelVoucherAsync(
        Guid id,
        HttpContext http,
        VoucherWorkflowService workflow,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out _)) return Results.Unauthorized();
        return await RunTransitionAsync(() => workflow.CancelAsync(id, ct));
    }

    /// <summary>POST <c>/api/nickfinance/vouchers/{id}/disburse</c>.</summary>
    public static async Task<IResult> DisburseVoucherAsync(
        Guid id,
        HttpContext http,
        VoucherWorkflowService workflow,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out _)) return Results.Unauthorized();
        return await RunTransitionAsync(() => workflow.DisburseAsync(id, ct));
    }

    /// <summary>POST <c>/api/nickfinance/vouchers/{id}/reconcile</c>; body = <see cref="ReconcileRequest"/>.</summary>
    public static async Task<IResult> ReconcileVoucherAsync(
        Guid id,
        HttpContext http,
        VoucherWorkflowService workflow,
        ReconcileRequest body,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out _)) return Results.Unauthorized();
        if (body is null) return Results.BadRequest("Body required.");
        return await RunTransitionAsync(() => workflow.ReconcileAsync(id, body.ReceiptTotal, ct));
    }

    // ---------------------------------------------------------------------
    // FX rates publish — gated on the publish-fx role.
    // ---------------------------------------------------------------------

    /// <summary>POST <c>/api/nickfinance/fx-rates</c>; body = <see cref="FxRatePublishBatchRequest"/>.</summary>
    public static async Task<IResult> PublishFxRatesAsync(
        HttpContext http,
        FxRatePublishService publisher,
        FxRatePublishBatchRequest body,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out var userId)) return Results.Unauthorized();
        if (!PeriodLockService.HasPublishFxRole(http.User))
        {
            return Results.Problem(
                title: "Missing role",
                detail: $"Publishing FX rates requires the '{NickFinance.Core.Roles.PettyCashRoles.PublishFx}' role.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (body is null || body.Rates is null || body.Rates.Count == 0)
        {
            return Results.BadRequest("At least one rate is required.");
        }

        try
        {
            var saved = await publisher.PublishAsync(userId,
                body.Rates.Select(r => new FxRatePublishRequest(r.From, r.To, r.Rate, r.EffectiveDate)).ToList(),
                ct);
            return Results.Ok(new
            {
                count = saved.Count,
                rates = saved.Select(s => new
                {
                    from = s.FromCurrency,
                    to = s.ToCurrency,
                    rate = s.Rate,
                    effectiveDate = s.EffectiveDate.ToString("yyyy-MM-dd")
                })
            });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // Period admin — gated on the manage-periods role.
    // ---------------------------------------------------------------------

    /// <summary>POST <c>/api/nickfinance/periods/{ym}/close</c>.</summary>
    public static async Task<IResult> ClosePeriodAsync(
        string ym,
        HttpContext http,
        PeriodLockService periodLock,
        ITenantBaseCurrencyLookup _,
        NickERP.Platform.Tenancy.ITenantContext tenant,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out var userId)) return Results.Unauthorized();
        if (!PeriodLockService.HasManagePeriodsRole(http.User))
        {
            return Results.Problem(
                title: "Missing role",
                detail: $"Closing a period requires the '{NickFinance.Core.Roles.PettyCashRoles.ManagePeriods}' role.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (!tenant.IsResolved || tenant.TenantId <= 0) return Results.Unauthorized();

        try
        {
            var period = await periodLock.CloseAsync(tenant.TenantId, ym, userId, ct);
            return Results.Ok(new
            {
                tenantId = period.TenantId,
                periodYearMonth = period.PeriodYearMonth,
                closedAt = period.ClosedAt,
                closedByUserId = period.ClosedByUserId
            });
        }
        catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
    }

    /// <summary>POST <c>/api/nickfinance/periods/{ym}/reopen</c>.</summary>
    public static async Task<IResult> ReopenPeriodAsync(
        string ym,
        HttpContext http,
        PeriodLockService periodLock,
        NickERP.Platform.Tenancy.ITenantContext tenant,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out _)) return Results.Unauthorized();
        if (!PeriodLockService.HasManagePeriodsRole(http.User))
        {
            return Results.Problem(
                title: "Missing role",
                detail: $"Reopening a period requires the '{NickFinance.Core.Roles.PettyCashRoles.ManagePeriods}' role.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        if (!tenant.IsResolved || tenant.TenantId <= 0) return Results.Unauthorized();

        try
        {
            var period = await periodLock.ReopenAsync(tenant.TenantId, ym, ct);
            return Results.Ok(new
            {
                tenantId = period.TenantId,
                periodYearMonth = period.PeriodYearMonth,
                reopenedAt = DateTimeOffset.UtcNow
            });
        }
        catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        catch (InvalidOperationException ex) { return Results.Conflict(ex.Message); }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static async Task<IResult> RunTransitionAsync(Func<Task> transition)
    {
        try
        {
            await transition();
            return Results.NoContent();
        }
        catch (UnauthorizedVoucherActorException ex)
        {
            return Results.Problem(
                title: "Forbidden",
                detail: ex.Message,
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (InvalidVoucherTransitionException ex)
        {
            return Results.Conflict(ex.Message);
        }
        catch (PeriodLockedException ex)
        {
            return Results.Problem(
                title: "Period locked",
                detail: ex.Message,
                statusCode: StatusCodes.Status423Locked);
        }
        catch (FxRateNotPublishedException ex)
        {
            return Results.Problem(
                title: "FX rate not published",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    /// <summary>Resolve the current user id from the canonical NickERP claims.</summary>
    public static bool TryGetCurrentUserId(HttpContext http, out Guid userId)
    {
        userId = Guid.Empty;
        var claim = http.User.FindFirst(NickErpClaims.Id)?.Value
                    ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrEmpty(claim) && Guid.TryParse(claim, out userId);
    }
}

/// <summary>Body for POST <c>.../reject</c>.</summary>
public sealed record RejectRequest(string Reason);

/// <summary>Body for POST <c>.../reconcile</c>.</summary>
public sealed record ReconcileRequest(decimal ReceiptTotal);

/// <summary>Body for POST <c>/api/nickfinance/fx-rates</c>.</summary>
public sealed record FxRatePublishBatchRequest(IReadOnlyList<FxRatePublishItemDto> Rates);

/// <summary>One rate row in an <see cref="FxRatePublishBatchRequest"/>.</summary>
public sealed record FxRatePublishItemDto(string From, string To, decimal Rate, DateTime EffectiveDate);
