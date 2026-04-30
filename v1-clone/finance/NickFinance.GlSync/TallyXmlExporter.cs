using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;

namespace NickFinance.GlSync;

/// <summary>
/// Produces Tally Prime XML import payload covering one date range. The
/// envelope follows the spec from <c>docs/modules/spikes/01-tally-xml-roundtrip.md</c>:
/// <code>
/// &lt;ENVELOPE&gt;
///   &lt;HEADER&gt;
///     &lt;TALLYREQUEST&gt;Import Data&lt;/TALLYREQUEST&gt;
///   &lt;/HEADER&gt;
///   &lt;BODY&gt;
///     &lt;IMPORTDATA&gt;
///       &lt;REQUESTDESC&gt;
///         &lt;REPORTNAME&gt;Vouchers&lt;/REPORTNAME&gt;
///         &lt;STATICVARIABLES&gt;
///           &lt;SVCURRENTCOMPANY&gt;{company}&lt;/SVCURRENTCOMPANY&gt;
///         &lt;/STATICVARIABLES&gt;
///       &lt;/REQUESTDESC&gt;
///       &lt;REQUESTDATA&gt;
///         &lt;TALLYMESSAGE&gt;
///           &lt;VOUCHER VCHTYPE="Journal" ACTION="Create"&gt; … &lt;/VOUCHER&gt;
///         &lt;/TALLYMESSAGE&gt;
///         …
///       &lt;/REQUESTDATA&gt;
///     &lt;/IMPORTDATA&gt;
///   &lt;/BODY&gt;
/// &lt;/ENVELOPE&gt;
/// </code>
/// </summary>
public sealed class TallyXmlExporter
{
    private readonly LedgerDbContext _ledger;
    public TallyXmlExporter(LedgerDbContext ledger) => _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public async Task<byte[]> ExportAsync(string companyName, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(companyName)) throw new ArgumentException("Company name required.", nameof(companyName));

        var events = await _ledger.Events
            .AsNoTracking()
            .Include(e => e.Lines)
            .Where(e => e.TenantId == tenantId
                     && e.EffectiveDate >= from
                     && e.EffectiveDate <= to
                     && e.EventType == LedgerEventType.Posted)
            .OrderBy(e => e.EffectiveDate)
            .ThenBy(e => e.CommittedAt)
            .ToListAsync(ct);

        var envelope = new XElement("ENVELOPE",
            new XElement("HEADER",
                new XElement("TALLYREQUEST", "Import Data")),
            new XElement("BODY",
                new XElement("IMPORTDATA",
                    new XElement("REQUESTDESC",
                        new XElement("REPORTNAME", "Vouchers"),
                        new XElement("STATICVARIABLES",
                            new XElement("SVCURRENTCOMPANY", companyName))),
                    new XElement("REQUESTDATA",
                        events.Select(BuildVoucher)))));
        var xml = new XDocument(envelope);
        using var ms = new MemoryStream();
        using (var sw = new StreamWriter(ms, new UTF8Encoding(false)))
        {
            xml.Save(sw);
        }
        return ms.ToArray();
    }

    private static XElement BuildVoucher(LedgerEvent e)
    {
        // Tally requires unique VCHKEY across imports — use the idempotency
        // key, prefixed so it's obviously NickFinance-issued.
        var vchKey = $"NF-{e.IdempotencyKey}";

        var voucher = new XElement("TALLYMESSAGE",
            new XElement("VOUCHER",
                new XAttribute("VCHTYPE", "Journal"),
                new XAttribute("ACTION", "Create"),
                new XElement("VCHKEY", vchKey),
                new XElement("DATE", e.EffectiveDate.ToString("yyyyMMdd")),
                new XElement("VOUCHERTYPENAME", "Journal"),
                new XElement("NARRATION", e.Narration),
                e.Lines.OrderBy(l => l.LineNo).Select(BuildLedgerEntry)));
        return voucher;
    }

    private static XElement BuildLedgerEntry(LedgerEventLine l)
    {
        // Tally encodes debits as positive and credits as negative on AMOUNT,
        // with ISDEEMEDPOSITIVE flagged accordingly.
        var dr = l.DebitMinor > 0;
        var amount = dr ? l.DebitMinor : -l.CreditMinor;
        return new XElement("ALLLEDGERENTRIES.LIST",
            new XElement("LEDGERNAME", l.AccountCode),
            new XElement("ISDEEMEDPOSITIVE", dr ? "Yes" : "No"),
            new XElement("AMOUNT", ((decimal)amount / 100m).ToString("0.00", CultureInfo.InvariantCulture)),
            string.IsNullOrEmpty(l.Description) ? null! : new XElement("NARRATION", l.Description));
    }
}
