using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Database;

/// <summary>
/// EF Core DbContext for the Inspection v2 module. Lives in its OWN
/// PostgreSQL database (<c>nickerp_inspection</c>) — modules own their
/// data per the platform contract. Default schema <c>inspection</c>.
/// </summary>
public sealed class InspectionDbContext : DbContext
{
    public const string SchemaName = "inspection";

    public InspectionDbContext(DbContextOptions<InspectionDbContext> options) : base(options) { }

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<ScannerDeviceInstance> ScannerDeviceInstances => Set<ScannerDeviceInstance>();
    public DbSet<ExternalSystemInstance> ExternalSystemInstances => Set<ExternalSystemInstance>();
    public DbSet<ExternalSystemBinding> ExternalSystemBindings => Set<ExternalSystemBinding>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<Location>(e =>
        {
            e.ToTable("locations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Region).HasMaxLength(100);
            e.Property(x => x.TimeZone).IsRequired().HasMaxLength(64);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_locations_tenant_code");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_locations_tenant");

            e.HasMany(x => x.Stations).WithOne(s => s.Location).HasForeignKey(s => s.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Station>(e =>
        {
            e.ToTable("stations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LocationId).IsRequired();
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => new { x.TenantId, x.LocationId, x.Code }).IsUnique().HasDatabaseName("ux_stations_tenant_loc_code");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_stations_tenant");
        });

        modelBuilder.Entity<ScannerDeviceInstance>(e =>
        {
            e.ToTable("scanner_device_instances");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LocationId).IsRequired();
            e.Property(x => x.StationId);
            e.Property(x => x.TypeCode).IsRequired().HasMaxLength(64);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ConfigJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_scanners_tenant");
            e.HasIndex(x => new { x.TenantId, x.LocationId }).HasDatabaseName("ix_scanners_tenant_loc");
            e.HasIndex(x => x.TypeCode).HasDatabaseName("ix_scanners_type");

            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ExternalSystemInstance>(e =>
        {
            e.ToTable("external_system_instances");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TypeCode).IsRequired().HasMaxLength(64);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Scope).IsRequired().HasConversion<int>();
            e.Property(x => x.ConfigJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_external_systems_tenant");
            e.HasIndex(x => x.TypeCode).HasDatabaseName("ix_external_systems_type");

            e.HasMany(x => x.Bindings).WithOne(b => b.Instance).HasForeignKey(b => b.ExternalSystemInstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalSystemBinding>(e =>
        {
            e.ToTable("external_system_bindings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ExternalSystemInstanceId).IsRequired();
            e.Property(x => x.LocationId).IsRequired();
            e.Property(x => x.Role).IsRequired().HasMaxLength(32).HasDefaultValue("primary");
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => new { x.TenantId, x.ExternalSystemInstanceId, x.LocationId })
                .IsUnique()
                .HasDatabaseName("ux_external_bindings_tenant_inst_loc");

            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Design-time factory; reads NICKERP_INSPECTION_DB_CONNECTION env var with a localhost fallback.</summary>
public sealed class InspectionDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<InspectionDbContext>
{
    public InspectionDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NICKERP_INSPECTION_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_inspection;Username=postgres;Password=designtime";

        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name))
            .Options;

        return new InspectionDbContext(options);
    }
}
