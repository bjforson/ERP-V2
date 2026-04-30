using Microsoft.EntityFrameworkCore;
using NickFinance.Banking;
using NickFinance.Ledger;
using Xunit;

namespace NickFinance.Banking.Tests;

[Collection("Banking")]
public class FxRateServiceTests
{
    private readonly BankingFixture _fx;
    public FxRateServiceTests(BankingFixture fx) => _fx = fx;

    private static FxRate Rate(string from, string to, decimal rate, DateOnly asOf, long tenantId = 1, string source = "manual")
        => new()
        {
            FromCurrency = from,
            ToCurrency = to,
            Rate = rate,
            AsOfDate = asOf,
            Source = source,
            RecordedAt = DateTimeOffset.UtcNow,
            RecordedByUserId = Guid.Empty,
            TenantId = tenantId,
        };

    private async Task<BankingDbContext> SeedAsync(params FxRate[] rates)
    {
        var bk = _fx.NewBanking();
        // Wipe first — these tests share the schema-creation fixture and
        // we want each test to define its own world.
        bk.FxRates.RemoveRange(await bk.FxRates.ToListAsync());
        await bk.SaveChangesAsync();
        if (rates.Length > 0)
        {
            bk.FxRates.AddRange(rates);
            await bk.SaveChangesAsync();
        }
        return bk;
    }

    [Fact]
    public async Task Convert_SameCurrency_ReturnsUnchanged()
    {
        await using var bk = await SeedAsync();
        var svc = new FxRateService(bk);
        var amount = new Money(123_45, "GHS");
        var result = await svc.ConvertAsync(amount, "GHS", new DateOnly(2026, 4, 28));
        Assert.Equal(123_45, result.Minor);
        Assert.Equal("GHS", result.CurrencyCode);
    }

    [Theory]
    [InlineData(100_00, 16.20, 1_620_00)]    // 100 USD * 16.20 = 1620.00 GHS → 162_000 minor
    [InlineData(1_00, 16.20, 16_20)]         // 1 USD * 16.20 = 16.20 GHS → 1_620 minor
    [InlineData(5000, 16.255, 81_275)]       // 50 USD * 16.255 = 812.75 GHS → 81_275 minor (exact)
    [InlineData(33_33, 0.0001, 0)]           // tiny rate × tiny amount → rounds down to zero
    [InlineData(1, 0.5, 0)]                  // banker's rounding: 0.5 → 0 (even)
    [InlineData(3, 0.5, 2)]                  // banker's rounding: 1.5 → 2 (even)
    public async Task Convert_DirectRate_RoundsCorrectly(long amountMinor, decimal rate, long expectedMinor)
    {
        var asOf = new DateOnly(2026, 4, 28);
        await using var bk = await SeedAsync(Rate("USD", "GHS", rate, asOf));
        var svc = new FxRateService(bk);
        var result = await svc.ConvertAsync(new Money(amountMinor, "USD"), "GHS", asOf);
        Assert.Equal(expectedMinor, result.Minor);
        Assert.Equal("GHS", result.CurrencyCode);
    }

    [Fact]
    public async Task Convert_InverseRate_WhenDirectMissing()
    {
        var asOf = new DateOnly(2026, 4, 28);
        // Only GHS->USD seeded; ask for USD->GHS — service should invert.
        await using var bk = await SeedAsync(Rate("GHS", "USD", 0.0625m, asOf));
        var svc = new FxRateService(bk);
        var rate = await svc.GetRateAsync("USD", "GHS", asOf);
        Assert.NotNull(rate);
        Assert.Equal(1m / 0.0625m, rate!.Value);

        // 100 USD * 16 = 1600 GHS → 160000 minor.
        var result = await svc.ConvertAsync(new Money(100_00, "USD"), "GHS", asOf);
        Assert.Equal(1600_00, result.Minor);
    }

    [Fact]
    public async Task Convert_LatestPriorRate_WhenAsOfHasNoExactRate()
    {
        await using var bk = await SeedAsync(
            Rate("USD", "GHS", 15.00m, new DateOnly(2026, 4, 1)),
            Rate("USD", "GHS", 16.20m, new DateOnly(2026, 4, 15)),
            Rate("USD", "GHS", 17.50m, new DateOnly(2026, 5, 1)));
        var svc = new FxRateService(bk);
        // As-of 2026-04-28 — should pick the 4/15 rate, not the 5/1 row.
        var rate = await svc.GetRateAsync("USD", "GHS", new DateOnly(2026, 4, 28));
        Assert.Equal(16.20m, rate);
    }

    [Fact]
    public async Task Convert_NoRate_Throws()
    {
        var asOf = new DateOnly(2026, 4, 28);
        await using var bk = await SeedAsync();
        var svc = new FxRateService(bk);
        await Assert.ThrowsAsync<MissingFxRateException>(
            () => svc.ConvertAsync(new Money(100_00, "USD"), "GHS", asOf));
    }

    [Fact]
    public async Task GetRate_NullForUnknownPair()
    {
        var asOf = new DateOnly(2026, 4, 28);
        await using var bk = await SeedAsync(Rate("USD", "GHS", 16.20m, asOf));
        var svc = new FxRateService(bk);
        var rate = await svc.GetRateAsync("EUR", "GHS", asOf);
        Assert.Null(rate);
    }

    [Fact]
    public async Task GetRate_TenantIsolation_DoesNotLeakAcrossTenants()
    {
        var asOf = new DateOnly(2026, 4, 28);
        await using var bk = await SeedAsync(Rate("USD", "GHS", 16.20m, asOf, tenantId: 2));
        var svc = new FxRateService(bk);
        // tenant 1 should not see tenant 2's row.
        var rateForOne = await svc.GetRateAsync("USD", "GHS", asOf, tenantId: 1);
        Assert.Null(rateForOne);
        var rateForTwo = await svc.GetRateAsync("USD", "GHS", asOf, tenantId: 2);
        Assert.Equal(16.20m, rateForTwo);
    }
}
