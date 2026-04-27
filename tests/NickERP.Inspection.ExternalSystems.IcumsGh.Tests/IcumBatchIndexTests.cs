using NickERP.Inspection.ExternalSystems.IcumsGh;

namespace NickERP.Inspection.ExternalSystems.IcumsGh.Tests;

/// <summary>
/// ICUMS schema-drift snapshot. Drops a representative <c>BOEScanDocument</c>
/// JSON file in a temp folder, refreshes the index, and asserts the
/// (ContainerNumber → DeclarationNumber, HouseBlNumber, RegimeCode) tuples
/// the index produces. Any future ICUMS schema rename or path-shift will
/// flip these assertions and force a deliberate update.
/// </summary>
public sealed class IcumBatchIndexTests : IDisposable
{
    private readonly string _tempDir;

    public IcumBatchIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nickerp-icums-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void RefreshIfStale_ParsesBoeScanDocumentTuples()
    {
        // Regression guarded: ICUMS schema drift on Header.DeclarationNumber /
        // ManifestDetails.HouseBL / Header.RegimeCode field paths used by
        // IcumBatchIndex.ReindexFile.
        const string sampleJson = """
        {
          "BOEScanDocument": [
            {
              "Header": {
                "DeclarationNumber": "C 123456 22",
                "RegimeCode": "40",
                "ClearanceType": "IM"
              },
              "ManifestDetails": {
                "HouseBL": "HBL-2026-001",
                "DeliveryPlace": "WTTMA1MPS3"
              },
              "ContainerDetails": {
                "ContainerNumber": "MSCU1234567"
              }
            },
            {
              "Header": {
                "DeclarationNumber": "C 999999 22",
                "RegimeCode": "80",
                "ClearanceType": "CMR"
              },
              "ManifestDetails": {
                "HouseBL": "HBL-2026-002",
                "DeliveryPlace": "WTTKD2MPS3"
              },
              "ContainerDetails": {
                "ContainerNumber": "TGHU7654321"
              }
            }
          ]
        }
        """;
        var path = Path.Combine(_tempDir, "batch-2026-04-26.json");
        File.WriteAllText(path, sampleJson);

        var index = new IcumBatchIndex(_tempDir, TimeSpan.Zero);
        index.RefreshIfStale();

        var stats = index.Stats();
        stats.Files.Should().Be(1);
        stats.Documents.Should().Be(2);

        var first = index.GetByContainer("MSCU1234567");
        first.Should().HaveCount(1);
        first[0].DeclarationNumber.Should().Be("C 123456 22");
        first[0].HouseBlNumber.Should().Be("HBL-2026-001");
        first[0].RegimeCode.Should().Be("40");

        var second = index.GetByContainer("TGHU7654321");
        second.Should().HaveCount(1);
        second[0].DeclarationNumber.Should().Be("C 999999 22");
        second[0].HouseBlNumber.Should().Be("HBL-2026-002");
        second[0].RegimeCode.Should().Be("80");
    }
}
