using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.Budgeting;

public class BudgetingDbContext : DbContext
{
    public const string SchemaName = "budgeting";

    private readonly ITenantAccessor? _tenantAccessor;

    public BudgetingDbContext(DbContextOptions<BudgetingDbContext> options) : base(options) { }

    public BudgetingDbContext(DbContextOptions<BudgetingDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<AnnualBudget> Budgets => Set<AnnualBudget>();
    public DbSet<BudgetLine> Lines => Set<BudgetLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<AnnualBudget>(e =>
        {
            e.ToTable("annual_budgets");
            e.HasKey(x => x.AnnualBudgetId);
            e.Property(x => x.AnnualBudgetId).HasColumnName("annual_budget_id");
            e.Property(x => x.FiscalYear).HasColumnName("fiscal_year").IsRequired();
            e.Property(x => x.DepartmentCode).HasColumnName("department_code").HasMaxLength(64);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.HasIndex(x => new { x.TenantId, x.FiscalYear, x.DepartmentCode })
             .IsUnique().HasDatabaseName("ux_annual_budgets_tenant_year_dept");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.AnnualBudgetId).OnDelete(DeleteBehavior.Cascade);

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });

        b.Entity<BudgetLine>(e =>
        {
            e.ToTable("budget_lines");
            e.HasKey(x => x.BudgetLineId);
            e.Property(x => x.BudgetLineId).HasColumnName("budget_line_id");
            e.Property(x => x.AnnualBudgetId).HasColumnName("annual_budget_id").IsRequired();
            e.Property(x => x.AccountCode).HasColumnName("account_code").HasMaxLength(32).IsRequired();
            e.Property(x => x.MonthNumber).HasColumnName("month_number").IsRequired();
            e.Property(x => x.AmountMinor).HasColumnName("amount_minor").IsRequired();
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.HasIndex(x => new { x.AnnualBudgetId, x.AccountCode, x.MonthNumber })
             .IsUnique().HasDatabaseName("ux_budget_lines_budget_account_month");
        });
    }
}
