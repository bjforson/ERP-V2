using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Plugins;

namespace NickERP.Platform.Tests;

/// <summary>
/// G1 #5 — plugin TypeCodes must be namespaced by Module. Two modules
/// shipping the same TypeCode (e.g. NickFinance's "momo" wallet adapter
/// and a hypothetical NickInspection "momo" camera adapter) must coexist
/// in the registry without colliding.
/// </summary>
public class PluginRegistryModuleNamespacingTests
{
    public interface IAdapter { string Tag { get; } }

    public sealed class FinanceMomoAdapter : IAdapter { public string Tag => "finance"; }
    public sealed class InspectionMomoAdapter : IAdapter { public string Tag => "inspection"; }

    [Fact]
    public void Two_plugins_with_the_same_TypeCode_in_different_modules_resolve_independently()
    {
        var manifest = new PluginManifest(
            TypeCode: "momo",
            DisplayName: "Momo",
            Version: "1.0",
            Description: null,
            Contracts: new[] { typeof(IAdapter).FullName! },
            ConfigSchema: null);

        var financePlugin = new RegisteredPlugin(
            Module: "finance",
            TypeCode: "momo",
            ConcreteType: typeof(FinanceMomoAdapter),
            ContractTypes: new[] { typeof(IAdapter) },
            Manifest: manifest with { } /* same shape OK */);
        var inspectionPlugin = new RegisteredPlugin(
            Module: "inspection",
            TypeCode: "momo",
            ConcreteType: typeof(InspectionMomoAdapter),
            ContractTypes: new[] { typeof(IAdapter) },
            Manifest: manifest with { });

        var registry = new PluginRegistry(new[] { financePlugin, inspectionPlugin });

        var services = new ServiceCollection()
            .AddSingleton<FinanceMomoAdapter>()
            .AddSingleton<InspectionMomoAdapter>()
            .BuildServiceProvider();

        registry.Resolve<IAdapter>("finance", "momo", services).Tag.Should().Be("finance");
        registry.Resolve<IAdapter>("inspection", "momo", services).Tag.Should().Be("inspection");
    }

    [Fact]
    public void Resolve_with_unknown_module_throws_KeyNotFound()
    {
        var registry = new PluginRegistry(Array.Empty<RegisteredPlugin>());
        Action act = () => registry.Resolve<IAdapter>("finance", "momo", new ServiceCollection().BuildServiceProvider());
        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*finance*momo*");
    }

    [Fact]
    public void FindByTypeCode_is_module_scoped()
    {
        var manifest = new PluginManifest(
            TypeCode: "momo", DisplayName: "Momo", Version: "1.0",
            Description: null,
            Contracts: new[] { typeof(IAdapter).FullName! },
            ConfigSchema: null);
        var fin = new RegisteredPlugin("finance", "momo", typeof(FinanceMomoAdapter),
            new[] { typeof(IAdapter) }, manifest);
        var registry = new PluginRegistry(new[] { fin });

        registry.FindByTypeCode("finance", "momo").Should().NotBeNull();
        registry.FindByTypeCode("inspection", "momo").Should().BeNull();
        registry.FindByTypeCode("", "momo").Should().BeNull();
        registry.FindByTypeCode("finance", "").Should().BeNull();
    }
}
