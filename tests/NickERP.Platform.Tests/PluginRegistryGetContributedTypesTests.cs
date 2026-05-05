using NickERP.Platform.Plugins;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 46 / Phase C — coverage for Sprint 42 FU-promote-validation-
/// rules-to-plugin-registry. The new accessor
/// <see cref="IPluginRegistry.GetContributedTypes"/> enumerates every
/// concrete type contributed by a registered plugin assembly that
/// implements a given contract — distinct from
/// <see cref="IPluginRegistry.ForContract"/>, which returns only
/// <see cref="PluginAttribute"/>-decorated types.
///
/// <para>
/// The use case is plugin-supplied
/// <see cref="System.Collections.Generic.IEnumerable{T}"/> contributions
/// that aren't themselves <c>[Plugin]</c>-decorated. Sprint 28's
/// <c>PluginValidationRuleRegistration</c> previously reflected over
/// every DLL in the plugins folder to find these; this accessor lets
/// the registration trust the registry instead.
/// </para>
/// </summary>
public class PluginRegistryGetContributedTypesTests
{
    /// <summary>
    /// A simple non-plugin contract — anything implementing this in a
    /// registered plugin assembly should appear in
    /// <see cref="IPluginRegistry.GetContributedTypes"/> output.
    /// </summary>
    public interface IContribution
    {
        string Name { get; }
    }

    /// <summary>The concrete plugin (decorated with <see cref="PluginAttribute"/>-equivalent metadata).</summary>
    public sealed class HostPlugin
    {
        public string TypeCode => "host";
    }

    /// <summary>Concrete contribution A — should show up.</summary>
    public sealed class ContributionA : IContribution
    {
        public string Name => "A";
    }

    /// <summary>Concrete contribution B — should show up too.</summary>
    public sealed class ContributionB : IContribution
    {
        public string Name => "B";
    }

    /// <summary>An abstract contribution — should NOT show up.</summary>
    public abstract class AbstractContribution : IContribution
    {
        public string Name => "abstract";
    }

    /// <summary>An interface marker — should NOT show up.</summary>
    public interface IExtraInterface : IContribution { }

    /// <summary>A type with no public ctor — should NOT show up.</summary>
    public sealed class NoCtorContribution : IContribution
    {
        private NoCtorContribution() { }
        public string Name => "no-ctor";
    }

    [Fact]
    public void GetContributedTypes_returns_concrete_types_implementing_contract()
    {
        var manifest = new PluginManifest(
            TypeCode: "host", DisplayName: "Host", Version: "1.0",
            Description: null,
            Contracts: Array.Empty<string>(), ConfigSchema: null);
        var plugin = new RegisteredPlugin(
            Module: "test",
            TypeCode: "host",
            ConcreteType: typeof(HostPlugin),
            ContractTypes: Array.Empty<Type>(),
            Manifest: manifest);
        var registry = new PluginRegistry(new[] { plugin });

        var contributed = registry.GetContributedTypes(typeof(IContribution));

        // ContributionA + ContributionB are concrete + have a public ctor
        // + implement IContribution. The abstract / interface / private-
        // ctor variants are excluded.
        contributed.Should().Contain(typeof(ContributionA));
        contributed.Should().Contain(typeof(ContributionB));
        contributed.Should().NotContain(typeof(AbstractContribution));
        contributed.Should().NotContain(typeof(IExtraInterface));
        contributed.Should().NotContain(typeof(NoCtorContribution));
    }

    [Fact]
    public void GetContributedTypes_returns_empty_when_contract_has_no_implementors()
    {
        var manifest = new PluginManifest(
            TypeCode: "host", DisplayName: "Host", Version: "1.0",
            Description: null,
            Contracts: Array.Empty<string>(), ConfigSchema: null);
        var plugin = new RegisteredPlugin(
            Module: "test",
            TypeCode: "host",
            ConcreteType: typeof(HostPlugin),
            ContractTypes: Array.Empty<Type>(),
            Manifest: manifest);
        var registry = new PluginRegistry(new[] { plugin });

        // No type in this assembly implements IDisposable AND
        // is a public concrete with a public ctor. The empty list
        // posture documented on IPluginRegistry holds.
        var contributed = registry.GetContributedTypes(typeof(IUnimplementedContract));
        contributed.Should().BeEmpty();
    }

    [Fact]
    public void GetContributedTypes_returns_empty_for_registry_with_no_plugins()
    {
        var registry = new PluginRegistry(Array.Empty<RegisteredPlugin>());
        var contributed = registry.GetContributedTypes(typeof(IContribution));
        contributed.Should().BeEmpty(
            because: "no plugin assemblies are registered, so there's nothing to enumerate");
    }

    [Fact]
    public void GetContributedTypes_dedupes_assemblies_when_multiple_plugins_share_one()
    {
        // Two plugins from the same assembly — GetContributedTypes
        // shouldn't double-count types from the shared assembly.
        var manifest = new PluginManifest(
            TypeCode: "host", DisplayName: "Host", Version: "1.0",
            Description: null,
            Contracts: Array.Empty<string>(), ConfigSchema: null);
        var p1 = new RegisteredPlugin("test", "host-1", typeof(HostPlugin),
            Array.Empty<Type>(), manifest with { TypeCode = "host-1" });
        var p2 = new RegisteredPlugin("test", "host-2", typeof(HostPlugin),
            Array.Empty<Type>(), manifest with { TypeCode = "host-2" });
        var registry = new PluginRegistry(new[] { p1, p2 });

        var contributed = registry.GetContributedTypes(typeof(IContribution));

        // ContributionA appears once even though there are two plugins
        // pointing at the same DLL.
        contributed.Count(t => t == typeof(ContributionA)).Should().Be(1);
    }

    [Fact]
    public void GetContributedTypes_throws_when_contract_is_null()
    {
        var registry = new PluginRegistry(Array.Empty<RegisteredPlugin>());
        Action act = () => registry.GetContributedTypes(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DefaultInterfaceMethod_returns_empty_for_test_stub()
    {
        // Test stubs that don't override GetContributedTypes get the
        // empty-list default-interface-method fallback per the
        // IPluginRegistry contract — Sprint 42's compatibility
        // promise that existing IPluginRegistry implementations
        // (test stubs, custom mocks) don't have to update.
        IPluginRegistry stub = new StubRegistry();
        var contributed = stub.GetContributedTypes(typeof(IContribution));
        contributed.Should().BeEmpty(
            because: "the default-interface-method fallback returns Array.Empty<Type>()");
    }

    [Fact]
    public void GetContributedTypes_filters_to_requested_contract()
    {
        // GetContributedTypes(typeof(IContribution)) should not return
        // the host plugin's concrete type (which doesn't implement
        // IContribution). Verifies the assignability filter actually
        // narrows results.
        var manifest = new PluginManifest(
            TypeCode: "host", DisplayName: "Host", Version: "1.0",
            Description: null,
            Contracts: Array.Empty<string>(), ConfigSchema: null);
        var plugin = new RegisteredPlugin("test", "host", typeof(HostPlugin),
            Array.Empty<Type>(), manifest);
        var registry = new PluginRegistry(new[] { plugin });

        var contributed = registry.GetContributedTypes(typeof(IContribution));
        contributed.Should().NotContain(typeof(HostPlugin),
            because: "HostPlugin doesn't implement IContribution and must be filtered out");
    }

    /// <summary>
    /// A contract with no implementors anywhere in the registered
    /// plugin assembly — used to exercise the empty-list path.
    /// </summary>
    public interface IUnimplementedContract { }

    /// <summary>
    /// Test stub of <see cref="IPluginRegistry"/> that does NOT
    /// override <see cref="IPluginRegistry.GetContributedTypes"/> —
    /// must fall through to the empty-list default-interface-method.
    /// </summary>
    private sealed class StubRegistry : IPluginRegistry
    {
        public IReadOnlyList<RegisteredPlugin> All => Array.Empty<RegisteredPlugin>();
        public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType)
            => Array.Empty<RegisteredPlugin>();
        public RegisteredPlugin? FindByTypeCode(string module, string typeCode) => null;
        public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
            => throw new NotSupportedException();
        // GetContributedTypes intentionally NOT overridden — exercises
        // the default-interface-method path.
    }
}
