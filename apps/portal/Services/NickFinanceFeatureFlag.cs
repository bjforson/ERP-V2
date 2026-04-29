namespace NickERP.Portal.Services;

/// <summary>
/// G2 — host-side flag indicating whether NickFinance is registered for
/// this deployment. Read by the sidenav / Home page to show or hide the
/// Petty Cash link. The flag mirrors the result of
/// <c>builder.Services.AddNickErpNickFinanceWeb(builder.Configuration)</c>:
/// empty connection string ⇒ module not deployed ⇒ hide the link.
/// </summary>
public sealed record NickFinanceFeatureFlag(bool Enabled);
