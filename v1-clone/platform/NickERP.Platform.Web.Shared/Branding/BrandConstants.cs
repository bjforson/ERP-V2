namespace NickERP.Platform.Web.Shared.Branding;

/// <summary>
/// Brand strings and asset paths used across all NICKSCAN ERP modules.
/// Single source of truth — change here, propagates everywhere.
/// </summary>
public static class BrandConstants
{
    public const string FullName = "NICKSCAN ERP SOLUTION";
    public const string ShortName = "NickScan ERP";
    public const string Tagline = "Powered by Nick TC-Scan Ltd";
    public const string Copyright = "© 2026 Nick TC-Scan Ltd. All rights reserved.";

    /// <summary>Title-bar prefix used by every module. Modules append " — &lt;Module Name&gt;".</summary>
    public const string PageTitlePrefix = "NICKSCAN ERP";

    /// <summary>Helper for building module page titles consistently.</summary>
    /// <example><c>BrandConstants.PageTitle("NSCIS", "Dashboard")</c> → "NICKSCAN ERP — NSCIS — Dashboard"</example>
    public static string PageTitle(string moduleName, string? pageName = null)
        => pageName is null
            ? $"{PageTitlePrefix} — {moduleName}"
            : $"{PageTitlePrefix} — {moduleName} — {pageName}";

    public static class Modules
    {
        public const string Nscis = "NSCIS";
        public const string NickHr = "NickHR";
        public const string Finance = "Finance";
        public const string Procurement = "Procurement";
        public const string Dms = "Documents";
        public const string Crm = "CRM";
    }

    public static class Colors
    {
        /// <summary>Primary brand colour (indigo, matches Portal-shipped reality).</summary>
        public const string Primary = "#4F46E5";
        /// <summary>Slightly darker indigo for hover/active states on the primary.</summary>
        public const string PrimaryHover = "#4338CA";

        public const string ModuleNscis = "#1d4ed8";
        public const string ModuleNickHr = "#7c3aed";
        public const string ModuleFinance = "#059669";
        public const string ModuleProcurement = "#d97706";
        public const string ModuleDms = "#db2777";
        public const string ModuleCrm = "#0891b2";
    }
}
