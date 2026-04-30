using System.Text;
using Xunit;

namespace NickFinance.Pdf.Tests;

/// <summary>
/// Smoke + determinism checks for <see cref="QrRenderer"/>. We don't try
/// to decode the QR back here — the encoder is a vendored library — but
/// we do check that we get a non-empty PNG and that the same payload
/// gives the same bytes (handy when generators want to dedupe pages).
/// </summary>
public sealed class QrRendererTests
{
    private static readonly byte[] PngMagic = { 0x89, (byte)'P', (byte)'N', (byte)'G' };

    [Fact]
    public void Generate_returns_a_non_empty_png()
    {
        var bytes = QrRenderer.Generate("IRN-2026-04-00001");

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 50, $"PNG should be at least 50 bytes, was {bytes.Length}");
        Assert.True(bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic),
            "Output is not a PNG (does not start with the PNG magic bytes).");
    }

    [Fact]
    public void Generate_is_deterministic_for_same_input()
    {
        // QRCoder doesn't bake timestamps into PNGs, so the same payload +
        // module size should produce byte-identical output. Useful for
        // snapshot tests downstream and for cache lookups.
        var first = QrRenderer.Generate("IRN-LIVE-12345", pixelsPerModule: 8);
        var second = QrRenderer.Generate("IRN-LIVE-12345", pixelsPerModule: 8);

        Assert.Equal(first, second);
    }
}
