using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Portal.Services.Modules;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 29 — coverage for <see cref="ModuleRegistryService"/>. Verifies
/// the catalogue assembly from <c>Portal:Modules:{Id}:BaseUrl</c>, the
/// per-tenant override application, and the disabled-row filtering.
/// EF in-memory provider for the DbContext — RLS is exercised in the
/// live-Postgres suite once the migration is applied.
/// </summary>
public sealed class ModuleRegistryServiceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void GetAllModules_ReturnsThreeExpectedModules()
    {
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>());
        using var ctx = BuildCtx();

        var svc = new ModuleRegistryService(ctx, options);
        var all = svc.GetAllModules();

        all.Should().HaveCount(3);
        all.Select(m => m.Id).Should().BeEquivalentTo(
            new[] { "inspection", "nickfinance", "nickhr" },
            opts => opts.WithStrictOrdering());
        all.All(m => m.Enabled).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetAllModules_BindsBaseUrlsFromConfig()
    {
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>
        {
            ["Portal:Modules:Inspection:BaseUrl"]   = "https://inspection.example",
            ["Portal:Modules:NickFinance:BaseUrl"]  = "https://finance.example",
            ["Portal:Modules:NickHr:BaseUrl"]       = "https://hr.example",
        });
        using var ctx = BuildCtx();

        var svc = new ModuleRegistryService(ctx, options);
        var all = svc.GetAllModules();

        all.Single(m => m.Id == "inspection").BaseUrl.Should().Be("https://inspection.example");
        all.Single(m => m.Id == "nickfinance").BaseUrl.Should().Be("https://finance.example");
        all.Single(m => m.Id == "nickhr").BaseUrl.Should().Be("https://hr.example");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GetAllModules_FallsBackToDevDefaults_WhenConfigMissing()
    {
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>());
        using var ctx = BuildCtx();

        var svc = new ModuleRegistryService(ctx, options);
        var all = svc.GetAllModules();

        all.Single(m => m.Id == "inspection").BaseUrl
            .Should().Be(ModuleRegistryServiceCollectionExtensions.DefaultInspectionBaseUrl);
        all.Single(m => m.Id == "nickfinance").BaseUrl
            .Should().Be(ModuleRegistryServiceCollectionExtensions.DefaultNickFinanceBaseUrl);
        all.Single(m => m.Id == "nickhr").BaseUrl
            .Should().Be(ModuleRegistryServiceCollectionExtensions.DefaultNickHrBaseUrl);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetModulesForTenant_NoOverrides_ReturnsAllEnabled()
    {
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>());
        using var ctx = BuildCtx();
        var svc = new ModuleRegistryService(ctx, options);

        var modules = await svc.GetModulesForTenantAsync(tenantId: 1);

        modules.Should().HaveCount(3);
        modules.All(m => m.Enabled).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetModulesForTenant_DisabledRow_HidesModuleByDefault()
    {
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>());
        using var ctx = BuildCtx();
        ctx.TenantModuleSettings.Add(new TenantModuleSetting
        {
            TenantId = 1,
            ModuleId = "nickhr",
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var svc = new ModuleRegistryService(ctx, options);
        var modules = await svc.GetModulesForTenantAsync(tenantId: 1);

        modules.Should().HaveCount(2);
        modules.Select(m => m.Id).Should().NotContain("nickhr");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetModulesForTenant_IncludeDisabled_SurfacesDisabledFlag()
    {
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>());
        using var ctx = BuildCtx();
        ctx.TenantModuleSettings.Add(new TenantModuleSetting
        {
            TenantId = 7,
            ModuleId = "nickfinance",
            Enabled = false,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var svc = new ModuleRegistryService(ctx, options);
        var modules = await svc.GetModulesForTenantAsync(tenantId: 7, includeDisabled: true);

        modules.Should().HaveCount(3);
        modules.Single(m => m.Id == "nickfinance").Enabled.Should().BeFalse();
        modules.Single(m => m.Id == "inspection").Enabled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetModulesForTenant_ScopesByTenantId()
    {
        // Sprint 43 / Phase D — FU-launcher-rls-with-postgres dropped
        // the explicit Where(TenantId == tenantId) belt-and-suspenders
        // filter that previously masked RLS gaps. Under the in-memory
        // provider there is no RLS, so this test now asserts the
        // catalogue-merging behaviour using DISTINCT DbContext
        // instances (= isolated in-memory stores) per tenant. Real
        // Postgres tests live in TenantModuleSettingsRlsIntegrationTests.
        var options = BuildOptionsFromConfig(new Dictionary<string, string?>());

        // Tenant 1 — disables nickhr.
        using (var ctx1 = BuildCtx())
        {
            ctx1.TenantModuleSettings.Add(new TenantModuleSetting
            {
                TenantId = 1, ModuleId = "nickhr", Enabled = false, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ctx1.SaveChangesAsync();
            var svc1 = new ModuleRegistryService(ctx1, options);
            var t1 = await svc1.GetModulesForTenantAsync(1);
            t1.Select(m => m.Id).Should().BeEquivalentTo(
                new[] { "inspection", "nickfinance" },
                opts => opts.WithStrictOrdering());
        }

        // Tenant 2 — disables inspection.
        using (var ctx2 = BuildCtx())
        {
            ctx2.TenantModuleSettings.Add(new TenantModuleSetting
            {
                TenantId = 2, ModuleId = "inspection", Enabled = false, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await ctx2.SaveChangesAsync();
            var svc2 = new ModuleRegistryService(ctx2, options);
            var t2 = await svc2.GetModulesForTenantAsync(2);
            t2.Select(m => m.Id).Should().BeEquivalentTo(
                new[] { "nickfinance", "nickhr" },
                opts => opts.WithStrictOrdering());
        }
    }

    private static TenancyDbContext BuildCtx()
    {
        var name = "module-registry-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TenancyDbContext(opts);
    }

    private static ModuleRegistryOptions BuildOptionsFromConfig(IDictionary<string, string?> values)
    {
        // Round-trip through ConfigurationBuilder so the test exercises
        // the same binding path the real host uses.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddNickErpModuleRegistry(config);
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ModuleRegistryOptions>();
    }
}
