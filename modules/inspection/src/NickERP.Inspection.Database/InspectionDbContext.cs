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
    public DbSet<LocationAssignment> LocationAssignments => Set<LocationAssignment>();
    public DbSet<InspectionCase> Cases => Set<InspectionCase>();
    public DbSet<Scan> Scans => Set<Scan>();
    public DbSet<ScanArtifact> ScanArtifacts => Set<ScanArtifact>();
    public DbSet<ScanRenderArtifact> ScanRenderArtifacts => Set<ScanRenderArtifact>();
    public DbSet<ScanRenderAttempt> ScanRenderAttempts => Set<ScanRenderAttempt>();
    public DbSet<AuthorityDocument> AuthorityDocuments => Set<AuthorityDocument>();
    public DbSet<ReviewSession> ReviewSessions => Set<ReviewSession>();
    public DbSet<AnalystReview> AnalystReviews => Set<AnalystReview>();
    public DbSet<Finding> Findings => Set<Finding>();
    public DbSet<Verdict> Verdicts => Set<Verdict>();
    public DbSet<OutboundSubmission> OutboundSubmissions => Set<OutboundSubmission>();
    public DbSet<RuleEvaluation> RuleEvaluations => Set<RuleEvaluation>();
    public DbSet<IcumsSigningKey> IcumsSigningKeys => Set<IcumsSigningKey>();
    public DbSet<ScannerThresholdProfile> ScannerThresholdProfiles => Set<ScannerThresholdProfile>();

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

        // ----- LocationAssignment ---------------------------------------------------
        modelBuilder.Entity<LocationAssignment>(e =>
        {
            e.ToTable("location_assignments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.IdentityUserId).IsRequired();
            e.Property(x => x.LocationId).IsRequired();
            e.Property(x => x.Roles).IsRequired().HasMaxLength(500).HasDefaultValue(string.Empty);
            e.Property(x => x.GrantedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.GrantedByUserId).IsRequired();
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.IdentityUserId, x.LocationId }).IsUnique().HasDatabaseName("ux_location_assignments_tenant_user_loc");
            e.HasIndex(x => x.IdentityUserId).HasDatabaseName("ix_location_assignments_user");
            e.HasIndex(x => x.LocationId).HasDatabaseName("ix_location_assignments_loc");

            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        // ----- InspectionCase -------------------------------------------------------
        modelBuilder.Entity<InspectionCase>(e =>
        {
            e.ToTable("cases");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.LocationId).IsRequired();
            e.Property(x => x.SubjectType).HasConversion<int>().IsRequired();
            e.Property(x => x.SubjectIdentifier).IsRequired().HasMaxLength(200);
            e.Property(x => x.SubjectPayloadJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.State).HasConversion<int>().IsRequired();
            e.Property(x => x.OpenedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.StateEnteredAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.LocationId, x.State, x.OpenedAt }).HasDatabaseName("ix_cases_tenant_loc_state_time");
            e.HasIndex(x => new { x.TenantId, x.SubjectIdentifier }).HasDatabaseName("ix_cases_tenant_subject");
            e.HasIndex(x => x.AssignedAnalystUserId).HasDatabaseName("ix_cases_assigned_analyst");

            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Scans).WithOne(s => s.Case).HasForeignKey(s => s.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Documents).WithOne(d => d.Case).HasForeignKey(d => d.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.ReviewSessions).WithOne(rs => rs.Case).HasForeignKey(rs => rs.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Submissions).WithOne(o => o.Case).HasForeignKey(o => o.CaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Verdict).WithOne(v => v.Case!).HasForeignKey<Verdict>(v => v.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- Scan -----------------------------------------------------------------
        modelBuilder.Entity<Scan>(e =>
        {
            e.ToTable("scans");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.ScannerDeviceInstanceId).IsRequired();
            e.Property(x => x.Mode).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.CaseId, x.CapturedAt }).HasDatabaseName("ix_scans_tenant_case_time");
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasDatabaseName("ux_scans_tenant_idempotency");
            e.HasIndex(x => x.ScannerDeviceInstanceId).HasDatabaseName("ix_scans_device");

            e.HasOne(x => x.ScannerDeviceInstance).WithMany().HasForeignKey(x => x.ScannerDeviceInstanceId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Artifacts).WithOne(a => a.Scan).HasForeignKey(a => a.ScanId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- ScanArtifact ---------------------------------------------------------
        modelBuilder.Entity<ScanArtifact>(e =>
        {
            e.ToTable("scan_artifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScanId).IsRequired();
            e.Property(x => x.ArtifactKind).IsRequired().HasMaxLength(32).HasDefaultValue("Primary");
            e.Property(x => x.StorageUri).IsRequired().HasMaxLength(500);
            e.Property(x => x.MimeType).IsRequired().HasMaxLength(64);
            e.Property(x => x.ContentHash).IsRequired().HasMaxLength(128);
            e.Property(x => x.MetadataJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.ContentHash).HasDatabaseName("ix_scan_artifacts_content_hash");
            e.HasIndex(x => new { x.TenantId, x.ScanId }).HasDatabaseName("ix_scan_artifacts_tenant_scan");

            e.HasMany<ScanRenderArtifact>().WithOne(r => r.ScanArtifact).HasForeignKey(r => r.ScanArtifactId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- ScanRenderArtifact ---------------------------------------------------
        modelBuilder.Entity<ScanRenderArtifact>(e =>
        {
            e.ToTable("scan_render_artifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScanArtifactId).IsRequired();
            e.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            e.Property(x => x.StorageUri).IsRequired().HasMaxLength(500);
            e.Property(x => x.MimeType).IsRequired().HasMaxLength(64);
            e.Property(x => x.ContentHash).IsRequired().HasMaxLength(128);
            e.Property(x => x.RenderedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            // One row per (artifact, kind) — re-renders update in place.
            e.HasIndex(x => new { x.ScanArtifactId, x.Kind }).IsUnique().HasDatabaseName("ux_render_artifact_kind");
            e.HasIndex(x => new { x.TenantId, x.ScanArtifactId }).HasDatabaseName("ix_render_tenant_artifact");
        });

        // ----- ScanRenderAttempt (Phase F5) -----------------------------------------
        // Sibling tracking table for the PreRenderWorker. Records every
        // failed render so we can stop retrying poison messages after
        // ImagingOptions.MaxRenderAttempts.
        modelBuilder.Entity<ScanRenderAttempt>(e =>
        {
            e.ToTable("scan_render_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScanArtifactId).IsRequired();
            e.Property(x => x.Kind).IsRequired().HasMaxLength(32);
            e.Property(x => x.AttemptCount).HasDefaultValue(0);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.LastAttemptAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.ScanArtifactId, x.Kind }).IsUnique().HasDatabaseName("ux_render_attempt_artifact_kind");
            e.HasIndex(x => x.PermanentlyFailedAt).HasDatabaseName("ix_render_attempt_failed");
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_render_attempt_tenant");
        });

        // ----- AuthorityDocument ----------------------------------------------------
        modelBuilder.Entity<AuthorityDocument>(e =>
        {
            e.ToTable("authority_documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.ExternalSystemInstanceId).IsRequired();
            e.Property(x => x.DocumentType).IsRequired().HasMaxLength(64);
            e.Property(x => x.ReferenceNumber).IsRequired().HasMaxLength(200);
            e.Property(x => x.PayloadJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.ReceivedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.CaseId }).HasDatabaseName("ix_authority_docs_tenant_case");
            e.HasIndex(x => new { x.TenantId, x.ReferenceNumber }).HasDatabaseName("ix_authority_docs_tenant_ref");

            e.HasOne(x => x.ExternalSystemInstance).WithMany().HasForeignKey(x => x.ExternalSystemInstanceId).OnDelete(DeleteBehavior.Restrict);
        });

        // ----- ReviewSession --------------------------------------------------------
        modelBuilder.Entity<ReviewSession>(e =>
        {
            e.ToTable("review_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.AnalystUserId).IsRequired();
            e.Property(x => x.StartedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.Outcome).IsRequired().HasMaxLength(32).HasDefaultValue("in-progress");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.CaseId, x.StartedAt }).HasDatabaseName("ix_review_sessions_tenant_case_time");
            e.HasIndex(x => x.AnalystUserId).HasDatabaseName("ix_review_sessions_analyst");

            e.HasMany(x => x.Reviews).WithOne(r => r.Session).HasForeignKey(r => r.ReviewSessionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- AnalystReview --------------------------------------------------------
        modelBuilder.Entity<AnalystReview>(e =>
        {
            e.ToTable("analyst_reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ReviewSessionId).IsRequired();
            e.Property(x => x.RoiInteractionsJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.VerdictChangesJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.PostHocOutcomeJson).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasMany(x => x.Findings).WithOne(f => f.Review).HasForeignKey(f => f.AnalystReviewId).OnDelete(DeleteBehavior.Cascade);
        });

        // ----- Finding --------------------------------------------------------------
        modelBuilder.Entity<Finding>(e =>
        {
            e.ToTable("findings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AnalystReviewId).IsRequired();
            e.Property(x => x.FindingType).IsRequired().HasMaxLength(64);
            e.Property(x => x.Severity).IsRequired().HasMaxLength(16).HasDefaultValue("info");
            e.Property(x => x.LocationInImageJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.FindingType }).HasDatabaseName("ix_findings_tenant_type");
            e.HasIndex(x => x.Severity).HasDatabaseName("ix_findings_severity");
        });

        // ----- Verdict --------------------------------------------------------------
        modelBuilder.Entity<Verdict>(e =>
        {
            e.ToTable("verdicts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.Decision).HasConversion<int>().IsRequired();
            e.Property(x => x.Basis).HasMaxLength(2000);
            e.Property(x => x.DecidedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.DecidedByUserId).IsRequired();
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.CaseId).IsUnique().HasDatabaseName("ux_verdicts_case");
            e.HasIndex(x => new { x.TenantId, x.Decision, x.DecidedAt }).HasDatabaseName("ix_verdicts_tenant_decision_time");
        });

        // ----- OutboundSubmission ---------------------------------------------------
        modelBuilder.Entity<OutboundSubmission>(e =>
        {
            e.ToTable("outbound_submissions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.ExternalSystemInstanceId).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            e.Property(x => x.Status).IsRequired().HasMaxLength(32).HasDefaultValue("pending");
            e.Property(x => x.ResponseJson).HasColumnType("jsonb");
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.Property(x => x.SubmittedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasDatabaseName("ux_outbound_tenant_idempotency");
            e.HasIndex(x => new { x.TenantId, x.Status }).HasDatabaseName("ix_outbound_tenant_status");

            e.HasOne(x => x.ExternalSystemInstance).WithMany().HasForeignKey(x => x.ExternalSystemInstanceId).OnDelete(DeleteBehavior.Restrict);
        });

        // ----- RuleEvaluation (Sprint A1) -------------------------------------------
        // Persisted snapshot of one authority's rules pack run against a case.
        // One row per (CaseId, AuthorityCode) — re-evaluation overwrites the
        // existing snapshot. The (TenantId, CaseId, EvaluatedAt DESC) index
        // serves the analyst's "latest per case" query in CaseDetail.Reload().
        modelBuilder.Entity<RuleEvaluation>(e =>
        {
            e.ToTable("rule_evaluations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.AuthorityCode).IsRequired().HasMaxLength(64);
            e.Property(x => x.EvaluatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.ViolationsJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.MutationsJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.ProviderErrorsJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.TenantId).IsRequired();

            // Composite (TenantId, CaseId, EvaluatedAt DESC) — the canonical
            // "latest evaluations for this case" query in the analyst UI.
            e.HasIndex(x => new { x.TenantId, x.CaseId, x.EvaluatedAt })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_rule_eval_tenant_case_at");

            // Snapshot semantics — at most one row per (CaseId, AuthorityCode).
            // Re-evaluation upserts on this constraint.
            e.HasIndex(x => new { x.TenantId, x.CaseId, x.AuthorityCode })
                .IsUnique()
                .HasDatabaseName("ux_rule_eval_tenant_case_authority");
        });

        // ----- ScannerThresholdProfile (Phase R3 / §6.5) ----------------------------
        // Per-scanner threshold profile with proposed/shadow/active lifecycle.
        // Table created by 20260429062458_Add_PhaseR3_TablesInferenceModernization;
        // RLS + FORCE RLS already installed by the same migration. The
        // mapping below was missing during R3 (table + entity + migration
        // shipped, OnModelCreating did not) — added now so the
        // ScannerThresholdResolver and the admin Thresholds page can
        // reach the rows via EF.
        modelBuilder.Entity<ScannerThresholdProfile>(e =>
        {
            e.ToTable("scanner_threshold_profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScannerDeviceInstanceId).IsRequired();
            e.Property(x => x.Version).IsRequired();
            e.Property(x => x.ValuesJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.EffectiveFrom);
            e.Property(x => x.EffectiveTo);
            e.Property(x => x.ProposedBy).HasConversion<int>().IsRequired();
            e.Property(x => x.ProposalRationaleJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.ApprovedByUserId);
            e.Property(x => x.ApprovedAt);
            e.Property(x => x.ShadowStartedAt);
            e.Property(x => x.ShadowCompletedAt);
            e.Property(x => x.ShadowOutcomeJson).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            // Partial unique index on (ScannerDeviceInstanceId) WHERE Status = 'active' (20).
            // Created by the R3 migration; mirrored here so the model
            // snapshot stays in sync (otherwise the next migration would
            // try to drop+recreate it).
            e.HasIndex(x => x.ScannerDeviceInstanceId)
                .IsUnique()
                .HasDatabaseName("ux_scanner_threshold_profiles_active")
                .HasFilter("\"Status\" = 20");

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_scanner_threshold_profiles_tenant");

            e.HasIndex(x => new { x.TenantId, x.ScannerDeviceInstanceId, x.Version })
                .IsUnique()
                .HasDatabaseName("ux_scanner_threshold_profiles_tenant_scanner_version");

            e.HasOne(x => x.ScannerDeviceInstance)
                .WithMany()
                .HasForeignKey(x => x.ScannerDeviceInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ----- IcumsSigningKey (Sprint 9 / FU-icums-signing) ------------------------
        // Per-tenant HMAC-SHA256 signing key for the IcumsGh adapter's
        // pre-emptive envelope-signing flow. Key material is wrapped via
        // ASP.NET Core data protection at the service layer; the column
        // here only ever holds ciphertext.
        modelBuilder.Entity<IcumsSigningKey>(e =>
        {
            e.ToTable("icums_signing_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.KeyId).IsRequired().HasMaxLength(32);
            e.Property(x => x.KeyMaterialEncrypted).IsRequired().HasColumnType("bytea");
            e.Property(x => x.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.ActivatedAt);
            e.Property(x => x.RetiredAt);
            e.Property(x => x.VerificationOnlyUntil);

            // Per-tenant key id is unique — allows the signature header to
            // carry just the short key id without ambiguity.
            e.HasIndex(x => new { x.TenantId, x.KeyId })
                .IsUnique()
                .HasDatabaseName("ux_icums_signing_keys_tenant_keyid");

            // Hot-path lookup: find the active-for-signing key for a tenant.
            e.HasIndex(x => new { x.TenantId, x.ActivatedAt, x.RetiredAt })
                .HasDatabaseName("ix_icums_signing_keys_tenant_active");
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
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name);
                // H3 — keep EF Core's history table inside the inspection
                // schema so nscim_app never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "inspection");
            })
            .Options;

        return new InspectionDbContext(options);
    }
}
