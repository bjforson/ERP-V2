// Empty marker so the legacy `NickFinance.Identity` namespace shows up to
// the C# / Razor compiler when downstream code still has
// `using NickFinance.Identity;` or `@using NickFinance.Identity` lines.
//
// The real types now live in `NickERP.Platform.Identity` and the rest of
// this assembly is a [TypeForwardedTo] shim — but Roslyn does not enumerate
// a namespace as "existing" purely from forwarder records, only from
// actual TypeDefs inside the assembly. So we keep one dummy type here.
//
// This whole assembly (and this file) is deletable as soon as every
// downstream consumer has migrated its `using` lines to
// `NickERP.Platform.Identity`.

namespace NickFinance.Identity;

internal static class _NamespaceMarker
{
    internal const string PlatformNamespace = "NickERP.Platform.Identity";
}
