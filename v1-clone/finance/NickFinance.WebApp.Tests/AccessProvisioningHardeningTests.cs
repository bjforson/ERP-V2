using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Identity;
using NickFinance.WebApp.Identity;
using Xunit;

namespace NickFinance.WebApp.Tests;

/// <summary>
/// W3B (2026-04-29) — verifies the production CF-Access path is now
/// lookup-only. Before W3B the factory lazy-created on first login;
/// afterwards it throws <see cref="AccessNotProvisionedException"/>
/// in production when no row exists, while keeping lazy-create in
/// Development so local <c>dotnet run</c> isn't disrupted.
/// </summary>
public sealed class AccessProvisioningHardeningTests
{
    private const string EnvVar = "NICKFINANCE_TEST_DB";

    private static string ResolveTestConnectionString()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar)
            ?? throw new InvalidOperationException(
                $"{EnvVar} env var is required. Example: "
                + "Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=...");
        return RewriteDb(raw, "nickfinance_hardening_test");
    }

    private static string RewriteDb(string conn, string newDbName)
    {
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rebuilt = new List<string>();
        var saw = false;
        foreach (var p in parts)
        {
            var ix = p.IndexOf('=', StringComparison.Ordinal);
            if (ix < 0) { rebuilt.Add(p); continue; }
            var key = p[..ix].Trim();
            if (string.Equals(key, "Database", StringComparison.OrdinalIgnoreCase))
            {
                rebuilt.Add($"Database={newDbName}");
                saw = true;
            }
            else
            {
                rebuilt.Add(p);
            }
        }
        if (!saw) rebuilt.Add($"Database={newDbName}");
        return string.Join(';', rebuilt);
    }

    private static async Task<IServiceScope> BuildScopeAsync(string env, ClaimsPrincipal? principal)
    {
        var conn = ResolveTestConnectionString();

        // One-time DB provisioning (per-test isolation comes from the unique
        // emails / subs each case uses; the schema is shared across tests).
        var bootOpts = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(conn).Options;
        await using (var boot = new IdentityDbContext(bootOpts))
        {
            await boot.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<IdentityDbContext>(o => o.UseNpgsql(conn));
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(env));

        // HttpContextAccessor with no principal — the factory's
        // AuthenticationStateProvider fallback (below) is what supplies
        // claims in tests. Real circuits already populate ctx.User; this
        // path mirrors what happens after the HTTP request rolls off the
        // Blazor circuit.
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        // AuthenticationStateProvider — the factory falls back to this
        // when ctx.User isn't authenticated. Reliable across both DI
        // setups (tests + live SignalR circuits).
        services.AddScoped<AuthenticationStateProvider>(_ =>
            new TestAuthenticationStateProvider(principal ?? new ClaimsPrincipal(new ClaimsIdentity())));

        return services.BuildServiceProvider().CreateScope();
    }

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static ClaimsPrincipal MakeCfPrincipal(string email, string sub, string name)
    {
        var id = new ClaimsIdentity(authenticationType: "cf-access");
        id.AddClaim(new Claim("sub", sub));
        id.AddClaim(new Claim("email", email));
        id.AddClaim(new Claim("name", name));
        return new ClaimsPrincipal(id);
    }

    [Fact]
    public async Task Production_WithoutIdentityRow_ThrowsAccessNotProvisioned()
    {
        var sub = "cf-h-" + Guid.NewGuid().ToString("N")[..10];
        var email = $"unprov-{Guid.NewGuid():N}@nickscan.com";
        using var scope = await BuildScopeAsync(Environments.Production, MakeCfPrincipal(email, sub, "Unprovisioned User"));
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        var ex = Assert.Throws<AccessNotProvisionedException>(() =>
            PersistentCurrentUserFactory.Resolve(scope.ServiceProvider, EmptyConfig(), cfAccessOn: true, defaultTenantId: 1L, env));
        Assert.Equal(email.ToLowerInvariant(), ex.Email);
        Assert.Equal(sub, ex.CfAccessSub);
    }

    [Fact]
    public async Task Production_WithIdentityRow_ReturnsCurrentUser()
    {
        var sub = "cf-h-" + Guid.NewGuid().ToString("N")[..10];
        var email = $"prov-{Guid.NewGuid():N}@nickscan.com";

        // Pre-seed an identity.users row — simulates HR having provisioned
        // the user before they ever logged into NickFinance.
        var conn = ResolveTestConnectionString();
        var seedOpts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        await using (var boot = new IdentityDbContext(seedOpts))
        {
            await boot.Database.MigrateAsync();
            boot.Users.Add(new User
            {
                Email = email.ToLowerInvariant(),
                DisplayName = "Pre-provisioned User",
                CfAccessSub = null, // populated on first login
                Status = UserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                TenantId = 1L,
            });
            await boot.SaveChangesAsync();
        }

        using var scope = await BuildScopeAsync(Environments.Production, MakeCfPrincipal(email, sub, "Pre-provisioned User"));
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        var cu = PersistentCurrentUserFactory.Resolve(scope.ServiceProvider, EmptyConfig(), cfAccessOn: true, defaultTenantId: 1L, env);

        Assert.Equal(email.ToLowerInvariant(), cu.Email);
        Assert.NotEqual(Guid.Empty, cu.UserId);

        // Sub backfill landed.
        await using var verify = new IdentityDbContext(seedOpts);
        var row = await verify.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email.ToLowerInvariant());
        Assert.Equal(sub, row.CfAccessSub);
    }

    [Fact]
    public async Task NonProduction_WithoutIdentityRow_LazyCreates()
    {
        var sub = "cf-h-" + Guid.NewGuid().ToString("N")[..10];
        var email = $"dev-{Guid.NewGuid():N}@nickscan.com";
        using var scope = await BuildScopeAsync(Environments.Development, MakeCfPrincipal(email, sub, "Dev User"));
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        // No throw — Development path keeps the v1 ergonomics for local `dotnet run`.
        var cu = PersistentCurrentUserFactory.Resolve(scope.ServiceProvider, EmptyConfig(), cfAccessOn: true, defaultTenantId: 1L, env);

        Assert.Equal(email.ToLowerInvariant(), cu.Email);

        // Row was lazy-created.
        var conn = ResolveTestConnectionString();
        var verifyOpts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(conn).Options;
        await using var verify = new IdentityDbContext(verifyOpts);
        var row = await verify.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == email.ToLowerInvariant());
        Assert.Equal(sub, row.CfAccessSub);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string env) { EnvironmentName = env; }
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "NickFinance.WebApp";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly AuthenticationState _state;
        public TestAuthenticationStateProvider(ClaimsPrincipal principal)
        {
            _state = new AuthenticationState(principal);
        }
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(_state);
    }
}
