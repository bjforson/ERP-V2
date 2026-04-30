namespace NickFinance.Pdf;

/// <summary>
/// One-shot static configuration for QuestPDF. Call <see cref="Configure"/>
/// from <c>Program.cs</c> immediately after the host builder is created — it
/// applies the Community license (see <c>LICENSING.md</c>) and turns off
/// debugging overlays that would otherwise leak layout boxes into PDFs.
/// </summary>
public static class QuestPdfBootstrap
{
    /// <summary>
    /// Apply runtime QuestPDF settings. Idempotent — calling twice is safe.
    /// </summary>
    public static void Configure()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        QuestPDF.Settings.EnableDebugging = false;
    }
}
