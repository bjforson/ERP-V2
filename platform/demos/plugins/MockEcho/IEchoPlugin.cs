namespace NickERP.Platform.Plugins.Demos.MockEcho;

/// <summary>
/// Sample plugin contract used only to validate the platform's plugin loader.
/// Not a real domain interface — when Inspection v2 ships, real contracts
/// (<c>IScannerAdapter</c> etc.) replace this in tests and demos.
/// </summary>
public interface IEchoPlugin
{
    string TypeCode { get; }
    string Echo(string input);
}
