namespace NickERP.Inspection.Edge.Abstractions;

/// <summary>
/// Sprint 40 / Phase A — canonical scan-package contract.
///
/// <para>
/// Adopted from the 2026-05-05 external doc-analysis (Central X-Ray Image
/// Analysis Engineering Design §9): "Each scan package should include a
/// canonical JSON metadata record plus one or more image files."
/// </para>
///
/// <para>
/// <b>Why a canonical bundle.</b> Pre-Sprint-40 the edge-node buffered
/// audit-event-replay payloads but the scan artifacts themselves had no
/// signed, sha256-checksummed wire shape. That left three holes:
/// <list type="bullet">
///   <item><description>No evidentiary chain-of-custody from gateway →
///   server. A truncated or tampered image could land silently if the
///   underlying transport's TLS terminated short of the storage layer.</description></item>
///   <item><description>No deduplication on retry. Two replays of the
///   same captured set hit the server as two distinct payloads with
///   independent storage paths.</description></item>
///   <item><description>No stable contract for offline-replay clients —
///   an air-gapped lane operator who hand-carries scan media to the
///   server had no documented bundle shape to put on disk.</description></item>
/// </list>
/// The <see cref="ScanPackage"/> shape closes those holes. Each package
/// carries (a) the universal customs concepts that uniquely identify the
/// scan (container/vehicle/declaration/manifest), (b) one or more
/// <see cref="ImageFile"/> entries with per-file sha256 hex digests, and
/// (c) an HMAC-signed manifest binding the whole bundle to the issuing
/// edge node's per-edge HMAC key (Sprint 13 T2 EdgeAuthHandler).
/// </para>
///
/// <para>
/// <b>Vendor-neutral by design.</b> No Ghana / FS6000 / ICUMS field names.
/// <see cref="ContainerNumber"/> / <see cref="DeclarationNumber"/> etc.
/// are universal customs concepts; vendor-specific extras live in the
/// adapter's parsed-shape <c>Metadata</c> and are not part of this
/// canonical contract.
/// </para>
///
/// <para>
/// <b>Backward-compatible.</b> Existing FS6000 / Mock / ASE adapters
/// continue using their existing <see cref="NickERP.Inspection.Scanners.Abstractions.IScannerAdapter"/>
/// surface unchanged; the canonical shape is additive — adapters that
/// implement the new <c>ParseScanAsync</c> overload start producing
/// <see cref="ScanPackage"/> bundles. See Sprint 40 Phase B.
/// </para>
/// </summary>
/// <param name="ScanId">
/// Stable UUID for this scan. Generated once at the edge; reused on every
/// replay so the server idempotency key collapses retries to a single
/// row. UUID-shaped (32 hex chars + 4 hyphens); validator rejects empty
/// or malformed.
/// </param>
/// <param name="SiteId">
/// Vendor-neutral site identifier — typically a port code or terminal
/// code (e.g. <c>"TKD"</c>, <c>"TMA"</c>). Free-form string, populated
/// from <c>Location.Code</c> on the server side.
/// </param>
/// <param name="ScannerId">
/// Vendor-neutral scanner identifier — typically the
/// <c>ScannerDeviceInstance.Id</c> string or a stable adapter-specific
/// device serial. Free-form string.
/// </param>
/// <param name="GatewayId">
/// Logical edge-node id (e.g. <c>"edge-tema-1"</c>). Matches the
/// <c>EdgeNodeAuthorization.EdgeNodeId</c> column. Used by the manifest
/// HMAC verification to look up the matching per-edge key.
/// </param>
/// <param name="ScanType">
/// What kind of scan this is — typically <c>"primary"</c>,
/// <c>"calibration"</c>, <c>"sweep"</c>. Free-form string at this layer;
/// downstream consumers may narrow.
/// </param>
/// <param name="OccurredAt">
/// Wall-clock timestamp when the scanner captured the bundle. Set at the
/// edge; UTC with offset preserved.
/// </param>
/// <param name="OperatorId">
/// Vendor-neutral operator/user identifier. Free-form; may be empty when
/// the scanner runs unattended.
/// </param>
/// <param name="ContainerNumber">
/// ISO 6346 container number (e.g. <c>"MSCU1234567"</c>). May be empty
/// when the subject is not a container (e.g. truck-only scan).
/// </param>
/// <param name="VehiclePlate">
/// Vehicle license plate. May be empty when the subject is a container
/// without a vehicle on this lane.
/// </param>
/// <param name="DeclarationNumber">
/// Customs declaration / BoE / SAD reference. May be empty when no
/// declaration is bound to this scan yet (the document-matcher worker
/// fills it later).
/// </param>
/// <param name="ManifestNumber">
/// Vessel manifest reference (e.g. ICUMS manifest id, Bill of Lading
/// number). May be empty when no manifest is bound yet.
/// </param>
/// <param name="ImageFiles">
/// One or more image files comprising this scan. Each file carries its
/// own sha256 — the <see cref="ScanPackageManifest"/> JSON binds these
/// hashes into the signed envelope.
/// </param>
/// <param name="ManifestSha256">
/// SHA-256 of the canonical manifest JSON (deterministic key ordering,
/// no whitespace). 32 bytes; hex-encoded forms in the JSON manifest.
/// Verifier recomputes this and rejects on mismatch.
/// </param>
/// <param name="ManifestSignature">
/// HMAC-SHA256 signature of the manifest JSON under the per-edge HMAC
/// key. 32 bytes. Verifier recomputes and constant-time-compares.
/// </param>
public sealed record ScanPackage(
    string ScanId,
    string SiteId,
    string ScannerId,
    string GatewayId,
    string ScanType,
    DateTimeOffset OccurredAt,
    string OperatorId,
    string ContainerNumber,
    string VehiclePlate,
    string DeclarationNumber,
    string ManifestNumber,
    IReadOnlyList<ImageFile> ImageFiles,
    byte[] ManifestSha256,
    byte[] ManifestSignature);

/// <summary>
/// Sprint 40 / Phase A — one image inside a <see cref="ScanPackage"/>.
///
/// <para>
/// The canonical contract carries the bytes inline. On the wire each
/// file's <see cref="Data"/> is base64-encoded as part of the JSON
/// envelope; in-memory we work with the raw bytes. The per-file
/// <see cref="Sha256Hex"/> is computed at edge-build-time and is the
/// load-bearing tamper detector — the manifest binds it but the file
/// content also recomputes against it, so any byte flip on the wire
/// fails verification independently of the manifest signature.
/// </para>
///
/// <para>
/// <b>View identifier.</b> <see cref="View"/> is vendor-neutral —
/// typical values <c>"top"</c>, <c>"side"</c>, <c>"high-energy"</c>,
/// <c>"low-energy"</c>, <c>"material"</c>. The canonical contract does
/// not enumerate them; downstream consumers interpret per their domain.
/// </para>
/// </summary>
/// <param name="FileName">
/// Short non-path filename (e.g. <c>"high.img"</c>). The validator
/// rejects path separators to keep the filename a pure leaf — storage
/// paths are server-built.
/// </param>
/// <param name="Sha256Hex">
/// Lowercase hex sha256 of <see cref="Data"/>. 64 chars exactly.
/// Validator recomputes and rejects on mismatch.
/// </param>
/// <param name="View">
/// Vendor-neutral view tag (see class remarks). Free-form; may be empty.
/// </param>
/// <param name="SizeBytes">
/// Size of <see cref="Data"/> in bytes. Validator rejects when this
/// disagrees with <c>Data.Length</c> — the size is part of the manifest
/// so a mismatch flags either tampering or a corrupted bundle.
/// </param>
/// <param name="Data">
/// Raw image bytes. The wire envelope base64-encodes this; in-memory
/// keep raw bytes for cheap sha256 recomputation.
/// </param>
public sealed record ImageFile(
    string FileName,
    string Sha256Hex,
    string View,
    long SizeBytes,
    byte[] Data);
