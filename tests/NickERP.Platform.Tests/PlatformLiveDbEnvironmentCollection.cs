namespace NickERP.Platform.Tests;

/// <summary>
/// Serializes live-Postgres tests that temporarily repoint process-wide
/// design-time connection-string environment variables.
/// </summary>
[CollectionDefinition("PlatformLiveDbEnvironment", DisableParallelization = true)]
public sealed class PlatformLiveDbEnvironmentCollection
{
    public const string Name = "PlatformLiveDbEnvironment";
}
