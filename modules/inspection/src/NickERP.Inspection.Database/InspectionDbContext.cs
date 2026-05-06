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
    /// <summary>
    /// Sprint 48 / Phase B — append-only per-(case, rule) snapshot rows
    /// from <see cref="NickERP.Inspection.Application.Validation.ValidationEngine"/>.
    /// Allows the case-detail page to hydrate the validation pane on
    /// cold reload without re-running the engine.
    /// </summary>
    public DbSet<ValidationRuleSnapshot> ValidationRuleSnapshots => Set<ValidationRuleSnapshot>();
    public DbSet<IcumsSigningKey> IcumsSigningKeys => Set<IcumsSigningKey>();
    public DbSet<ScannerThresholdProfile> ScannerThresholdProfiles => Set<ScannerThresholdProfile>();
    public DbSet<OutcomePullCursor> OutcomePullCursors => Set<OutcomePullCursor>();
    public DbSet<PostHocRolloutPhase> PostHocRolloutPhases => Set<PostHocRolloutPhase>();
    public DbSet<AnalysisService> AnalysisServices => Set<AnalysisService>();
    public DbSet<AnalysisServiceLocation> AnalysisServiceLocations => Set<AnalysisServiceLocation>();
    public DbSet<AnalysisServiceUser> AnalysisServiceUsers => Set<AnalysisServiceUser>();
    public DbSet<CaseClaim> CaseClaims => Set<CaseClaim>();

    /// <summary>
    /// Sprint 31 / B5.1 — wall-clock SLA window rows; one per
    /// (CaseId, WindowName). Auto-opened on case creation and
    /// auto-closed on terminal-state transitions by
    /// <c>SlaTracker</c>.
    /// </summary>
    public DbSet<SlaWindow> SlaWindows => Set<SlaWindow>();

    /// <summary>
    /// Sprint 31 / B5.2 — cross-record-scan detection rows for
    /// multi-container case candidates. One row per detection event;
    /// state transitions via <c>CrossRecordScanService</c>.
    /// </summary>
    public DbSet<CrossRecordDetection> CrossRecordDetections => Set<CrossRecordDetection>();

    /// <summary>
    /// Sprint 41 / Phase A — scanner onboarding questionnaire responses
    /// (per Annex B Table 55). One row per (TenantId, ScannerDeviceTypeId,
    /// FieldName) per recording. Append-on-overwrite — the
    /// <c>ScannerOnboardingService</c> reader takes the latest
    /// <see cref="ScannerOnboardingResponse.RecordedAt"/> per field.
    /// </summary>
    public DbSet<ScannerOnboardingResponse> ScannerOnboardingResponses => Set<ScannerOnboardingResponse>();

    /// <summary>
    /// Sprint 41 / Phase B — append-only history of threshold-value
    /// changes per scanner. One row per (ScannerDeviceInstanceId,
    /// ModelId, ClassId) per change. Auto-emitted from
    /// <c>ThresholdAdminService.ApproveAsync</c>.
    /// </summary>
    public DbSet<ThresholdProfileHistory> ThresholdProfileHistory => Set<ThresholdProfileHistory>();

    /// <summary>
    /// Sprint 41 / Phase C — per-tenant per-adapter cursor for the
    /// outbound webhook dispatcher. One row per (TenantId, AdapterName).
    /// </summary>
    public DbSet<WebhookCursor> WebhookCursors => Set<WebhookCursor>();

    /// <summary>
    /// Sprint 50 / FU-cursor-state-persistence — per-tenant per-(scanner
    /// type, adapter) cursor durable state for the AseSyncWorker (and
    /// any future cursor-sync worker). Replaces the in-memory dict
    /// AseSyncWorker shipped with at Sprint 24.
    /// </summary>
    public DbSet<ScannerCursorSyncState> ScannerCursorSyncStates => Set<ScannerCursorSyncState>();

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
            // Sprint 34 / B6 — review queue priority bucket. Persisted
            // as int via HasConversion<int>() for stable wire format.
            e.Property(x => x.ReviewQueue).HasConversion<int>().IsRequired().HasDefaultValue(ReviewQueue.Standard);
            // Sprint 39 — retention class + legal hold. Persisted as int
            // via HasConversion<int>() for stable wire format. LegalHold
            // defaults to false; LegalHoldReason bounded to 500 chars
            // matching TenantState.DeletionReason.
            e.Property(x => x.RetentionClass)
                .HasConversion<int>()
                .IsRequired()
                .HasDefaultValue(NickERP.Inspection.Core.Retention.RetentionClass.Standard);
            e.Property(x => x.LegalHold).IsRequired().HasDefaultValue(false);
            e.Property(x => x.LegalHoldReason).HasMaxLength(500);
            // Sprint 38 — synthetic-data marker for pilot readiness gate.
            // Default false; tests opt-in to true so the
            // gate.analyst.decisioned_real_case probe can ignore them.
            e.Property(x => x.IsSynthetic).IsRequired().HasDefaultValue(false);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.LocationId, x.State, x.OpenedAt }).HasDatabaseName("ix_cases_tenant_loc_state_time");
            e.HasIndex(x => new { x.TenantId, x.SubjectIdentifier }).HasDatabaseName("ix_cases_tenant_subject");
            e.HasIndex(x => x.AssignedAnalystUserId).HasDatabaseName("ix_cases_assigned_analyst");
            // Sprint 34 / B6 — review-queue ordering: highest priority
            // first, then oldest open case. Composite (TenantId,
            // ReviewQueue DESC, State, OpenedAt) covers the
            // /reviews/queue and supervisor /reviews/audit page hot
            // paths on every load.
            e.HasIndex(x => new { x.TenantId, x.ReviewQueue, x.State, x.OpenedAt })
                .IsDescending(false, true, false, false)
                .HasDatabaseName("ix_cases_tenant_queue_state_time");
            // Sprint 39 — supporting indexes for the RetentionEnforcerWorker
            // hot path + the /admin/retention list view. Filtered partial
            // index on legal-hold cases keeps it tiny (most cases never
            // hold). The class+ClosedAt composite covers the
            // purge-candidate selection (TenantId, RetentionClass,
            // ClosedAt) for Standard/Extended classes.
            e.HasIndex(x => new { x.TenantId, x.LegalHold })
                .HasFilter("\"LegalHold\" = TRUE")
                .HasDatabaseName("ix_cases_legal_hold");
            e.HasIndex(x => new { x.TenantId, x.RetentionClass, x.ClosedAt })
                .HasDatabaseName("ix_cases_retention_class");

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
            // Sprint 39 — retention class + legal hold mirror the
            // InspectionCase shape. Cascades on case-level reclassify
            // by default; can be set independently for narrow subpoena
            // scope.
            e.Property(x => x.RetentionClass)
                .HasConversion<int>()
                .IsRequired()
                .HasDefaultValue(NickERP.Inspection.Core.Retention.RetentionClass.Standard);
            e.Property(x => x.LegalHold).IsRequired().HasDefaultValue(false);
            e.Property(x => x.LegalHoldReason).HasMaxLength(500);
            // Sprint 45 / Phase B — canonical ScanPackage manifest fields.
            // Nullable across the board so artifacts ingested via the
            // pre-Sprint-45 path (no canonical bundle) coexist with
            // canonical-bundle artifacts post-rollout.
            e.Property(x => x.ManifestJson).HasColumnType("jsonb");
            e.Property(x => x.ManifestSha256).HasColumnType("bytea");
            e.Property(x => x.ManifestSignature).HasColumnType("bytea");
            e.Property(x => x.ManifestVerifiedAt);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.ContentHash).HasDatabaseName("ix_scan_artifacts_content_hash");
            e.HasIndex(x => new { x.TenantId, x.ScanId }).HasDatabaseName("ix_scan_artifacts_tenant_scan");
            // Sprint 39 — supporting indexes mirror the case-level
            // shape. Filtered partial index on legal hold keeps it tiny
            // (most artifacts never hold).
            e.HasIndex(x => new { x.TenantId, x.LegalHold })
                .HasFilter("\"LegalHold\" = TRUE")
                .HasDatabaseName("ix_scan_artifacts_legal_hold");
            e.HasIndex(x => new { x.TenantId, x.RetentionClass, x.CreatedAt })
                .HasDatabaseName("ix_scan_artifacts_retention_class");
            // Sprint 45 / Phase B — newly-verified feed for the
            // /admin/sla per-tier dashboard's "manifest replays in the
            // last hour" panel. (TenantId, ManifestVerifiedAt DESC).
            e.HasIndex(x => new { x.TenantId, x.ManifestVerifiedAt })
                .IsDescending(false, true)
                .HasDatabaseName("ix_scan_artifacts_tenant_manifest_verified");

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
            // Sprint 34 / B6 — review type + outcome + completion
            // timestamp + supervisor-distinct started-by user id. All
            // additive; existing rows backfill ReviewType=Standard
            // (the enum's 0 value) and the rest stay null.
            e.Property(x => x.ReviewType).HasConversion<int>().IsRequired().HasDefaultValue(ReviewType.Standard);
            e.Property(x => x.Outcome).HasMaxLength(64);
            e.Property(x => x.CompletedAt);
            e.Property(x => x.StartedByUserId);
            e.Property(x => x.TenantId).IsRequired();

            // Sprint 34 / B6 — throughput dashboard query path:
            // (TenantId, ReviewType, CreatedAt). Covers the "how many
            // BL reviews this week" / "supervisor pass-rate" rollups
            // in ReviewQueueService.GetThroughputAsync.
            e.HasIndex(x => new { x.TenantId, x.ReviewType, x.CreatedAt })
                .HasDatabaseName("ix_analyst_reviews_tenant_type_time");

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
            e.Property(x => x.Priority).IsRequired().HasDefaultValue(0);
            e.Property(x => x.LastAttemptAt);
            // Sprint 36 / FU-outbound-dispatch-retry — bounded retry budget +
            // exponential backoff scaffolding. Existing rows inherit
            // RetryCount=0 (default) + NextAttemptAt=NULL (eligible now)
            // after the migration so the production queue keeps moving
            // without operator action.
            e.Property(x => x.RetryCount).IsRequired().HasDefaultValue(0);
            e.Property(x => x.NextAttemptAt);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique().HasDatabaseName("ux_outbound_tenant_idempotency");
            e.HasIndex(x => new { x.TenantId, x.Status }).HasDatabaseName("ix_outbound_tenant_status");
            // Sprint 22 / B2.1 — admin queue ordering: highest priority
            // first, oldest submission next. Composite (TenantId, Status,
            // Priority DESC, SubmittedAt) covers the submission-queue
            // page's hot path on every refresh.
            e.HasIndex(x => new { x.TenantId, x.Status, x.Priority, x.SubmittedAt })
                .IsDescending(false, false, true, false)
                .HasDatabaseName("ix_outbound_tenant_status_priority_time");
            // Sprint 36 / FU-outbound-dispatch-retry — pickup query needs
            // (Status, NextAttemptAt) so the dispatcher can ignore rows
            // currently inside their backoff window. Postgres can use the
            // existing (TenantId, Status, ...) index for the equality but
            // the NULL-or-LE filter on NextAttemptAt is cheap to add here.
            e.HasIndex(x => new { x.TenantId, x.Status, x.NextAttemptAt })
                .HasDatabaseName("ix_outbound_tenant_status_next_attempt");

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

        // ----- ValidationRuleSnapshot (Sprint 48 / Phase B) -------------------------
        // Append-only per-(case, rule) snapshot from the ValidationEngine.
        // No unique index — each evaluation appends a fresh row so the
        // history of past validation runs is implicit (most-recent-per-
        // (case, rule) is the read path used by the case-detail page on
        // cold reload). Composite (TenantId, CaseId, EvaluatedAt DESC)
        // covers the read-by-case ordering; (TenantId, CaseId, RuleId,
        // EvaluatedAt DESC) covers per-rule history drill-downs.
        modelBuilder.Entity<ValidationRuleSnapshot>(e =>
        {
            e.ToTable("validation_rule_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.RuleId).IsRequired().HasMaxLength(128);
            e.Property(x => x.Severity).IsRequired();
            e.Property(x => x.Outcome).IsRequired().HasMaxLength(16);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.PropertiesJson)
                .IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.EvaluatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            // Hot path — page reload reads "snapshots for this case,
            // most-recent first, then dedupes by RuleId in memory".
            e.HasIndex(x => new { x.TenantId, x.CaseId, x.EvaluatedAt })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_validation_snap_tenant_case_at");

            // Per-rule history drill-down on /admin/rules/{ruleId}.
            e.HasIndex(x => new { x.TenantId, x.RuleId, x.EvaluatedAt })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_validation_snap_tenant_rule_at");
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

        // ----- OutcomePullCursor (Sprint 13 / §6.11.8) ------------------------------
        // Per-instance pull cursor for the inbound post-hoc outcome
        // adapter. Table created by 20260429062458_Add_PhaseR3_TablesInferenceModernization;
        // RLS + FORCE RLS already installed. The mapping below was missing
        // during R3 — added now so the OutcomePullWorker can reach the rows
        // via EF.
        modelBuilder.Entity<OutcomePullCursor>(e =>
        {
            e.ToTable("outcome_pull_cursors");
            e.HasKey(x => x.ExternalSystemInstanceId);
            e.Property(x => x.ExternalSystemInstanceId).ValueGeneratedNever();
            e.Property(x => x.LastSuccessfulPullAt).IsRequired();
            e.Property(x => x.LastPullWindowUntil).IsRequired();
            e.Property(x => x.ConsecutiveFailures).IsRequired().HasDefaultValue(0);
            e.Property(x => x.TenantId).IsRequired();

            // Mirror the R3 migration's index so the model snapshot stays in sync.
            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_outcome_pull_cursors_tenant");

            e.HasOne(x => x.ExternalSystemInstance)
                .WithMany()
                .HasForeignKey(x => x.ExternalSystemInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ----- PostHocRolloutPhase (Sprint 13 / §6.11.13) ---------------------------
        // Per-authority rollout-phase tracking. One row per
        // (TenantId, ExternalSystemInstanceId). Table created by
        // 20260429062458_Add_PhaseR3_TablesInferenceModernization; RLS +
        // FORCE RLS already installed. Adding the EF mapping now so the
        // OutcomePullWorker can enumerate phase rows.
        modelBuilder.Entity<PostHocRolloutPhase>(e =>
        {
            e.ToTable("posthoc_rollout_phase");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.ExternalSystemInstanceId).IsRequired();
            e.Property(x => x.CurrentPhase).HasConversion<int>().IsRequired();
            e.Property(x => x.PhaseEnteredAt).IsRequired();
            e.Property(x => x.PromotedByUserId);
            e.Property(x => x.GateNotesJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            // R3 migration index parity:
            //   IX_posthoc_rollout_phase_ExternalSystemInstanceId — single-col
            //   ux_posthoc_rollout_tenant_instance — unique on (TenantId, InstanceId)
            e.HasIndex(x => x.ExternalSystemInstanceId)
                .HasDatabaseName("IX_posthoc_rollout_phase_ExternalSystemInstanceId");
            e.HasIndex(x => new { x.TenantId, x.ExternalSystemInstanceId })
                .IsUnique()
                .HasDatabaseName("ux_posthoc_rollout_tenant_instance");

            e.HasOne(x => x.ExternalSystemInstance)
                .WithMany()
                .HasForeignKey(x => x.ExternalSystemInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ----- AnalysisService (Sprint 14 / VP6 Phase A) ----------------------------
        // VP6: image-analysis is organised into one or more services per
        // tenant. N:N location↔service via AnalysisServiceLocation.
        // Users join services via AnalysisServiceUser. Built-in immutable
        // "All Locations" service per tenant via the unique partial index
        // ux_analysis_services_tenant_built_in WHERE IsBuiltInAllLocations.
        modelBuilder.Entity<AnalysisService>(e =>
        {
            e.ToTable("analysis_services");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.IsBuiltInAllLocations).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.CreatedByUserId);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_analysis_services_tenant");
            e.HasIndex(x => new { x.TenantId, x.Name })
                .IsUnique()
                .HasDatabaseName("ux_analysis_services_tenant_name");

            // Partial unique index: at most one IsBuiltInAllLocations=true row per tenant.
            e.HasIndex(x => x.TenantId)
                .IsUnique()
                .HasFilter("\"IsBuiltInAllLocations\" = TRUE")
                .HasDatabaseName("ux_analysis_services_tenant_built_in");
        });

        // ----- AnalysisServiceLocation (Sprint 14 / VP6 Phase A) --------------------
        modelBuilder.Entity<AnalysisServiceLocation>(e =>
        {
            e.ToTable("analysis_service_locations");
            e.HasKey(x => new { x.AnalysisServiceId, x.LocationId });
            e.Property(x => x.AddedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_analysis_service_locations_tenant");
            e.HasIndex(x => x.LocationId).HasDatabaseName("ix_analysis_service_locations_location");

            e.HasOne(x => x.AnalysisService)
                .WithMany()
                .HasForeignKey(x => x.AnalysisServiceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ----- AnalysisServiceUser (Sprint 14 / VP6 Phase A) ------------------------
        modelBuilder.Entity<AnalysisServiceUser>(e =>
        {
            e.ToTable("analysis_service_users");
            e.HasKey(x => new { x.AnalysisServiceId, x.UserId });
            e.Property(x => x.AssignedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.AssignedByUserId);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_analysis_service_users_tenant");
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_analysis_service_users_user");

            e.HasOne(x => x.AnalysisService)
                .WithMany()
                .HasForeignKey(x => x.AnalysisServiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ----- CaseClaim (Sprint 14 / VP6 Phase A) ----------------------------------
        // First-claim-wins lock under shared visibility. The unique
        // partial index ux_case_claims_active_per_case enforces
        // at-most-one-active-claim per case (WHERE ReleasedAt IS NULL).
        modelBuilder.Entity<CaseClaim>(e =>
        {
            e.ToTable("case_claims");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.AnalysisServiceId).IsRequired();
            e.Property(x => x.ClaimedByUserId).IsRequired();
            e.Property(x => x.ClaimedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.ReleasedAt);
            e.Property(x => x.ReleasedByUserId);
            e.Property(x => x.TenantId).IsRequired();

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_case_claims_tenant");
            e.HasIndex(x => x.CaseId).HasDatabaseName("ix_case_claims_case");
            e.HasIndex(x => x.AnalysisServiceId).HasDatabaseName("ix_case_claims_service");

            // At most one active claim per case (ReleasedAt IS NULL).
            e.HasIndex(x => x.CaseId)
                .IsUnique()
                .HasFilter("\"ReleasedAt\" IS NULL")
                .HasDatabaseName("ux_case_claims_active_per_case");

            e.HasOne(x => x.Case)
                .WithMany()
                .HasForeignKey(x => x.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AnalysisService)
                .WithMany()
                .HasForeignKey(x => x.AnalysisServiceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ----- SlaWindow (Sprint 31 / B5.1) ----------------------------------------
        // One row per (CaseId, WindowName) per case. Auto-opened on
        // case creation; auto-closed on terminal-state transitions.
        // Composite index (TenantId, State, DueAt) backs the SLA
        // dashboard's hottest query — "open windows past their
        // deadline, ordered by oldest" — without a sort.
        modelBuilder.Entity<SlaWindow>(e =>
        {
            e.ToTable("sla_window");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.WindowName).IsRequired().HasMaxLength(128);
            e.Property(x => x.StartedAt).IsRequired();
            e.Property(x => x.DueAt).IsRequired();
            e.Property(x => x.ClosedAt);
            e.Property(x => x.State).HasConversion<int>().IsRequired();
            e.Property(x => x.BudgetMinutes).IsRequired();
            // Sprint 45 / Phase C — queue tier (default Standard for
            // pre-Sprint-45 rows). Manual flag is fail-safe false so
            // legacy rows participate in auto-escalation by default.
            e.Property(x => x.QueueTier)
                .HasConversion<int>()
                .IsRequired()
                .HasDefaultValue(QueueTier.Standard);
            e.Property(x => x.QueueTierIsManual)
                .IsRequired()
                .HasDefaultValue(false);
            e.Property(x => x.TenantId).IsRequired();

            // One open window per (Case, WindowName) — the unique
            // partial index lets the SlaTracker idempotency-guard the
            // open path without a transactional read-then-write race.
            e.HasIndex(x => new { x.CaseId, x.WindowName })
                .IsUnique()
                .HasFilter("\"ClosedAt\" IS NULL")
                .HasDatabaseName("ux_sla_window_open_per_case_window");

            // Dashboard "all open / breached for tenant" page; covered
            // by (TenantId, State, DueAt).
            e.HasIndex(x => new { x.TenantId, x.State, x.DueAt })
                .HasDatabaseName("ix_sla_window_tenant_state_due");

            // Per-case scan; useful for the case-detail SLA pane.
            e.HasIndex(x => new { x.TenantId, x.CaseId })
                .HasDatabaseName("ix_sla_window_tenant_case");

            // Sprint 45 / Phase C — per-tier dashboard breakdown +
            // QueueEscalatorWorker scan path: "open windows of tier X
            // older than threshold Y". Composite (TenantId, QueueTier,
            // State) covers the breakdown card query and the escalator
            // worker's tier filter.
            e.HasIndex(x => new { x.TenantId, x.QueueTier, x.State })
                .HasDatabaseName("ix_sla_window_tenant_tier_state");
        });

        // ----- CrossRecordDetection (Sprint 31 / B5.2) ------------------------------
        // One row per detector hit. Composite (TenantId, State,
        // DetectedAt DESC) backs the admin /admin/cross-record-scans
        // page's "pending review, newest first" query.
        modelBuilder.Entity<CrossRecordDetection>(e =>
        {
            e.ToTable("cross_record_detection");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.CaseId).IsRequired();
            e.Property(x => x.DetectedAt).IsRequired();
            e.Property(x => x.DetectorVersion).IsRequired().HasMaxLength(32);
            e.Property(x => x.State).HasConversion<int>().IsRequired();
            e.Property(x => x.DetectedSubjectsJson)
                .IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
            e.Property(x => x.SplitCaseIdsJson).HasColumnType("jsonb");
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.ReviewedByUserId);
            e.Property(x => x.ReviewedAt);
            e.Property(x => x.TenantId).IsRequired();

            // Idempotent re-detection — at most one row per (CaseId,
            // DetectorVersion) keeps the table small and lets the
            // detector use ON CONFLICT semantics without a separate
            // dedupe table.
            e.HasIndex(x => new { x.CaseId, x.DetectorVersion })
                .IsUnique()
                .HasDatabaseName("ux_cross_record_detection_case_version");

            // Admin queue: pending detections newest first.
            e.HasIndex(x => new { x.TenantId, x.State, x.DetectedAt })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_cross_record_detection_tenant_state_detected");

            e.HasIndex(x => new { x.TenantId, x.CaseId })
                .HasDatabaseName("ix_cross_record_detection_tenant_case");
        });

        // ----- ScannerOnboardingResponse (Sprint 41 / Phase A) ----------------------
        // One row per (TenantId, ScannerDeviceTypeId, FieldName) per
        // recording. Append-on-overwrite; reader takes the latest
        // RecordedAt per field. No unique constraint — the history view
        // is the typical query so we keep prior rows.
        modelBuilder.Entity<ScannerOnboardingResponse>(e =>
        {
            e.ToTable("scanner_onboarding_responses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScannerDeviceTypeId).IsRequired().HasMaxLength(64);
            e.Property(x => x.FieldName).IsRequired().HasMaxLength(64);
            e.Property(x => x.Value).IsRequired().HasColumnType("text");
            e.Property(x => x.RecordedAt).IsRequired();
            e.Property(x => x.RecordedByUserId);
            e.Property(x => x.TenantId).IsRequired();

            // Reader hot-path — "latest answers for this scanner type
            // for this tenant". Composite (TenantId,
            // ScannerDeviceTypeId, FieldName, RecordedAt DESC) covers it.
            e.HasIndex(x => new { x.TenantId, x.ScannerDeviceTypeId, x.FieldName, x.RecordedAt })
                .IsDescending(false, false, false, true)
                .HasDatabaseName("ix_scanner_onboarding_tenant_type_field_time");

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_scanner_onboarding_tenant");
        });

        // ----- ThresholdProfileHistory (Sprint 41 / Phase B) ------------------------
        // Append-only — no DELETE. Mirrors the audit posture of
        // audit.events for the "reversible" requirement of doc-analysis
        // Table 21. One row per (ScannerDeviceInstanceId, ModelId,
        // ClassId) per change.
        modelBuilder.Entity<ThresholdProfileHistory>(e =>
        {
            e.ToTable("threshold_profile_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScannerDeviceInstanceId).IsRequired();
            e.Property(x => x.ModelId).IsRequired().HasMaxLength(128);
            e.Property(x => x.ClassId).IsRequired().HasMaxLength(128);
            e.Property(x => x.OldThreshold);
            e.Property(x => x.NewThreshold).IsRequired();
            e.Property(x => x.ChangedAt).IsRequired();
            e.Property(x => x.ChangedByUserId);
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.TenantId).IsRequired();

            // Hot path — "show me every change for scanner X, newest first".
            e.HasIndex(x => new { x.TenantId, x.ScannerDeviceInstanceId, x.ChangedAt })
                .IsDescending(false, false, true)
                .HasDatabaseName("ix_threshold_history_tenant_scanner_time");

            e.HasIndex(x => x.TenantId).HasDatabaseName("ix_threshold_history_tenant");
        });

        // ----- WebhookCursor (Sprint 41 / Phase C) ----------------------------------
        // One row per (TenantId, AdapterName); the dispatcher records
        // the highest audit.events.event_id it has successfully forwarded
        // to the adapter, and reads forward from there on the next tick.
        modelBuilder.Entity<WebhookCursor>(e =>
        {
            e.ToTable("webhook_cursors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.AdapterName).IsRequired().HasMaxLength(128);
            e.Property(x => x.LastProcessedEventId).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.TenantId).IsRequired();

            // One cursor per (Tenant, Adapter); upsert on this constraint.
            e.HasIndex(x => new { x.TenantId, x.AdapterName })
                .IsUnique()
                .HasDatabaseName("ux_webhook_cursors_tenant_adapter");
        });

        // ----- ScannerCursorSyncState (Sprint 50 / FU-cursor-state-persistence) ----------
        // One row per (TenantId, ScannerDeviceTypeId, AdapterName). Optimistic
        // concurrency on ConcurrencyToken — multi-host advances converge via
        // SaveChanges retry, not a lock.
        modelBuilder.Entity<ScannerCursorSyncState>(e =>
        {
            e.ToTable("scanner_cursor_sync_states");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.ScannerDeviceTypeId).IsRequired().HasMaxLength(64);
            e.Property(x => x.AdapterName).IsRequired().HasMaxLength(64);
            e.Property(x => x.LastCursorValue).IsRequired().HasMaxLength(256);
            e.Property(x => x.LastAdvancedAt).IsRequired();
            // EF Core treats a uint xmin shadow as the row-version, but we
            // own the bump explicitly so the test path on EF in-memory
            // stays predictable. ConcurrencyCheck (not RowVersion) keeps
            // the increment under our control while still failing
            // SaveChanges on a stale value.
            e.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
            e.Property(x => x.TenantId).IsRequired();

            // One cursor per (Tenant, ScannerDeviceTypeId, AdapterName).
            e.HasIndex(x => new { x.TenantId, x.ScannerDeviceTypeId, x.AdapterName })
                .IsUnique()
                .HasDatabaseName("ux_cursor_sync_state_tenant_type_adapter");
            e.HasIndex(x => x.TenantId)
                .HasDatabaseName("ix_cursor_sync_state_tenant");
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
