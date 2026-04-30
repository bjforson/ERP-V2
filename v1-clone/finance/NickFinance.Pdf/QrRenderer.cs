using QRCoder;

namespace NickFinance.Pdf;

/// <summary>
/// Tiny wrapper around <see cref="QRCodeGenerator"/> + <see cref="PngByteQRCode"/>
/// so the generator can swap implementations later without touching the
/// PDF pipelines. Emits a PNG byte array — QuestPDF's <c>Image()</c> API
/// consumes that directly.
/// </summary>
public static class QrRenderer
{
    /// <summary>Returns PNG bytes of the QR code for the given payload.</summary>
    /// <param name="payload">Free-form string to encode. Caller is
    /// responsible for length / character-set sanity.</param>
    /// <param name="pixelsPerModule">Module size in pixels. The default of
    /// 8 produces ~200x200 PNGs for a typical IRN payload — large enough
    /// to scan from a printed A4 invoice.</param>
    public static byte[] Generate(string payload, int pixelsPerModule = 8)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (pixelsPerModule < 1) throw new ArgumentOutOfRangeException(nameof(pixelsPerModule));

        using var qrGen = new QRCodeGenerator();
        var data = qrGen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var qr = new PngByteQRCode(data);
        return qr.GetGraphic(pixelsPerModule);
    }
}
