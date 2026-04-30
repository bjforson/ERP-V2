using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NickFinance.Banking;

/// <summary>
/// One observed mid-rate from a provider. The provider is currency-pair-agnostic
/// — Nick TC-Scan v1 only ever asks BoG for "today's GHS rates against
/// USD / EUR / GBP / NGN" and inserts each one as <c>FromCurrency = X, ToCurrency = GHS</c>.
/// </summary>
public sealed record BogMidRate(string Currency, decimal MidRate);

/// <summary>
/// Fetches FX mid-rates from Bank of Ghana. Concrete implementations:
/// <list type="bullet">
///   <item><see cref="BogRateProvider"/> — HTTP client. When <c>NICKFINANCE_BOG_API_URL</c>
///         is set, it GETs the URL and expects a JSON shape
///         <c>{ "rates": [ { "currency": "USD", "midRate": 16.20 }, ... ] }</c>.
///         When the env var is unset, returns an empty list — callers fall back
///         to the manual UI.</item>
///   <item><see cref="EmptyBogRateProvider"/> — explicit no-op for tests / dev.</item>
/// </list>
/// </summary>
/// <remarks>
/// BoG's public statistical-bulletin endpoint is not always reliable; the
/// CSV scrape of <c>https://www.bog.gov.gh/treasury-and-the-markets/daily-interbank-fx-rates/</c>
/// is documented as a manual import path. Once the orchestrator points
/// <c>NICKFINANCE_BOG_API_URL</c> at a stable mirror, the importer activates
/// without code changes.
/// </remarks>
public interface IBogRateProvider
{
    /// <summary>Returns mid-rates for the requested date, empty if unavailable.</summary>
    Task<IReadOnlyList<BogMidRate>> FetchAsync(DateOnly date, CancellationToken ct = default);
}

/// <summary>No-op implementation — returns an empty rate set. Wired in tests + when no env var is set.</summary>
public sealed class EmptyBogRateProvider : IBogRateProvider
{
    public Task<IReadOnlyList<BogMidRate>> FetchAsync(DateOnly date, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BogMidRate>>(Array.Empty<BogMidRate>());
}

/// <summary>
/// HTTP-based BoG rate provider. Activates iff <c>NICKFINANCE_BOG_API_URL</c>
/// is set; otherwise behaves as <see cref="EmptyBogRateProvider"/>.
/// </summary>
public sealed class BogRateProvider : IBogRateProvider
{
    private readonly HttpClient _http;
    private readonly string? _baseUrl;
    private readonly ILogger<BogRateProvider> _log;

    public BogRateProvider(HttpClient http, ILogger<BogRateProvider> log)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _baseUrl = Environment.GetEnvironmentVariable("NICKFINANCE_BOG_API_URL");
    }

    public async Task<IReadOnlyList<BogMidRate>> FetchAsync(DateOnly date, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl))
        {
            // Keep the warning at info level — empty fetch is the documented v1 default.
            _log.LogInformation("NICKFINANCE_BOG_API_URL not set; BoG provider returning empty (manual entry expected).");
            return Array.Empty<BogMidRate>();
        }

        try
        {
            var url = _baseUrl.Contains("{date}", StringComparison.Ordinal)
                ? _baseUrl.Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
                : $"{_baseUrl.TrimEnd('/')}?date={date:yyyy-MM-dd}";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var payload = await resp.Content.ReadFromJsonAsync<BogPayload>(cancellationToken: ct).ConfigureAwait(false);
            if (payload?.Rates is null) return Array.Empty<BogMidRate>();
            return payload.Rates
                .Where(r => !string.IsNullOrWhiteSpace(r.Currency) && r.MidRate > 0m)
                .Select(r => new BogMidRate(r.Currency!.Trim().ToUpperInvariant(), r.MidRate))
                .ToList();
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "BoG provider HTTP failure; returning empty rate set.");
            return Array.Empty<BogMidRate>();
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "BoG provider payload parse failure; returning empty rate set.");
            return Array.Empty<BogMidRate>();
        }
        catch (TaskCanceledException ex)
        {
            _log.LogWarning(ex, "BoG provider timeout; returning empty rate set.");
            return Array.Empty<BogMidRate>();
        }
    }

    private sealed class BogPayload
    {
        public List<BogPayloadRate>? Rates { get; set; }
    }

    private sealed class BogPayloadRate
    {
        public string? Currency { get; set; }
        public decimal MidRate { get; set; }
    }
}

/// <summary>
/// Imports yesterday's rates each night. Idempotent — if a row already
/// exists for <c>(tenant, pair, date)</c> the row is updated in place;
/// the unique index in <see cref="BankingDbContext"/> prevents duplicates.
/// </summary>
public interface IRateImporter
{
    /// <summary>Fetch + persist any missing rates for <paramref name="date"/>. Returns inserted/updated count.</summary>
    Task<int> ImportForAsync(DateOnly date, long tenantId = 1, CancellationToken ct = default);
}

public sealed class RateImporter : IRateImporter
{
    private readonly BankingDbContext _db;
    private readonly IBogRateProvider _provider;
    private readonly ILogger<RateImporter> _log;
    private readonly TimeProvider _clock;

    public RateImporter(BankingDbContext db, IBogRateProvider provider, ILogger<RateImporter> log, TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<int> ImportForAsync(DateOnly date, long tenantId = 1, CancellationToken ct = default)
    {
        var rates = await _provider.FetchAsync(date, ct).ConfigureAwait(false);
        if (rates.Count == 0)
        {
            _log.LogWarning("BoG returned no rates for {Date}; manual entry expected.", date);
            return 0;
        }

        var changed = 0;
        var now = _clock.GetUtcNow();
        foreach (var r in rates)
        {
            var existing = await _db.FxRates.FirstOrDefaultAsync(
                x => x.TenantId == tenantId
                  && x.FromCurrency == r.Currency
                  && x.ToCurrency == "GHS"
                  && x.AsOfDate == date, ct).ConfigureAwait(false);
            if (existing is null)
            {
                _db.FxRates.Add(new FxRate
                {
                    FromCurrency = r.Currency,
                    ToCurrency = "GHS",
                    Rate = r.MidRate,
                    AsOfDate = date,
                    Source = "BoG-API",
                    RecordedAt = now,
                    RecordedByUserId = Guid.Empty,
                    TenantId = tenantId,
                });
                changed++;
            }
            else if (existing.Source != "manual")
            {
                // Manual rows are sticky — operator wins over automated providers.
                existing.Rate = r.MidRate;
                existing.Source = "BoG-API";
                existing.RecordedAt = now;
                changed++;
            }
        }
        if (changed > 0) await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Imported {Count} BoG rate(s) for {Date}.", changed, date);
        return changed;
    }
}

/// <summary>
/// Background worker — runs the rate importer roughly daily. Wakes on
/// startup (so a fresh boot back-fills the previous day immediately) and
/// then every 24h. If anything throws, logs and waits for the next tick;
/// FX rates are not on the critical path for any kernel write.
/// </summary>
public sealed class FxRateImportHostedService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<FxRateImportHostedService> _log;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _period;

    public FxRateImportHostedService(IServiceProvider sp, ILogger<FxRateImportHostedService> log, TimeProvider? clock = null)
    {
        _sp = sp ?? throw new ArgumentNullException(nameof(sp));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? TimeProvider.System;
        _period = TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Light delay on boot so we don't fight database migrations.
        try { await Task.Delay(TimeSpan.FromSeconds(30), _clock, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var importer = scope.ServiceProvider.GetService<IRateImporter>();
                if (importer is not null)
                {
                    var yesterday = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime).AddDays(-1);
                    await importer.ImportForAsync(yesterday, tenantId: 1, ct: stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Nightly FX rate import failed; will retry next cycle.");
            }

            try { await Task.Delay(_period, _clock, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
