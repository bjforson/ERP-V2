using Xunit;

namespace NickFinance.Pdf.Tests;

/// <summary>
/// xUnit fixture that runs <see cref="QuestPdfBootstrap.Configure"/> exactly
/// once for the test assembly. QuestPDF refuses to render until a license
/// has been declared, so every test class that touches a generator opts
/// into this collection.
/// </summary>
public sealed class PdfFixture
{
    public PdfFixture()
    {
        QuestPdfBootstrap.Configure();
    }
}

[CollectionDefinition("pdf")]
public sealed class PdfCollection : ICollectionFixture<PdfFixture>
{
}
