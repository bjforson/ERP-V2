using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using NickERP.Platform.Audit.Database;

namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 13 / P2-FU-edge-auth — portal-side
/// <see cref="IEdgeKeyHashEnvelope"/> backed by ASP.NET Core data
/// protection. Mirrors the inspection-host-side
/// <c>DataProtectionEdgeKeyHashEnvelope</c>; both must derive the
/// SAME bytes for any value of <c>EdgeAuth:HashKey</c> so a key
/// issued via the portal authenticates against the inspection-host
/// auth handler.
///
/// <para>
/// Co-locating both implementations is intentional: the abstraction
/// lives in <c>Platform.Audit.Database</c> (no ASP.NET dependency) so
/// each host wires its own DP-backed implementation. The two derivations
/// MUST stay in sync — both use the same purpose string + the same
/// pattern bytes + the same SHA-256 hash, so any host that has the same
/// data-protection key ring + the same explicit
/// <c>EdgeAuth:HashKey</c> config produces the same hash key.
/// </para>
///
/// <para>
/// In multi-host clusters, ops should set <c>EdgeAuth:HashKey</c>
/// explicitly so the data-protection key-ring divergence between
/// hosts doesn't matter — surfaced as the
/// <c>FU-icums-cluster-key-ring</c> follow-up; same root cause.
/// </para>
/// </summary>
public sealed class PortalEdgeKeyHashEnvelope : IEdgeKeyHashEnvelope
{
    /// <summary>
    /// Same purpose string as the inspection-host envelope so the two
    /// hosts derive identical keys when they share a data-protection
    /// key ring.
    /// </summary>
    public const string DataProtectionPurpose = "edge-auth-hash-key-v1";

    private readonly IDataProtectionProvider _dataProtection;

    public PortalEdgeKeyHashEnvelope(IDataProtectionProvider dataProtection)
    {
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
    }

    public byte[] DeriveFallbackHashKey()
    {
        var protector = _dataProtection.CreateProtector(DataProtectionPurpose);
        var pattern = Encoding.UTF8.GetBytes("edge-auth-hash-key-derivation-v1");
        var enc = protector.Protect(pattern);
        return SHA256.HashData(enc);
    }
}
