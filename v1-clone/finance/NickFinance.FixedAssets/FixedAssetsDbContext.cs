using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.FixedAssets;

public class FixedAssetsDbContext : DbContext
{
    public const string SchemaName = "fixed_assets";

    private readonly ITenantAccessor? _tenantAccessor;

    public FixedAssetsDbContext(DbContextOptions<FixedAssetsDbContext> options) : base(options) { }

    public FixedAssetsDbContext(DbContextOptions<FixedAssetsDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<FixedAsset> Assets => Set<FixedAsset>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<FixedAsset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.FixedAssetId);
            e.Property(x => x.FixedAssetId).HasColumnName("asset_id");
            e.Property(x => x.AssetTag).HasColumnName("asset_tag").HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Category).HasColumnName("category").HasConversion<short>().IsRequired();
            e.Property(x => x.SiteId).HasColumnName("site_id");
            e.Property(x => x.AcquiredOn).HasColumnName("acquired_on").IsRequired();
            e.Property(x => x.AcquisitionCostMinor).HasColumnName("acquisition_cost_minor").IsRequired();
            e.Property(x => x.SalvageValueMinor).HasColumnName("salvage_value_minor").IsRequired();
            e.Property(x => x.UsefulLifeMonths).HasColumnName("useful_life_months").IsRequired();
            e.Property(x => x.Method).HasColumnName("method").HasConversion<short>().IsRequired();
            e.Property(x => x.DecliningBalanceRate).HasColumnName("declining_balance_rate").HasPrecision(6, 4).IsRequired();
            e.Property(x => x.AccumulatedDepreciationMinor).HasColumnName("accumulated_depreciation_minor").IsRequired();
            e.Property(x => x.LastDepreciatedThrough).HasColumnName("last_depreciated_through");
            e.Property(x => x.CostAccount).HasColumnName("cost_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.AccumulatedDepreciationAccount).HasColumnName("accumulated_depreciation_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.DepreciationExpenseAccount).HasColumnName("depreciation_expense_account").HasMaxLength(32).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.DisposedOn).HasColumnName("disposed_on");
            e.Property(x => x.DisposalProceedsMinor).HasColumnName("disposal_proceeds_minor");
            e.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(2000);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Ignore(x => x.NetBookValueMinor);
            e.HasIndex(x => new { x.TenantId, x.AssetTag }).IsUnique().HasDatabaseName("ux_assets_tenant_tag");
            e.HasIndex(x => new { x.TenantId, x.Status, x.Category }).HasDatabaseName("ix_assets_tenant_status_cat");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });
    }
}
