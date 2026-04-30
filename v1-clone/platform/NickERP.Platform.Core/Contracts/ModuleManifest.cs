namespace NickERP.Platform.Core.Contracts;

/// <summary>
/// JSON-serializable manifest returned by every module's
/// <c>GET /api/_module/manifest</c> endpoint. The unified ERP portal
/// fetches this to render the App Launcher.
/// </summary>
public sealed record ModuleManifest
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required string PlatformContractVersion { get; init; }
    public required string Icon { get; init; }
    public required string Color { get; init; }
    public required ModuleRoutes Routes { get; init; }
    public required IReadOnlyList<string> PlatformDependencies { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required string Health { get; init; }

    public static ModuleManifest FromModule(IPlatformModule module, string healthRoute = "/health")
        => new()
        {
            Name = module.Name,
            DisplayName = module.DisplayName,
            Version = module.Version,
            Description = module.Description,
            PlatformContractVersion = module.PlatformContractVersion,
            Icon = module.Icon,
            Color = module.Color,
            Routes = new ModuleRoutes { Home = module.HomeRoute },
            PlatformDependencies = module.PlatformDependencies,
            Capabilities = module.Capabilities,
            Health = healthRoute
        };
}

public sealed record ModuleRoutes
{
    public required string Home { get; init; }
    public string? Settings { get; init; }
    public string? Help { get; init; }
}
