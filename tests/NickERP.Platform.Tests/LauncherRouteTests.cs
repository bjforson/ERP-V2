using System.Linq;
using Microsoft.AspNetCore.Components;
using NickERP.Portal.Components.Pages;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 49 / FU-launcher-route — pin the portal's @page wiring after
/// the route swap. Launcher must answer at both "/" and "/launcher";
/// Home must answer at "/dashboard" (and only there). Reflection-only
/// — we don't render the components here, that work belongs to the
/// existing FeatureFlagsPage / Pilot* page tests; this test is the
/// route table contract.
/// </summary>
public sealed class LauncherRouteTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Launcher_AnswersAt_RootAndLauncher()
    {
        var routes = typeof(Launcher)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain("/", because: "Sprint 49 made the launcher the root route");
        routes.Should().Contain("/launcher", because: "backward-compat with the original Sprint 29 route");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Home_AnswersAt_DashboardOnly()
    {
        var routes = typeof(Home)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(r => r.Template)
            .ToHashSet(StringComparer.Ordinal);

        routes.Should().Contain("/dashboard", because: "Sprint 49 moved the legacy stats grid off the root");
        routes.Should().NotContain("/", because: "the launcher owns the root after Sprint 49");
    }
}
