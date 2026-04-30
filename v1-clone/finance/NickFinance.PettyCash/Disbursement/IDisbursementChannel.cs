namespace NickFinance.PettyCash.Disbursement;

/// <summary>
/// Pluggable rail for disbursing approved petty-cash vouchers. v1 ships
/// two: <see cref="OfflineCashChannel"/> (the custodian hands cash over
/// physically — no API call) and <see cref="NickCommsMomoChannel"/>
/// (HTTPS POST to NickComms.Gateway's <c>/api/disburse/momo</c> endpoint
/// which fronts Hubtel Merchant API).
/// </summary>
public interface IDisbursementChannel
{
    /// <summary>Channel identifier persisted on the voucher for audit (<c>"cash"</c>, <c>"momo:hubtel"</c>, …).</summary>
    string Channel { get; }

    /// <summary>Send the funds. Returns the rail's reference (transaction id, MoMo reference, etc.) for posting alongside the journal entry.</summary>
    Task<DisbursementResult> DisburseAsync(DisbursementRequest req, CancellationToken ct = default);
}

public sealed record DisbursementRequest(
    Guid VoucherId,
    string VoucherNo,
    long AmountMinor,
    string CurrencyCode,
    string PayeeName,
    string? PayeeMomoNumber,
    string? PayeeMomoNetwork,
    string ClientReference,
    long TenantId = 1);

public sealed record DisbursementResult(
    bool Accepted,
    string Channel,
    string? RailReference,
    string? FailureReason);

/// <summary>
/// Default offline channel — assumes the custodian hands cash over in
/// person. Returns <see cref="DisbursementResult.Accepted"/> immediately
/// and uses the voucher number as the rail reference.
/// </summary>
public sealed class OfflineCashChannel : IDisbursementChannel
{
    public string Channel => "cash";

    public Task<DisbursementResult> DisburseAsync(DisbursementRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        return Task.FromResult(new DisbursementResult(true, Channel, req.VoucherNo, null));
    }
}
