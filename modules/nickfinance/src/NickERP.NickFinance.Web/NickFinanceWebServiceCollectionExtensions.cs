using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Database;
using NickERP.NickFinance.Web.Services;

namespace NickERP.NickFinance.Web;

/// <summary>
/// One-call host extension for the NickFinance pathfinder. Hosts (today
/// just <c>apps/portal</c>) call <see cref="AddNickErpNickFinanceWeb"/>
/// to register every NickFinance service in the right scope. The DB
/// registration in <c>NickFinance.Database</c> is wrapped in a
/// connection-string check so a deployment without
/// <c>ConnectionStrings:NickFinance</c> simply doesn't register the
/// module — see G2 §11.
///
/// <para>
/// Hosts that wire NickFinance MUST also wire (a) Identity / Tenancy /
/// Audit (the standard platform stack), and (b) the
/// <see cref="Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider"/>
/// (already on Blazor Server hosts via
/// <c>AddRazorComponents().AddInteractiveServerComponents()</c>). The
/// extension does NOT add Razor itself — that's a host-level concern.
/// </para>
/// </summary>
public static class NickFinanceWebServiceCollectionExtensions
{
    /// <summary>
    /// Register the full NickFinance stack: DbContext + workflow service +
    /// FX publisher + period locks + base-currency lookup. Returns the
    /// registered <c>connectionString</c> — empty string if the host
    /// configured no connection (caller should treat this as
    /// "module not deployed for this host" and skip endpoint mapping).
    /// </summary>
    public static string AddNickErpNickFinanceWeb(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("NickFinance");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Per §11 — null-or-empty means "not deployed here". Caller
            // skips the endpoint map and the sidenav link; the host boots
            // without NickFinance. No exception.
            return string.Empty;
        }

        services.AddNickErpNickFinance(connectionString);

        // Cross-DB tenant base-currency lookup. Needs IMemoryCache; add
        // it idempotently — the portal host already has it via the
        // notification projector but other hosts may not.
        services.AddMemoryCache();
        services.AddScoped<ITenantBaseCurrencyLookup, TenantBaseCurrencyLookup>();

        // Per-request workflow / publisher / period-lock services.
        services.AddScoped<VoucherWorkflowService>();
        services.AddScoped<FxRatePublishService>();
        services.AddScoped<PeriodLockService>();

        return connectionString;
    }
}
