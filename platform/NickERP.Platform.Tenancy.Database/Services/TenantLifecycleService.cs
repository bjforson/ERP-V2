using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tenancy.Database.Services;

/// <summary>
/// Sprint 18 — admin operations on the tenant lifecycle:
/// suspend / resume / soft-delete / restore / mark-pending-hard-purge /
/// hard-purge. Each transition is gated on the current
/// <see cref="Tenant.State"/> and emits a <see cref="DomainEvent"/> to
/// <c>audit.events</c> via <see cref="IEventPublisher"/>.
/// </summary>
/// <remarks>
/// Hard-purge is delegated to <see cref="TenantPurgeOrchestrator"/> which
/// handles the cross-DB cascade. The lifecycle service itself never
/// touches inspection / nickfinance data — it only flips state on the
/// <see cref="Tenant"/> row and audits.
/// </remarks>
public interface ITenantLifecycleService
{
    Task SuspendTenantAsync(long tenantId, string? reason, Guid suspendingUserId, CancellationToken ct = default);
    Task ResumeTenantAsync(long tenantId, Guid resumingUserId, CancellationToken ct = default);
    Task SoftDeleteTenantAsync(long tenantId, string? reason, Guid deletingUserId, int? retentionDays = null, CancellationToken ct = default);
    Task RestoreTenantAsync(long tenantId, Guid restoringUserId, CancellationToken ct = default);
    Task<bool> MarkPendingHardPurgeAsync(long tenantId, CancellationToken ct = default);
    Task<TenantPurgeResult> HardPurgeTenantAsync(long tenantId, Guid confirmingUserId, CancellationToken ct = default);
}

/// <summary>
/// Outcome record returned by <see cref="ITenantLifecycleService.HardPurgeTenantAsync"/>.
/// </summary>
/// <param name="PurgeLogId">The id of the <see cref="TenantPurgeLog"/> row that records this operation.</param>
/// <param name="Outcome">"completed" or "partial" — matches <see cref="TenantPurgeLog.Outcome"/>.</param>
/// <param name="RowCounts">Per-table row counts of what was deleted.</param>
/// <param name="FailureNote">Concise reason when <paramref name="Outcome"/> is partial; null otherwise.</param>
public sealed record TenantPurgeResult(
    Guid PurgeLogId,
    string Outcome,
    IReadOnlyDictionary<string, long> RowCounts,
    string? FailureNote);

/// <summary>
/// Default <see cref="ITenantLifecycleService"/>. All transitions are
/// idempotent where the target state is already reached (suspend a
/// suspended tenant = no-op, resume an active tenant = no-op).
/// </summary>
public sealed class TenantLifecycleService : ITenantLifecycleService
{
    private readonly TenancyDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly ITenantPurgeOrchestrator _orchestrator;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantLifecycleService> _logger;

    public TenantLifecycleService(
        TenancyDbContext db,
        IEventPublisher publisher,
        ITenantPurgeOrchestrator orchestrator,
        ILogger<TenantLifecycleService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _publisher = publisher;
        _orchestrator = orchestrator;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task SuspendTenantAsync(long tenantId, string? reason, Guid suspendingUserId, CancellationToken ct = default)
    {
        var tenant = await LoadAsync(tenantId, ct);
        if (tenant.State == TenantState.Suspended)
        {
            return; // idempotent
        }
        if (tenant.State != TenantState.Active)
        {
            throw new InvalidOperationException(
                $"Cannot suspend tenant {tenantId}: current state is {tenant.State}, "
                + "Suspend requires Active.");
        }

        tenant.State = TenantState.Suspended;
        await _db.SaveChangesAsync(ct);

        await EmitEventAsync(tenant, "nickerp.tenancy.tenant_suspended",
            JsonSerializer.SerializeToElement(new { tenant.Id, tenant.Code, reason }),
            suspendingUserId, ct);
    }

    /// <inheritdoc />
    public async Task ResumeTenantAsync(long tenantId, Guid resumingUserId, CancellationToken ct = default)
    {
        var tenant = await LoadAsync(tenantId, ct);
        if (tenant.State == TenantState.Active)
        {
            return; // idempotent
        }
        if (tenant.State != TenantState.Suspended)
        {
            throw new InvalidOperationException(
                $"Cannot resume tenant {tenantId}: current state is {tenant.State}, "
                + "Resume requires Suspended.");
        }

        tenant.State = TenantState.Active;
        await _db.SaveChangesAsync(ct);

        await EmitEventAsync(tenant, "nickerp.tenancy.tenant_resumed",
            JsonSerializer.SerializeToElement(new { tenant.Id, tenant.Code }),
            resumingUserId, ct);
    }

    /// <inheritdoc />
    public async Task SoftDeleteTenantAsync(long tenantId, string? reason, Guid deletingUserId, int? retentionDays = null, CancellationToken ct = default)
    {
        var tenant = await LoadAsync(tenantId, ct);
        if (tenant.State != TenantState.Active && tenant.State != TenantState.Suspended)
        {
            throw new InvalidOperationException(
                $"Cannot soft-delete tenant {tenantId}: current state is {tenant.State}, "
                + "SoftDelete requires Active or Suspended.");
        }

        var now = _clock.GetUtcNow();
        var retention = retentionDays ?? tenant.RetentionDays;
        if (retention < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays),
                "RetentionDays must be non-negative.");
        }

        tenant.State = TenantState.SoftDeleted;
        tenant.DeletedAt = now;
        tenant.DeletedByUserId = deletingUserId;
        tenant.DeletionReason = Truncate(reason, 500);
        tenant.RetentionDays = retention;
        tenant.HardPurgeAfter = now.AddDays(retention);

        await _db.SaveChangesAsync(ct);

        await EmitEventAsync(tenant, "nickerp.tenancy.tenant_soft_deleted",
            JsonSerializer.SerializeToElement(new
            {
                tenant.Id,
                tenant.Code,
                reason,
                retentionDays = retention,
                tenant.HardPurgeAfter
            }),
            deletingUserId, ct);
    }

    /// <inheritdoc />
    public async Task RestoreTenantAsync(long tenantId, Guid restoringUserId, CancellationToken ct = default)
    {
        var tenant = await LoadAsync(tenantId, ct);
        if (tenant.State != TenantState.SoftDeleted)
        {
            throw new InvalidOperationException(
                $"Cannot restore tenant {tenantId}: current state is {tenant.State}, "
                + "Restore requires SoftDeleted.");
        }
        var now = _clock.GetUtcNow();
        if (tenant.HardPurgeAfter is null || now >= tenant.HardPurgeAfter.Value)
        {
            throw new InvalidOperationException(
                $"Cannot restore tenant {tenantId}: retention window expired at "
                + $"{tenant.HardPurgeAfter:O}. The tenant must be hard-purged or "
                + "the operator must manually re-create it.");
        }

        tenant.State = TenantState.Active;
        tenant.DeletedAt = null;
        tenant.DeletedByUserId = null;
        tenant.DeletionReason = null;
        tenant.HardPurgeAfter = null;
        // RetentionDays is intentionally NOT reset — the tenant's prior
        // choice carries forward to the next soft-delete.

        await _db.SaveChangesAsync(ct);

        await EmitEventAsync(tenant, "nickerp.tenancy.tenant_restored",
            JsonSerializer.SerializeToElement(new { tenant.Id, tenant.Code }),
            restoringUserId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> MarkPendingHardPurgeAsync(long tenantId, CancellationToken ct = default)
    {
        var tenant = await LoadAsync(tenantId, ct);
        if (tenant.State == TenantState.PendingHardPurge)
        {
            return true; // already there
        }
        if (tenant.State != TenantState.SoftDeleted)
        {
            return false; // not eligible — quiet no-op (sweeper-friendly)
        }
        var now = _clock.GetUtcNow();
        if (tenant.HardPurgeAfter is null || now < tenant.HardPurgeAfter.Value)
        {
            return false; // retention window hasn't expired yet
        }

        tenant.State = TenantState.PendingHardPurge;
        await _db.SaveChangesAsync(ct);

        await EmitEventAsync(tenant, "nickerp.tenancy.tenant_pending_hard_purge",
            JsonSerializer.SerializeToElement(new
            {
                tenant.Id,
                tenant.Code,
                tenant.DeletedAt,
                tenant.HardPurgeAfter
            }),
            actorUserId: null, ct);

        return true;
    }

    /// <inheritdoc />
    public async Task<TenantPurgeResult> HardPurgeTenantAsync(long tenantId, Guid confirmingUserId, CancellationToken ct = default)
    {
        var tenant = await LoadAsync(tenantId, ct);
        if (tenant.State != TenantState.PendingHardPurge)
        {
            throw new InvalidOperationException(
                $"Cannot hard-purge tenant {tenantId}: current state is {tenant.State}, "
                + "HardPurge requires PendingHardPurge. Run MarkPendingHardPurgeAsync first "
                + "(only valid once the retention window has expired).");
        }

        // Snapshot the fields needed to populate tenant_purge_log BEFORE
        // the tenant row gets deleted by the orchestrator. The orchestrator
        // itself writes the log row (cross-DB orchestration would otherwise
        // pivot off a row that no longer exists).
        var purgeContext = new TenantPurgeContext(
            TenantId: tenant.Id,
            TenantCode: tenant.Code,
            TenantName: tenant.Name,
            DeletionReason: tenant.DeletionReason,
            SoftDeletedAt: tenant.DeletedAt,
            ConfirmingUserId: confirmingUserId);

        var result = await _orchestrator.PurgeAsync(purgeContext, ct);

        // Audit event lands on `audit.events` AFTER the orchestrator has
        // run — at this point the originating tenant's audit rows are
        // gone, so this event is written under the SYSTEM tenant
        // (TenantId = null). The opt-in clause on audit.events admits
        // the NULL-tenant insert.
        await EmitSystemEventAsync("nickerp.tenancy.tenant_hard_purged",
            JsonSerializer.SerializeToElement(new
            {
                tenant.Id,
                tenant.Code,
                tenant.Name,
                rowCounts = result.RowCounts,
                outcome = result.Outcome,
                failureNote = result.FailureNote,
                purgeLogId = result.PurgeLogId
            }),
            confirmingUserId, ct);

        return result;
    }

    private async Task<Tenant> LoadAsync(long tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters so SoftDeleted / PendingHardPurge tenants
        // (which the global filter hides from business code) are visible
        // to the lifecycle service — restore + hard-purge operate on
        // exactly those states.
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            throw new InvalidOperationException(
                $"Tenant {tenantId} not found.");
        }
        return tenant;
    }

    private async Task EmitEventAsync(Tenant tenant, string eventType, JsonElement payload, Guid? actorUserId, CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow();
            var key = IdempotencyKey.ForEntityChange(
                tenantId: tenant.Id,
                eventType: eventType,
                entityType: nameof(Tenant),
                entityId: tenant.Id.ToString(),
                occurredAt: now);
            var evt = DomainEvent.Create(
                tenantId: tenant.Id,
                actorUserId: actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: eventType,
                entityType: nameof(Tenant),
                entityId: tenant.Id.ToString(),
                payload: payload,
                idempotencyKey: key,
                clock: _clock);
            await _publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Audit emission is best-effort — never fail a successful
            // state transition because the event bus is wedged.
            _logger.LogWarning(ex,
                "Failed to publish {EventType} for tenant {TenantId}; lifecycle change persisted.",
                eventType, tenant.Id);
        }
    }

    private async Task EmitSystemEventAsync(string eventType, JsonElement payload, Guid? actorUserId, CancellationToken ct)
    {
        try
        {
            var now = _clock.GetUtcNow();
            // Hard-purge: TenantId is null (system-level event because the
            // originating tenant is gone). IdempotencyKey hashes on the
            // post-purge timestamp so re-purging an id (impossible since
            // the row's gone) wouldn't collide.
            var key = IdempotencyKey.From(eventType, now.ToString("O"));
            var evt = DomainEvent.Create(
                tenantId: null,
                actorUserId: actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: eventType,
                entityType: nameof(Tenant),
                entityId: payload.TryGetProperty("Id", out var idProp) ? idProp.ToString() : "system",
                payload: payload,
                idempotencyKey: key,
                clock: _clock);
            await _publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish system event {EventType}; tenant_purge_log row already persisted.",
                eventType);
        }
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max];
    }
}

/// <summary>
/// Per-purge context captured by <see cref="ITenantLifecycleService.HardPurgeTenantAsync"/>
/// before it hands off to <see cref="ITenantPurgeOrchestrator"/>. Snapshots
/// fields the tenant row carries so the orchestrator can write a complete
/// <see cref="TenantPurgeLog"/> entry even after the row is deleted.
/// </summary>
public sealed record TenantPurgeContext(
    long TenantId,
    string TenantCode,
    string TenantName,
    string? DeletionReason,
    DateTimeOffset? SoftDeletedAt,
    Guid ConfirmingUserId);
