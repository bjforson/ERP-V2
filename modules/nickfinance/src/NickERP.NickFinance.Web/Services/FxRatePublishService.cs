using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.NickFinance.Web.Services;

/// <summary>
/// Publishes (or updates) one or more rows in the suite-wide
/// <c>fx_rate</c> table — see G2 §1.10. The FIRST NickFinance use of
/// <see cref="ITenantContext.SetSystemContext"/>; entry registered in
/// <c>docs/system-context-audit-register.md</c>.
///
/// <para>
/// <strong>System-context rationale.</strong> FX rates are suite-wide
/// (NULL <c>TenantId</c>); a normal per-tenant insert would fail the
/// RLS WITH CHECK clause. SetSystemContext flips the session into
/// <c>app.tenant_id = '-1'</c>, which the
/// <c>tenant_isolation_fx_rate</c> policy admits via its OR clause.
/// </para>
///
/// <para>
/// <strong>Idempotent publish.</strong> Re-publishing the same
/// (from, to, effective_date) tuple is an UPDATE in place, not an
/// insert — finance can correct a mis-typed rate by republishing.
/// Each publish (insert or update) emits one
/// <c>nickfinance.fx_rate.published</c> audit event.
/// </para>
/// </summary>
public sealed class FxRatePublishService
{
    private readonly NickFinanceDbContext _db;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly ILogger<FxRatePublishService> _logger;

    public FxRatePublishService(
        NickFinanceDbContext db,
        IEventPublisher events,
        ITenantContext tenant,
        ILogger<FxRatePublishService> logger)
    {
        _db = db;
        _events = events;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Publish one or more rates. Switches the calling DbContext into
    /// system context for the duration of the call so the inserts /
    /// updates are admitted by the RLS policy. Reverts to the previous
    /// tenant on completion.
    /// </summary>
    public async Task<IReadOnlyList<FxRate>> PublishAsync(
        Guid actorUserId,
        IReadOnlyList<FxRatePublishRequest> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (actorUserId == Guid.Empty)
        {
            throw new ArgumentException("Actor user id is required to publish FX rates.", nameof(actorUserId));
        }

        // Capture and restore tenant scope around the system-context block.
        var priorTenantId = _tenant.IsResolved && !_tenant.IsSystem ? (long?)_tenant.TenantId : null;
        _tenant.SetSystemContext();
        try
        {
            var saved = new List<FxRate>(requests.Count);
            foreach (var req in requests)
            {
                req.Validate();
                var from = req.FromCurrency.ToUpperInvariant();
                var to = req.ToCurrency.ToUpperInvariant();
                var effective = req.EffectiveDate.Date;

                var existing = await _db.FxRates
                    .FirstOrDefaultAsync(
                        r => r.FromCurrency == from
                          && r.ToCurrency == to
                          && r.EffectiveDate == effective,
                        ct);

                bool isUpdate = existing is not null;
                if (existing is null)
                {
                    existing = new FxRate
                    {
                        TenantId = null,
                        FromCurrency = from,
                        ToCurrency = to,
                        EffectiveDate = effective,
                        Rate = req.Rate,
                        PublishedAt = DateTimeOffset.UtcNow,
                        PublishedByUserId = actorUserId
                    };
                    _db.FxRates.Add(existing);
                }
                else
                {
                    existing.Rate = req.Rate;
                    existing.PublishedAt = DateTimeOffset.UtcNow;
                    existing.PublishedByUserId = actorUserId;
                }

                saved.Add(existing);

                // Emit one audit event per rate. Tenant id is null on the
                // event since the rate is suite-wide; the audit.events
                // table opted in to NULL-tenant rows in Sprint 5.
                try
                {
                    var payload = JsonSerializer.SerializeToElement(new
                    {
                        existing.FromCurrency,
                        existing.ToCurrency,
                        EffectiveDate = existing.EffectiveDate.ToString("yyyy-MM-dd"),
                        existing.Rate,
                        ActorUserId = actorUserId,
                        Action = isUpdate ? "update" : "insert"
                    });
                    var key = IdempotencyKey.From(
                        "nickfinance.fx_rate.published",
                        existing.FromCurrency,
                        existing.ToCurrency,
                        existing.EffectiveDate.ToString("yyyy-MM-dd"),
                        DateTimeOffset.UtcNow.ToString("O"));
                    var evt = DomainEvent.Create(
                        tenantId: null,
                        actorUserId: actorUserId,
                        correlationId: System.Diagnostics.Activity.Current?.RootId,
                        eventType: "nickfinance.fx_rate.published",
                        entityType: "FxRate",
                        entityId: $"{existing.FromCurrency}->{existing.ToCurrency}@{existing.EffectiveDate:yyyy-MM-dd}",
                        payload: payload,
                        idempotencyKey: key);
                    await _events.PublishAsync(evt, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to emit nickfinance.fx_rate.published for {From}->{To} on {Date}",
                        existing.FromCurrency, existing.ToCurrency, existing.EffectiveDate);
                }
            }

            await _db.SaveChangesAsync(ct);
            return saved;
        }
        finally
        {
            // Restore prior scope if we had one; if not, leaving system-mode
            // active until the request scope ends is fine because the scoped
            // ITenantContext is request-local.
            if (priorTenantId.HasValue)
            {
                _tenant.SetTenant(priorTenantId.Value);
            }
        }
    }
}

/// <summary>One row to publish in a batch. Validation enforces ISO-shape and positive rate.</summary>
public sealed record FxRatePublishRequest(
    string FromCurrency,
    string ToCurrency,
    decimal Rate,
    DateTime EffectiveDate)
{
    /// <summary>Throws on shape violations.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FromCurrency) || FromCurrency.Length != 3)
            throw new ArgumentException("FromCurrency must be a 3-letter ISO code.", nameof(FromCurrency));
        if (string.IsNullOrWhiteSpace(ToCurrency) || ToCurrency.Length != 3)
            throw new ArgumentException("ToCurrency must be a 3-letter ISO code.", nameof(ToCurrency));
        if (string.Equals(FromCurrency, ToCurrency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("FromCurrency and ToCurrency must differ; identity rate is implicit (1.0).");
        if (Rate <= 0m)
            throw new ArgumentException("Rate must be > 0.", nameof(Rate));
    }
}
