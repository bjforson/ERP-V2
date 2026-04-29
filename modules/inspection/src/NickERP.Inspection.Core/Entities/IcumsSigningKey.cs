using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 9 / FU-icums-signing — per-tenant HMAC-SHA256 signing key for
/// the IcumsGh adapter's outbound envelope signing flow.
///
/// <para>
/// One row per (tenant, key generation). Keys are NEVER deleted — they
/// are retired with <see cref="RetiredAt"/> set + a
/// <see cref="VerificationOnlyUntil"/> window during which the verifier
/// still accepts signatures produced under this key. Once the window
/// closes the key remains in the table for forensic / audit purposes
/// but is treated as fully revoked.
/// </para>
///
/// <para>
/// <b>At rest.</b> <see cref="KeyMaterialEncrypted"/> holds the raw
/// HMAC key wrapped via ASP.NET Core data protection with the purpose
/// string <c>icums-signing-keys-v1</c>. The plain HMAC key never lands
/// in the database. Compromise of the row alone (e.g. an unscoped
/// <c>SELECT</c> via <c>postgres</c>) does not yield the key — the
/// attacker would also need the data-protection key ring on the host.
/// </para>
///
/// <para>
/// <b>Tenancy.</b> Tenant-scoped, RLS-enforced through
/// <c>tenant_isolation_icums_signing_keys</c> on the <c>inspection</c>
/// schema. Two tenants with their own ICUMS instances each maintain
/// their own key generation; rotation in tenant A does not touch
/// tenant B.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b>
/// <list type="number">
///   <item><description><c>created</c> — row inserted with
///   <see cref="ActivatedAt"/>, <see cref="RetiredAt"/>,
///   <see cref="VerificationOnlyUntil"/> all NULL. Not yet usable for
///   signing. The very first key for a tenant is created
///   pre-activated (see <see cref="ActivatedAt"/> populated at insert
///   time) so the adapter can sign immediately on first enable.</description></item>
///   <item><description><c>active for signing</c> —
///   <see cref="ActivatedAt"/> populated, <see cref="RetiredAt"/>
///   NULL. The signer picks this key when asked to sign.</description></item>
///   <item><description><c>verification-only</c> —
///   <see cref="RetiredAt"/> populated,
///   <see cref="VerificationOnlyUntil"/> in the future. Signer no
///   longer picks this key for new signatures, but verifier accepts
///   signatures whose <c>keyId</c> matches this row.</description></item>
///   <item><description><c>fully retired</c> — both
///   <see cref="RetiredAt"/> and <see cref="VerificationOnlyUntil"/>
///   in the past. Signatures are rejected.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class IcumsSigningKey : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant scope. Mandatory; rotation is per-tenant for v0.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Short identifier embedded in the signature header
    /// (<c>icums-hmac-sha256 keyId=k1 sig=...</c>). Unique within a
    /// tenant — see <c>ux_icums_signing_keys_tenant_keyid</c>.
    /// Convention: <c>k1</c>, <c>k2</c>, ... incremented on each
    /// rotation.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>
    /// HMAC key material wrapped via ASP.NET Core data protection
    /// (purpose <c>icums-signing-keys-v1</c>). Never the raw bytes.
    /// </summary>
    public byte[] KeyMaterialEncrypted { get; set; } = Array.Empty<byte>();

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the key entered "active for signing" state. NULL = not yet activated.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>When the key stopped being used to sign. NULL = still active (or not yet activated).</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>
    /// End of acceptance window after retirement. Verifier accepts
    /// signatures whose <c>keyId</c> matches this row while
    /// <c>now &lt; VerificationOnlyUntil</c>. NULL = no window
    /// (fully retired immediately) or never retired.
    /// </summary>
    public DateTimeOffset? VerificationOnlyUntil { get; set; }
}
