using Microsoft.EntityFrameworkCore;
using NickFinance.PettyCash.Receipts;

namespace NickFinance.PettyCash.Fraud;

/// <summary>
/// Eight detection rules over the petty-cash data. Cheap to run on
/// demand against a date range; meant to be triggered by an internal
/// auditor's UI page or a nightly job.
/// </summary>
/// <remarks>
/// Catalogue (per <c>PETTY_CASH.md §11</c>):
/// <list type="bullet">
///   <item><description><b>F1 Salami slicing</b> — same requester, same day, two or more vouchers each just under a known approval band.</description></item>
///   <item><description><b>F2 Ghost payee</b> — non-self, non-vendor payee with no prior history (one-shot beneficiaries are higher risk).</description></item>
///   <item><description><b>F3 Duplicate receipt</b> — receipt SHA-256 (or approximate hash) repeated across distinct vouchers.</description></item>
///   <item><description><b>F4 GPS mismatch</b> — receipt geo-tag too far from the voucher's site (no site coordinates in v1 — flagged where the receipt has GPS but the voucher has no project tying it to a site, marked low-severity placeholder).</description></item>
///   <item><description><b>F5 Benford anomaly</b> — leading-digit distribution of voucher amounts deviates from Benford by &gt; 30%.</description></item>
///   <item><description><b>F6 Round-number bias</b> — &gt; 60% of vouchers in the window are exact thousands.</description></item>
///   <item><description><b>F7 After-hours submit</b> — submission timestamp outside 06:00–20:00 UTC.</description></item>
///   <item><description><b>F8 Approver-requester proximity</b> — same approver clears &gt; 80% of one requester's vouchers in the window.</description></item>
/// </list>
/// </remarks>
public interface IFraudDetector
{
    Task<IReadOnlyList<FraudSignal>> ScanAsync(DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default);
}

public sealed record FraudSignal(
    string Code,
    FraudSeverity Severity,
    string Description,
    Guid? VoucherId = null,
    Guid? RequesterUserId = null,
    Guid? ApproverUserId = null,
    string? Evidence = null);

public enum FraudSeverity
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed class FraudDetector : IFraudDetector
{
    private readonly PettyCashDbContext _db;

    public FraudDetector(PettyCashDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<IReadOnlyList<FraudSignal>> ScanAsync(DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default)
    {
        if (to < from) throw new ArgumentException("`to` is before `from`.", nameof(to));

        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtcExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var vouchers = await _db.Vouchers
            .Where(v => v.TenantId == tenantId
                     && v.CreatedAt >= fromUtc
                     && v.CreatedAt < toUtcExclusive)
            .ToListAsync(ct);

        var receipts = await _db.VoucherReceipts
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(ct);

        var voucherIds = vouchers.Select(v => v.VoucherId).ToHashSet();
        var inWindowReceipts = receipts.Where(r => voucherIds.Contains(r.VoucherId)).ToList();

        var signals = new List<FraudSignal>();
        signals.AddRange(F1_Salami(vouchers));
        signals.AddRange(F2_GhostPayee(vouchers));
        signals.AddRange(F3_DuplicateReceipt(inWindowReceipts, receipts));
        signals.AddRange(F4_GpsMismatch(inWindowReceipts, vouchers));
        signals.AddRange(F5_Benford(vouchers));
        signals.AddRange(F6_RoundNumber(vouchers));
        signals.AddRange(F7_AfterHours(vouchers));
        signals.AddRange(F8_ApproverProximity(vouchers));
        return signals;
    }

    // -----------------------------------------------------------------
    // F1 Salami
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F1_Salami(List<Voucher> vouchers)
    {
        // Same requester, same calendar day, 3+ vouchers each within
        // 90% of the next-up approval-band amount (we don't have policy
        // here; use 20K and 100K pesewa as known band thresholds for v1).
        var bandThresholds = new long[] { 20_000, 100_000, 500_000 };
        var grouped = vouchers
            .GroupBy(v => (v.RequesterUserId, Day: DateOnly.FromDateTime(v.CreatedAt.UtcDateTime.Date)));
        foreach (var g in grouped)
        {
            if (g.Count() < 3) continue;
            var nearMisses = g.Where(v =>
                bandThresholds.Any(b => v.AmountRequestedMinor >= 9 * b / 10 && v.AmountRequestedMinor <= b)).ToList();
            if (nearMisses.Count >= 3)
            {
                yield return new FraudSignal(
                    Code: "F1_SALAMI",
                    Severity: FraudSeverity.High,
                    Description: $"Salami pattern: requester {g.Key.RequesterUserId} submitted {nearMisses.Count} vouchers each just under a band threshold on {g.Key.Day:yyyy-MM-dd}.",
                    RequesterUserId: g.Key.RequesterUserId,
                    Evidence: string.Join(",", nearMisses.Select(v => v.VoucherNo)));
            }
        }
    }

    // -----------------------------------------------------------------
    // F2 Ghost payee
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F2_GhostPayee(List<Voucher> vouchers)
    {
        // A "ghost payee" is one that appears once in the window with no other
        // voucher pointing to the same name. Real life would also cross-check
        // a vendor master — we don't have one in v1, so we just look for
        // single-shot non-empty payees.
        var byPayee = vouchers
            .Where(v => !string.IsNullOrWhiteSpace(v.PayeeName))
            .GroupBy(v => v.PayeeName!.Trim().ToUpperInvariant());
        foreach (var g in byPayee)
        {
            if (g.Count() != 1) continue;
            var v = g.First();
            // Only flag amounts above GHS 200 — small one-shots are noise.
            if (v.AmountRequestedMinor < 20_000) continue;
            yield return new FraudSignal(
                Code: "F2_GHOST_PAYEE",
                Severity: FraudSeverity.Medium,
                Description: $"Ghost payee: '{v.PayeeName}' appears once in the window for voucher {v.VoucherNo}.",
                VoucherId: v.VoucherId,
                RequesterUserId: v.RequesterUserId,
                Evidence: $"PayeeName='{v.PayeeName}', Amount={v.AmountRequestedMinor}");
        }
    }

    // -----------------------------------------------------------------
    // F3 Duplicate receipt
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F3_DuplicateReceipt(
        List<VoucherReceipt> windowReceipts,
        List<VoucherReceipt> allReceipts)
    {
        // Exact SHA-256 dup across vouchers — high severity.
        foreach (var g in windowReceipts.GroupBy(r => r.Sha256))
        {
            var bucket = allReceipts.Where(r => r.Sha256 == g.Key).ToList();
            var distinctVouchers = bucket.Select(r => r.VoucherId).Distinct().ToList();
            if (distinctVouchers.Count > 1)
            {
                yield return new FraudSignal(
                    Code: "F3_DUPLICATE_RECEIPT",
                    Severity: FraudSeverity.High,
                    Description: $"Identical receipt (SHA-256 {g.Key[..12]}…) attached to {distinctVouchers.Count} different vouchers.",
                    Evidence: string.Join(",", distinctVouchers.Select(id => id.ToString("N")[..8])));
            }
        }
        // Approximate dup — medium severity.
        foreach (var g in windowReceipts.GroupBy(r => r.ApproximateHash))
        {
            var bucket = allReceipts
                .Where(r => r.ApproximateHash == g.Key && r.Sha256 != g.First().Sha256)
                .ToList();
            var distinctVouchers = bucket.Select(r => r.VoucherId).Distinct().ToList();
            if (distinctVouchers.Count > 0)
            {
                yield return new FraudSignal(
                    Code: "F3_NEAR_DUPLICATE_RECEIPT",
                    Severity: FraudSeverity.Medium,
                    Description: $"Near-duplicate receipt (approximate hash {g.Key[..12]}…) seen across {distinctVouchers.Count + 1} vouchers.",
                    Evidence: string.Join(",", distinctVouchers.Select(id => id.ToString("N")[..8])));
            }
        }
    }

    // -----------------------------------------------------------------
    // F4 GPS mismatch (placeholder — needs site coordinates)
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F4_GpsMismatch(List<VoucherReceipt> windowReceipts, List<Voucher> vouchers)
    {
        // Without a site-coordinates table we can only flag receipts that
        // CARRY GPS where the voucher has no ProjectCode pointing to a
        // site. Low severity placeholder until the site registry lands.
        var voucherById = vouchers.ToDictionary(v => v.VoucherId);
        foreach (var r in windowReceipts.Where(r => r.GpsLatitude is not null && r.GpsLongitude is not null))
        {
            if (!voucherById.TryGetValue(r.VoucherId, out var v)) continue;
            if (string.IsNullOrWhiteSpace(v.ProjectCode))
            {
                yield return new FraudSignal(
                    Code: "F4_GPS_NO_SITE_CONTEXT",
                    Severity: FraudSeverity.Low,
                    Description: $"Receipt has GPS ({r.GpsLatitude}, {r.GpsLongitude}) but voucher {v.VoucherNo} has no project/site context for cross-check.",
                    VoucherId: v.VoucherId,
                    Evidence: $"lat={r.GpsLatitude}, lng={r.GpsLongitude}");
            }
        }
    }

    // -----------------------------------------------------------------
    // F5 Benford anomaly
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F5_Benford(List<Voucher> vouchers)
    {
        if (vouchers.Count < 30) yield break;   // small samples are too noisy

        var observed = new int[10];
        foreach (var v in vouchers)
        {
            var d = LeadingDigit(v.AmountRequestedMinor);
            if (d >= 1 && d <= 9) observed[d]++;
        }
        var total = observed.Sum();
        if (total < 30) yield break;
        // Expected per Benford
        var expected = new double[10];
        for (var d = 1; d <= 9; d++) expected[d] = total * Math.Log10(1 + 1.0 / d);
        // Chi-squared
        double chi = 0;
        for (var d = 1; d <= 9; d++)
        {
            if (expected[d] <= 0) continue;
            chi += Math.Pow(observed[d] - expected[d], 2) / expected[d];
        }
        // df=8; critical chi² @ 0.001 ≈ 26.12 ; @ 0.01 ≈ 20.09
        if (chi > 20)
        {
            yield return new FraudSignal(
                Code: "F5_BENFORD",
                Severity: chi > 26 ? FraudSeverity.High : FraudSeverity.Medium,
                Description: $"Voucher leading-digit distribution deviates strongly from Benford (chi²={chi:F1}, n={total}).",
                Evidence: string.Join(",", Enumerable.Range(1, 9).Select(d => $"{d}:{observed[d]}/{expected[d]:F1}")));
        }
    }

    private static int LeadingDigit(long amount)
    {
        var n = Math.Abs(amount);
        while (n >= 10) n /= 10;
        return (int)n;
    }

    // -----------------------------------------------------------------
    // F6 Round-number bias
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F6_RoundNumber(List<Voucher> vouchers)
    {
        if (vouchers.Count < 10) yield break;
        var rounds = vouchers.Count(v => v.AmountRequestedMinor % 100_000 == 0);   // exact multiples of GHS 1,000
        var pct = (double)rounds / vouchers.Count;
        if (pct > 0.6)
        {
            yield return new FraudSignal(
                Code: "F6_ROUND_NUMBER_BIAS",
                Severity: FraudSeverity.Medium,
                Description: $"{pct:P0} of vouchers in the window are exact multiples of GHS 1,000 (n={vouchers.Count}).",
                Evidence: $"rounds={rounds}, total={vouchers.Count}");
        }
    }

    // -----------------------------------------------------------------
    // F7 After-hours
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F7_AfterHours(List<Voucher> vouchers)
    {
        foreach (var v in vouchers.Where(v => v.SubmittedAt is not null))
        {
            var hour = v.SubmittedAt!.Value.UtcDateTime.Hour;
            if (hour < 6 || hour >= 20)
            {
                yield return new FraudSignal(
                    Code: "F7_AFTER_HOURS_SUBMIT",
                    Severity: FraudSeverity.Low,
                    Description: $"Voucher {v.VoucherNo} submitted at {v.SubmittedAt:HH:mm} UTC (outside business hours).",
                    VoucherId: v.VoucherId,
                    RequesterUserId: v.RequesterUserId);
            }
        }
    }

    // -----------------------------------------------------------------
    // F8 Approver-requester proximity
    // -----------------------------------------------------------------

    private static IEnumerable<FraudSignal> F8_ApproverProximity(List<Voucher> vouchers)
    {
        var decided = vouchers.Where(v => v.DecidedByUserId is not null).ToList();
        if (decided.Count < 5) yield break;

        var byRequester = decided.GroupBy(v => v.RequesterUserId);
        foreach (var g in byRequester)
        {
            var total = g.Count();
            if (total < 5) continue;
            var byApprover = g.GroupBy(v => v.DecidedByUserId!.Value);
            var top = byApprover.OrderByDescending(a => a.Count()).First();
            var pct = (double)top.Count() / total;
            if (pct > 0.8)
            {
                yield return new FraudSignal(
                    Code: "F8_APPROVER_PROXIMITY",
                    Severity: FraudSeverity.Medium,
                    Description: $"Requester {g.Key} had {pct:P0} of their {total} decisions made by approver {top.Key}.",
                    RequesterUserId: g.Key,
                    ApproverUserId: top.Key);
            }
        }
    }
}
