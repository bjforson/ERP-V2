using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using NickERP.Platform.Identity.Database.Services;

namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 21 / Phase B — portal-side
/// <see cref="IInviteTokenHashEnvelope"/> backed by ASP.NET Core
/// data protection. Mirrors the
/// <see cref="PortalEdgeKeyHashEnvelope"/> shape (Sprint 13);
/// kept as a separate envelope so an invite-token hash never
/// collides with an edge-API-key hash even when both share the
/// same data-protection key ring.
/// </summary>
/// <remarks>
/// In multi-host clusters, ops should set <c>InviteTokens:HashKey</c>
/// explicitly so the data-protection key-ring divergence between
/// hosts doesn't matter — same root cause as
/// <c>FU-icums-cluster-key-ring</c>.
/// </remarks>
public sealed class PortalInviteTokenHashEnvelope : IInviteTokenHashEnvelope
{
    /// <summary>
    /// Distinct data-protection purpose so an invite-token hash
    /// derivation never aliases an edge-key derivation.
    /// </summary>
    public const string DataProtectionPurpose = "invite-token-hash-key-v1";

    private readonly IDataProtectionProvider _dataProtection;

    public PortalInviteTokenHashEnvelope(IDataProtectionProvider dataProtection)
    {
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
    }

    public byte[] DeriveFallbackHashKey()
    {
        var protector = _dataProtection.CreateProtector(DataProtectionPurpose);
        var pattern = Encoding.UTF8.GetBytes("invite-token-hash-key-derivation-v1");
        var enc = protector.Protect(pattern);
        return SHA256.HashData(enc);
    }
}
