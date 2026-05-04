using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Web.Shared.Modules;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 29 — coverage for <see cref="ModuleContext"/> and the
/// <see cref="SharedChromeServiceCollectionExtensions.AddNickErpSharedChrome"/>
/// DI helper. Verifies fallback behaviour when options are blank, the
/// per-host configuration shape, and that double-registration is
/// idempotent.
/// </summary>
public sealed class ModuleContextTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Construct_WithExplicitOptions_PreservesAllFields()
    {
        var ctx = new ModuleContext(
            moduleId: "inspection",
            displayName: "Inspection v2",
            portalLauncherUrl: "https://portal.example/");

        ctx.ModuleId.Should().Be("inspection");
        ctx.DisplayName.Should().Be("Inspection v2");
        ctx.PortalLauncherUrl.Should().Be("https://portal.example/");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Construct_WithBlankModuleId_FallsBackToPortalSentinel()
    {
        var ctx = new ModuleContext("   ", "Display", "https://x");
        ctx.ModuleId.Should().Be(ModuleContext.DefaultModuleId);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("inspection", "Inspection")]
    [InlineData("nickfinance", "NickFinance")]
    [InlineData("nickhr", "NickHR")]
    [InlineData("portal", "NickERP Portal")]
    [InlineData("unknown-module", "unknown-module")]
    public void Construct_WithoutDisplayName_FallsBackByModuleId(string moduleId, string expected)
    {
        var ctx = new ModuleContext(moduleId, null, null);
        ctx.DisplayName.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Construct_WithBlankLauncherUrl_FallsBackToDefault()
    {
        var ctx = new ModuleContext("inspection", null, "");
        ctx.PortalLauncherUrl.Should().Be(ModuleContext.DefaultLauncherUrl);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddNickErpSharedChrome_RegistersResolvableContext()
    {
        var services = new ServiceCollection();
        services.AddNickErpSharedChrome(opts =>
        {
            opts.ModuleId = "nickfinance";
            opts.DisplayName = "NickFinance G2";
            opts.PortalLauncherUrl = "https://portal.example/launcher";
        });

        using var sp = services.BuildServiceProvider();
        var ctx = sp.GetRequiredService<IModuleContext>();

        ctx.ModuleId.Should().Be("nickfinance");
        ctx.DisplayName.Should().Be("NickFinance G2");
        ctx.PortalLauncherUrl.Should().Be("https://portal.example/launcher");

        // The options instance is also registered so the footer can
        // read the supplied Version (or fall back to the assembly
        // attribute when blank).
        var opts = sp.GetRequiredService<NickErpSharedChromeOptions>();
        opts.ModuleId.Should().Be("nickfinance");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddNickErpSharedChrome_FirstRegistrationWins()
    {
        // The portal host registers chrome with ModuleId="portal" first,
        // and then NickFinance.Web's optional AddNickErpNickFinanceSharedChrome
        // would re-register with "nickfinance". TryAddSingleton means
        // the first registration wins so the portal-hosted G2 pages
        // keep showing the portal label.
        var services = new ServiceCollection();
        services.AddNickErpSharedChrome(opts =>
        {
            opts.ModuleId = "portal";
            opts.DisplayName = "NickERP Portal";
        });
        services.AddNickErpSharedChrome(opts =>
        {
            opts.ModuleId = "nickfinance";
            opts.DisplayName = "NickFinance";
        });

        using var sp = services.BuildServiceProvider();
        var ctx = sp.GetRequiredService<IModuleContext>();

        ctx.ModuleId.Should().Be("portal");
        ctx.DisplayName.Should().Be("NickERP Portal");
    }
}
