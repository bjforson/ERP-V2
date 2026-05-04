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

    /// <summary>
    /// Sprint 18 — append-only log of hard-purge operations. Survives
    /// the tenant rows it describes. Not tenant-scoped (cross-tenant by
    /// design); not under RLS.
    /// </summary>
    public DbSet<TenantPurgeLog> TenantPurgeLog => Set<TenantPurgeLog>();

    /// <summary>
    /// Sprint 25 — admin-initiated scoped export requests for tenant
    /// data. Cross-tenant by design (admin tooling); not under RLS.
    /// </summary>
    public DbSet<TenantExportRequest> TenantExportRequests => Set<TenantExportRequest>();

    /// <summary>
    /// Sprint 28 — per-tenant on/off flags for inspection
    /// <c>IValidationRule</c>s. Sparse rows (only persists when admin
    /// disables a rule); under tenancy RLS via the
    /// <c>tenant_isolation_tenant_validation_rule_settings</c> policy
    /// added in <c>Add_TenantValidationRuleSettings</c>.
    /// </summary>
    public DbSet<TenantValidationRuleSetting> TenantValidationRuleSettings => Set<TenantValidationRuleSetting>();

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

            // Sprint 18 — lifecycle state. Default Active so brand-new
            // tenants are reachable without an explicit transition.
            e.Property(x => x.State)
                .HasConversion<int>()
                .HasDefaultValue(TenantState.Active);
            e.Property(x => x.DeletedAt);
            e.Property(x => x.DeletedByUserId);
            e.Property(x => x.DeletionReason).HasMaxLength(500);
            e.Property(x => x.RetentionDays).HasDefaultValue(90);
            e.Property(x => x.HardPurgeAfter);

            // Computed property — not mapped to a column.
            e.Ignore(x => x.IsActive);

            e.Property(x => x.CaseVisibilityModel).HasConversion<int>().HasDefaultValue(CaseVisibilityModel.Shared);
            e.Property(x => x.AllowMultiServiceMembership).HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_tenants_code");
            // Sprint 18 — supports the "active tenants" filter query and
            // the "all soft-deleted tenants pending purge" admin view.
            e.HasIndex(x => x.State).HasDatabaseName("ix_tenants_state");

            // Sprint 18 — global query filter hides SoftDeleted +
            // PendingHardPurge tenants from default queries. Admin pages
            // override via IgnoreQueryFilters() to surface them.
            // Suspended is intentionally NOT filtered out — a suspended
            // tenant still exists for ops listings; only deleted-but-
            // retained tenants are hidden by default.
            e.HasQueryFilter(t =>
                t.State != TenantState.SoftDeleted
                && t.State != TenantState.PendingHardPurge);

            // Seed the default tenant. Stable id 1; matches the DefaultTenantId
            // constant so business-data DEFAULTs and seed migrations on other
            // schemas can reference it directly.
            e.HasData(new
            {
                Id = Tenant.DefaultTenantId,
                Code = Tenant.DefaultTenantCode,
                Name = "Nick TC-Scan Operations",
                BillingPlan = "internal",
                TimeZone = "Africa/Accra",
                Locale = "en-GH",
                Currency = "GHS",
                State = TenantState.Active,
                RetentionDays = 90,
                CaseVisibilityModel = CaseVisibilityModel.Shared,
                AllowMultiServiceMembership = true,
                CreatedAt = new DateTimeOffset(2026, 4, 26, 0, 0, 0, TimeSpan.Zero)
            });
        });

        modelBuilder.Entity<TenantPurgeLog>(e =>
        {
            e.ToTable("tenant_purge_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.TenantCode).IsRequired().HasMaxLength(64);
            e.Property(x => x.TenantName).IsRequired().HasMaxLength(200);
            e.Property(x => x.PurgedAt).IsRequired();
            e.Property(x => x.PurgedByUserId).IsRequired();
            e.Property(x => x.DeletionReason).HasMaxLength(500);
            e.Property(x => x.SoftDeletedAt);
            e.Property(x => x.RowCounts).HasColumnType("jsonb");
            e.Property(x => x.Outcome).IsRequired().HasMaxLength(32);
            e.Property(x => x.FailureNote).HasMaxLength(1000);

            e.HasIndex(x => x.PurgedAt).HasDatabaseName("ix_tenant_purge_log_purgedat");
        });

        // Sprint 25 — TenantExportRequest. Mirrors the TenantPurgeLog
        // posture: cross-tenant by design, not under RLS, surfaced only
        // through admin tooling. The (TenantId, RequestedAt DESC) index
        // backs the per-tenant "recent exports" admin view.
        modelBuilder.Entity<TenantExportRequest>(e =>
        {
            e.ToTable("tenant_export_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.RequestedAt).IsRequired();
            e.Property(x => x.RequestedByUserId).IsRequired();
            e.Property(x => x.Format).HasConversion<int>().HasDefaultValue(TenantExportFormat.JsonBundle);
            e.Property(x => x.Scope).HasConversion<int>().HasDefaultValue(TenantExportScope.All);
            e.Property(x => x.Status).HasConversion<int>().HasDefaultValue(TenantExportStatus.Pending);
            e.Property(x => x.ArtifactPath).HasMaxLength(500);
            e.Property(x => x.ArtifactSizeBytes);
            // SHA-256 = 32 raw bytes; pin column type so Postgres uses
            // bytea rather than the bigint default for byte[].
            e.Property(x => x.ArtifactSha256).HasColumnType("bytea");
            e.Property(x => x.ExpiresAt);
            e.Property(x => x.CompletedAt);
            e.Property(x => x.FailureReason).HasMaxLength(1000);
            e.Property(x => x.DownloadCount).HasDefaultValue(0);
            e.Property(x => x.LastDownloadedAt);
            e.Property(x => x.RevokedAt);
            e.Property(x => x.RevokedByUserId);

            // Index supports the per-tenant "recent exports" view in the
            // admin UI: SELECT ... WHERE TenantId = @t ORDER BY RequestedAt DESC.
            e.HasIndex(x => new { x.TenantId, x.RequestedAt })
                .HasDatabaseName("ix_tenant_export_requests_tenant_requestedat")
                .IsDescending(false, true);
            // Index supports the runner's pickup query: SELECT ... WHERE
            // Status = Pending ORDER BY RequestedAt ASC LIMIT N.
            e.HasIndex(x => new { x.Status, x.RequestedAt })
                .HasDatabaseName("ix_tenant_export_requests_status_requestedat");
        });

        // Sprint 28 — per-tenant validation-rule enable flags.
        // Tenant-scoped + RLS-enforced (see Add_TenantValidationRuleSettings
        // migration). Sparse: a missing row implies Enabled=true so the
        // admin only persists rows for the rules they want to disable.
        modelBuilder.Entity<TenantValidationRuleSetting>(e =>
        {
            e.ToTable("tenant_validation_rule_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.RuleId).IsRequired().HasMaxLength(128);
            e.Property(x => x.Enabled).HasDefaultValue(true);
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.UpdatedByUserId);

            // Unique index — one row per (TenantId, RuleId). Backs the
            // upsert path in RulesAdminService.SetRuleEnabledAsync and the
            // bulk-disabled-rules read in DbRuleEnablementProvider.
            e.HasIndex(x => new { x.TenantId, x.RuleId })
                .IsUnique()
                .HasDatabaseName("ux_tenant_validation_rule_settings_tenant_rule");
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
