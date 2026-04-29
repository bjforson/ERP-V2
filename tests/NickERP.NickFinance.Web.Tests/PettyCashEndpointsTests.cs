using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Core.Roles;
using NickERP.NickFinance.Web.Endpoints;
using NickERP.NickFinance.Web.Services;
using NickERP.Platform.Identity.Auth;

namespace NickERP.NickFinance.Web.Tests;

/// <summary>
/// Endpoint-shape tests for PettyCashEndpoints. Mirrors the
/// NotificationsEndpoints pattern: drives the static handler methods
/// directly with a hand-built HttpContext + in-memory DbContext.
/// </summary>
public sealed class PettyCashEndpointsTests
{
    private const long TenantId = 1L;

    [Fact]
    public async Task ApproveVoucher_returns_401_when_no_user_claim()
    {
        await using var db = TestDb.Build();
        var events = new CapturingEventPublisher();
        var workflow = BuildWorkflow(db, events, Guid.Empty);

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var result = await PettyCashEndpoints.ApproveVoucherAsync(Guid.NewGuid(), http, workflow);

        result.GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    public async Task DisburseVoucher_returns_403_when_actor_is_not_custodian()
    {
        await using var db = TestDb.Build();
        var events = new CapturingEventPublisher();

        var custodian = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var requester = Guid.NewGuid();

        var box = new PettyCashBox
        {
            Code = "smk",
            Name = "smoke",
            CurrencyCode = "GHS",
            CustodianUserId = custodian,
            ApproverUserId = approver,
            OpeningBalanceCurrency = "GHS",
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId
        };
        db.Boxes.Add(box);

        // Hand-build a voucher already in Approve state.
        var voucher = new Voucher
        {
            BoxId = box.Id,
            SequenceNumber = 1,
            State = NickERP.NickFinance.Core.Enums.VoucherState.Approve,
            Purpose = "x",
            RequestedAmount = 10m,
            RequestedCurrency = "GHS",
            RequestedAmountBase = 10m,
            RequestedCurrencyBase = "GHS",
            RequestedByUserId = requester,
            RequestedAt = DateTimeOffset.UtcNow,
            ApproverUserId = approver,
            ApprovedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();

        // The requester is NOT the custodian; the workflow should throw and
        // the endpoint should map to 403.
        var workflow = BuildWorkflow(db, events, requester);

        var http = new DefaultHttpContext { User = PrincipalFor(requester) };
        var result = await PettyCashEndpoints.DisburseVoucherAsync(voucher.Id, http, workflow);

        ResultStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task RejectVoucher_with_invalid_state_returns_409()
    {
        await using var db = TestDb.Build();
        var events = new CapturingEventPublisher();

        var approver = Guid.NewGuid();
        var custodian = Guid.NewGuid();
        var requester = Guid.NewGuid();

        var box = new PettyCashBox
        {
            Code = "smk",
            Name = "smoke",
            CurrencyCode = "GHS",
            CustodianUserId = custodian,
            ApproverUserId = approver,
            OpeningBalanceCurrency = "GHS",
            TenantId = TenantId
        };
        db.Boxes.Add(box);
        var voucher = new Voucher
        {
            BoxId = box.Id,
            SequenceNumber = 1,
            State = NickERP.NickFinance.Core.Enums.VoucherState.Approve, // can't Reject from Approve
            Purpose = "x",
            RequestedAmount = 10m,
            RequestedCurrency = "GHS",
            RequestedAmountBase = 10m,
            RequestedCurrencyBase = "GHS",
            RequestedByUserId = requester,
            RequestedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();

        var workflow = BuildWorkflow(db, events, approver);

        var http = new DefaultHttpContext { User = PrincipalFor(approver) };
        var result = await PettyCashEndpoints.RejectVoucherAsync(voucher.Id, http, workflow, new RejectRequest("x"));

        ResultStatusCode(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task PublishFxRates_returns_403_without_role()
    {
        await using var db = TestDb.Build();
        var events = new CapturingEventPublisher();
        var publisher = new FxRatePublishService(db, events, TestTenant.For(TenantId), NullLogger<FxRatePublishService>.Instance);

        var actor = Guid.NewGuid();
        var http = new DefaultHttpContext { User = PrincipalFor(actor) }; // no role claims

        var body = new FxRatePublishBatchRequest(new[]
        {
            new FxRatePublishItemDto("USD", "GHS", 12m, DateTime.UtcNow.Date)
        });
        var result = await PettyCashEndpoints.PublishFxRatesAsync(http, publisher, body);

        ResultStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PublishFxRates_succeeds_with_role_and_writes_NULL_tenant_row()
    {
        await using var db = TestDb.Build();
        var events = new CapturingEventPublisher();
        var publisher = new FxRatePublishService(db, events, TestTenant.For(TenantId), NullLogger<FxRatePublishService>.Instance);

        var actor = Guid.NewGuid();
        var http = new DefaultHttpContext { User = PrincipalForWithRole(actor, PettyCashRoles.PublishFx) };

        var body = new FxRatePublishBatchRequest(new[]
        {
            new FxRatePublishItemDto("USD", "GHS", 12m, DateTime.UtcNow.Date)
        });
        var result = await PettyCashEndpoints.PublishFxRatesAsync(http, publisher, body);

        result.GetType().Name.Should().Contain("Ok");
        // Verify row landed in DB (in-memory provider doesn't enforce
        // RLS so this just asserts the publish path worked end-to-end).
        var rate = db.FxRates.SingleOrDefault();
        rate.Should().NotBeNull();
        rate!.TenantId.Should().BeNull();
        rate.Rate.Should().Be(12m);
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.fx_rate.published");
    }

    [Fact]
    public async Task PublishFxRates_rejects_empty_batch()
    {
        await using var db = TestDb.Build();
        var events = new CapturingEventPublisher();
        var publisher = new FxRatePublishService(db, events, TestTenant.For(TenantId), NullLogger<FxRatePublishService>.Instance);

        var http = new DefaultHttpContext { User = PrincipalForWithRole(Guid.NewGuid(), PettyCashRoles.PublishFx) };
        var body = new FxRatePublishBatchRequest(Array.Empty<FxRatePublishItemDto>());
        var result = await PettyCashEndpoints.PublishFxRatesAsync(http, publisher, body);

        ResultStatusCode(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task ClosePeriod_requires_manage_role()
    {
        await using var db = TestDb.Build();
        var period = new PeriodLockService(db);
        var lookup = new StubBaseCurrencyLookup();
        var http = new DefaultHttpContext { User = PrincipalFor(Guid.NewGuid()) };
        var result = await PettyCashEndpoints.ClosePeriodAsync("2026-04", http, period, lookup, TestTenant.For(TenantId));

        ResultStatusCode(result).Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ClosePeriod_with_role_writes_period_row()
    {
        await using var db = TestDb.Build();
        var period = new PeriodLockService(db);
        var lookup = new StubBaseCurrencyLookup();

        var actor = Guid.NewGuid();
        var http = new DefaultHttpContext { User = PrincipalForWithRole(actor, PettyCashRoles.ManagePeriods) };

        var result = await PettyCashEndpoints.ClosePeriodAsync("2026-04", http, period, lookup, TestTenant.For(TenantId));
        result.GetType().Name.Should().Contain("Ok");

        var row = db.Periods.SingleOrDefault();
        row.Should().NotBeNull();
        row!.PeriodYearMonth.Should().Be("2026-04");
        row.IsClosed.Should().BeTrue();
        row.ClosedByUserId.Should().Be(actor);
    }

    // -- helpers ------------------------------------------------------------

    private static VoucherWorkflowService BuildWorkflow(NickERP.NickFinance.Database.NickFinanceDbContext db, CapturingEventPublisher events, Guid actor)
    {
        var fx = new FakeFxRateLookup(1m);
        var baseCurrency = new StubBaseCurrencyLookup("GHS");
        var lockSvc = new PeriodLockService(db);
        var tenant = TestTenant.For(TenantId);
        var auth = new FakeAuthStateProvider(actor);
        return new VoucherWorkflowService(
            db, events, fx, baseCurrency, lockSvc, tenant, auth,
            NullLogger<VoucherWorkflowService>.Instance);
    }

    private static ClaimsPrincipal PrincipalFor(Guid userId)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(NickErpClaims.Id, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        identity.AddClaim(new Claim(NickErpClaims.TenantId, "1"));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal PrincipalForWithRole(Guid userId, string role)
    {
        var p = PrincipalFor(userId);
        ((ClaimsIdentity)p.Identity!).AddClaim(new Claim(ClaimTypes.Role, role));
        return p;
    }

    private static int ResultStatusCode(IResult result)
    {
        // ProblemHttpResult / IStatusCodeHttpResult expose StatusCode
        // — pull via reflection so we don't take a coupling on the
        // sealed concrete result types.
        var prop = result.GetType().GetProperty("StatusCode");
        if (prop is null) return 0;
        return (int)(prop.GetValue(result) ?? 0);
    }
}
