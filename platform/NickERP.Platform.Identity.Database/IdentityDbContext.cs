using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity.Entities;

namespace NickERP.Platform.Identity.Database;

/// <summary>
/// EF Core DbContext for the canonical identity store. Schema <c>identity</c>
/// inside the <c>nickerp_platform</c> Postgres DB (the v2 platform DB; not
/// the v1 <c>nick_platform</c>).
///
/// Modules NEVER reference this assembly directly — they consume
/// <see cref="Services.IIdentityResolver"/> instead.
/// </summary>
public sealed class IdentityDbContext : DbContext
{
    public const string SchemaName = "identity";

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }

    public DbSet<IdentityUser> Users => Set<IdentityUser>();
    public DbSet<AppScope> AppScopes => Set<AppScope>();
    public DbSet<UserScope> UserScopes => Set<UserScope>();
    public DbSet<ServiceTokenIdentity> ServiceTokens => Set<ServiceTokenIdentity>();
    public DbSet<ServiceTokenScope> ServiceTokenScopes => Set<ServiceTokenScope>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<IdentityUser>(e =>
        {
            e.ToTable("identity_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever(); // client-side Guid (entity has Guid.NewGuid() default)
            e.Property(x => x.Email).IsRequired().HasMaxLength(320);
            e.Property(x => x.NormalizedEmail).IsRequired().HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).HasDefaultValue(1L);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique().HasDatabaseName("ux_identity_users_tenant_normalized_email");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_identity_users_tenant");
            e.HasIndex(x => x.LastSeenAt).HasDatabaseName("ix_identity_users_last_seen");

            e.HasMany(x => x.Scopes)
                .WithOne(s => s.User)
                .HasForeignKey(s => s.IdentityUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AppScope>(e =>
        {
            e.ToTable("app_scopes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Code).IsRequired().HasMaxLength(100);
            e.Property(x => x.AppName).IsRequired().HasMaxLength(50);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).HasDefaultValue(1L);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasDatabaseName("ux_app_scopes_tenant_code");
            e.HasIndex(x => new { x.TenantId, x.AppName }).HasDatabaseName("ix_app_scopes_tenant_app");
        });

        modelBuilder.Entity<UserScope>(e =>
        {
            e.ToTable("user_scopes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IdentityUserId).IsRequired();
            e.Property(x => x.AppScopeCode).IsRequired().HasMaxLength(100);
            e.Property(x => x.GrantedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.GrantedByUserId).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.TenantId).HasDefaultValue(1L);

            // Unique active grant per (user, scope) — but allow multiple historical (revoked) rows.
            e.HasIndex(x => new { x.TenantId, x.IdentityUserId, x.AppScopeCode })
                .HasDatabaseName("ix_user_scopes_tenant_user_scope");
            e.HasIndex(x => new { x.TenantId, x.AppScopeCode }).HasDatabaseName("ix_user_scopes_tenant_scope");
            e.HasIndex(x => x.RevokedAt).HasDatabaseName("ix_user_scopes_revoked_at");
        });

        modelBuilder.Entity<ServiceTokenIdentity>(e =>
        {
            e.ToTable("service_token_identities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TokenClientId).IsRequired().HasMaxLength(255);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.Purpose).HasMaxLength(500);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.TenantId).HasDefaultValue(1L);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => new { x.TenantId, x.TokenClientId }).IsUnique().HasDatabaseName("ux_service_tokens_tenant_client_id");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_service_tokens_tenant");

            e.HasMany(x => x.Scopes)
                .WithOne(s => s.ServiceToken)
                .HasForeignKey(s => s.ServiceTokenIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ServiceTokenScope>(e =>
        {
            e.ToTable("service_token_scopes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ServiceTokenIdentityId).IsRequired();
            e.Property(x => x.AppScopeCode).IsRequired().HasMaxLength(100);
            e.Property(x => x.GrantedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.GrantedByUserId).IsRequired();
            e.Property(x => x.TenantId).HasDefaultValue(1L);

            e.HasIndex(x => new { x.TenantId, x.ServiceTokenIdentityId, x.AppScopeCode })
                .HasDatabaseName("ix_service_token_scopes_tenant_token_scope");
            e.HasIndex(x => x.RevokedAt).HasDatabaseName("ix_service_token_scopes_revoked_at");
        });
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. Reads connection
/// string from env var <c>NICKERP_PLATFORM_DB_CONNECTION</c> with a localhost
/// fallback so EF tooling works on developer machines without env config.
/// </summary>
public sealed class IdentityDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=designtime";

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(IdentityDbContext).Assembly.GetName().Name))
            .Options;

        return new IdentityDbContext(options);
    }
}
