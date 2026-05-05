using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Pilot;

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

    /// <summary>
    /// Sprint 29 — per-tenant module enable/disable rows for the portal
    /// launcher. Tenant-scoped (<see cref="ITenantOwned"/>); the
    /// <c>(TenantId, ModuleId)</c> pair is unique. Backs
    /// <c>IModuleRegistry.GetModulesAsync(tenantId)</c>.
    /// </summary>
    public DbSet<TenantModuleSetting> TenantModuleSettings => Set<TenantModuleSetting>();

    /// <summary>
    /// Sprint 31 / B5.1 — per-tenant on/off flags + threshold overrides
    /// for inspection completeness requirements. Tenant-scoped
    /// (<see cref="ITenantOwned"/>); under tenancy RLS via the
    /// <c>tenant_isolation_tenant_completeness_settings</c> policy added
    /// in <c>Add_TenantCompletenessSettings</c>. Sparse rows — a missing
    /// row implies Enabled=true at the requirement's default threshold.
    /// </summary>
    public DbSet<TenantCompletenessSetting> TenantCompletenessSettings => Set<TenantCompletenessSetting>();

    /// <summary>
    /// Sprint 31 / B5.2 — per-tenant SLA-window budget overrides for
    /// inspection. Tenant-scoped + RLS-enforced (mirrors the Sprint 28
    /// validation-rule settings posture). Sparse rows — a missing row
    /// implies "use the engine default budget".
    /// </summary>
    public DbSet<TenantSlaSetting> TenantSlaSettings => Set<TenantSlaSetting>();

    /// <summary>
    /// Sprint 35 / B8.2 — per-tenant feature flag rows. Sparse rows —
    /// a missing row implies "use the default the calling code passed
    /// to <c>IFeatureFlagService.IsEnabledAsync</c>". Tenant-scoped +
    /// RLS-enforced via <c>tenant_isolation_feature_flags</c>.
    /// </summary>
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();

    /// <summary>
    /// Sprint 35 / B8.2 — generic per-tenant key/value settings table
    /// (default SLA budgets, retention windows, comms-gateway endpoints).
    /// Sparse rows — a missing row implies "use the default the calling
    /// code passed to <c>ITenantSettingsService.GetAsync</c>".
    /// Tenant-scoped + RLS-enforced via
    /// <c>tenant_isolation_tenant_settings</c>.
    /// </summary>
    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    /// <summary>
    /// Sprint 43 — append-only snapshot rows from
    /// <c>PilotReadinessService.GetReadinessAsync</c>. One row per
    /// <c>(TenantId, GateId)</c> per refresh; the dashboard at
    /// <c>/admin/pilot-readiness</c> reads the latest row per gate.
    /// Cross-tenant by design (admin tooling); not under RLS — same
    /// posture as <c>TenantPurgeLog</c> + <c>TenantExportRequest</c>.
    /// </summary>
    public DbSet<PilotReadinessSnapshot> PilotReadinessSnapshots => Set<PilotReadinessSnapshot>();

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

        // Sprint 29 — TenantModuleSetting. Tenant-scoped (ITenantOwned);
        // stamped by the existing TenantOwnedEntityInterceptor on insert.
        // The (TenantId, ModuleId) unique index enforces "at most one row
        // per module per tenant" and is the index the registry's
        // upsert path collides on.
        modelBuilder.Entity<TenantModuleSetting>(e =>
        {
            e.ToTable("tenant_module_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.ModuleId).IsRequired().HasMaxLength(64);
            e.Property(x => x.Enabled).IsRequired().HasDefaultValue(true);
            e.Property(x => x.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.UpdatedByUserId);

            e.HasIndex(x => new { x.TenantId, x.ModuleId })
                .IsUnique()
                .HasDatabaseName("ux_tenant_module_settings_tenant_module");
        });

        // Sprint 31 / B5.1 — per-tenant completeness-requirement settings.
        // Mirrors the Sprint 28 TenantValidationRuleSetting posture
        // (sparse rows, RLS-enforced, ITenantOwned). Adds an optional
        // numeric MinThreshold override so percent-based + count-based
        // requirements can share the same row shape without a
        // discriminator column.
        modelBuilder.Entity<TenantCompletenessSetting>(e =>
        {
            e.ToTable("tenant_completeness_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.RequirementId).IsRequired().HasMaxLength(128);
            e.Property(x => x.Enabled).HasDefaultValue(true);
            // numeric(9,4) — generous for percent (0..100.0000) and
            // count-style thresholds (up to 99999.9999) without the
            // serialisation cost of decimal(38).
            e.Property(x => x.MinThreshold).HasColumnType("numeric(9,4)");
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.UpdatedByUserId);

            // Unique index — one row per (TenantId, RequirementId).
            // Backs the upsert path in CompletenessService.SetEnabledAsync.
            e.HasIndex(x => new { x.TenantId, x.RequirementId })
                .IsUnique()
                .HasDatabaseName("ux_tenant_completeness_settings_tenant_req");
        });

        // Sprint 31 / B5.2 — per-tenant SLA-window budget overrides.
        // Same shape as TenantCompletenessSetting; kept as a separate
        // entity so SLA budgets ("how long is the window?") and
        // completeness requirements ("does the case have an X?") stay
        // disjoint for analytics queries.
        modelBuilder.Entity<TenantSlaSetting>(e =>
        {
            e.ToTable("tenant_sla_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.WindowName).IsRequired().HasMaxLength(128);
            e.Property(x => x.TargetMinutes).IsRequired();
            e.Property(x => x.Enabled).HasDefaultValue(true);
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.UpdatedByUserId);

            e.HasIndex(x => new { x.TenantId, x.WindowName })
                .IsUnique()
                .HasDatabaseName("ux_tenant_sla_settings_tenant_window");
        });

        // Sprint 35 / B8.2 — per-tenant feature flag rows. Same posture
        // as the Sprint 28 validation-rule settings: sparse rows,
        // RLS-enforced, ITenantOwned, no DELETE in the role grants.
        modelBuilder.Entity<FeatureFlag>(e =>
        {
            e.ToTable("feature_flags");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.FlagKey).IsRequired().HasMaxLength(128);
            e.Property(x => x.Enabled).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.UpdatedByUserId);

            // Unique index — one row per (TenantId, FlagKey). Backs
            // the upsert path in FeatureFlagService.SetAsync.
            e.HasIndex(x => new { x.TenantId, x.FlagKey })
                .IsUnique()
                .HasDatabaseName("ux_feature_flags_tenant_flag");
        });

        // Sprint 35 / B8.2 — generic per-tenant key/value settings.
        // Same shape as FeatureFlag but with a string Value instead of
        // a bool Enabled.
        modelBuilder.Entity<TenantSetting>(e =>
        {
            e.ToTable("tenant_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.SettingKey).IsRequired().HasMaxLength(128);
            // text — no length cap. SMTP hostnames, JSON snippets,
            // multi-line PEM material can all live here.
            e.Property(x => x.Value).IsRequired().HasColumnType("text");
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.UpdatedByUserId);

            e.HasIndex(x => new { x.TenantId, x.SettingKey })
                .IsUnique()
                .HasDatabaseName("ux_tenant_settings_tenant_key");
        });

        // Sprint 43 — PilotReadinessSnapshot. Cross-tenant by design
        // (admin tooling); not under RLS, same posture as
        // tenant_purge_log + tenant_export_requests. Append-only —
        // INSERT only in the role grants, no UPDATE / DELETE so the
        // gate-state history is preserved across refreshes for the
        // dashboard's "first observed / latest observed" rendering.
        modelBuilder.Entity<PilotReadinessSnapshot>(e =>
        {
            e.ToTable("pilot_readiness_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.GateId).IsRequired().HasMaxLength(128);
            e.Property(x => x.State).HasConversion<int>().IsRequired();
            e.Property(x => x.ObservedAt).IsRequired();
            e.Property(x => x.ProofEventId);
            e.Property(x => x.Note).HasMaxLength(1000);

            // Index supports the dashboard's "latest snapshot per gate per
            // tenant" query: SELECT ... WHERE TenantId = @t ORDER BY
            // ObservedAt DESC LIMIT 1 (per gate). DESC on ObservedAt so
            // the index can satisfy the ORDER BY without a sort.
            e.HasIndex(x => new { x.TenantId, x.GateId, x.ObservedAt })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_pilot_readiness_snapshots_tenant_gate_observedat");
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
