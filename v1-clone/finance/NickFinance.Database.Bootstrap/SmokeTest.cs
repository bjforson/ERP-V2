using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.Ledger;
using NickFinance.PettyCash;

namespace NickFinance.Database.Bootstrap;

/// <summary>
/// End-to-end smoke runner. Exercises the same code path the WebApp uses
/// (CreateFloat → Submit → Approve → Disburse → ledger post) so a single
/// CLI invocation proves a deploy is healthy.
///
/// Deterministic identifiers — the float, custodian, and approver GUIDs
/// are all derived from a SHA-256 of the literal string they encode, so
/// rerunning the smoke test reuses the same float and never tries to
/// provision a duplicate. Each run still creates a fresh voucher (the
/// number is timestamped) so the audit trail shows one row per smoke
/// invocation, all clearly tagged "SMOKE TEST" in the purpose column.
/// </summary>
public static class SmokeTest
{
    /// <summary>
    /// Sentinel tenant for smoke runs. All real tenants are positive integers
    /// counting up from 1; production reports filter on the actual tenant ids.
    /// Smoke rows live under this number so they're invisible to operators
    /// and trivial to clean up (`DELETE ... WHERE tenant_id = 999_999`).
    /// </summary>
    public const long SmokeTenantId = 999_999L;

    public static async Task<int> RunAsync(string conn)
    {
        Console.WriteLine();
        Console.WriteLine("Smoke test — Petty Cash full chain (CreateFloat → Submit → Approve → Disburse)");
        Console.WriteLine("---------------------------------------------------------------------------");

        var ledgerOpts = new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(conn).Options;
        var pcOpts = new DbContextOptionsBuilder<PettyCashDbContext>().UseNpgsql(conn).Options;
        var idOpts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;

        // Stable identities — same on every run.
        var siteId = StableGuid("nickerp-site:tema-smoke");
        var requester = StableGuid("nickerp-user:smoke-requester");
        var approver = StableGuid("nickerp-user:smoke-approver");
        var custodian = StableGuid("nickerp-user:smoke-custodian");

        // 0. Ensure the smoke user rows exist in identity.users so the audit
        //    columns reference real rows. Idempotent — uses InternalUserId
        //    as the upsert key so a re-run is a no-op.
        await using (var id = new IdentityDbContext(idOpts))
        {
            await EnsureSmokeUserAsync(id, requester, "smoke-requester@nickscan.local", "Smoke Requester");
            await EnsureSmokeUserAsync(id, approver, "smoke-approver@nickscan.local", "Smoke Approver");
            await EnsureSmokeUserAsync(id, custodian, "smoke-custodian@nickscan.local", "Smoke Custodian");
            Console.WriteLine($"  [0/5] Identity smoke users ensured (requester / approver / custodian).");
        }

        // 1. Period for the current month (idempotent — PeriodService creates if absent).
        var nowUtc = DateTimeOffset.UtcNow;
        var fiscalYear = nowUtc.Year;
        var monthNo = (byte)nowUtc.Month;
        await using (var lg = new LedgerDbContext(ledgerOpts))
        {
            var existingPeriod = await lg.Periods
                .Where(p => p.TenantId == SmokeTenantId && p.FiscalYear == fiscalYear && p.MonthNumber == monthNo)
                .Select(p => new { p.PeriodId })
                .FirstOrDefaultAsync();
            if (existingPeriod is null)
            {
                Console.WriteLine($"  [1/5] Creating ledger period {fiscalYear}-{monthNo:D2} (tenant {SmokeTenantId})...");
                await new PeriodService(lg).CreateAsync(fiscalYear, monthNo, tenantId: SmokeTenantId);
            }
            else
            {
                Console.WriteLine($"  [1/5] Period {fiscalYear}-{monthNo:D2} already exists ({existingPeriod.PeriodId}).");
            }
        }

        Float fl;
        await using (var pc = new PettyCashDbContext(pcOpts))
        await using (var lg = new LedgerDbContext(ledgerOpts))
        {
            // 2. Float — reuse if present, create otherwise.
            var svc = new PettyCashService(pc, new LedgerWriter(lg));
            var existing = await pc.Floats
                .FirstOrDefaultAsync(f => f.TenantId == SmokeTenantId && f.SiteId == siteId && f.CurrencyCode == "GHS" && f.IsActive);
            if (existing is not null)
            {
                fl = existing;
                Console.WriteLine($"  [2/5] Reusing smoke float {fl.FloatId} on site {siteId}.");
            }
            else
            {
                Console.WriteLine($"  [2/5] Provisioning smoke float on site {siteId}...");
                fl = await svc.CreateFloatAsync(siteId, custodian, new Money(100_00, "GHS"), actorUserId: requester, tenantId: SmokeTenantId);
                Console.WriteLine($"        Created float {fl.FloatId} (initial GHS 100.00).");
            }
        }

        Voucher disbursed;
        Guid postedEventId;
        await using (var pc = new PettyCashDbContext(pcOpts))
        await using (var lg = new LedgerDbContext(ledgerOpts))
        {
            var svc = new PettyCashService(pc, new LedgerWriter(lg));

            // 3. Submit a small (GHS 1.00) smoke voucher with a clearly-marked purpose.
            var stamp = DateTimeOffset.UtcNow.ToString("u");
            var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
                FloatId: fl.FloatId,
                RequesterUserId: requester,
                Category: VoucherCategory.Transport,
                Purpose: $"SMOKE TEST {stamp} — deploy verification, safe to ignore",
                Amount: new Money(1_00, "GHS"),
                Lines: new[] { new VoucherLineInput("Smoke verification line", new Money(1_00, "GHS")) },
                PayeeName: "smoke-runner",
                TenantId: SmokeTenantId));
            Console.WriteLine($"  [3/5] Submitted voucher {v.VoucherNo} ({v.VoucherId}).");

            // 4. Approve.
            var approved = await svc.ApproveVoucherAsync(v.VoucherId, approver, amountApprovedMinor: null, comment: "smoke-approve");
            if (approved.Status != VoucherStatus.Approved)
            {
                throw new InvalidOperationException($"Approve did not transition to Approved (status={approved.Status}).");
            }
            Console.WriteLine($"  [3/5] Approved by {approver}.");

            // 5. Disburse — kernel post lands here.
            var period = await lg.Periods
                .Where(p => p.TenantId == SmokeTenantId && p.FiscalYear == fiscalYear && p.MonthNumber == monthNo)
                .FirstAsync();
            disbursed = await svc.DisburseVoucherAsync(
                voucherId: v.VoucherId,
                custodianUserId: custodian,
                effectiveDate: DateOnly.FromDateTime(DateTime.UtcNow),
                periodId: period.PeriodId);
            if (disbursed.Status != VoucherStatus.Disbursed || disbursed.LedgerEventId is null)
            {
                throw new InvalidOperationException($"Disburse did not transition to Disbursed (status={disbursed.Status}, event={disbursed.LedgerEventId}).");
            }
            postedEventId = disbursed.LedgerEventId.Value;
            Console.WriteLine($"  [4/5] Disbursed → ledger event {postedEventId}.");
        }

        // 6. Verify the posted journal: ≥2 lines and SUM(debits) == SUM(credits).
        await using (var lg = new LedgerDbContext(ledgerOpts))
        {
            var posted = await lg.Events.Include(e => e.Lines).FirstAsync(e => e.EventId == postedEventId);
            var debits = posted.Lines.Sum(l => l.DebitMinor);
            var credits = posted.Lines.Sum(l => l.CreditMinor);
            if (posted.Lines.Count < 2 || debits != credits || debits == 0)
            {
                throw new InvalidOperationException(
                    $"Posted journal is malformed: lines={posted.Lines.Count}, DR={debits}, CR={credits}.");
            }
            Console.WriteLine($"  [5/5] Journal verified — {posted.Lines.Count} lines, DR=CR={debits} minor units.");
        }

        Console.WriteLine();
        Console.WriteLine("Smoke test PASSED. The deploy can disburse a real voucher end-to-end.");
        Console.WriteLine($"  Voucher purpose tagged 'SMOKE TEST {DateTimeOffset.UtcNow:u}' for the audit trail.");
        return 0;
    }

    /// <summary>
    /// Idempotent upsert of a smoke user row. Uses the deterministic GUID
    /// as the row id; a second call updates LastSeenAt and otherwise is a
    /// no-op. Avoids the "phantom user" problem in audit reports.
    /// </summary>
    private static async Task EnsureSmokeUserAsync(IdentityDbContext db, Guid userId, string email, string displayName)
    {
        var now = DateTimeOffset.UtcNow;
        var row = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.InternalUserId == userId);
        if (row is null)
        {
            db.Users.Add(new User
            {
                InternalUserId = userId,
                CfAccessSub = null,
                Email = email,
                DisplayName = displayName,
                Status = UserStatus.Active,
                CreatedAt = now,
                LastSeenAt = now,
                TenantId = SmokeTenantId
            });
        }
        else
        {
            row.LastSeenAt = now;
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Same hash recipe as <c>FloatNew.razor</c> uses for the quick-fill site
    /// buttons — keeps every "Tema-SMOKE" reference in the system pointing at
    /// the same row no matter who provisions it.
    /// </summary>
    private static Guid StableGuid(string seed)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(seed);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x40); // v4 nibble
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
