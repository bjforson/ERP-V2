using System.Security.Claims;
using NickERP.NickFinance.Core.Roles;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Web.Services;

namespace NickERP.NickFinance.Web.Tests;

/// <summary>
/// G2 §1.7 — period lock service: format helper, role check, soft-lock
/// enforcement.
/// </summary>
public sealed class PeriodLockServiceTests
{
    [Theory]
    [InlineData(2026, 4, "2026-04")]
    [InlineData(2026, 12, "2026-12")]
    [InlineData(2025, 1, "2025-01")]
    public void GetYearMonth_formats_canonical(int y, int m, string expected)
    {
        var at = new DateTimeOffset(y, m, 15, 12, 0, 0, TimeSpan.Zero);
        PeriodLockService.GetYearMonth(at).Should().Be(expected);
    }

    [Fact]
    public void HasReopenRole_returns_false_for_anonymous()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        svc.HasReopenRole(null).Should().BeFalse();
        svc.HasReopenRole(new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();
    }

    [Fact]
    public void HasReopenRole_accepts_role_claim()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var p = WithRole(PettyCashRoles.ReopenPeriod);
        svc.HasReopenRole(p).Should().BeTrue();
    }

    [Fact]
    public void HasPublishFxRole_accepts_scope_claim_alternate_shape()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim("nickerp:scope", PettyCashRoles.PublishFx));
        var p = new ClaimsPrincipal(identity);
        PeriodLockService.HasPublishFxRole(p).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureCanPostAsync_open_period_no_role_no_throw()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var result = await svc.EnsureCanPostAsync(1L, DateTimeOffset.UtcNow, principal: null);
        result.IsLatePost.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCanPostAsync_closed_period_without_role_throws()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var ym = PeriodLockService.GetYearMonth(DateTimeOffset.UtcNow);

        // Seed a closed period.
        db.Periods.Add(new NickERP.NickFinance.Core.Entities.PettyCashPeriod
        {
            TenantId = 1L,
            PeriodYearMonth = ym,
            ClosedAt = DateTimeOffset.UtcNow,
            ClosedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var act = () => svc.EnsureCanPostAsync(1L, DateTimeOffset.UtcNow, principal: null);
        await act.Should().ThrowAsync<PeriodLockedException>().Where(ex => ex.PeriodYearMonth == ym);
    }

    [Fact]
    public async Task EnsureCanPostAsync_closed_period_with_role_returns_late_post()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var ym = PeriodLockService.GetYearMonth(DateTimeOffset.UtcNow);

        db.Periods.Add(new NickERP.NickFinance.Core.Entities.PettyCashPeriod
        {
            TenantId = 1L,
            PeriodYearMonth = ym,
            ClosedAt = DateTimeOffset.UtcNow,
            ClosedByUserId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var result = await svc.EnsureCanPostAsync(1L, DateTimeOffset.UtcNow, WithRole(PettyCashRoles.ReopenPeriod));
        result.IsLatePost.Should().BeTrue();
        result.PeriodYearMonth.Should().Be(ym);
    }

    [Fact]
    public async Task CloseAsync_idempotent_throws_on_already_closed()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var actor = Guid.NewGuid();
        await svc.CloseAsync(1L, "2026-04", actor);

        var act = () => svc.CloseAsync(1L, "2026-04", actor);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ReopenAsync_throws_when_not_closed()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var act = () => svc.ReopenAsync(1L, "2026-04");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CloseAsync_then_ReopenAsync_round_trip()
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var actor = Guid.NewGuid();
        await svc.CloseAsync(1L, "2026-04", actor);

        var reopened = await svc.ReopenAsync(1L, "2026-04");
        reopened.IsClosed.Should().BeFalse();
        reopened.ClosedByUserId.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("foo-bar")]
    public async Task CloseAsync_rejects_malformed_yearmonth(string ym)
    {
        using var db = TestDb.Build();
        var svc = new PeriodLockService(db);
        var act = () => svc.CloseAsync(1L, ym, Guid.NewGuid());
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static ClaimsPrincipal WithRole(string role)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(identity);
    }
}
