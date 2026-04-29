using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Core.Enums;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Tenancy;

namespace NickERP.NickFinance.Web.Services;

/// <summary>
/// Voucher state-machine orchestration — see G2 §1.4. Every transition
/// passes through this service so the audit trail and ledger writes are
/// consistent regardless of trigger (Razor page button, API endpoint,
/// future workflow agent).
///
/// <para>
/// Pages and endpoints call methods like <see cref="RequestAsync"/> /
/// <see cref="ApproveAsync"/> / <see cref="DisburseAsync"/>; they do
/// NOT mutate the <see cref="Voucher"/> entity directly. The service
/// validates the transition + actor authorization, mutates the voucher,
/// emits audit events, and (for Disburse / Reconcile) emits ledger
/// events.
/// </para>
///
/// <para>
/// <strong>Audit events emitted (G2 §6):</strong>
/// </para>
/// <list type="bullet">
///   <item><description><c>nickfinance.box.created</c></description></item>
///   <item><description><c>nickfinance.voucher.requested</c></description></item>
///   <item><description><c>nickfinance.voucher.approved</c></description></item>
///   <item><description><c>nickfinance.voucher.rejected</c></description></item>
///   <item><description><c>nickfinance.voucher.disbursed</c></description></item>
///   <item><description><c>nickfinance.voucher.reconciled</c></description></item>
///   <item><description><c>nickfinance.voucher.cancelled</c></description></item>
///   <item><description><c>nickfinance.petty_cash.late_post</c> (when a Disburse / Reconcile lands in a closed period under the reopen-period role)</description></item>
/// </list>
/// </summary>
public sealed class VoucherWorkflowService
{
    private readonly NickFinanceDbContext _db;
    private readonly IEventPublisher _events;
    private readonly IFxRateLookup _fx;
    private readonly ITenantBaseCurrencyLookup _baseCurrency;
    private readonly PeriodLockService _periodLock;
    private readonly ITenantContext _tenant;
    private readonly AuthenticationStateProvider _auth;
    private readonly ILogger<VoucherWorkflowService> _logger;

    public VoucherWorkflowService(
        NickFinanceDbContext db,
        IEventPublisher events,
        IFxRateLookup fx,
        ITenantBaseCurrencyLookup baseCurrency,
        PeriodLockService periodLock,
        ITenantContext tenant,
        AuthenticationStateProvider auth,
        ILogger<VoucherWorkflowService> logger)
    {
        _db = db;
        _events = events;
        _fx = fx;
        _baseCurrency = baseCurrency;
        _periodLock = periodLock;
        _tenant = tenant;
        _auth = auth;
        _logger = logger;
    }

    // ---------------------------------------------------------------------
    // Box lifecycle (creation only — boxes don't mutate beyond archive)
    // ---------------------------------------------------------------------

    /// <summary>Create a new petty-cash box. Emits <c>nickfinance.box.created</c>.</summary>
    public async Task<PettyCashBox> CreateBoxAsync(
        string code,
        string name,
        string currencyCode,
        Guid custodianUserId,
        Guid approverUserId,
        decimal openingBalanceAmount,
        CancellationToken ct = default)
    {
        var (actor, tenantId, principal) = await CurrentActorAsync();
        // Pre-check the SoD invariant in code so we get a friendly error
        // rather than the bare DB CHECK violation.
        if (custodianUserId == approverUserId)
        {
            throw new InvalidOperationException(
                "Custodian and approver must be different users (separation-of-duties).");
        }

        var box = new PettyCashBox
        {
            Code = code.Trim(),
            Name = name.Trim(),
            CurrencyCode = currencyCode.ToUpperInvariant(),
            CustodianUserId = custodianUserId,
            ApproverUserId = approverUserId,
            OpeningBalanceAmount = openingBalanceAmount,
            OpeningBalanceCurrency = currencyCode.ToUpperInvariant(),
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenantId
        };
        // Validate via the value object so a negative opening balance
        // throws here, not at the DB layer.
        _ = box.OpeningBalance;

        _db.Boxes.Add(box);
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.box.created",
            "PettyCashBox", box.Id.ToString(),
            new { box.Id, box.Code, box.Name, box.CurrencyCode, box.CustodianUserId, box.ApproverUserId },
            ct);

        return box;
    }

    // ---------------------------------------------------------------------
    // Voucher.Request — anyone in the tenant can raise; emits requested event.
    // ---------------------------------------------------------------------
    public async Task<Voucher> RequestAsync(
        Guid boxId,
        string purpose,
        decimal requestedAmount,
        CancellationToken ct = default)
    {
        var (actor, tenantId, _) = await CurrentActorAsync();
        if (actor is null) throw new InvalidOperationException("Cannot request — no authenticated user.");

        var box = await _db.Boxes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == boxId, ct)
            ?? throw new InvalidOperationException($"Box {boxId} not found.");

        if (box.ArchivedAt is not null)
        {
            throw new InvalidOperationException($"Box {box.Code} is archived; cannot raise vouchers on it.");
        }

        // Money invariant on the requested amount.
        var requested = new Money(requestedAmount, box.CurrencyCode);

        // Snapshot the base-currency conversion at request time. If the
        // currencies match this is a no-op 1.0 lookup; otherwise we need
        // a published rate. Reuse the same fail-fast story as Disburse
        // — better to block at request-time than at the cash-out moment.
        var baseCurrency = await _baseCurrency.GetBaseCurrencyAsync(tenantId, ct);
        var now = DateTimeOffset.UtcNow;
        var (baseAmount, _, _) = await ConvertAsync(requested, baseCurrency, now.UtcDateTime, ct);

        // Per-box monotonic sequence number — coarse but adequate. A
        // production system would push this to a Postgres sequence; for
        // the pathfinder we serialize through a per-box increment.
        var nextSeq = await _db.Vouchers
            .Where(v => v.BoxId == boxId)
            .Select(v => (long?)v.SequenceNumber)
            .MaxAsync(ct) ?? 0L;

        var voucher = new Voucher
        {
            BoxId = boxId,
            SequenceNumber = nextSeq + 1,
            State = VoucherState.Request,
            Purpose = purpose.Trim(),
            RequestedAmount = requested.Amount,
            RequestedCurrency = requested.CurrencyCode,
            RequestedAmountBase = baseAmount.Amount,
            RequestedCurrencyBase = baseAmount.CurrencyCode,
            RequestedByUserId = actor.Value,
            RequestedAt = now,
            TenantId = tenantId
        };
        _db.Vouchers.Add(voucher);
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.voucher.requested",
            "PettyCashVoucher", voucher.Id.ToString(),
            new
            {
                voucher.Id,
                voucher.BoxId,
                BoxApproverUserId = box.ApproverUserId,
                voucher.RequestedByUserId,
                Amount = requested.ToString(),
                voucher.Purpose
            }, ct);

        return voucher;
    }

    // ---------------------------------------------------------------------
    // Voucher.Approve — approver only.
    // ---------------------------------------------------------------------
    public async Task<Voucher> ApproveAsync(Guid voucherId, CancellationToken ct = default)
    {
        var (actor, tenantId, _) = await CurrentActorAsync();
        if (actor is null) throw new InvalidOperationException("Cannot approve — no authenticated user.");

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new InvalidOperationException($"Voucher {voucherId} not found.");
        var box = await _db.Boxes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == voucher.BoxId, ct)
            ?? throw new InvalidOperationException($"Box {voucher.BoxId} not found.");

        VoucherTransitions.EnsureAllowed(voucher.State, VoucherState.Approve);
        if (actor != box.ApproverUserId)
        {
            throw new UnauthorizedVoucherActorException(actor.Value, "Approve", box.ApproverUserId);
        }

        var now = DateTimeOffset.UtcNow;
        voucher.State = VoucherState.Approve;
        voucher.ApproverUserId = actor;
        voucher.ApprovedAt = now;
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.voucher.approved",
            "PettyCashVoucher", voucher.Id.ToString(),
            new
            {
                voucher.Id,
                voucher.BoxId,
                ApproverUserId = actor,
                voucher.RequestedByUserId
            }, ct);

        return voucher;
    }

    // ---------------------------------------------------------------------
    // Voucher.Reject — approver only; from Request only.
    // ---------------------------------------------------------------------
    public async Task<Voucher> RejectAsync(Guid voucherId, string reason, CancellationToken ct = default)
    {
        var (actor, tenantId, _) = await CurrentActorAsync();
        if (actor is null) throw new InvalidOperationException("Cannot reject — no authenticated user.");

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new InvalidOperationException($"Voucher {voucherId} not found.");
        var box = await _db.Boxes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == voucher.BoxId, ct)
            ?? throw new InvalidOperationException($"Box {voucher.BoxId} not found.");

        VoucherTransitions.EnsureAllowed(voucher.State, VoucherState.Rejected);
        if (actor != box.ApproverUserId)
        {
            throw new UnauthorizedVoucherActorException(actor.Value, "Reject", box.ApproverUserId);
        }

        voucher.State = VoucherState.Rejected;
        voucher.ApproverUserId = actor;
        voucher.RejectedReason = string.IsNullOrWhiteSpace(reason) ? "(no reason given)" : reason.Trim();
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.voucher.rejected",
            "PettyCashVoucher", voucher.Id.ToString(),
            new { voucher.Id, voucher.BoxId, voucher.RejectedReason, voucher.RequestedByUserId }, ct);

        return voucher;
    }

    // ---------------------------------------------------------------------
    // Voucher.Cancel — requester or approver, only pre-Disburse (state machine).
    // ---------------------------------------------------------------------
    public async Task<Voucher> CancelAsync(Guid voucherId, CancellationToken ct = default)
    {
        var (actor, tenantId, _) = await CurrentActorAsync();
        if (actor is null) throw new InvalidOperationException("Cannot cancel — no authenticated user.");

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new InvalidOperationException($"Voucher {voucherId} not found.");
        var box = await _db.Boxes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == voucher.BoxId, ct)
            ?? throw new InvalidOperationException($"Box {voucher.BoxId} not found.");

        VoucherTransitions.EnsureAllowed(voucher.State, VoucherState.Cancelled);
        // Cancel is allowed for the requester or the approver. Custodian
        // explicitly cannot cancel — they only handle Disburse, and the
        // state machine already forbids Disburse→Cancelled.
        if (actor != voucher.RequestedByUserId && actor != box.ApproverUserId)
        {
            throw new UnauthorizedVoucherActorException(actor.Value, "Cancel", voucher.RequestedByUserId);
        }

        voucher.State = VoucherState.Cancelled;
        voucher.CancelledAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.voucher.cancelled",
            "PettyCashVoucher", voucher.Id.ToString(),
            new { voucher.Id, voucher.BoxId, voucher.RequestedByUserId, ActorUserId = actor }, ct);

        return voucher;
    }

    // ---------------------------------------------------------------------
    // Voucher.Disburse — custodian only; emits Debit ledger event + audit.
    // ---------------------------------------------------------------------
    public async Task<Voucher> DisburseAsync(Guid voucherId, CancellationToken ct = default)
    {
        var (actor, tenantId, principal) = await CurrentActorAsync();
        if (actor is null) throw new InvalidOperationException("Cannot disburse — no authenticated user.");

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new InvalidOperationException($"Voucher {voucherId} not found.");
        var box = await _db.Boxes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == voucher.BoxId, ct)
            ?? throw new InvalidOperationException($"Box {voucher.BoxId} not found.");

        VoucherTransitions.EnsureAllowed(voucher.State, VoucherState.Disburse);
        if (actor != box.CustodianUserId)
        {
            throw new UnauthorizedVoucherActorException(actor.Value, "Disburse", box.CustodianUserId);
        }

        var now = DateTimeOffset.UtcNow;
        var periodCheck = await _periodLock.EnsureCanPostAsync(tenantId, now, principal, ct);

        // FX snapshot AT DISBURSE TIME — see G2 §1.8. Even if the request
        // was raised three days ago at a different rate, the ledger row
        // captures today's rate.
        var native = new Money(voucher.RequestedAmount, voucher.RequestedCurrency);
        var baseCurrency = await _baseCurrency.GetBaseCurrencyAsync(tenantId, ct);
        var (baseAmount, fxRate, fxRateDate) = await ConvertAsync(native, baseCurrency, now.UtcDateTime, ct);

        voucher.State = VoucherState.Disburse;
        voucher.DisbursedAmount = native.Amount;
        voucher.DisbursedCurrency = native.CurrencyCode;
        voucher.DisbursedAmountBase = baseAmount.Amount;
        voucher.DisbursedCurrencyBase = baseAmount.CurrencyCode;
        voucher.DisbursedAt = now;

        var ledger = new LedgerEvent
        {
            BoxId = box.Id,
            VoucherId = voucher.Id,
            EventType = LedgerEventType.Disburse,
            Direction = LedgerDirection.Debit,
            AmountNative = native.Amount,
            CurrencyNative = native.CurrencyCode,
            AmountBase = baseAmount.Amount,
            CurrencyBase = baseAmount.CurrencyCode,
            FxRate = fxRate,
            FxRateDate = fxRateDate,
            PostedAt = now,
            PostedByUserId = actor.Value,
            TenantId = tenantId
        };
        _db.LedgerEvents.Add(ledger);

        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.voucher.disbursed",
            "PettyCashVoucher", voucher.Id.ToString(),
            new
            {
                voucher.Id,
                voucher.BoxId,
                CustodianUserId = actor,
                voucher.RequestedByUserId,
                Native = native.ToString(),
                Base = baseAmount.ToString(),
                LedgerEventId = ledger.Id
            }, ct);

        if (periodCheck.IsLatePost)
        {
            await EmitAsync(tenantId, actor, "nickfinance.petty_cash.late_post",
                "PettyCashLedgerEvent", ledger.Id.ToString(),
                new
                {
                    LedgerEventId = ledger.Id,
                    periodCheck.PeriodYearMonth,
                    VoucherId = voucher.Id,
                    ActorUserId = actor
                }, ct);
        }

        return voucher;
    }

    // ---------------------------------------------------------------------
    // Voucher.Reconcile — terminal; may emit an Adjust ledger event.
    //
    // receiptTotal is the sum of receipts the requester turned in. If it
    // differs from disbursed_amount we post an Adjust event for the
    // difference: receiptTotal < disbursed → cash short → Adjust DEBIT
    // (more cash left the box than receipts justify, lost or owed back);
    // receiptTotal > disbursed → over-justified → Adjust CREDIT (rare,
    // means the requester proves they spent more than disbursed; the
    // box should be reimbursed). The simpler interpretation for v0:
    //   * receiptTotal < disbursed: an Adjust DEBIT for (disbursed - receiptTotal)
    //     — extra cash leaving the box's "book" balance, the difference
    //     was either lost or to be repaid by the requester (out of scope).
    //   * receiptTotal > disbursed: an Adjust CREDIT for (receiptTotal - disbursed)
    //     — the requester provably spent more, the box is informally short
    //     by the same amount and someone needs to reimburse.
    // The accounting team will refine this in a future sprint; the shape
    // is correct.
    // ---------------------------------------------------------------------
    public async Task<Voucher> ReconcileAsync(
        Guid voucherId,
        decimal receiptTotal,
        CancellationToken ct = default)
    {
        var (actor, tenantId, principal) = await CurrentActorAsync();
        if (actor is null) throw new InvalidOperationException("Cannot reconcile — no authenticated user.");

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId, ct)
            ?? throw new InvalidOperationException($"Voucher {voucherId} not found.");
        var box = await _db.Boxes.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == voucher.BoxId, ct)
            ?? throw new InvalidOperationException($"Box {voucher.BoxId} not found.");

        VoucherTransitions.EnsureAllowed(voucher.State, VoucherState.Reconcile);
        // Reconcile is open to either custodian or requester — they're
        // both involved in matching receipts. Custodian as the box-owner
        // makes the final call.
        if (actor != box.CustodianUserId && actor != voucher.RequestedByUserId)
        {
            throw new UnauthorizedVoucherActorException(actor.Value, "Reconcile", box.CustodianUserId);
        }

        if (receiptTotal < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(receiptTotal), receiptTotal,
                "Receipt total must be non-negative.");
        }

        var disbursedAmount = voucher.DisbursedAmount
            ?? throw new InvalidOperationException("Voucher has no disbursed amount; cannot reconcile.");
        var nativeCurrency = voucher.DisbursedCurrency
            ?? voucher.RequestedCurrency;

        var now = DateTimeOffset.UtcNow;
        var periodCheck = await _periodLock.EnsureCanPostAsync(tenantId, now, principal, ct);

        voucher.State = VoucherState.Reconcile;
        voucher.ReconciledAt = now;

        LedgerEvent? adjust = null;
        var diff = receiptTotal - disbursedAmount;
        if (diff != 0m)
        {
            var native = new Money(Math.Abs(diff), nativeCurrency);
            var baseCurrency = await _baseCurrency.GetBaseCurrencyAsync(tenantId, ct);
            var (baseAmount, fxRate, fxRateDate) = await ConvertAsync(native, baseCurrency, now.UtcDateTime, ct);

            adjust = new LedgerEvent
            {
                BoxId = box.Id,
                VoucherId = voucher.Id,
                EventType = LedgerEventType.Adjust,
                // diff > 0 means the requester proved they spent more than
                // we disbursed — the box informally owes them, model as a
                // Credit (cash conceptually owed back into the box ledger).
                // diff < 0 means cash short; model as Debit.
                Direction = diff > 0m ? LedgerDirection.Credit : LedgerDirection.Debit,
                AmountNative = native.Amount,
                CurrencyNative = native.CurrencyCode,
                AmountBase = baseAmount.Amount,
                CurrencyBase = baseAmount.CurrencyCode,
                FxRate = fxRate,
                FxRateDate = fxRateDate,
                PostedAt = now,
                PostedByUserId = actor.Value,
                TenantId = tenantId
            };
            _db.LedgerEvents.Add(adjust);
        }

        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, "nickfinance.voucher.reconciled",
            "PettyCashVoucher", voucher.Id.ToString(),
            new
            {
                voucher.Id,
                voucher.BoxId,
                ReceiptTotal = receiptTotal,
                Disbursed = disbursedAmount,
                AdjustmentLedgerEventId = adjust?.Id,
                ActorUserId = actor
            }, ct);

        if (periodCheck.IsLatePost && adjust is not null)
        {
            await EmitAsync(tenantId, actor, "nickfinance.petty_cash.late_post",
                "PettyCashLedgerEvent", adjust.Id.ToString(),
                new
                {
                    LedgerEventId = adjust.Id,
                    periodCheck.PeriodYearMonth,
                    VoucherId = voucher.Id,
                    ActorUserId = actor
                }, ct);
        }

        return voucher;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// Convert a Money value to the tenant base currency at a specific
    /// effective date. Returns the converted value plus the FX rate +
    /// the rate's effective date for storage on the ledger event.
    /// Throws <see cref="FxRateNotPublishedException"/> if no rate is
    /// available.
    /// </summary>
    private async Task<(Money Base, decimal FxRate, DateTime FxRateDate)> ConvertAsync(
        Money native,
        string baseCurrency,
        DateTime effectiveDate,
        CancellationToken ct)
    {
        var rate = await _fx.ResolveAsync(native.CurrencyCode, baseCurrency, effectiveDate, ct)
            ?? throw new FxRateNotPublishedException(native.CurrencyCode, baseCurrency, effectiveDate);

        // Multiplication is currency-aware via the operator; we stamp the
        // result's currency to the target so the result reads naturally.
        var rawConverted = native * rate.Rate;
        var asBase = new Money(rawConverted.Amount, baseCurrency);
        return (asBase, rate.Rate, rate.EffectiveDate);
    }

    private async Task<(Guid? UserId, long TenantId, ClaimsPrincipal? Principal)> CurrentActorAsync()
    {
        Guid? id = null;
        ClaimsPrincipal? principal = null;
        try
        {
            var state = await _auth.GetAuthenticationStateAsync();
            principal = state.User;
            var idClaim = principal?.FindFirst(NickErpClaims.Id)?.Value;
            if (Guid.TryParse(idClaim, out var g)) id = g;
        }
        catch (InvalidOperationException)
        {
            // Outside a Razor scope (background job / endpoint test
            // harness): no principal, fall through with null actor.
            id = null;
            principal = null;
        }

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved. Verify NickErpTenancy middleware ran for this request "
                + "(it must follow UseAuthentication/UseAuthorization in Program.cs) and that the "
                + "principal carries a valid 'nickerp:tenant_id' claim.");
        }
        if (_tenant.TenantId <= 0)
        {
            throw new InvalidOperationException(
                $"NickFinance workflow service requires a real tenant id (got {_tenant.TenantId}).");
        }
        return (id, _tenant.TenantId, principal);
    }

    private async Task EmitAsync(
        long tenantId, Guid? actor,
        string eventType, string entityType, string entityId, object payload,
        CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(tenantId, eventType, entityType, entityId, DateTimeOffset.UtcNow);
            var evt = DomainEvent.Create(tenantId, actor, correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType, entityType, entityId, json, key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Audit emission must not break user-facing workflows.
            _logger.LogWarning(ex,
                "NickFinance: failed to emit DomainEvent {EventType} for {EntityType} {EntityId}",
                eventType, entityType, entityId);
        }
    }
}
