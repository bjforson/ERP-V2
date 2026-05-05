namespace NickERP.Platform.Tenancy.Database.Storage;

/// <summary>
/// Sprint 51 / Phase E — abstraction over the storage backend for
/// tenant export bundles. Pre-Sprint-51 the runner wrote zip bundles
/// directly to the filesystem under
/// <c>Tenancy:Export:OutputPath</c>. The abstraction lets ops swap to
/// S3 / blob storage without touching the bundle builder.
/// </summary>
/// <remarks>
/// <para>
/// Two implementations live in this namespace:
/// <list type="bullet">
///   <item><see cref="FilesystemTenantExportStorage"/> — preserves the
///     pre-Phase-E behaviour. Default when no
///     <c>Tenancy:Export:Storage:Type</c> is configured.</item>
///   <item><see cref="S3TenantExportStorage"/> — S3-compatible HTTP
///     PUT/GET/DELETE against any AWS S3, Minio, or compatible
///     endpoint. Tunable via <c>Tenancy:Export:Storage:S3:*</c>.</item>
/// </list>
/// </para>
/// <para>
/// The abstraction surface is intentionally small (write / open-read /
/// delete) — bundle builds don't need streaming append, since the
/// builder writes to a temp filesystem path then hands the whole
/// blob to the storage layer. That keeps the S3 implementation a
/// single PutObject; we don't need multipart upload until bundles
/// regularly exceed ~5 GB.
/// </para>
/// </remarks>
public interface ITenantExportStorage
{
    /// <summary>
    /// A short identifier for this storage backend used in logs and
    /// audit events. e.g. <c>"filesystem"</c> or <c>"s3"</c>.
    /// </summary>
    string BackendName { get; }

    /// <summary>
    /// Persist <paramref name="bytes"/> as the artifact for the given
    /// export id. Returns an opaque locator (filesystem path or
    /// <c>s3://bucket/key</c>) that the runner stores in
    /// <c>TenantExportRequest.ArtifactPath</c>; the same locator is
    /// later passed to <see cref="OpenReadAsync"/> on download.
    /// </summary>
    /// <param name="exportId">The export id; used to build the
    /// per-export storage key.</param>
    /// <param name="tenantId">The tenant id; namespaces the storage
    /// path so a tenant's exports stay grouped.</param>
    /// <param name="bytes">The full bundle bytes. Already a complete
    /// zip on disk; the storage layer just persists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opaque locator. For filesystem this is the absolute
    /// path; for S3 this is <c>s3://bucket/key</c>.</returns>
    Task<string> WriteAsync(Guid exportId, long tenantId, byte[] bytes, CancellationToken ct = default);

    /// <summary>
    /// Open a read stream over the artifact at <paramref name="locator"/>.
    /// Returns null if the artifact is missing — the caller should map
    /// that to a 404 or "artifact missing" error in the audit trail.
    /// </summary>
    /// <param name="locator">The opaque locator returned by
    /// <see cref="WriteAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open read stream the caller disposes, or null if
    /// the artifact has been deleted/expired/never-existed.</returns>
    Task<Stream?> OpenReadAsync(string locator, CancellationToken ct = default);

    /// <summary>
    /// Delete the artifact at <paramref name="locator"/>. Idempotent —
    /// returns true if anything was deleted, false if the artifact was
    /// already gone. The runner uses this for both the expiry sweep
    /// and explicit revoke.
    /// </summary>
    /// <param name="locator">The opaque locator returned by
    /// <see cref="WriteAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DeleteAsync(string locator, CancellationToken ct = default);
}
