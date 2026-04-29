using NickERP.NickFinance.Core.Enums;
using NickERP.NickFinance.Core.Services;

namespace NickERP.NickFinance.Core.Tests;

/// <summary>
/// G2 §1.4 — voucher state-machine transitions. Pure-function tests
/// covering every allowed path + every forbidden transition.
/// </summary>
public sealed class VoucherTransitionsTests
{
    // -------- Allowed transitions -------------------------------------

    [Theory]
    [InlineData(VoucherState.Request, VoucherState.Approve)]
    [InlineData(VoucherState.Request, VoucherState.Rejected)]
    [InlineData(VoucherState.Request, VoucherState.Cancelled)]
    [InlineData(VoucherState.Approve, VoucherState.Disburse)]
    [InlineData(VoucherState.Approve, VoucherState.Cancelled)]
    [InlineData(VoucherState.Disburse, VoucherState.Reconcile)]
    public void Allowed_transitions_pass(VoucherState from, VoucherState to)
    {
        VoucherTransitions.IsAllowed(from, to).Should().BeTrue();
        var act = () => VoucherTransitions.EnsureAllowed(from, to);
        act.Should().NotThrow();
    }

    // -------- Forbidden transitions (a representative sample) ---------

    [Theory]
    // Cannot skip approval.
    [InlineData(VoucherState.Request, VoucherState.Disburse)]
    [InlineData(VoucherState.Request, VoucherState.Reconcile)]
    // Cannot reject after approval.
    [InlineData(VoucherState.Approve, VoucherState.Rejected)]
    // Cannot cancel after disburse (must reconcile-Adjust instead, §1.4).
    [InlineData(VoucherState.Disburse, VoucherState.Cancelled)]
    [InlineData(VoucherState.Disburse, VoucherState.Approve)]
    [InlineData(VoucherState.Disburse, VoucherState.Rejected)]
    // Terminal states are dead-ends.
    [InlineData(VoucherState.Reconcile, VoucherState.Approve)]
    [InlineData(VoucherState.Reconcile, VoucherState.Disburse)]
    [InlineData(VoucherState.Reconcile, VoucherState.Cancelled)]
    [InlineData(VoucherState.Rejected, VoucherState.Approve)]
    [InlineData(VoucherState.Rejected, VoucherState.Cancelled)]
    [InlineData(VoucherState.Cancelled, VoucherState.Approve)]
    [InlineData(VoucherState.Cancelled, VoucherState.Disburse)]
    // Self-loops are forbidden.
    [InlineData(VoucherState.Request, VoucherState.Request)]
    [InlineData(VoucherState.Approve, VoucherState.Approve)]
    [InlineData(VoucherState.Disburse, VoucherState.Disburse)]
    public void Forbidden_transitions_throw(VoucherState from, VoucherState to)
    {
        VoucherTransitions.IsAllowed(from, to).Should().BeFalse();
        var act = () => VoucherTransitions.EnsureAllowed(from, to);
        act.Should().Throw<InvalidVoucherTransitionException>()
            .Which.From.Should().Be(from);
    }

    [Fact]
    public void Exception_carries_From_and_To()
    {
        try { VoucherTransitions.EnsureAllowed(VoucherState.Request, VoucherState.Disburse); }
        catch (InvalidVoucherTransitionException ex)
        {
            ex.From.Should().Be(VoucherState.Request);
            ex.To.Should().Be(VoucherState.Disburse);
            return;
        }
        throw new Xunit.Sdk.XunitException("Expected InvalidVoucherTransitionException.");
    }

    [Fact]
    public void UnauthorizedVoucherActorException_carries_actor_and_action()
    {
        var actor = Guid.NewGuid();
        var expected = Guid.NewGuid();
        var ex = new UnauthorizedVoucherActorException(actor, "Approve", expected);
        ex.ActorUserId.Should().Be(actor);
        ex.Action.Should().Be("Approve");
        ex.ExpectedActorUserId.Should().Be(expected);
    }

    [Fact]
    public void PeriodLockedException_carries_period()
    {
        var ex = new PeriodLockedException("2026-04");
        ex.PeriodYearMonth.Should().Be("2026-04");
        ex.Message.Should().Contain("2026-04");
        ex.Message.Should().Contain("petty_cash.reopen_period");
    }

    [Fact]
    public void FxRateNotPublishedException_carries_pair_and_date()
    {
        var ex = new FxRateNotPublishedException("USD", "GHS", new DateTime(2026, 4, 29));
        ex.FromCurrency.Should().Be("USD");
        ex.ToCurrency.Should().Be("GHS");
        ex.EffectiveDate.Should().Be(new DateTime(2026, 4, 29));
        ex.Message.Should().Contain("ask finance");
    }
}
