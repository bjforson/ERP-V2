using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 50 / FU-cursor-state-persistence — durable cursor state for
/// any <see cref="NickERP.Inspection.Scanners.Abstractions.IScannerCursorSyncAdapter"/>-
/// backed device. One row per <c>(TenantId, ScannerDeviceTypeId,
/// AdapterName)</c>; <c>AseSyncWorker</c> reads + writes the cursor here
/// instead of the in-memory <c>_cursorByInstance</c> dict it shipped
/// with at Sprint 24.
///
/// <para>
/// <b>Why persistent.</b> Sprint 24's in-memory cursor was fine for the
/// single-host shape; on a host restart the adapter re-emitted every
/// row from the start of time, and the unique index on
/// <c>Scan.IdempotencyKey</c> dedupedup. That works for ASE specifically
/// because the upstream is bounded, but:
/// <list type="bullet">
///   <item><description>Multi-host pilots want the cursor durable across
///   restarts so a fresh host doesn't re-pull a backlog the previous
///   host already drained.</description></item>
///   <item><description>Two hosts can race on the same cursor; they
///   must converge on the same monotonic value rather than each
///   advancing their own private counter.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Per-tenant + per-(scannerType, adapter).</b> The cursor key is
/// (TenantId, ScannerDeviceTypeId, AdapterName). One row per
/// "what's the cursor for ASE-shaped scanners under tenant X via the
/// 'ase' adapter". Per-instance cursoring is intentionally NOT used:
/// two scanners of the same type in the same tenant share a cursor
/// because the upstream is one logical source — splitting would
/// duplicate rows.
/// </para>
///
/// <para>
/// <b>Optimistic concurrency.</b>
/// <see cref="ConcurrencyToken"/> gates multi-host updates: each cursor
/// advance reads the current row, sets <see cref="LastCursorValue"/> +
/// bumps the token, and SaveChanges fails on a stale-token race. The
/// loser silently retries on the next tick — same posture as the
/// outbound submission row (ICUMS dispatch). Sprint 24's wider
/// architectural decision was "no new tracking tables for cursor state";
/// Sprint 50 revisits because (a) the persistence shape is small (one
/// row per scannerType+adapter, not per-record) and (b) the multi-host
/// pilot dictates durability.
/// </para>
///
/// <para>
/// <b>RLS-enforced.</b> Standard FORCE RLS + tenant_isolation_*; same
/// posture as <c>WebhookCursor</c>. <c>nscim_app</c> gets SELECT /
/// INSERT / UPDATE; no DELETE — removing a scanner-type from
/// configuration shouldn't drop the cursor row, so re-adding the
/// adapter resumes from where it left off.
/// </para>
/// </summary>
public sealed class ScannerCursorSyncState : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Scanner type-code from
    /// <see cref="ScannerDeviceInstance.TypeCode"/> — namespaces the
    /// cursor by adapter-managed source (ASE, future cursor-shaped
    /// scanners). Max 64 chars, matches ScannerDeviceInstance.TypeCode.
    /// </summary>
    public string ScannerDeviceTypeId { get; set; } = string.Empty;

    /// <summary>
    /// Adapter name — typically the same value as
    /// <see cref="ScannerDeviceTypeId"/>, but kept distinct so a future
    /// "two adapters for the same scanner type" path stays clean (e.g.
    /// staging shadow + prod). Max 64 chars.
    /// </summary>
    public string AdapterName { get; set; } = string.Empty;

    /// <summary>
    /// The vendor-defined cursor value the adapter last returned. Empty
    /// string is valid + means "stay at the start" (mirrors
    /// <c>IScannerCursorSyncAdapter</c> contract). Bounded to 256 chars
    /// — the longest reasonable cursor encoding (UUID + timestamp + a
    /// few bytes of state).
    /// </summary>
    public string LastCursorValue { get; set; } = string.Empty;

    /// <summary>UTC timestamp the cursor was last advanced.</summary>
    public DateTimeOffset LastAdvancedAt { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. EF Core uses this as a
    /// <c>[ConcurrencyCheck]</c> column; SaveChanges throws on a
    /// stale-token mismatch. Sprint 50 keeps it simple — increment by
    /// 1 on every advance + re-read on race. Stored as a Postgres
    /// <c>bigint</c> because Postgres has no unsigned-int type.
    /// </summary>
    public long ConcurrencyToken { get; set; }

    public long TenantId { get; set; }
}
