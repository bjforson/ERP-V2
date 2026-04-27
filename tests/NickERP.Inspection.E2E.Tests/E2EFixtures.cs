using NickERP.Inspection.Scanners.FS6000.Tests;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Test fixtures shared across the e2e test. Two helpers:
/// <list type="number">
///   <item>FS6000 triplet drop — synthesizes <c>{stem}high.img</c>,
///         <c>{stem}low.img</c>, <c>{stem}material.img</c> bytes via the
///         existing <see cref="FS6000FormatDecoderTests.BuildChannel"/>
///         (the same synthesizer that produced the recorded byte-parity
///         constants in the parity test).</item>
///   <item>ICUMS BOE batch JSON — a minimum-shape <c>BOEScanDocument[]</c>
///         payload mirroring the schema-drift test fixture in
///         <c>IcumBatchIndexTests</c>. The <c>ContainerNumber</c> is keyed
///         to the same stem as the scan so <c>FetchDocumentsAsync</c>
///         finds it.</item>
/// </list>
/// </summary>
internal static class E2EFixtures
{
    /// <summary>
    /// Drop a complete FS6000 triplet under <paramref name="watchPath"/>
    /// with the supplied stem. Bytes go through the same synthesizer as
    /// the FS6000 decoder parity test.
    /// </summary>
    public static void WriteFs6000Triplet(string watchPath, string stem)
    {
        Directory.CreateDirectory(watchPath);

        var high = FS6000FormatDecoderTests.BuildChannel(bitDepth: 16, fillSeed: 0x10);
        var low = FS6000FormatDecoderTests.BuildChannel(bitDepth: 16, fillSeed: 0x40);
        var material = FS6000FormatDecoderTests.BuildChannel(bitDepth: 8, fillSeed: 0x07);

        File.WriteAllBytes(Path.Combine(watchPath, stem + "high.img"), high);
        File.WriteAllBytes(Path.Combine(watchPath, stem + "low.img"), low);
        File.WriteAllBytes(Path.Combine(watchPath, stem + "material.img"), material);
    }

    /// <summary>
    /// Drop one ICUMS batch JSON file in <paramref name="dropPath"/> with
    /// a single BOE keyed to <paramref name="containerNumber"/>. The BOE
    /// uses <c>DeliveryPlace = "WTTMA1MPS3"</c> (port code <c>TMA</c>) so
    /// the gh-customs <c>GH-PORT-MATCH</c> rule passes for a Tema scan.
    /// </summary>
    public static void WriteIcumsBatchForContainer(string dropPath, string containerNumber)
    {
        Directory.CreateDirectory(dropPath);

        var json = $$"""
        {
          "BOEScanDocument": [
            {
              "Header": {
                "DeclarationNumber": "C 123456 26",
                "RegimeCode": "40",
                "ClearanceType": "IM"
              },
              "ManifestDetails": {
                "HouseBL": "HBL-2026-D4-001",
                "DeliveryPlace": "WTTMA1MPS3"
              },
              "ContainerDetails": {
                "ContainerNumber": "{{containerNumber}}"
              }
            }
          ]
        }
        """;

        File.WriteAllText(Path.Combine(dropPath, $"e2e-batch-{containerNumber}.json"), json);
    }
}
