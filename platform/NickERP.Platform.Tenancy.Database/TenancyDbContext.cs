using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tenancy.Database;

/// <summary>
/// EF Core DbContext for the canonical tenants table. Schema <c>tenancy</c>
/// inside the <c>nickerp_platform</c> Postgres DB (sibling to the
/// <c>identity</c> schema).
/// </summary>
public sealed class TenancyDbContext : DbContext
{
    public const string SchemaName = "tenancy";

    public TenancyDbContext(DbContextOptions<TenancyDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn(); // long auto-increment
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.BillingPlan).IsRequired().HasMaxLength(50).HasDefaultValue("internal");
            e.Property(x => x.TimeZone).IsRequired().HasMaxLength(64).HasDefaultValue("Africa/Accra");
            e.Property(x => x.Locale).IsRequired().HasMaxLength(20).HasDefaultValue("en-GH");
            e.Property(x => x.Currency).IsRequired().HasMaxLength(3).HasDefaultValue("GHS");
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.CaseVisibilityModel).HasConversion<int>().HasDefaultValue(CaseVisibilityModel.Shared);
            e.Property(x => x.AllowMultiServiceMembership).HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_tenants_code");

            // Seed the default tenant. Stable id 1; matches the DefaultTenantId
            // constant so business-data DEFAULTs and seed migrations on other
            // schemas can reference it directly.
            e.HasData(new Tenant
            {
                Id = Tenant.DefaultTenantId,
                Code = Tenant.DefaultTenantCode,
                Name = "Nick TC-Scan Operations",
                BillingPlan = "internal",
                TimeZone = "Africa/Accra",
                Locale = "en-GH",
                Currency = "GHS",
                IsActive = true,
                CreatedAt = new DateTimeOffset(2026, 4, 26, 0, 0, 0, TimeSpan.Zero)
            });
        });
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. Reads connection
/// string from env var <c>NICKERP_PLATFORM_DB_CONNECTION</c> (same env var the
/// Identity layer uses — they share the <c>nickerp_platform</c> DB) with a
/// localhost fallback.
/// </summary>
public sealed class TenancyDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=designtime";

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(TenancyDbContext).Assembly.GetName().Name);
                // H3 — keep EF Core's history table inside the tenancy
                // schema so nscim_app never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy");
            })
            .Options;

        return new TenancyDbContext(options);
    }
}
