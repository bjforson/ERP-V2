namespace NickERP.Inspection.Scanners.Abstractions;

/// <summary>
/// Sprint 24 / B3.1 — derived contract for scanner adapters that pull
/// new scan records from a <b>remote DB cursor</b> rather than streaming
/// over a watched filesystem. Drives the <c>AseSyncWorker</c> in v2;
/// mirrors v1's <c>AseBackgroundService</c> shape but kept vendor-neutral
/// (any cursor-shaped source can implement this).
///
/// <para>
/// The contract is purely additive — adapters that only stream over a
/// filesystem (FS6000, mock-scanner) are unaffected and do not need to
/// implement this surface. Workers that need cursor-pull capability
/// resolve the adapter via <see cref="IPluginRegistry"/> and downcast
/// to <see cref="IScannerCursorSyncAdapter"/>; if the cast fails, the
/// worker logs and skips.
/// </para>
///
/// <para>
/// <b>Idempotency.</b> Implementations MUST be idempotent — the worker
/// may replay the same cursor window after a transient failure (e.g.
/// the host crashed before the cursor advance saved). Implementations
/// surface duplicate records via <see cref="CursorSyncRecord.IdempotencyKey"/>;
/// the host's <c>Scan.IdempotencyKey</c> uniqueness then dedupes.
/// </para>
///
/// <para>
/// <b>Cursor semantics.</b> The cursor is a string (vendor-defined; for
/// ASE it's typically the latest <c>row_id</c> or <c>last_modified</c>
/// timestamp). The host stores it on the
/// <c>scanner_cursor_sync_state</c> row keyed on
/// <c>(ScannerDeviceInstanceId)</c> and hands it back unchanged on the
/// next call. Implementations must tolerate <see cref="string.Empty"/>
/// as the "first cycle ever" cursor.
/// </para>
/// </summary>
public interface IScannerCursorSyncAdapter : IScannerAdapter
{
    /// <summary>
    /// Pull the next batch of scan records from the cursor source.
    /// </summary>
    /// <param name="config">Per-instance config the worker resolves from the device row.</param>
    /// <param name="cursor">Vendor-defined cursor; <see cref="string.Empty"/> means "from the beginning".</param>
    /// <param name="batchLimit">
    /// Soft upper bound on the batch size — implementations may emit fewer rows
    /// (cursor end) but should never exceed this. Worker honours the result.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<CursorSyncBatch> PullAsync(
        ScannerDeviceConfig config,
        string cursor,
        int batchLimit,
        CancellationToken ct);
}

/// <summary>
/// One batch returned by <see cref="IScannerCursorSyncAdapter.PullAsync"/>.
/// </summary>
/// <param name="Records">
/// Records emitted in this batch. Order is implementation-defined; the
/// host surfaces them as <see cref="RawScanArtifact"/> equivalents and
/// dedupes on <see cref="CursorSyncRecord.IdempotencyKey"/>.
/// </param>
/// <param name="NextCursor">
/// Cursor to hand back on the next call. The host persists this even
/// when <paramref name="Records"/> is empty — keeps cursors monotonic
/// across "no new data" cycles. <see cref="string.Empty"/> is valid and
/// means "stay at the start".
/// </param>
/// <param name="HasMore">
/// True iff calling again with <paramref name="NextCursor"/> would
/// return a non-empty batch. Worker uses this to decide whether to
/// drain in one cycle or yield to other workers and pick up next cycle.
/// </param>
public sealed record CursorSyncBatch(
    IReadOnlyList<CursorSyncRecord> Records,
    string NextCursor,
    bool HasMore);

/// <summary>
/// One record emitted by <see cref="IScannerCursorSyncAdapter.PullAsync"/>.
/// Mirrors the <see cref="RawScanArtifact"/> shape but sourced from a DB
/// row rather than a filesystem entry.
/// </summary>
/// <param name="DeviceId">Device id from the config — surfaced for traceability.</param>
/// <param name="SourceReference">
/// Vendor-defined reference (e.g. ASE row_id, BatchId/RecordIndex) so
/// post-mortem can locate the source. Goes into log lines + (optionally)
/// telemetry tags.
/// </param>
/// <param name="CapturedAt">When the upstream device captured the record.</param>
/// <param name="Format">Vendor-neutral content type (e.g. <c>image/png</c>, <c>vendor/ase</c>).</param>
/// <param name="Bytes">The raw bytes (image / payload).</param>
/// <param name="IdempotencyKey">
/// Stable key for this record across replays. Default: SHA-256 of the
/// adapter-specific natural key (e.g. ASE row_id) so the same row always
/// hashes the same. Host writes this onto <c>Scan.IdempotencyKey</c>;
/// the unique index <c>ux_scans_tenant_idempotency</c> dedupes.
/// </param>
public sealed record CursorSyncRecord(
    Guid DeviceId,
    string SourceReference,
    DateTimeOffset CapturedAt,
    string Format,
    byte[] Bytes,
    string IdempotencyKey);
