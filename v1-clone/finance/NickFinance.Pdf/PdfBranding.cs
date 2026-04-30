namespace NickFinance.Pdf;

/// <summary>
/// Shared layout + colour + company-block constants for every NickFinance
/// PDF. Keeping these in one place lets the orchestrator tweak the visual
/// language (e.g. swap a colour, add a strapline) without touching three
/// generators. The hex values mirror the <c>--nep-*</c> tokens used in
/// <c>wwwroot/app.css</c> so on-screen and on-paper artifacts feel like
/// the same product.
/// </summary>
public static class PdfBranding
{
    // Colour palette — keep aligned with --nep-* in app.css.
    /// <summary>Primary heading / accent. Matches <c>--nep-indigo-600</c>.</summary>
    public const string ColorIndigo = "#4F46E5";

    /// <summary>Muted body text. Matches <c>--nep-slate-500</c>.</summary>
    public const string ColorSlateMuted = "#64748B";

    /// <summary>Default body text. Matches <c>--nep-slate-900</c>.</summary>
    public const string ColorSlateBody = "#0F172A";

    /// <summary>Totals strip background. Matches <c>--nep-emerald-600</c>.</summary>
    public const string ColorEmerald = "#059669";

    /// <summary>Sandbox / warning watermark. Matches <c>--nep-amber-400</c>.</summary>
    public const string ColorAmber = "#FBBF24";

    /// <summary>Soft section background.</summary>
    public const string ColorSurface = "#F8FAFC";

    /// <summary>Hairline borders.</summary>
    public const string ColorBorder = "#E2E8F0";

    // Typography
    public const float BodyFontSize = 10f;
    public const float HeadingFontSize = 18f;
    public const float SubheadingFontSize = 12f;
    public const float SmallFontSize = 8.5f;

    // Page layout — A4 portrait, 1.5cm = 42.52pt margins.
    public const float MarginPoints = 42.52f;

    // Company block (hardcoded for v1 — see `LICENSING.md` /
    // `docs/HANDOFF-2026-04-29.md` for the brief on configurability).
    public const string CompanyName = "Nick TC-Scan Ltd";
    public const string CompanyTagline = "Ghana customs scanning · NickFinance";
    public const string CompanyAddressLine1 = "Tema Port Scanner Site";
    public const string CompanyAddressLine2 = "Greater Accra · Ghana";
    public const string CompanyTin = "C0001234567";
    public const string CompanyEmail = "finance@nicktcscan.com";

    /// <summary>The single-line footer used on every PDF. Includes the
    /// reg / TIN block so an invoice or voucher carries the legal anchor
    /// without depending on a per-document template.</summary>
    public static string CompanyFooterLine =>
        $"{CompanyName} · TIN {CompanyTin} · {CompanyAddressLine1}, {CompanyAddressLine2} · {CompanyEmail}";
}
