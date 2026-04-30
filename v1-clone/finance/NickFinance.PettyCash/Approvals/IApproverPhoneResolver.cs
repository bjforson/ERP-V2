namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// Resolves approver users to / from their E.164 phone numbers. The
/// <see cref="WhatsAppApprovalNotifier"/> uses
/// <see cref="ResolvePhoneByUserIdAsync"/> to find where to send the
/// outbound template message; the inbound webhook handler in the WebApp
/// uses <see cref="ResolveUserIdByPhoneAsync"/> to map an incoming
/// <c>APPROVE PC-...</c> reply back to the canonical user that decides
/// the voucher.
///
/// <para>
/// The real implementation will live in <c>NickERP.Platform.Identity</c>
/// once that module exists — the platform owns the user-profile store and
/// the phone column. For now NickFinance ships
/// <see cref="NoopApproverPhoneResolver"/> which returns null in both
/// directions; this keeps the engine and webhook compilable and the
/// outbound notify path becomes a no-op when no phone is registered. Once
/// the Identity module ships its resolver, swap the DI registration in
/// <c>Program.cs</c> — no other call-site changes.
/// </para>
/// </summary>
public interface IApproverPhoneResolver
{
    /// <summary>Returns the E.164 phone for the given user, or null if unknown.</summary>
    Task<string?> ResolvePhoneByUserIdAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Reverse lookup for inbound webhooks. Returns the user id whose registered phone matches, or null.</summary>
    Task<Guid?> ResolveUserIdByPhoneAsync(string phoneE164, CancellationToken ct = default);
}

/// <summary>
/// Default resolver — returns null in both directions. Keeps NickFinance
/// shippable without the platform identity store; the WhatsApp outbound
/// path silently no-ops and the inbound webhook ignores any reply because
/// it can't map a phone back to a user. Replace with the
/// platform-identity-backed implementation once
/// <c>NickERP.Platform.Identity</c> ships.
/// </summary>
public sealed class NoopApproverPhoneResolver : IApproverPhoneResolver
{
    public Task<string?> ResolvePhoneByUserIdAsync(Guid userId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<Guid?> ResolveUserIdByPhoneAsync(string phoneE164, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
}
