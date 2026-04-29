using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Inference.Abstractions;

namespace NickERP.Inspection.Inference.Mock;

/// <summary>
/// Direct DI wiring for the mock <see cref="IInferenceRunner"/>. Used by
/// tests that bypass the plugin loader and resolve the runner directly
/// from DI.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Stable DI key matching <see cref="MockInferenceRunner.TypeCode"/>; resolve via <c>GetRequiredKeyedService&lt;IInferenceRunner&gt;("mock")</c>.</summary>
    public const string ServiceKey = "mock";

    /// <summary>Register <see cref="MockInferenceRunner"/> as a keyed singleton under <see cref="ServiceKey"/>.</summary>
    /// <param name="services">DI container.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddInferenceMock(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<MockInferenceRunner>();
        services.AddKeyedSingleton<IInferenceRunner>(
            ServiceKey,
            (sp, _) => sp.GetRequiredService<MockInferenceRunner>());
        return services;
    }
}
