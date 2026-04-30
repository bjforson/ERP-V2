namespace NickFinance.AR;

/// <summary>
/// GRA e-VAT IRN issuance. Production wires the certified Hubtel /
/// Persol / Blue Skies partner once the contract lands (CFO + CTO
/// decision pending). Until then, hosts ship <see cref="StubEvatProvider"/>
/// which mints a deterministic IRN — useful for end-to-end tests but
/// must be swapped before any real customer-facing invoice goes out.
/// </summary>
public interface IEvatProvider
{
    /// <summary>Provider tag persisted on the invoice for audit.</summary>
    string Provider { get; }

    /// <summary>Submit an issued invoice and get back the GRA-allocated IRN + a QR-code payload.</summary>
    Task<EvatIssueResult> IssueAsync(EvatIssueRequest req, CancellationToken ct = default);
}

public sealed record EvatIssueRequest(
    Guid InvoiceId,
    string InvoiceNo,
    string CustomerName,
    string? CustomerTin,
    DateOnly InvoiceDate,
    string CurrencyCode,
    long NetMinor,
    long LeviesMinor,
    long VatMinor,
    long GrossMinor,
    long TenantId = 1);

public sealed record EvatIssueResult(
    bool Accepted,
    string? Irn,
    string? QrPayload,
    string? FailureReason);

/// <summary>
/// Stub — returns a visibly-sandbox IRN so an invoice issued through it
/// cannot be confused with a real GRA-allocated number. The
/// <c>SANDBOX-IRN-</c> prefix is the contract: AR + UI code use
/// <see cref="IsSandbox"/> to flag "this invoice was NOT submitted to
/// GRA". When the certified partner is wired (CFO + CTO decision —
/// see <c>finance/DEFERRED.md</c>) the partner provider takes over and
/// real <c>IRN-...</c> values land for new invoices.
/// </summary>
/// <remarks>
/// <b>Important:</b> never invoice a real customer through this provider.
/// Tests + acceptance demos only.
/// </remarks>
public sealed class StubEvatProvider : IEvatProvider
{
    /// <summary>The literal prefix every <see cref="StubEvatProvider"/>-issued IRN starts with. Use <see cref="IsSandbox"/> to check at the call-site.</summary>
    public const string SandboxPrefix = "SANDBOX-IRN-";

    /// <summary>True if the supplied IRN was minted by <see cref="StubEvatProvider"/> (case-insensitive prefix match).</summary>
    public static bool IsSandbox(string? irn) =>
        !string.IsNullOrEmpty(irn) && irn.StartsWith(SandboxPrefix, StringComparison.OrdinalIgnoreCase);

    public string Provider => "stub";

    public Task<EvatIssueResult> IssueAsync(EvatIssueRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.GrossMinor <= 0)
        {
            return Task.FromResult(new EvatIssueResult(false, null, null, "Gross amount must be positive."));
        }
        var ts = req.InvoiceDate.ToString("yyyyMMdd");
        var hex = req.InvoiceId.ToString("N")[..12].ToUpperInvariant();
        var irn = $"{SandboxPrefix}{ts}-{hex}";
        var qr = $"{irn}|{req.GrossMinor}|{req.CurrencyCode}";
        return Task.FromResult(new EvatIssueResult(true, irn, qr, null));
    }
}
