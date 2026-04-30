using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.Coa;

/// <summary>
/// EF Core context for the chart of accounts. Owns the <c>coa</c> schema.
/// Modules consume CoA lookups via <see cref="ICoaService"/> rather than
/// touching this directly so a future swap to a remote-CoA service stays
/// transparent.
/// </summary>
public class CoaDbContext : DbContext
{
    public const string SchemaName = "coa";

    private readonly ITenantAccessor? _tenantAccessor;

    public CoaDbContext(DbContextOptions<CoaDbContext> options) : base(options) { }

    public CoaDbContext(DbContextOptions<CoaDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        var filterEnabled = _tenantAccessor is not null;

        b.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasConversion<short>().IsRequired();
            e.Property(x => x.ParentCode).HasColumnName("parent_code").HasMaxLength(32);
            e.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsRequired();
            e.Property(x => x.IsControl).HasColumnName("is_control").IsRequired();
            e.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_accounts_tenant_code");
            e.HasIndex(x => new { x.TenantId, x.Type, x.IsActive }).HasDatabaseName("ix_accounts_tenant_type_active");

            if (filterEnabled) e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
        });
    }
}
