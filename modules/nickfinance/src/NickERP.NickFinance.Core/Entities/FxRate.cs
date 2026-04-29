namespace NickERP.NickFinance.Core.Entities;

/// <summary>
/// FX rate row — see G2 §1.10. Suite-wide (NOT per-tenant) — daily
/// manual publish by a finance admin via <c>SetSystemContext()</c>.
/// One row per (FromCurrency, ToCurrency, EffectiveDate); the latest
/// row whose <see cref="EffectiveDate"/> &lt;= the ledger event's
/// posted-at date wins.
///
/// <para>
/// <strong>Tenant ownership:</strong> intentionally NOT
/// <see cref="NickERP.Platform.Tenancy.Entities.ITenantOwned"/>. The
/// <see cref="TenantId"/> column exists and is nullable — NULL means
/// "applies to all tenants" (the v0 default; see §1.10). The RLS
/// policy on <c>fx_rate</c> opts in to the
/// <c>app.tenant_id = '-1'</c> system context: NULL-tenant rows are
/// admitted for INSERT under <see cref="NickERP.Platform.Tenancy.ITenantContext.SetSystemContext"/>,
/// and SELECT works for any session because the
/// <c>OR ... = '-1'</c> disjunct is permissive on read too.
/// </para>
///
/// <para>
/// The interceptor pipeline doesn't touch this entity (it's not
/// <c>ITenantOwned</c>) so insert paths must explicitly leave
/// <see cref="TenantId"/> null when publishing suite-wide. A future
/// extension could add per-tenant overrides — those would be inserted
/// from a regular tenant scope and would NOT need system context.
/// </para>
/// </summary>
public sealed class FxRate
{
    /// <summary>NULL = suite-wide; non-null = tenant override (reserved for future).</summary>
    public long? TenantId { get; set; }

    /// <summary>ISO 4217 source currency.</summary>
    public string FromCurrency { get; set; } = string.Empty;

    /// <summary>ISO 4217 target currency.</summary>
    public string ToCurrency { get; set; } = string.Empty;

    /// <summary>Conversion rate: <c>amount_in_to = amount_in_from * Rate</c>. <c>numeric(18,8)</c>.</summary>
    public decimal Rate { get; set; }

    /// <summary>Date this rate is effective from (inclusive). PK part.</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>When the rate was published.</summary>
    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>Who published the rate. Required.</summary>
    public Guid PublishedByUserId { get; set; }
}
