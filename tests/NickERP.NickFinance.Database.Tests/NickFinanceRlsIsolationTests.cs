using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Database;
using NickERP.Platform.Tenancy;
using Npgsql;

namespace NickERP.NickFinance.Database.Tests;

/// <summary>
/// G2 — RLS smoke tests against a real Postgres cluster, mirroring the
/// shape of <c>NotificationsRlsIsolationTests</c> in the platform tests.
///
/// <para>
/// Tenant-isolation is checked on every NickFinance table; user-isolation
/// is NOT exercised here (NickFinance is per-tenant, not per-user).
/// </para>
///
/// <para>
/// Skipped silently when <c>NICKSCAN_DB_PASSWORD</c> is not set so CI
/// without dev Postgres / NickFinance DB doesn't choke.
/// </para>
/// </summary>
public sealed class NickFinanceRlsIsolationTests : IDisposable
{
    private const string FinanceDb = "nickerp_nickfinance";
    private const long TenantA = 1L;
    private const long TenantB = 9999L;

    private readonly string? _password;
    private readonly List<Guid> _seededBoxIds = new();
    private readonly List<Guid> _seededVoucherIds = new();
    private readonly List<Guid> _seededLedgerIds = new();
    private readonly List<(long t, string ym)> _seededPeriodKeys = new();
    private readonly List<(string from, string to, DateTime eff)> _seededFxKeys = new();

    public NickFinanceRlsIsolationTests()
    {
        _password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
    }

    /// <summary>
    /// True only when the password is set AND the nickerp_nickfinance
    /// database exists. The Postgres-backed tests must skip silently
    /// (return without asserting) when either is missing — CI without a
    /// dev cluster, fresh dev box without the NickFinance DB created
    /// yet — so they don't choke. Mirrors the
    /// <c>NotificationsRlsIsolationTests</c> shape but adds the DB-exists
    /// probe because <c>nickerp_nickfinance</c> is a NEW database (G2
    /// pathfinder); existing platform tests assume <c>nickerp_platform</c>
    /// is already up.
    /// </summary>
    private bool DbReachable()
    {
        if (string.IsNullOrEmpty(_password)) return false;
        try
        {
            using var conn = new NpgsqlConnection(BuildAdminConnectionString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM nickfinance.petty_cash_boxes LIMIT 1;";
            cmd.ExecuteScalar();
            return true;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task TenantA_cannot_see_TenantB_box()
    {
        if (!DbReachable()) return;

        var aId = await SeedBoxAsync(TenantA);
        var bId = await SeedBoxAsync(TenantB);

        await using var ctx = BuildDbContext(out var tenant, out _);
        tenant.SetTenant(TenantA);

        var visible = await ctx.Boxes.AsNoTracking().Select(b => b.Id).ToListAsync();
        visible.Should().Contain(aId, "Tenant A sees its own box");
        visible.Should().NotContain(bId, "Tenant A must not see Tenant B's box");
    }

    [Fact]
    public async Task TenantA_cannot_insert_voucher_for_TenantB()
    {
        if (!DbReachable()) return;

        var bBoxId = await SeedBoxAsync(TenantB);

        await using var ctx = BuildDbContext(out var tenant, out _);
        tenant.SetTenant(TenantA);

        var voucher = new Voucher
        {
            BoxId = bBoxId,
            SequenceNumber = 1,
            State = NickERP.NickFinance.Core.Enums.VoucherState.Request,
            Purpose = "smoke",
            RequestedAmount = 1m,
            RequestedCurrency = "GHS",
            RequestedAmountBase = 1m,
            RequestedCurrencyBase = "GHS",
            RequestedByUserId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow,
            // Lying about the tenant — should fail the WITH CHECK.
            TenantId = TenantB
        };
        ctx.Vouchers.Add(voucher);

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>(
            "the WITH CHECK side of the policy must reject inserts that lie about TenantId");
    }

    [Fact]
    public async Task System_context_can_publish_NULL_tenant_fx_rate()
    {
        if (!DbReachable()) return;

        await using var ctx = BuildDbContext(out var tenant, out _);
        tenant.SetSystemContext();

        var key = ("USD", "GHS", DateTime.UtcNow.Date.AddDays(-30 - new Random().Next(1, 365)));
        var rate = new FxRate
        {
            TenantId = null,
            FromCurrency = key.Item1,
            ToCurrency = key.Item2,
            EffectiveDate = key.Item3,
            Rate = 12.34m,
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedByUserId = Guid.NewGuid()
        };
        ctx.FxRates.Add(rate);
        await ctx.SaveChangesAsync();
        _seededFxKeys.Add(key);

        // Verify under postgres that the NULL-tenant row landed.
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM nickfinance.fx_rate "
            + "WHERE \"FromCurrency\" = @f AND \"ToCurrency\" = @t AND \"EffectiveDate\" = @d AND \"TenantId\" IS NULL;";
        cmd.Parameters.Add(new NpgsqlParameter("f", key.Item1));
        cmd.Parameters.Add(new NpgsqlParameter("t", key.Item2));
        cmd.Parameters.Add(new NpgsqlParameter("d", key.Item3));
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1L);
    }

    [Fact]
    public async Task Tenant_session_can_read_NULL_tenant_fx_rate()
    {
        // Per the tenant_isolation_fx_rate policy, "TenantId IS NULL"
        // is permissive on USING — every tenant can SELECT suite-wide
        // rates so ledger writes can resolve them.
        if (!DbReachable()) return;

        // Seed under postgres to bypass RLS for setup.
        var key = ("EUR", "GHS", DateTime.UtcNow.Date.AddDays(-31 - new Random().Next(1, 365)));
        await SeedFxRateAsync(null, key.Item1, key.Item2, key.Item3, 14.5m);

        await using var ctx = BuildDbContext(out var tenant, out _);
        tenant.SetTenant(TenantA);

        var visible = await ctx.FxRates.AsNoTracking()
            .Where(r => r.FromCurrency == key.Item1 && r.ToCurrency == key.Item2 && r.EffectiveDate == key.Item3)
            .ToListAsync();
        visible.Should().HaveCount(1, "the OR clause on the policy USING side admits NULL-tenant rows");
        visible[0].TenantId.Should().BeNull();
    }

    [Fact]
    public async Task TenantA_cannot_insert_period_for_TenantB()
    {
        if (!DbReachable()) return;

        var ym = "2026-12";
        await using var ctx = BuildDbContext(out var tenant, out _);
        tenant.SetTenant(TenantA);

        var period = new PettyCashPeriod
        {
            TenantId = TenantB, // mismatch
            PeriodYearMonth = ym,
            ClosedAt = DateTimeOffset.UtcNow,
            ClosedByUserId = Guid.NewGuid()
        };
        ctx.Periods.Add(period);

        var act = async () => await ctx.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // -- helpers ------------------------------------------------------------

    private async Task<Guid> SeedBoxAsync(long tenantId)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO nickfinance.petty_cash_boxes "
            + "(\"Id\", \"Code\", \"Name\", \"CurrencyCode\", \"CustodianUserId\", \"ApproverUserId\", "
            + "\"OpeningBalanceAmount\", \"OpeningBalanceCurrency\", \"CreatedAt\", \"TenantId\") "
            + "VALUES (@id, @code, 'smoke', 'GHS', @c, @a, 0, 'GHS', now(), @t);";
        cmd.Parameters.Add(new NpgsqlParameter("id", id));
        cmd.Parameters.Add(new NpgsqlParameter("code", "smk-" + Guid.NewGuid().ToString("N")[..8]));
        cmd.Parameters.Add(new NpgsqlParameter("c", Guid.NewGuid()));
        cmd.Parameters.Add(new NpgsqlParameter("a", Guid.NewGuid()));
        cmd.Parameters.Add(new NpgsqlParameter("t", tenantId));
        await cmd.ExecuteNonQueryAsync();
        _seededBoxIds.Add(id);
        return id;
    }

    private async Task SeedFxRateAsync(long? tenantId, string from, string to, DateTime effectiveDate, decimal rate)
    {
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO nickfinance.fx_rate "
            + "(\"FromCurrency\", \"ToCurrency\", \"EffectiveDate\", \"TenantId\", \"Rate\", \"PublishedAt\", \"PublishedByUserId\") "
            + "VALUES (@f, @t, @d, @tid, @r, now(), @uid) "
            + "ON CONFLICT DO NOTHING;";
        cmd.Parameters.Add(new NpgsqlParameter("f", from));
        cmd.Parameters.Add(new NpgsqlParameter("t", to));
        cmd.Parameters.Add(new NpgsqlParameter("d", effectiveDate));
        var tidParam = cmd.CreateParameter();
        tidParam.ParameterName = "tid";
        tidParam.Value = (object?)tenantId ?? DBNull.Value;
        cmd.Parameters.Add(tidParam);
        cmd.Parameters.Add(new NpgsqlParameter("r", rate));
        cmd.Parameters.Add(new NpgsqlParameter("uid", Guid.NewGuid()));
        await cmd.ExecuteNonQueryAsync();
        _seededFxKeys.Add((from, to, effectiveDate));
    }

    private NickFinanceDbContext BuildDbContext(out TenantContext tenant, out UserContext user)
    {
        var ctxTenant = new TenantContext();
        var ctxUser = new UserContext();
        tenant = ctxTenant;
        user = ctxUser;
        var options = new DbContextOptionsBuilder<NickFinanceDbContext>()
            .UseNpgsql(BuildAppConnectionString())
            .AddInterceptors(
                new TenantConnectionInterceptor(ctxTenant, ctxUser, NullLogger<TenantConnectionInterceptor>.Instance),
                new TenantOwnedEntityInterceptor(ctxTenant))
            .Options;
        return new NickFinanceDbContext(options);
    }

    private string BuildAppConnectionString()
        => $"Host=localhost;Port=5432;Database={FinanceDb};Username=nscim_app;Password={_password};Pooling=false";

    private string BuildAdminConnectionString()
        => $"Host=localhost;Port=5432;Database={FinanceDb};Username=postgres;Password={_password};Pooling=false";

    public void Dispose()
    {
        if (_password is null) return;
        try
        {
            using var conn = new NpgsqlConnection(BuildAdminConnectionString());
            conn.Open();
            foreach (var id in _seededLedgerIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM nickfinance.petty_cash_ledger_events WHERE \"Id\" = @id;";
                cmd.Parameters.Add(new NpgsqlParameter("id", id));
                cmd.ExecuteNonQuery();
            }
            foreach (var id in _seededVoucherIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM nickfinance.petty_cash_vouchers WHERE \"Id\" = @id;";
                cmd.Parameters.Add(new NpgsqlParameter("id", id));
                cmd.ExecuteNonQuery();
            }
            foreach (var id in _seededBoxIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM nickfinance.petty_cash_boxes WHERE \"Id\" = @id;";
                cmd.Parameters.Add(new NpgsqlParameter("id", id));
                cmd.ExecuteNonQuery();
            }
            foreach (var (t, ym) in _seededPeriodKeys)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM nickfinance.petty_cash_periods WHERE \"TenantId\" = @t AND \"PeriodYearMonth\" = @ym;";
                cmd.Parameters.Add(new NpgsqlParameter("t", t));
                cmd.Parameters.Add(new NpgsqlParameter("ym", ym));
                cmd.ExecuteNonQuery();
            }
            foreach (var (f, t, d) in _seededFxKeys)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM nickfinance.fx_rate WHERE \"FromCurrency\" = @f AND \"ToCurrency\" = @t AND \"EffectiveDate\" = @d;";
                cmd.Parameters.Add(new NpgsqlParameter("f", f));
                cmd.Parameters.Add(new NpgsqlParameter("t", t));
                cmd.Parameters.Add(new NpgsqlParameter("d", d));
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // best-effort
        }
    }
}
