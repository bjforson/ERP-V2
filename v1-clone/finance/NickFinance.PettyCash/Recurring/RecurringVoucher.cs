namespace NickFinance.PettyCash.Recurring;

/// <summary>
/// Template for a voucher that fires on a schedule. The runner
/// (<see cref="IRecurringVoucherRunner"/>) walks active templates
/// nightly and submits one voucher per template that is due.
/// </summary>
public class RecurringVoucherTemplate
{
    public Guid RecurringVoucherTemplateId { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid FloatId { get; set; }
    public Guid RequesterUserId { get; set; }
    public VoucherCategory Category { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "GHS";
    public RecurrenceFrequency Frequency { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? LastFiredOn { get; set; }
    public bool IsActive { get; set; } = true;
    public string? PayeeName { get; set; }
    public string? ProjectCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }
    public long TenantId { get; set; } = 1;
}

public enum RecurrenceFrequency
{
    Daily = 1,
    Weekly = 2,
    BiWeekly = 3,
    Monthly = 4
}
