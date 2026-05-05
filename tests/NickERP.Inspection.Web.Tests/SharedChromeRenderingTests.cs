using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Web.Shared.Components;
using NickERP.Platform.Web.Shared.Modules;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 29 — bunit coverage for the cross-module shared chrome
/// (<see cref="SharedHeader"/> + <see cref="SharedFooter"/>).
/// Verifies the components render with the right module label, the
/// back-to-launcher link points at the configured URL, and the
/// identity dropdown surfaces the resolved display name.
/// </summary>
public sealed class SharedChromeRenderingTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();

    public SharedChromeRenderingTests()
    {
        // Sprint 29 — Web.Shared chrome resolution. ModuleContext is a
        // singleton; AuthenticationStateProvider feeds the identity
        // dropdown.
        _ctx.Services.AddNickErpSharedChrome(opts =>
        {
            opts.ModuleId = "inspection";
            opts.DisplayName = "Inspection v2";
            opts.PortalLauncherUrl = "https://portal.test/";
        });
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHeader_RendersModuleLabelAndLauncherLink()
    {
        var header = _ctx.RenderComponent<SharedHeader>();

        header.Markup.Should().Contain("Inspection v2");
        header.Markup.Should().Contain("href=\"https://portal.test/\"");
        header.Markup.Should().Contain("Launcher");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHeader_OverrideParameters_TakePrecedence()
    {
        var header = _ctx.RenderComponent<SharedHeader>(
            ps => ps.Add(p => p.ModuleId, "nickfinance")
                    .Add(p => p.ModuleName, "NickFinance"));

        header.Markup.Should().Contain("NickFinance");
        // Resolved values exposed for tests.
        header.Instance.ResolvedModuleId.Should().Be("nickfinance");
        header.Instance.ResolvedDisplayName.Should().Be("NickFinance");
        // Launcher URL still comes from the injected context (no override).
        header.Instance.LauncherHref.Should().Be("https://portal.test/");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHeader_RendersIdentityFromAuthState()
    {
        var header = _ctx.RenderComponent<SharedHeader>();

        // The fake principal has nickerp:display_name = "Alice Bondegaard".
        header.Markup.Should().Contain("Alice Bondegaard");
        header.Markup.Should().Contain("alice@example.test");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SharedHeader_ComputeInitials_Pure()
    {
        SharedHeader.ComputeInitials("Alice Bondegaard").Should().Be("AB");
        SharedHeader.ComputeInitials("Alice").Should().Be("A");
        SharedHeader.ComputeInitials("alice m bondegaard").Should().Be("AB");
        SharedHeader.ComputeInitials("").Should().Be("?");
        SharedHeader.ComputeInitials("   ").Should().Be("?");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHeader_NotificationsSlot_RendersInActionsRow()
    {
        // Sprint 49 / FU-inspection-topnav-fold — when a host supplies
        // a NotificationsSlot the wrapper hook is present and the slot
        // content sits inside the actions div, before the identity
        // dropdown. Empty slot ⇒ no wrapper rendered (covered by the
        // existing slot-less render assertions above).
        var header = _ctx.RenderComponent<SharedHeader>(ps => ps
            .Add<RenderFragment>(p => p.NotificationsSlot, builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "test-bell");
                builder.AddContent(2, "BELL_3");
                builder.CloseElement();
            }));

        header.Markup.Should().Contain("nickerp-shared-header-notifications");
        header.Markup.Should().Contain("test-bell");
        header.Markup.Should().Contain("BELL_3");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHeader_AppSwitcherSlot_RendersInActionsRow()
    {
        // Sprint 49 / FU-inspection-topnav-fold — same shape as the
        // notifications-slot test, but for the app-switcher slot.
        var header = _ctx.RenderComponent<SharedHeader>(ps => ps
            .Add<RenderFragment>(p => p.AppSwitcherSlot, builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "class", "test-switcher");
                builder.AddContent(2, "SWITCH_x");
                builder.CloseElement();
            }));

        header.Markup.Should().Contain("nickerp-shared-header-app-switcher");
        header.Markup.Should().Contain("test-switcher");
        header.Markup.Should().Contain("SWITCH_x");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedHeader_SlotsAbsent_RendersNoSlotWrappers()
    {
        // Defensive: a SharedHeader with no slot fragments does not
        // emit empty slot wrappers (we use a null check, not always-on
        // markup) so modules that haven't migrated keep clean chrome.
        var header = _ctx.RenderComponent<SharedHeader>();

        header.Markup.Should().NotContain("nickerp-shared-header-notifications");
        header.Markup.Should().NotContain("nickerp-shared-header-app-switcher");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedFooter_RendersModuleNameAndLauncherLink()
    {
        var footer = _ctx.RenderComponent<SharedFooter>();

        footer.Markup.Should().Contain("Inspection v2");
        footer.Markup.Should().Contain("href=\"https://portal.test/\"");
        footer.Markup.Should().Contain("Portal launcher");
        footer.Markup.Should().Contain("NICKSCAN ERP SOLUTION");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SharedFooter_OptionsVersion_TakesPrecedenceOverAssemblyVersion()
    {
        // Sprint 29 — explicit Version on the options surface wins
        // over the entry-assembly InformationalVersion fallback.
        using var ctx = new BunitTestContext();
        ctx.Services.AddNickErpSharedChrome(opts =>
        {
            opts.ModuleId = "inspection";
            opts.DisplayName = "Inspection v2";
            opts.PortalLauncherUrl = "https://portal.test/";
            opts.Version = "9.9.9-test";
        });
        ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider());

        var footer = ctx.RenderComponent<SharedFooter>();

        footer.Markup.Should().Contain("v9.9.9-test");
        footer.Instance.ResolvedVersion.Should().Be("9.9.9-test");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Alice Bondegaard"),
                new Claim("nickerp:id", Guid.NewGuid().ToString()),
                new Claim("nickerp:display_name", "Alice Bondegaard"),
                new Claim("nickerp:email", "alice@example.test"),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
