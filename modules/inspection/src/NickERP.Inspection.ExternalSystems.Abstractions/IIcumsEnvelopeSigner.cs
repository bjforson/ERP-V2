namespace NickERP.Inspection.ExternalSystems.Abstractions;

/// <summary>
/// Sprint 9 / FU-icums-signing — pre-emptive HMAC-SHA256 detached
/// envelope signing for IcumsGh outbound submissions.
///
/// <para>
/// Lives in the <c>Abstractions</c> assembly so it is the same
/// <see cref="System.Type"/> seen by both the plugin DLL (loaded via
/// reflection from the <c>plugins/</c> folder) and the host (which
/// registers the concrete implementation in DI). If the contract
/// drifts, the plugin's <c>minHostContractVersion</c> in
/// <c>plugin.json</c> protects against load-time mismatches.
/// </para>
///
/// <para>
/// <b>Why detached.</b> The downstream ICUMS-side pickup historically
/// reads the envelope JSON file as-is. Embedding the signature inside
/// the JSON would force every existing consumer to learn a new shape.
/// A sibling <c>.sig</c> file alongside the envelope keeps the
/// envelope unchanged and lets the verifier read both files without
/// parsing the JSON. The signature header carries the key id so a
/// rotated verifier knows which key to attempt first.
/// </para>
///
/// <para>
/// <b>Pre-emptive.</b> ICUMS has not asked for signed envelopes yet.
/// Signing lands behind feature flag <c>IcumsGh:Sign</c> (default
/// false) so when the contract drops we just enable the flag.
/// </para>
///
/// <para>
/// <b>Tenant scope.</b> Implementations resolve the active key by the
/// caller's tenant. There is no cross-tenant signing — rotation is
/// per-tenant for v0 (FU-icums-signing).
/// </para>
/// </summary>
public interface IIcumsEnvelopeSigner
{
    /// <summary>
    /// Sign a serialised envelope payload for the given tenant. The
    /// returned <see cref="SignedEnvelope.Payload"/> is the SAME bytes
    /// the caller passed in (signature is detached); the signature is
    /// in <see cref="SignedEnvelope.SignatureHeader"/>.
    /// </summary>
    /// <param name="tenantId">Tenant scope as a string (matches the host's tenant claim format).</param>
    /// <param name="envelopePayload">The serialised envelope bytes (typically UTF-8 JSON).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The original payload + a signature header.</returns>
    /// <exception cref="System.InvalidOperationException">No active signing key for the tenant.</exception>
    Task<SignedEnvelope> SignAsync(string tenantId, ReadOnlyMemory<byte> envelopePayload, CancellationToken ct = default);

    /// <summary>
    /// Verify a previously-signed envelope. Accepts the active key OR
    /// any key in its <c>VerificationOnlyUntil &gt; now</c> overlap
    /// window. A key whose window has closed is rejected even if its
    /// id matches the header.
    /// </summary>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="envelopePayload">The envelope bytes whose signature is being verified.</param>
    /// <param name="signatureHeader">The header value (the contents of the <c>.sig</c> sibling file).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verdict + the key id used (when valid) or a failure reason (when invalid).</returns>
    Task<SignatureVerification> VerifyAsync(string tenantId, ReadOnlyMemory<byte> envelopePayload, string signatureHeader, CancellationToken ct = default);
}

/// <summary>
/// Output of <see cref="IIcumsEnvelopeSigner.SignAsync"/>.
///
/// <para>
/// <see cref="SignatureHeader"/> format:
/// <c>icums-hmac-sha256 keyId=&lt;k&gt; sig=&lt;base64-of-hmac&gt;</c>.
/// The verifier parses this header to pick the key. The exact format
/// is part of the on-disk contract — once a real ICUMS receiver is
/// reading <c>.sig</c> files, do not rev this without coordinating.
/// </para>
/// </summary>
public sealed record SignedEnvelope(byte[] Payload, string SignatureHeader);

/// <summary>
/// Outcome of <see cref="IIcumsEnvelopeSigner.VerifyAsync"/>. On
/// success, <see cref="KeyIdUsed"/> identifies which generation of
/// the key matched (useful for telemetry: are we still serving sigs
/// from a retired-but-in-window key?). On failure,
/// <see cref="FailureReason"/> is human-readable; do not surface to
/// untrusted callers verbatim.
/// </summary>
public sealed record SignatureVerification(bool Valid, string? KeyIdUsed, string? FailureReason);
