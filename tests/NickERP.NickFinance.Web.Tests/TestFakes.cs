using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Tenancy;

namespace NickERP.NickFinance.Web.Tests;

/// <summary>
/// In-memory <see cref="NickFinanceDbContext"/> for unit tests.
/// </summary>
public static class TestDb
{
    public static NickFinanceDbContext Build(string name = "")
    {
        var options = new DbContextOptionsBuilder<NickFinanceDbContext>()
            .UseInMemoryDatabase("nickfinance-" + (string.IsNullOrEmpty(name) ? Guid.NewGuid().ToString() : name))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new NickFinanceDbContext(options);
    }
}

/// <summary>Fake auth state provider; returns a principal carrying the supplied user id.</summary>
public sealed class FakeAuthStateProvider : AuthenticationStateProvider
{
    private readonly Guid _userId;

    public FakeAuthStateProvider(Guid userId) { _userId = userId; }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(NickErpClaims.Id, _userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, _userId.ToString()));
        identity.AddClaim(new Claim(NickErpClaims.TenantId, "1"));
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}

/// <summary>Fake auth state provider with extra role claims.</summary>
public sealed class FakeAuthStateProviderWithRoles : AuthenticationStateProvider
{
    private readonly Guid _userId;
    private readonly string[] _roles;

    public FakeAuthStateProviderWithRoles(Guid userId, params string[] roles)
    {
        _userId = userId;
        _roles = roles;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(NickErpClaims.Id, _userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, _userId.ToString()));
        identity.AddClaim(new Claim(NickErpClaims.TenantId, "1"));
        foreach (var r in _roles)
            identity.AddClaim(new Claim(ClaimTypes.Role, r));
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}

/// <summary>No-op event publisher; collects events for assertion.</summary>
public sealed class CapturingEventPublisher : IEventPublisher
{
    public List<DomainEvent> Captured { get; } = new();

    public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
    {
        Captured.Add(evt);
        return Task.FromResult(evt);
    }

    public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
    {
        Captured.AddRange(events);
        return Task.FromResult<IReadOnlyList<DomainEvent>>(events);
    }
}

/// <summary>Fake FX rate lookup returning a fixed rate.</summary>
public sealed class FakeFxRateLookup : IFxRateLookup
{
    private readonly decimal _rate;

    public FakeFxRateLookup(decimal rate = 1m) { _rate = rate; }

    public Task<FxRate?> ResolveAsync(string fromCurrency, string toCurrency, DateTime effectiveDate, CancellationToken ct = default)
    {
        return Task.FromResult<FxRate?>(new FxRate
        {
            FromCurrency = fromCurrency,
            ToCurrency = toCurrency,
            Rate = _rate,
            EffectiveDate = effectiveDate.Date,
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedByUserId = Guid.Empty,
            TenantId = null
        });
    }
}

/// <summary>
/// Fake FX lookup that returns null (simulates a rate that hasn't
/// been published) — used to verify the FxRateNotPublishedException
/// path on cross-currency transitions.
/// </summary>
public sealed class NullFxRateLookup : IFxRateLookup
{
    public Task<FxRate?> ResolveAsync(string fromCurrency, string toCurrency, DateTime effectiveDate, CancellationToken ct = default)
        => string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase)
            ? Task.FromResult<FxRate?>(new FxRate
            {
                FromCurrency = fromCurrency,
                ToCurrency = toCurrency,
                Rate = 1m,
                EffectiveDate = effectiveDate.Date,
                PublishedAt = DateTimeOffset.UtcNow,
                PublishedByUserId = Guid.Empty,
                TenantId = null
            })
            : Task.FromResult<FxRate?>(null);
}

/// <summary>Stub tenant base-currency lookup returning the configured currency.</summary>
public sealed class StubBaseCurrencyLookup : ITenantBaseCurrencyLookup
{
    private readonly string _currency;
    public StubBaseCurrencyLookup(string currency = "GHS") { _currency = currency; }
    public Task<string> GetBaseCurrencyAsync(long tenantId, CancellationToken ct = default)
        => Task.FromResult(_currency);
}

/// <summary>Pre-configured tenant context for tests.</summary>
public static class TestTenant
{
    public static TenantContext For(long tenantId)
    {
        var t = new TenantContext();
        t.SetTenant(tenantId);
        return t;
    }
}
