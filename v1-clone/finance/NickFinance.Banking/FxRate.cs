namespace NickFinance.Banking;

/// <summary>
/// One foreign-exchange rate observation. Read-side only — every rate is
/// quoted "FromCurrency → ToCurrency" so a USD→GHS rate of 16.20 means
/// 1 USD costs 16.20 GHS. Phase 1 (Wave 2B) just stores rates and feeds
/// the read path; Phase 2 (Wave 3) will use them to drive period-close
/// revaluation journals.
/// </summary>
/// <remarks>
/// Uniqueness is on <c>(tenant_id, from_currency, to_currency, as_of_date)</c>
/// so a re-import for the same day is idempotent — the latest run wins
/// because the importer upserts on collision.
/// </remarks>
public class FxRate
{
    public Guid FxRateId { get; set; } = Guid.NewGuid();

    /// <summary>ISO-4217 code (e.g. "USD"). Always quote against the functional currency (GHS).</summary>
    public string FromCurrency { get; set; } = "USD";

    /// <summary>Functional currency (always "GHS" for Nick TC-Scan v1).</summary>
    public string ToCurrency { get; set; } = "GHS";

    /// <summary>Rate as a decimal — e.g. 1 USD = 16.20 GHS → Rate = 16.20.</summary>
    public decimal Rate { get; set; }

    /// <summary>Date the rate is for. Stored at midnight UTC.</summary>
    public DateOnly AsOfDate { get; set; }

    /// <summary>Source — "BoG-API", "BoG-csv", "manual", "interbank-mid".</summary>
    public string Source { get; set; } = "manual";

    public DateTimeOffset RecordedAt { get; set; }
    public Guid RecordedByUserId { get; set; }

    public long TenantId { get; set; } = 1;

    /// <summary>Optional metadata — JSON of provider response, used for audit trace.</summary>
    public string? ProviderPayloadJson { get; set; }
}
