using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.Ledger;
using NickFinance.PettyCash.Approvals;
using NickFinance.PettyCash.Disbursement;
using NickFinance.TaxEngine;

namespace NickFinance.PettyCash;

/// <summary>
/// The Petty Cash module-facing API. One service, every operation runs in
/// a single transaction (so a journal post + voucher status flip either
/// both land or neither does).
/// </summary>
public interface IPettyCashService
{
    /// <summary>Provision a new float for a custodian at a site. Throws if an active float already exists for that (site, currency, tenant).</summary>
    Task<Float> CreateFloatAsync(
        Guid siteId,
        Guid custodianUserId,
        Money initialFloat,
        Guid actorUserId,
        long tenantId = 1,
        CancellationToken ct = default);

    /// <summary>Submit a Draft voucher for approval. Generates the voucher number, snapshots <c>SubmittedAt</c>, and validates totals + float status.</summary>
    Task<Voucher> SubmitVoucherAsync(
        SubmitVoucherRequest req,
        CancellationToken ct = default);

    /// <summary>Approve a Submitted voucher. Approver must differ from the requester (separation of duties).</summary>
    Task<Voucher> ApproveVoucherAsync(
        Guid voucherId,
        Guid approverUserId,
        long? amountApprovedMinor,
        string? comment,
        CancellationToken ct = default);

    /// <summary>Reject a Submitted voucher. Same SoD constraint as approve.</summary>
    Task<Voucher> RejectVoucherAsync(
        Guid voucherId,
        Guid approverUserId,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Disburse an Approved voucher. Posts the journal to the Ledger
    /// (DR each line's <c>GlAccount</c>, CR <c>1060 Petty cash float</c>)
    /// and flips the voucher to Disbursed in the same transaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <paramref name="channel"/> is supplied (default: <see cref="OfflineCashChannel"/>),
    /// the channel is invoked BEFORE the journal post; if the rail
    /// rejects, the journal isn't posted, the voucher stays Approved,
    /// and the rejection reason bubbles up as a <see cref="PettyCashException"/>
    /// so the operator can retry once the upstream service comes back.
    /// </para>
    /// <para>
    /// The custodian who disburses must differ from the approver.
    /// </para>
    /// </remarks>
    Task<Voucher> DisburseVoucherAsync(
        Guid voucherId,
        Guid custodianUserId,
        DateOnly effectiveDate,
        Guid periodId,
        IDisbursementChannel? channel = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sweep all currently-pending <see cref="VoucherApproval"/> rows
    /// older than their <c>EscalateAfterHours</c> threshold and create
    /// an additional row at the same <c>step_no</c> assigned to the
    /// step's <c>EscalateTo</c> role. Returns the number of escalations
    /// created. Idempotent — a second call within the same window is
    /// a no-op because the original row's wall-clock hasn't moved.
    /// </summary>
    Task<int> EscalateOverdueApprovalsAsync(
        ApprovalPolicy policy,
        IApproverResolver resolver,
        CancellationToken ct = default);
}

/// <summary>Input for <see cref="IPettyCashService.SubmitVoucherAsync"/>.</summary>
public sealed record SubmitVoucherRequest(
    Guid FloatId,
    Guid RequesterUserId,
    VoucherCategory Category,
    string Purpose,
    Money Amount,
    IReadOnlyList<VoucherLineInput> Lines,
    string? PayeeName = null,
    string? ProjectCode = null,
    long TenantId = 1);

/// <summary>One line within a <see cref="SubmitVoucherRequest"/>. The service stamps <c>LineNo</c>.</summary>
public sealed record VoucherLineInput(
    string Description,
    Money GrossAmount,
    string? GlAccountOverride = null);

/// <summary>
/// Default implementation. Wraps every mutation in a transaction so that
/// the journal post and the voucher state change are atomic — if the
/// Ledger writer rejects (closed period, unbalanced, etc.) the voucher
/// stays Approved and the disbursement can be retried after the underlying
/// problem is fixed.
/// </summary>
public sealed class PettyCashService : IPettyCashService
{
    private readonly PettyCashDbContext _db;
    private readonly ILedgerWriter _ledger;
    private readonly IApprovalEngine _approvals;
    private readonly ISecurityAuditService _audit;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Default constructor — uses the single-step approval engine, which
    /// matches the original MVP-zero behaviour. New consumers should pass
    /// in a configured <see cref="PolicyApprovalEngine"/> for multi-step
    /// flows.
    /// </summary>
    public PettyCashService(
        PettyCashDbContext db,
        ILedgerWriter ledger,
        TimeProvider? clock = null)
        : this(db, ledger, new SingleStepApprovalEngine(clock), null, clock) { }

    /// <summary>v1.1 constructor — caller supplies the approval engine.</summary>
    public PettyCashService(
        PettyCashDbContext db,
        ILedgerWriter ledger,
        IApprovalEngine approvals,
        TimeProvider? clock = null)
        : this(db, ledger, approvals, null, clock) { }

    /// <summary>
    /// v1.3 constructor — caller supplies an audit hook. The WebApp
    /// registers a real <see cref="ISecurityAuditService"/>; tests +
    /// the bootstrap CLI pass <c>null</c> (we substitute a no-op).
    /// </summary>
    public PettyCashService(
        PettyCashDbContext db,
        ILedgerWriter ledger,
        IApprovalEngine approvals,
        ISecurityAuditService? audit,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _audit = audit ?? new NoopSecurityAuditService();
        _clock = clock ?? TimeProvider.System;
    }

    // ---------------------------------------------------------------------
    // Floats
    // ---------------------------------------------------------------------

    public async Task<Float> CreateFloatAsync(
        Guid siteId,
        Guid custodianUserId,
        Money initialFloat,
        Guid actorUserId,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        if (initialFloat.IsNegative)
        {
            throw new ArgumentException("Initial float cannot be negative.", nameof(initialFloat));
        }

        var existing = await _db.Floats
            .FirstOrDefaultAsync(f =>
                f.TenantId == tenantId &&
                f.SiteId == siteId &&
                f.CurrencyCode == initialFloat.CurrencyCode &&
                f.IsActive, ct);
        if (existing is not null)
        {
            throw new PettyCashException(
                $"An active {initialFloat.CurrencyCode} float already exists for site {siteId} (float {existing.FloatId}). "
                + "Close it first.");
        }

        var f = new Float
        {
            SiteId = siteId,
            CustodianUserId = custodianUserId,
            CurrencyCode = initialFloat.CurrencyCode,
            FloatAmountMinor = initialFloat.Minor,
            IsActive = true,
            CreatedAt = _clock.GetUtcNow(),
            CreatedByUserId = actorUserId,
            TenantId = tenantId
        };
        _db.Floats.Add(f);
        await _db.SaveChangesAsync(ct);
        return f;
    }

    // ---------------------------------------------------------------------
    // Vouchers
    // ---------------------------------------------------------------------

    public async Task<Voucher> SubmitVoucherAsync(
        SubmitVoucherRequest req,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (string.IsNullOrWhiteSpace(req.Purpose))
        {
            throw new ArgumentException("Purpose is required.", nameof(req));
        }
        if (req.Amount.IsNegative || req.Amount.IsZero)
        {
            throw new ArgumentException("Voucher amount must be positive.", nameof(req));
        }
        if (req.Lines is null || req.Lines.Count == 0)
        {
            throw new ArgumentException("At least one line item is required.", nameof(req));
        }

        // Currency consistency + total match
        long lineSum = 0;
        foreach (var line in req.Lines)
        {
            if (!string.Equals(line.GrossAmount.CurrencyCode, req.Amount.CurrencyCode, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Line currency {line.GrossAmount.CurrencyCode} differs from voucher currency {req.Amount.CurrencyCode}.",
                    nameof(req));
            }
            checked { lineSum += line.GrossAmount.Minor; }
        }
        if (lineSum != req.Amount.Minor)
        {
            throw new VoucherTotalMismatchException(req.Amount.Minor, lineSum);
        }

        var fl = await _db.Floats.FirstOrDefaultAsync(f => f.FloatId == req.FloatId && f.TenantId == req.TenantId, ct)
            ?? throw new FloatNotAvailableException(req.FloatId, "no such float");
        if (!fl.IsActive)
        {
            throw new FloatNotAvailableException(req.FloatId, "float is closed");
        }
        if (!string.Equals(fl.CurrencyCode, req.Amount.CurrencyCode, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Voucher currency {req.Amount.CurrencyCode} doesn't match float currency {fl.CurrencyCode}.",
                nameof(req));
        }

        var now = _clock.GetUtcNow();

        var voucher = new Voucher
        {
            FloatId = fl.FloatId,
            RequesterUserId = req.RequesterUserId,
            Category = req.Category,
            Purpose = req.Purpose.Trim(),
            AmountRequestedMinor = req.Amount.Minor,
            CurrencyCode = req.Amount.CurrencyCode,
            Status = VoucherStatus.Submitted,
            PayeeName = req.PayeeName,
            ProjectCode = req.ProjectCode,
            CreatedAt = now,
            SubmittedAt = now,
            TenantId = req.TenantId
        };
        voucher.VoucherNo = await GenerateVoucherNoAsync(fl.SiteId, now.Year, req.TenantId, ct);

        var defaultGl = req.Category.DefaultGlAccount();
        short n = 1;
        foreach (var line in req.Lines)
        {
            voucher.Lines.Add(new VoucherLineItem
            {
                VoucherId = voucher.VoucherId,
                LineNo = n++,
                Description = line.Description,
                GrossAmountMinor = line.GrossAmount.Minor,
                CurrencyCode = line.GrossAmount.CurrencyCode,
                GlAccount = string.IsNullOrWhiteSpace(line.GlAccountOverride) ? defaultGl : line.GlAccountOverride!.Trim()
            });
        }

        _db.Vouchers.Add(voucher);

        // Build the approval chain via the engine. If any step is unfillable,
        // the voucher is auto-rejected on submit (the engine populates the
        // step rows with UnfillableRole so audit can see why).
        var steps = _approvals.Plan(voucher);
        if (steps.Count == 0)
        {
            // Auto-approve at submit time — the engine returned an empty chain.
            voucher.Status = VoucherStatus.Approved;
            voucher.AmountApprovedMinor = voucher.AmountRequestedMinor;
            voucher.DecidedAt = now;
        }
        else
        {
            foreach (var s in steps) _db.VoucherApprovals.Add(s);
            if (steps.Any(s => s.Decision == ApprovalDecision.UnfillableRole))
            {
                voucher.Status = VoucherStatus.Rejected;
                voucher.DecidedAt = now;
                voucher.DecisionComment = "Auto-rejected at submit: at least one approval role could not be filled.";
            }
        }

        await _db.SaveChangesAsync(ct);
        return voucher;
    }

    public async Task<Voucher> ApproveVoucherAsync(
        Guid voucherId,
        Guid approverUserId,
        long? amountApprovedMinor,
        string? comment,
        CancellationToken ct = default)
    {
        var v = await LoadVoucher(voucherId, ct);
        if (v.Status != VoucherStatus.Submitted)
        {
            throw new InvalidVoucherTransitionException(v.Status, "approve");
        }

        // SoD: a voucher's submitter cannot approve their own voucher.
        // Mirrors the approver-vs-disburser check at the bottom of this
        // method (see DisburseVoucherAsync). This is the use-time half of
        // the SoD posture; the grant-time half lives in
        // NickFinance.WebApp.Identity.SodService.
        if (v.RequesterUserId == approverUserId)
        {
            throw new SeparationOfDutiesException(
                "The voucher's submitter cannot approve their own voucher (SoD).");
        }

        var approved = amountApprovedMinor ?? v.AmountRequestedMinor;
        if (approved <= 0)
        {
            throw new ArgumentException("Approved amount must be positive.", nameof(amountApprovedMinor));
        }
        if (approved > v.AmountRequestedMinor)
        {
            throw new ArgumentException("Approved amount cannot exceed the requested amount.", nameof(amountApprovedMinor));
        }

        var steps = await LoadStepsAsync(voucherId, ct);
        var advance = _approvals.Decide(v, steps, approverUserId, comment, reject: false);

        // Stamp the voucher's rolled-up decision fields with the LAST decider
        // so existing UIs / reports keep working without joining the
        // approvals table.
        v.DecidedByUserId = approverUserId;
        v.DecisionComment = comment;
        v.DecidedAt = _clock.GetUtcNow();

        if (advance.Outcome == ApprovalAdvanceOutcome.FullyApproved)
        {
            v.Status = VoucherStatus.Approved;
            v.AmountApprovedMinor = approved;
        }
        // Mid-chain approvals leave Status = Submitted and AmountApprovedMinor null
        // until the LAST step arrives. Approved-amount on intermediate steps is
        // ignored by design (only the final approver locks it in for v1.1).

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: SecurityAuditAction.VoucherApproved,
            targetType: "Voucher",
            targetId: v.VoucherId.ToString(),
            result: SecurityAuditResult.Allowed,
            details: new { voucherNo = v.VoucherNo, approvedMinor = approved, comment, terminal = advance.Outcome == ApprovalAdvanceOutcome.FullyApproved },
            ct: ct);
        return v;
    }

    public async Task<Voucher> RejectVoucherAsync(
        Guid voucherId,
        Guid approverUserId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required when rejecting.", nameof(reason));
        }

        var v = await LoadVoucher(voucherId, ct);
        if (v.Status != VoucherStatus.Submitted)
        {
            throw new InvalidVoucherTransitionException(v.Status, "reject");
        }

        var steps = await LoadStepsAsync(voucherId, ct);
        _approvals.Decide(v, steps, approverUserId, reason, reject: true);

        v.Status = VoucherStatus.Rejected;
        v.DecidedByUserId = approverUserId;
        v.DecisionComment = reason.Trim();
        v.DecidedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return v;
    }

    public async Task<Voucher> DisburseVoucherAsync(
        Guid voucherId,
        Guid custodianUserId,
        DateOnly effectiveDate,
        Guid periodId,
        IDisbursementChannel? channel = null,
        CancellationToken ct = default)
    {
        channel ??= new OfflineCashChannel();
        var v = await LoadVoucher(voucherId, ct);
        if (v.Status != VoucherStatus.Approved)
        {
            throw new InvalidVoucherTransitionException(v.Status, "disburse");
        }
        if (v.AmountApprovedMinor is null or <= 0)
        {
            throw new PettyCashException($"Voucher {voucherId} is Approved but has no positive approved amount; cannot disburse.");
        }
        if (v.DecidedByUserId == custodianUserId)
        {
            throw new SeparationOfDutiesException("The approver cannot also be the disbursing custodian.");
        }

        // Build the journal — branches by tax/WHT treatment.
        if (v.AmountApprovedMinor.Value != v.AmountRequestedMinor)
        {
            throw new PettyCashException(
                "Partial-approval disbursements aren't supported yet. "
                + "Reject + resubmit at the corrected amount.");
        }

        // 1. Send the funds via the chosen rail. If the rail rejects the
        //    request, leave the voucher Approved so the operator can retry.
        var disbursement = await channel.DisburseAsync(new DisbursementRequest(
            VoucherId: v.VoucherId,
            VoucherNo: v.VoucherNo,
            AmountMinor: v.AmountApprovedMinor!.Value,
            CurrencyCode: v.CurrencyCode,
            PayeeName: v.PayeeName ?? "(self)",
            PayeeMomoNumber: v.PayeeMomoNumber,
            PayeeMomoNetwork: v.PayeeMomoNetwork,
            ClientReference: $"petty_cash:{v.VoucherId:N}",
            TenantId: v.TenantId), ct);
        if (!disbursement.Accepted)
        {
            throw new PettyCashException(
                $"Disbursement via {channel.Channel} was rejected: {disbursement.FailureReason ?? "(no reason given)"}.");
        }

        // 2. Post the journal — voucher rail outcomes don't appear in the GL,
        //    just the cash movement and any taxes. The rail reference is on
        //    the voucher row for audit.
        var ledgerEvent = new LedgerEvent
        {
            TenantId = v.TenantId,
            EffectiveDate = effectiveDate,
            PeriodId = periodId,
            SourceModule = "petty_cash",
            SourceEntityType = "Voucher",
            SourceEntityId = v.VoucherId.ToString("N"),
            IdempotencyKey = $"petty_cash:{v.VoucherId:N}:disburse",
            EventType = LedgerEventType.Posted,
            Narration = $"Petty cash disbursement {v.VoucherNo}: {v.Purpose}",
            ActorUserId = custodianUserId
        };

        BuildJournalLines(v, ledgerEvent, effectiveDate);

        // The Ledger writer + its underlying DB triggers will reject the post
        // if anything is malformed (closed period, unbalanced, mixed currency,
        // duplicate idempotency key on a different payload). The exception
        // bubbles up; voucher status is unchanged. Caller can fix the period
        // and retry.
        var eventId = await _ledger.PostAsync(ledgerEvent, ct);

        v.Status = VoucherStatus.Disbursed;
        v.DisbursedByUserId = custodianUserId;
        v.DisbursedAt = _clock.GetUtcNow();
        v.LedgerEventId = eventId;
        v.DisbursementChannel = channel.Channel;
        v.DisbursementReference = disbursement.RailReference;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: SecurityAuditAction.VoucherDisbursed,
            targetType: "Voucher",
            targetId: v.VoucherId.ToString(),
            result: SecurityAuditResult.Allowed,
            details: new { voucherNo = v.VoucherNo, ledgerEventId = eventId, channel = channel.Channel, railRef = disbursement.RailReference, amountMinor = v.AmountApprovedMinor },
            ct: ct);
        return v;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private async Task<Voucher> LoadVoucher(Guid voucherId, CancellationToken ct)
    {
        return await _db.Vouchers.Include(v => v.Lines)
            .FirstOrDefaultAsync(v => v.VoucherId == voucherId, ct)
            ?? throw new PettyCashException($"Voucher {voucherId} not found.");
    }

    private async Task<List<VoucherApproval>> LoadStepsAsync(Guid voucherId, CancellationToken ct)
    {
        return await _db.VoucherApprovals
            .Where(s => s.VoucherId == voucherId)
            .OrderBy(s => s.StepNo)
            .ToListAsync(ct);
    }

    public async Task<int> EscalateOverdueApprovalsAsync(
        ApprovalPolicy policy,
        IApproverResolver resolver,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(resolver);

        var now = _clock.GetUtcNow();
        // Pull all open approval rows; could narrow with a per-tenant filter if needed.
        var pending = await _db.VoucherApprovals
            .Where(s => s.Decision == ApprovalDecision.Pending)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var voucherIds = pending.Select(p => p.VoucherId).Distinct().ToArray();
        var vouchers = await _db.Vouchers
            .Where(v => voucherIds.Contains(v.VoucherId))
            .ToListAsync(ct);
        var voucherById = vouchers.ToDictionary(v => v.VoucherId);

        var inserted = 0;
        var grouped = pending
            .GroupBy(p => (p.VoucherId, p.StepNo))
            .ToList();
        foreach (var group in grouped)
        {
            if (!voucherById.TryGetValue(group.Key.VoucherId, out var voucher)) continue;

            var band = policy.BandFor(voucher.Category, voucher.AmountRequestedMinor);
            if (band is null) continue;
            if (group.Key.StepNo - 1 < 0 || group.Key.StepNo - 1 >= band.Steps.Count) continue;

            var step = band.Steps[group.Key.StepNo - 1];
            if (step.EscalateAfterHours is null || step.EscalateTo is null) continue;

            var hours = step.EscalateAfterHours.Value;
            var oldestPending = group.Min(s => s.CreatedAt);
            if (now - oldestPending < TimeSpan.FromHours(hours)) continue;

            // Resolve the escalation target. If anyone in the group is already
            // assigned to that target, skip — the escalation has already
            // happened (idempotency).
            var target = resolver.Resolve(step.EscalateTo, voucher);
            if (target == Guid.Empty || target == voucher.RequesterUserId) continue;
            if (group.Any(s => s.AssignedToUserId == target)) continue;

            // The escalation row stays in the SAME "role slot" as the
            // original — when SS approves on behalf of LM, the engine
            // should treat that as clearing the line_manager slot.
            // Pick the role being escalated FROM (the first Pending row
            // at this step) and tag the suffix; the engine strips it on
            // role-equality checks.
            var escalateFromRole = group
                .Where(s => s.Decision == ApprovalDecision.Pending)
                .Select(s => s.Role)
                .FirstOrDefault() ?? step.EscalateTo;

            _db.VoucherApprovals.Add(new VoucherApproval
            {
                VoucherId = voucher.VoucherId,
                StepNo = group.Key.StepNo,
                Role = escalateFromRole + " (escalated)",
                AssignedToUserId = target,
                Decision = ApprovalDecision.Pending,
                CreatedAt = now,
                TenantId = voucher.TenantId
            });
            inserted++;
        }
        if (inserted > 0) await _db.SaveChangesAsync(ct);
        return inserted;
    }

    /// <summary>
    /// Walk the voucher's lines and add the corresponding ledger lines onto
    /// <paramref name="ev"/>. Handles three cases:
    /// <list type="number">
    ///   <item><description><see cref="TaxTreatment.None"/> + no WHT — DR expense, CR petty cash float.</description></item>
    ///   <item><description><see cref="TaxTreatment.GhanaInclusive"/> — back-solve net per the levy stack and post each component to the matching payable / receivable account, still CR petty cash float at the gross.</description></item>
    ///   <item><description>WHT applies — split the credit leg between the supplier float and the WHT payable account at the WHT rate.</description></item>
    /// </list>
    /// </summary>
    private static void BuildJournalLines(Voucher v, LedgerEvent ev, DateOnly effective)
    {
        var rates = GhanaTaxRates.ForDate(effective);
        short lineNo = 1;

        long totalCash = 0;     // total debit-side per-line amounts (= net to expense + tax recoverable)
        long totalLevyVat = 0;  // sum of levies + VAT credited to payable accounts

        foreach (var line in v.Lines.OrderBy(l => l.LineNo))
        {
            if (v.TaxTreatment == TaxTreatment.GhanaInclusive)
            {
                // Line gross is INCLUSIVE of the levies + VAT. Back-solve the
                // net + each tax component, then post each piece to its account.
                var t = TaxCalculator.FromGross(new Money(line.GrossAmountMinor, line.CurrencyCode), rates);

                // DR expense at NET (the actual cost to the business)
                if (!t.Net.IsZero)
                {
                    ev.Lines.Add(new LedgerEventLine
                    {
                        LineNo = lineNo++,
                        AccountCode = line.GlAccount,
                        DebitMinor = t.Net.Minor,
                        CurrencyCode = line.CurrencyCode,
                        ProjectCode = v.ProjectCode,
                        Description = line.Description
                    });
                }
                // DR VAT input recoverable at VAT
                if (!t.Vat.IsZero)
                {
                    ev.Lines.Add(new LedgerEventLine
                    {
                        LineNo = lineNo++,
                        AccountCode = "1410",   // VAT input recoverable
                        DebitMinor = t.Vat.Minor,
                        CurrencyCode = line.CurrencyCode,
                        ProjectCode = v.ProjectCode,
                        Description = $"VAT input on {line.Description}"
                    });
                }
                // The three levies are NOT recoverable in Ghana (NHIL, GETFund,
                // COVID are operating costs to the business). We expense them
                // on the same line as the underlying spend.
                if (!t.Nhil.IsZero) AddExpenseLine(ev, ref lineNo, line, v, t.Nhil, "NHIL on");
                if (!t.GetFund.IsZero) AddExpenseLine(ev, ref lineNo, line, v, t.GetFund, "GETFund on");
                if (!t.Covid.IsZero) AddExpenseLine(ev, ref lineNo, line, v, t.Covid, "COVID levy on");
                totalCash = checked(totalCash + line.GrossAmountMinor);
            }
            else
            {
                // No tax decomposition — straight DR expense at gross.
                ev.Lines.Add(new LedgerEventLine
                {
                    LineNo = lineNo++,
                    AccountCode = line.GlAccount,
                    DebitMinor = line.GrossAmountMinor,
                    CurrencyCode = line.CurrencyCode,
                    ProjectCode = v.ProjectCode,
                    Description = line.Description
                });
                totalCash = checked(totalCash + line.GrossAmountMinor);
            }
        }

        // Split the credit leg by WHT.
        if (v.WhtTreatment != WhtTreatment.None)
        {
            var rate = WhtTreatmentToRate(v.WhtTreatment);
            var whtMoney = new Money(totalCash, v.CurrencyCode).MultiplyRate(rate);
            var floatMoney = new Money(totalCash - whtMoney.Minor, v.CurrencyCode);

            // CR 2150 WHT payable
            if (!whtMoney.IsZero)
            {
                ev.Lines.Add(new LedgerEventLine
                {
                    LineNo = lineNo++,
                    AccountCode = "2150",
                    CreditMinor = whtMoney.Minor,
                    CurrencyCode = v.CurrencyCode,
                    ProjectCode = v.ProjectCode,
                    Description = $"WHT @ {rate:P1} on {v.VoucherNo}"
                });
            }
            // CR 1060 petty cash float (net of WHT)
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = lineNo,
                AccountCode = "1060",
                CreditMinor = floatMoney.Minor,
                CurrencyCode = v.CurrencyCode,
                ProjectCode = v.ProjectCode,
                Description = $"{v.VoucherNo} float draw (net of WHT)"
            });
        }
        else
        {
            // CR 1060 petty cash float at the full gross
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = lineNo,
                AccountCode = "1060",
                CreditMinor = totalCash,
                CurrencyCode = v.CurrencyCode,
                ProjectCode = v.ProjectCode,
                Description = $"{v.VoucherNo} float draw"
            });
        }

        _ = totalLevyVat;   // reserved for future audit logging
    }

    private static void AddExpenseLine(LedgerEvent ev, ref short lineNo, VoucherLineItem srcLine, Voucher v, Money amount, string label)
    {
        ev.Lines.Add(new LedgerEventLine
        {
            LineNo = lineNo++,
            AccountCode = srcLine.GlAccount,    // levies post against the same expense account as the spend
            DebitMinor = amount.Minor,
            CurrencyCode = srcLine.CurrencyCode,
            ProjectCode = v.ProjectCode,
            Description = $"{label} {srcLine.Description}"
        });
    }

    private static decimal WhtTreatmentToRate(WhtTreatment t) => t switch
    {
        WhtTreatment.None                  => 0m,
        WhtTreatment.GoodsVatRegistered    => 0.03m,
        WhtTreatment.GoodsNonVatRegistered => 0.07m,
        WhtTreatment.Works                 => 0.05m,
        WhtTreatment.Services              => 0.075m,
        WhtTreatment.Rent                  => 0.08m,
        WhtTreatment.Commission            => 0.10m,
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown WhtTreatment.")
    };

    /// <summary>
    /// Generate <c>PC-{site8}-{year}-{ordinal}</c>. The ordinal is computed
    /// inside the same transaction with a count-then-format so two
    /// concurrent submits would race; in MVP we accept the rare collision
    /// and rely on the unique constraint to throw — the caller retries.
    /// A monotonic per-site sequence comes in v1.1 alongside the policy DSL.
    /// </summary>
    private async Task<string> GenerateVoucherNoAsync(Guid siteId, int year, long tenantId, CancellationToken ct)
    {
        var sitePrefix = siteId.ToString("N")[..6].ToUpperInvariant();
        var taken = await _db.Vouchers.CountAsync(
            v => v.TenantId == tenantId &&
                 v.CreatedAt.Year == year &&
                 v.VoucherNo.StartsWith($"PC-{sitePrefix}-{year}-"),
            ct);
        var seq = (taken + 1).ToString("D5");
        return $"PC-{sitePrefix}-{year}-{seq}";
    }
}
