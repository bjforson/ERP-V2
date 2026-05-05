using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Application.Detection;
using NickERP.Inspection.Application.Downloads;
using NickERP.Inspection.Application.ExternalSystems;
using NickERP.Inspection.Application.Icums;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Application.Sla;
using NickERP.Inspection.Application.Submissions;
using NickERP.Inspection.Application.Thresholds;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Database;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Components;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Inspection.Web.HealthChecks;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Identity.Database.Services;
using NickERP.Platform.Logging;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
// Sprint 29 — three-module co-deploy chrome.
using NickERP.Platform.Web.Shared.Modules;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Track A — observability (Logging + Telemetry).
// ---------------------------------------------------------------------------
builder.UseNickErpLogging("NickERP.Inspection.Web");
builder.UseNickErpTelemetry("NickERP.Inspection.Web");

// ---------------------------------------------------------------------------
// Track A — Identity (auth + DB-backed resolver), Tenancy, Audit (events).
// All three live in the platform DB; inspection has its own DB.
// ---------------------------------------------------------------------------
var platformConn = builder.Configuration.GetConnectionString("Platform");
var inspectionConn = builder.Configuration.GetConnectionString("Inspection");

builder.Services.AddNickErpIdentity(builder.Configuration, builder.Environment);
builder.Services.AddNickErpIdentityCore(platformConn);
builder.Services.AddNickErpTenancy();
// D2 — TenancyDbContext registered so the ScannerIngestionWorker can
// enumerate active tenants for cross-tenant scanner discovery
// (`tenancy.tenants` is the only table not under RLS).
builder.Services.AddNickErpTenancyCore(platformConn);
builder.Services.AddNickErpAuditCore(platformConn);

// Sprint 8 P3 — projection of audit.events into the user-facing
// notifications inbox. 1s poll in dev / 5s in prod (default). Three
// hardcoded rules: case-opened (notifies opener), case-assigned
// (notifies analyst), verdict-rendered (notifies opener).
builder.Services.AddNickErpAuditNotifications(opts =>
{
    opts.PollIntervalSeconds = builder.Environment.IsDevelopment() ? 1 : 5;
});

// Sprint 14 / VP6 Phase A.5 — auto-join interceptor + bootstrap registrations.
// The interceptor adds an AnalysisServiceLocation row for every new Location
// in the same SaveChanges. The bootstrap is registered for parity with
// apps/portal so direct callers in Inspection.Web (e.g., admin tooling)
// can ensure a tenant's "All Locations" service exists before the first
// location insert. Both registrations are idempotent (TryAdd*).
builder.Services.AddAnalysisServiceBootstrap();
builder.Services.AddAnalysisServiceLocationAutoJoinInterceptor();

// Sprint 14 / VP6 Phase B — admin service for the /admin/analysis-services
// list + detail Razor pages. Scoped (mirrors the Phase A.5 bootstrap).
builder.Services.AddAnalysisServiceAdmin();

// Sprint 16 / LA1 extension — admin service for /external-systems Razor
// page. Centralises the per-scope (PerLocation/SubsetOfLocations/Shared)
// validation + binding writes so the page doesn't have to. Scoped.
builder.Services.AddExternalSystemAdmin();

// Sprint 22 / B2.1 — ICUMS submission queue admin service for the
// /admin/icums/submission-queue Razor page. Scoped; idempotent
// (TryAddScoped under the hood).
builder.Services.AddIcumsSubmissionQueueAdmin();

// Sprint 22 / B2.2 — ICUMS download queue admin service for the
// /admin/icums/download-queue Razor page. Reads AuthorityDocument +
// OutcomePullCursor; supports admin re-link of a case override.
builder.Services.AddIcumsDownloadQueueAdmin();

// Sprint 22 / B2.3 — ICUMS dashboard sub-page admin services
// (dashboard summary, loose cargo, BOE lookup) + the manual-pull
// service that re-fetches outcomes from one external system instance
// on operator command. Manual-pull lives in Inspection.Web because
// it needs IPluginRegistry to resolve adapters.
builder.Services.AddIcumsDashboardSuite();
builder.Services.AddScoped<NickERP.Inspection.Web.Services.IcumsManualPullService>();

// Sprint 14 / VP6 Phase C — claim semantics + case-visibility helper.
// Cases.razor consults CaseVisibilityService for the access-list under
// the tenant's CaseVisibilityModel; CaseDetail.razor consults
// CaseClaimService for the active-claim badge + acquire/release flow.
builder.Services.AddCaseClaimAndVisibility();

// Inspection's own DbContext. Phase F1 — wires the tenancy interceptors
// (push app.tenant_id to Postgres for RLS + stamp TenantId on inserts).
// Sprint 14 / VP6 Phase A.5 — also wires the AnalysisServiceLocation
// auto-join interceptor so every Location insert through this DbContext
// auto-joins the tenant's "All Locations" service in the same SaveChanges.
builder.Services.AddDbContext<InspectionDbContext>((sp, opts) =>
{
    opts.UseNpgsql(inspectionConn ?? throw new InvalidOperationException(
        "ConnectionStrings:Inspection is required (the nickerp_inspection Postgres DB)."),
        npgsql =>
        {
            npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name);
            // H3 — keep EF Core's history table inside the inspection
            // schema so nscim_app never needs CREATE on `public`.
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "inspection");
        });
    opts.AddInterceptors(
        sp.GetRequiredService<TenantConnectionInterceptor>(),
        sp.GetRequiredService<TenantOwnedEntityInterceptor>(),
        sp.GetRequiredService<AnalysisServiceLocationAutoJoinInterceptor>());
});

// ---------------------------------------------------------------------------
// Sprint 9 / FU-icums-signing — ASP.NET Core data protection. Used to
// wrap the per-tenant HMAC keys at rest in inspection.icums_signing_keys.
// Key ring lives at the platform default (
// %LOCALAPPDATA%\ASP.NET\DataProtection-Keys on Windows / ~/.aspnet/DataProtection-Keys
// on Linux); for clustered deploys the ring needs to be shared (FileShare
// or AzureBlob persistKeysToX). Out of scope for FU-icums-signing — the
// rotation runbook calls this out as a follow-up.
//
// The application name keeps the key ring scoped to this host's purpose
// strings; without it, two NickERP services on the same box would share
// data-protection keys (harmless but surprising).
// ---------------------------------------------------------------------------
builder.Services.AddDataProtection()
    .SetApplicationName("NickERP.Inspection.Web");

// IIcumsEnvelopeSigner — registered unconditionally (cheap; only called
// when the IcumsGh:Sign feature flag is on). Scoped because it captures
// the request-scoped InspectionDbContext.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.IcumsHmacEnvelopeSigner>();
builder.Services.AddScoped<NickERP.Inspection.ExternalSystems.Abstractions.IIcumsEnvelopeSigner>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.IcumsHmacEnvelopeSigner>());
builder.Services.AddScoped<NickERP.Inspection.Web.Services.IcumsKeyRotationService>();

// Sprint 13 / P2-FU-edge-auth — per-edge-node API key auth handler for
// /api/edge/replay + the issuance/revocation service the admin UI in
// apps/portal calls. The hasher + envelope live in Platform.Audit.Database
// (alongside the entity) so the portal can use them too without a
// cross-host project reference.
//
// EdgeKeyHasher is scoped because it captures IConfiguration + the
// envelope; the envelope depends on IDataProtectionProvider so it's
// registered as a singleton (the DP provider is itself a singleton).
builder.Services.AddSingleton<NickERP.Platform.Audit.Database.IEdgeKeyHashEnvelope,
    NickERP.Inspection.Web.Services.DataProtectionEdgeKeyHashEnvelope>();
builder.Services.AddScoped<NickERP.Platform.Audit.Database.EdgeKeyHasher>();
builder.Services.AddScoped<NickERP.Platform.Audit.Database.EdgeNodeApiKeyService>();
builder.Services.AddScoped<NickERP.Inspection.Web.Services.EdgeAuthHandler>();

// ---------------------------------------------------------------------------
// Track A — Plugins. Loads adapter DLLs from {ContentRoot}/plugins. The
// inspection module's plugin contracts (IScannerAdapter, etc.) are
// implemented by adapters that drop into this folder.
// ---------------------------------------------------------------------------
var pluginsDir = Path.Combine(builder.Environment.ContentRootPath, "plugins");
builder.Services.AddNickErpPluginsEager(pluginsDir);

// ---------------------------------------------------------------------------
// Auth + Razor Components.
// ---------------------------------------------------------------------------
builder.Services.AddAuthorization(opts =>
{
    opts.DefaultPolicy = new AuthorizationPolicyBuilder(CfAccessAuthenticationOptions.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// Case workflow orchestration — single place where state transitions +
// DomainEvent emission happen. Pages call this; pages don't open the
// DbContext directly for workflow operations.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.CaseWorkflowService>();

// Sprint 28 / B4 — vendor-neutral validation engine + per-tenant rule
// enable flags (DbRuleEnablementProvider reads
// tenancy.tenant_validation_rule_settings via TenancyDbContext). The
// engine fires automatically inside CaseWorkflowService.FetchDocumentsAsync
// when the case enters Validated; pages can also call
// CaseWorkflowService.EvaluateValidationRulesAsync to re-run on demand.
//
// Built-in rules (vendor-neutral) ship via AddNickErpInspectionValidation.
// Plugin-shipped rules (e.g. CustomsGh) live in plugin DLLs loaded by
// AddNickErpPluginsEager above; we reflect over the registered plugin
// concretes to register any IValidationRule implementations as scoped
// IEnumerable<IValidationRule> contributions, so the engine picks them
// up transparently without the host project taking a hard reference on
// the plugin assembly.
builder.Services.AddNickErpInspectionValidation();
builder.Services.AddNickErpInspectionValidationDbProvider();
NickERP.Inspection.Application.Validation.PluginValidationRuleRegistration
    .RegisterPluginValidationRules(builder.Services, pluginsDir);

// Sprint 28 / B4 Phase C — admin service backing the /admin/rules pages.
// Reads/writes tenant_validation_rule_settings + reads recent failures
// from audit.events.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.RulesAdminService>();

// Sprint 31 / B5.1 — vendor-neutral completeness rollup engine. Same
// posture as the Sprint 28 validation engine: built-in requirements
// ship with the host, plugin requirements would land via a future
// CompletenessRequirementRegistration pass; the engine auto-fires on
// the Open→Validated transition.
builder.Services.AddNickErpInspectionCompleteness();
builder.Services.AddNickErpInspectionCompletenessDbProvider();
builder.Services.AddScoped<NickERP.Inspection.Web.Services.CompletenessService>();

// Sprint 31 / B5.1 — SLA window tracker + dashboard service. Tracker
// auto-opens windows on case creation + auto-closes them on terminal
// transitions; dashboard service backs /admin/sla. Both scoped.
builder.Services.AddNickErpInspectionSla(builder.Configuration);
builder.Services.AddNickErpInspectionSlaDbProvider();
builder.Services.AddScoped<NickERP.Inspection.Web.Services.SlaDashboardService>();

// Sprint 31 / B5.2 — cross-record-scan detector + admin service.
// Detector reads scan + document state for multi-subject hints;
// service drives the analyst confirm/dismiss/split workflow.
// (CaseWorkflowService registered earlier; CrossRecordScanService
// depends on it for the SplitCaseAsync hand-off.)
builder.Services.AddNickErpInspectionDetection();
builder.Services.AddScoped<NickERP.Inspection.Web.Services.CrossRecordScanService>();

// §6.5 admin actions — Approve / Reject for ScannerThresholdProfile.
// Mirrors CaseWorkflowService — pages call this so the audit emission
// + state-transition rules live in one place.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.ThresholdAdminService>();

// §6.11.9 — manual-entry path for post-hoc outcomes. Wraps the
// IPostHocOutcomeWriter so the Razor admin page (Components/Pages/
// PostHocOutcomes.razor) and any direct caller (tests) route through
// the same persistence + audit-emit shape as the worker.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.PostHocOutcomeManualEntryService>();

// Sprint 20 / B1.2 — analyst annotation persistence (Finding-backed)
// for the W/L viewer. Scoped because it captures InspectionDbContext.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.AnalystAnnotationService>();

// Sprint A2 — in-process MeterListener powering the /perf admin page.
// Singleton so the listener spans the host's lifetime; instruments are
// auto-discovered via NickErpActivity.Meter (the OTel pipeline picks
// them up the same way).
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.MeterSnapshotService>();

// Image pre-rendering pipeline (ARCHITECTURE §7.7) — IImageRenderer +
// IImageStore + the PreRenderWorker background service. The render
// endpoint is mapped below after app.Build().
builder.Services.AddNickErpImaging(builder.Configuration);

// Per-scanner threshold calibration (§6.5). Registers the resolver
// (IScannerThresholdResolver hot-path), the LISTEN/NOTIFY hosted
// service, and a degraded-on-disconnect health check. The threshold
// resolver is shared infrastructure — same instance services every
// request and every per-scan call.
builder.Services.AddScannerThresholdCalibration(builder.Configuration);

// D2 — ScannerIngestionWorker: drives every active ScannerDeviceInstance
// through IScannerAdapter.StreamAsync and creates/reuses a case for each
// emitted artifact. Closes the demo loop so dropping a real FS6000
// triplet into a watch folder produces a case end-to-end without a
// button click.
//
// Sprint 9 / FU-host-status — register as a singleton, then resolve it
// for both the hosted-service slot AND the IBackgroundServiceProbe slot.
// Critical invariant: ONE instance per worker. If AddHostedService<T>()
// alone is used, the host creates a separate instance and the probe
// registration resolves a different one — /healthz/workers would always
// show this worker as "never ticked".
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.ScannerIngestionWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.ScannerIngestionWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.ScannerIngestionWorker>());

// Sprint 13 / §6.11 — post-hoc outcome adapter pull pipeline. Binds
// OutcomeIngestionOptions from PostHocOutcomes:* config and registers
// the OutcomePullWorker as a singleton so the (hosted-service slot,
// probe slot) idiom resolves the same instance — same ScannerIngestion
// shape. The worker walks every active PostHocRolloutPhase row (phase
// >= Shadow), resolves its IInboundOutcomeAdapter plugin, and pulls
// outcomes via FetchOutcomesAsync into the AuthorityDocument table.
builder.Services.AddPostHocOutcomeIngestion(builder.Configuration);
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.OutcomePullWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.OutcomePullWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.OutcomePullWorker>());

// ---------------------------------------------------------------------------
// Sprint 24 / B3.1 — Specialised scanner workers. Both default-disabled
// per the Sprint 24 architectural decision; opt-in per environment via
// the corresponding Inspection:Workers:<Name>:Enabled flag.
//
// ScannerHealthSweepWorker — periodic IScannerAdapter.TestAsync sweep
// across every active scanner. Surfaces vendor connectivity loss
// without waiting for an inbound scan. Vendor-neutral.
//
// AseSyncWorker — periodic cursor pull for any scanner whose plugin
// implements IScannerCursorSyncAdapter (replaces v1's
// AseBackgroundService; vendor-neutralised).
// ---------------------------------------------------------------------------
builder.Services.Configure<ScannerHealthSweepOptions>(
    builder.Configuration.GetSection(ScannerHealthSweepOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.ScannerHealthSweepWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.ScannerHealthSweepWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.ScannerHealthSweepWorker>());

builder.Services.Configure<AseSyncOptions>(
    builder.Configuration.GetSection(AseSyncOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.AseSyncWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AseSyncWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AseSyncWorker>());

// ---------------------------------------------------------------------------
// Sprint 24 / B3.2 — ICUMS pipeline workers (vendor-neutralised). All
// default-disabled per Sprint 24 architectural decision; opt-in per
// environment via the corresponding Inspection:Workers:<Name>:Enabled
// flag.
//
// AuthorityDocumentBackfillWorker — periodic per-tenant fetch of
// authority documents via IExternalSystemAdapter.FetchDocumentsAsync.
// Replaces v1 IcumBackgroundService.
//
// AuthorityDocumentInboxWorker — watches a configurable filesystem
// drop folder for adapter-shaped JSON exports. Replaces v1
// IcumFileScannerService.
//
// OutboundSubmissionDispatchWorker — dispatches OutboundSubmission
// rows in 'pending' status. Replaces v1 ICUMSSubmissionService.
//
// OutboundSubmissionResultPollerWorker — re-polls in-flight
// submissions for deferred outcomes. Replaces v1
// ICUMSDownloadBackgroundService.
//
// AuthorityDocumentMatcherWorker — matches Scan rows to
// AuthorityDocument rows by container number + capture window.
// Replaces v1 ContainerDataMapperService.
// ---------------------------------------------------------------------------
builder.Services.Configure<IcumsApiPullOptions>(
    builder.Configuration.GetSection(IcumsApiPullOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.AuthorityDocumentBackfillWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AuthorityDocumentBackfillWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AuthorityDocumentBackfillWorker>());

builder.Services.Configure<IcumsFileScannerOptions>(
    builder.Configuration.GetSection(IcumsFileScannerOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.AuthorityDocumentInboxWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AuthorityDocumentInboxWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AuthorityDocumentInboxWorker>());

builder.Services.Configure<IcumsSubmissionDispatchOptions>(
    builder.Configuration.GetSection(IcumsSubmissionDispatchOptions.SectionName));
// Sprint 36 / FU-outbound-dispatch-retry — bounded retry budget +
// exponential backoff for the dispatch worker. Defaults applied when
// the section is absent (BaseBackoff=30s, MaxBackoff=1h, MaxRetries=5).
builder.Services.Configure<OutboundSubmissionRetryOptions>(
    builder.Configuration.GetSection(OutboundSubmissionRetryOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.OutboundSubmissionDispatchWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.OutboundSubmissionDispatchWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.OutboundSubmissionDispatchWorker>());

builder.Services.Configure<IcumsSubmissionResultPollerOptions>(
    builder.Configuration.GetSection(IcumsSubmissionResultPollerOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.OutboundSubmissionResultPollerWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.OutboundSubmissionResultPollerWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.OutboundSubmissionResultPollerWorker>());

builder.Services.Configure<ContainerDataMatcherOptions>(
    builder.Configuration.GetSection(ContainerDataMatcherOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.AuthorityDocumentMatcherWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AuthorityDocumentMatcherWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.AuthorityDocumentMatcherWorker>());

// ---------------------------------------------------------------------------
// Sprint 36 / FU-sla-state-refresher-worker — periodic SLA window state
// refresher. Calls SlaTracker.RefreshStatesAsync per case per tenant so
// AtRisk → Breached transitions write to inspection.sla_window without
// requiring a dashboard load. Default-disabled per Sprint 24 architectural
// decision; opt-in per environment via
// Inspection:Workers:SlaStateRefresher:Enabled=true.
// ---------------------------------------------------------------------------
builder.Services.Configure<SlaStateRefresherOptions>(
    builder.Configuration.GetSection(SlaStateRefresherOptions.SectionName));
builder.Services.AddSingleton<NickERP.Inspection.Web.Services.SlaStateRefresherWorker>();
builder.Services.AddHostedService(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.SlaStateRefresherWorker>());
builder.Services.AddSingleton<NickERP.Platform.Telemetry.IBackgroundServiceProbe>(
    sp => sp.GetRequiredService<NickERP.Inspection.Web.Services.SlaStateRefresherWorker>());

// ---------------------------------------------------------------------------
// Sprint 33 / B7 — reports + diagnostics services. Both scoped because
// they consume the per-request InspectionDbContext (and AuditDbContext
// for the reports service). Read-only by design — neither service
// performs writes; the dashboard pages only project existing data.
// ADDITIVE registration; doesn't touch any existing line.
// ---------------------------------------------------------------------------
builder.Services.AddScoped<NickERP.Inspection.Web.Services.ReportsService>();
builder.Services.AddScoped<NickERP.Inspection.Web.Services.DiagnosticsService>();

// Sprint 29 — wire shared chrome (SharedHeader + SharedFooter +
// IModuleContext). ADDITIVE: doesn't touch any existing registration.
// ModuleId pinned to "inspection" so the back-to-launcher link in the
// header always names the right module. PortalLauncherUrl reads from
// Portal:LauncherUrl with a dev-default fallback (http://localhost:5400/).
builder.Services.AddNickErpSharedChrome(opts =>
{
    opts.ModuleId = "inspection";
    opts.DisplayName = "Inspection v2";
    opts.PortalLauncherUrl = builder.Configuration["Portal:LauncherUrl"]
        ?? ModuleContext.DefaultLauncherUrl;
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------------------
// Phase F5 — health checks. /healthz/live is unconditional; /healthz/ready
// asserts every dependency the host needs to serve a real request:
//   - Postgres reachable on nickerp_platform (Identity + Audit DBs)
//   - Postgres reachable on nickerp_inspection
//   - Plugin registry populated (≥ 1 plugin loaded)
//   - ImagingOptions.StorageRoot writable
// Tag-filtered endpoints keep the live probe cheap.
// ---------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("process running"), tags: new[] { "live" })
    .AddCheck<PostgresHealthCheck<IdentityDbContext>>("postgres-platform-identity", tags: new[] { "ready" })
    .AddCheck<PostgresHealthCheck<NickERP.Platform.Audit.Database.AuditDbContext>>("postgres-platform-audit", tags: new[] { "ready" })
    .AddCheck<PostgresHealthCheck<InspectionDbContext>>("postgres-inspection", tags: new[] { "ready" })
    .AddCheck<PluginRegistryHealthCheck>("plugin-registry", tags: new[] { "ready" })
    .AddCheck<ImagingStorageHealthCheck>("imaging-storage", tags: new[] { "ready" });

// The DbContext-typed checks are resolved through DI — register them as
// scoped so each probe gets a fresh DbContext (Postgres connection
// pooling means this is cheap; matches how Razor pages consume it).
builder.Services.AddScoped(sp => new PostgresHealthCheck<IdentityDbContext>(
    sp.GetRequiredService<IdentityDbContext>(), "Postgres (identity)"));
builder.Services.AddScoped(sp => new PostgresHealthCheck<NickERP.Platform.Audit.Database.AuditDbContext>(
    sp.GetRequiredService<NickERP.Platform.Audit.Database.AuditDbContext>(), "Postgres (audit)"));
builder.Services.AddScoped(sp => new PostgresHealthCheck<InspectionDbContext>(
    sp.GetRequiredService<InspectionDbContext>(), "Postgres (inspection)"));

var app = builder.Build();

// Sprint A2 — eagerly instantiate MeterSnapshotService at startup so its
// MeterListener is wired before any meter records. Without this, the
// service stays uncreated until the first /perf request, missing every
// histogram/counter sample emitted in the meantime.
_ = app.Services.GetRequiredService<NickERP.Inspection.Web.Services.MeterSnapshotService>();

// ---------------------------------------------------------------------------
// Phase F5 — migrations at startup. Default ON in dev, OFF elsewhere; flag
// is RunMigrationsOnStartup so ops can override per environment without
// rebuilding. The migration applier runs synchronously inside the host's
// startup so a bad migration fails the boot rather than hiding behind a
// silent first-request error.
// ---------------------------------------------------------------------------
{
    var runMigrations = app.Configuration.GetValue<bool?>("RunMigrationsOnStartup")
        ?? app.Environment.IsDevelopment();
    if (runMigrations)
    {
        using var migrationScope = app.Services.CreateScope();
        var sp = migrationScope.ServiceProvider;
        var migrateLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Migrations");
        try
        {
            sp.GetRequiredService<IdentityDbContext>().Database.Migrate();
            sp.GetRequiredService<NickERP.Platform.Audit.Database.AuditDbContext>().Database.Migrate();
            // D2 — TenancyDbContext is registered (used by the
            // ScannerIngestionWorker for cross-tenant discovery); apply
            // its migrations at startup so the worker doesn't hit a
            // missing-table on first poll if Inspection boots before
            // the portal app has had a chance to seed.
            sp.GetRequiredService<TenancyDbContext>().Database.Migrate();
            sp.GetRequiredService<InspectionDbContext>().Database.Migrate();
            migrateLogger.LogInformation("Migrations applied for Identity, Audit, Tenancy, Inspection.");
        }
        catch (Exception ex)
        {
            migrateLogger.LogCritical(ex, "Migration at startup failed; aborting host bootstrap.");
            throw;
        }
    }
    else
    {
        var noMigrateLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Migrations");
        noMigrateLogger.LogInformation("RunMigrationsOnStartup=false; skipping Database.Migrate().");
    }
}

// ---------------------------------------------------------------------------
// Sprint 2 — H2 Identity-Tenancy Interlock guard. Verifies that
// identity.identity_users is NOT under FORCE ROW LEVEL SECURITY (the
// carve-out installed by 20260428104421_RemoveRlsFromIdentityUsers).
// Runs regardless of RunMigrationsOnStartup so a host booted against an
// already-migrated DB still detects a regression. Never throws — logs a
// structured IDENTITY-USERS-RLS-RE-ENABLED warning if RLS is re-enabled.
// ---------------------------------------------------------------------------
{
    using var guardScope = app.Services.CreateScope();
    var guardSp = guardScope.ServiceProvider;
    var guardLogger = guardSp.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Startup.IdentityUsersRlsGuard");
    await IdentityUsersRlsGuard.EnsureCarveOutAsync(
        guardSp.GetRequiredService<IdentityDbContext>(),
        guardLogger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseStaticFiles();
app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();
app.UseNickErpTenancy();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Sprint 8 P3 — notifications inbox API. Tenant + user scoping enforced
// at the endpoint layer (LINQ) and at the DB layer (RLS); auth required.
app.MapNotificationsEndpoints();

// Sprint 9 / FU-icums-signing — admin endpoints for rotating per-tenant
// IcumsGh signing keys. Auth + Inspection.Admin role required; tenant-
// scoped via the caller's tenant claim + RLS narrowing.
app.MapIcumsKeyRotationEndpoints();

// Sprint 11 / P2 — server-side replay endpoint. Anonymous endpoint
// gated by an X-Edge-Token header (shared secret matched against
// EdgeNode:SharedSecret in config). Writes audit.events under
// SetSystemContext on behalf of multiple tenants in a single batch
// — registered in docs/system-context-audit-register.md.
app.MapEdgeReplayEndpoint();

// Sprint 20 / B1.2 — analyst annotation REST surface backing the
// W/L viewer's markup tool. Auth required, tenant scoped via
// AnalystAnnotationService.
app.MapAnnotationsEndpoints();

// Sprint 9 / FU-host-status — /healthz/workers aggregator over every
// registered IBackgroundServiceProbe (PreRenderWorker, SourceJanitor,
// ScannerIngestion, AuditNotificationProjector). Auth required —
// runbook 03 calls it for worker-wedge diagnosis instead of
// log-grepping. Distinct from /healthz/ready (anonymous, kubelet-style)
// because the body carries last-error messages we don't expose
// anonymously.
app.MapWorkersHealthzEndpoint();

// ---------------------------------------------------------------------------
// Image pipeline endpoint — streams the pre-rendered derivative for a given
// ScanArtifact. Auth required (analyst flow); ETag against the render row's
// content hash so the browser short-circuits to 304 on subsequent loads.
// NO Range support yet, NO byte-range, NO output caching attribute (would
// re-serialize the stream and corrupt the bytes per ARCHITECTURE §7.7).
// ---------------------------------------------------------------------------
app.MapGet("/api/images/{scanArtifactId:guid}/{kind}", async (
    Guid scanArtifactId,
    string kind,
    HttpContext http,
    NickERP.Inspection.Database.InspectionDbContext db,
    NickERP.Inspection.Imaging.IImageStore store,
    Microsoft.Extensions.Options.IOptions<NickERP.Inspection.Imaging.ImagingOptions> opts,
    CancellationToken ct) =>
{
    // Sprint A2 — wall-clock the whole lambda so the /perf page can show
    // the p95 against ARCHITECTURE §7.7's bars (thumbs 50ms / preview 80ms).
    // Every return path records to nickerp.inspection.image.serve_ms; the
    // try/finally guarantees we never miss one.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    string status = "200";
    // Tag value defaults to whatever was requested so a 400 still tells
    // us which malformed kind the client tried.
    string kindTag = kind ?? "unknown";
    try
    {
        if (!NickERP.Inspection.Imaging.RenderKinds.IsKnown(kind))
        {
            status = "400";
            return Results.BadRequest($"Unknown kind '{kind}'. Expected 'thumbnail' or 'preview'.");
        }

        var row = await db.ScanRenderArtifacts.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ScanArtifactId == scanArtifactId && r.Kind == kind, ct);
        if (row is null)
        {
            status = "404";
            return Results.NotFound("Render not available yet — pre-render worker hasn't reached this artifact.");
        }

        // ETag handling — content-addressed, so the hash IS the version.
        var etag = $"\"{(row.ContentHash.Length >= 16 ? row.ContentHash[..16] : row.ContentHash)}\"";
        var ifNoneMatch = http.Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(etag, StringComparison.Ordinal))
        {
            status = "304";
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        // kind is non-null here (IsKnown(null) returns false → 400 above);
        // the try/finally restructure broke the compiler's flow analysis,
        // hence the explicit `!`. Behaviourally identical to the pre-A2 lambda.
        var stream = store.OpenRenderRead(scanArtifactId, kind!);
        if (stream is null)
        {
            status = "404";
            return Results.NotFound("Render row exists but storage blob is missing — most likely a stale row after manual cleanup.");
        }

        var o = opts.Value;
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl =
            $"public, max-age={o.HttpCacheMaxAgeSeconds}, s-maxage={o.HttpCacheSharedMaxAgeSeconds}, immutable";
        return Results.Stream(stream, contentType: row.MimeType, enableRangeProcessing: false);
    }
    finally
    {
        sw.Stop();
        NickERP.Platform.Telemetry.NickErpActivity.ImageServeMs.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("kind", kindTag),
            new KeyValuePair<string, object?>("status", status));
    }
})
.RequireAuthorization();

// ---------------------------------------------------------------------------
// Phase F5 — health endpoints. Both are anonymous so kubelets / load
// balancers don't have to carry credentials. /healthz/live is the cheap
// liveness probe (process is up); /healthz/ready runs every "ready" check
// and returns 503 with a JSON body listing the failing component(s).
// ---------------------------------------------------------------------------
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live")
}).AllowAnonymous();

app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            })
        };
        await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, payload);
    }
}).AllowAnonymous();

app.Run();

// Sprint D4 — expose the auto-generated top-level Program type so the
// E2E test project (tests/NickERP.Inspection.E2E.Tests) can spin up the
// host via `WebApplicationFactory<Program>`. Without this, the generated
// Program is internal and the factory can't see it. No runtime cost.
public partial class Program;
