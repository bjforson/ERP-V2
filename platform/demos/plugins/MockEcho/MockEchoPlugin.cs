namespace NickERP.Platform.Plugins.Demos.MockEcho;

/// <summary>
/// The [Plugin]-decorated concrete class. Its TypeCode "mock-echo" must
/// match the manifest's <c>plugin.json:typeCode</c>; the loader rejects
/// mismatches.
/// </summary>
[Plugin("mock-echo")]
public sealed class MockEchoPlugin : IEchoPlugin
{
    public string TypeCode => "mock-echo";

    public string Echo(string input) => $"echo({input ?? string.Empty})";
}
