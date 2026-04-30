namespace NickFinance.FixedAssets;

/// <summary>One fixed asset row in the register.</summary>
public class FixedAsset
{
    public Guid FixedAssetId { get; set; } = Guid.NewGuid();
    public string AssetTag { get; set; } = string.Empty;          // e.g. "SCAN-TEMA-001"
    public string Name { get; set; } = string.Empty;
    public AssetCategory Category { get; set; }
    public Guid? SiteId { get; set; }
    public DateOnly AcquiredOn { get; set; }
    public long AcquisitionCostMinor { get; set; }
    /// <summary>Estimated salvage at end of useful life, in minor units.</summary>
    public long SalvageValueMinor { get; set; }
    /// <summary>Useful life in months — drives the depreciation period.</summary>
    public int UsefulLifeMonths { get; set; }
    public DepreciationMethod Method { get; set; } = DepreciationMethod.StraightLine;
    /// <summary>For declining-balance — the rate as a percentage. Ignored on StraightLine.</summary>
    public decimal DecliningBalanceRate { get; set; } = 0m;
    /// <summary>Running accumulated depreciation; advanced by <see cref="IFixedAssetService.PostMonthlyDepreciationAsync"/>.</summary>
    public long AccumulatedDepreciationMinor { get; set; }
    public DateOnly? LastDepreciatedThrough { get; set; }
    public string CostAccount { get; set; } = "1500";
    public string AccumulatedDepreciationAccount { get; set; } = "1510";
    public string DepreciationExpenseAccount { get; set; } = "6700";
    public string CurrencyCode { get; set; } = "GHS";
    public AssetStatus Status { get; set; } = AssetStatus.Active;
    public DateOnly? DisposedOn { get; set; }
    public long? DisposalProceedsMinor { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long TenantId { get; set; } = 1;

    /// <summary>Net book value = cost − accumulated depreciation. Both in minor units.</summary>
    public long NetBookValueMinor => AcquisitionCostMinor - AccumulatedDepreciationMinor;
}

public enum AssetCategory
{
    Scanner = 1,
    Vehicle = 2,
    ItEquipment = 3,
    OfficeFurniture = 4,
    Building = 5,
    Other = 99
}

public enum DepreciationMethod
{
    StraightLine = 0,
    DecliningBalance = 1
}

public enum AssetStatus
{
    Active = 0,
    Disposed = 1,
    Impaired = 2
}
