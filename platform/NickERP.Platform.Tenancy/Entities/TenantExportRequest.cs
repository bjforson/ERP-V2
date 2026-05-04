namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 25 — platform-admin-generated scoped export of a tenant's data.
/// Locked answer 4 in the v2 plan: "platform-admin-generated scoped exports
/// on tenant request (audit-trailed)". Each row tracks one export from
/// request through Pending / Running / Completed / Failed / Expired /
/// Revoked.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>tenancy.tenant_export_requests</c> in <c>nickerp_platform</c>.
/// </para>
/// <para>
/// Cross-tenant by design (admin tooling); not under RLS. The same posture
/// as <see cref="TenantPurgeLog"/> — operator dashboards must see exports
/// across the whole suite, and the row's <see cref="TenantId"/> records
/// the export target, not the requesting user's tenant.
/// </para>
/// <para>
/// Lifecycle: <c>ITenantExportService.RequestExportAsync</c> creates
/// the row in <see cref="TenantExportStatus.Pending"/>; the
/// <c>TenantExportRunner</c> background service picks it up, transitions
/// it to <see cref="TenantExportStatus.Running"/> while building the
/// artifact, then to <see cref="TenantExportStatus.Completed"/> (with
/// <see cref="ArtifactPath"/>, <see cref="ArtifactSizeBytes"/>,
/// <see cref="ArtifactSha256"/>, <see cref="ExpiresAt"/>) or
/// <see cref="TenantExportStatus.Failed"/> (with
/// <see cref="FailureReason"/>). A scheduled sweep flips Completed rows
/// past their <see cref="ExpiresAt"/> to
/// <see cref="TenantExportStatus.Expired"/> and deletes the artifact;
/// admin revoke is <see cref="TenantExportStatus.Revoked"/>.
/// </para>
/// </remarks>
public sealed class TenantExportRequest
{
    /// <summary>Server-assigned primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant this export targets. Carries even after the
    /// tenant has been hard-purged (rows are retained for the audit trail).</summary>
    public long TenantId { get; set; }

    /// <summary>Wallclock the export was first requested.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Identity user id of the operator who initiated the export.</summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary>Bundle format. Picked at request time; the runner branches
    /// on it to choose how to write per-table data inside the zip.</summary>
    public TenantExportFormat Format { get; set; } = TenantExportFormat.JsonBundle;

    /// <summary>What slice of the tenant's data to include. Picked at
    /// request time; the runner branches on it to choose which DBs and
    /// tables to read.</summary>
    public TenantExportScope Scope { get; set; } = TenantExportScope.All;

    /// <summary>Current lifecycle status. See <see cref="TenantExportStatus"/>
    /// for the state machine.</summary>
    public TenantExportStatus Status { get; set; } = TenantExportStatus.Pending;

    /// <summary>Filesystem path to the bundle once the export is
    /// <see cref="TenantExportStatus.Completed"/>. Configurable via
    /// <c>Tenancy:Export:OutputPath</c>; default
    /// <c>var/tenant-exports/{tenantId}/{exportId}.zip</c>.</summary>
    public string? ArtifactPath { get; set; }

    /// <summary>Bundle size in bytes once written. Surfaced in the admin
    /// dashboard so operators don't initiate multi-GB downloads blind.</summary>
    public long? ArtifactSizeBytes { get; set; }

    /// <summary>SHA-256 of the bundle bytes. Stored as raw 32 bytes;
    /// downloads can re-verify on the way out to detect tampering.</summary>
    public byte[]? ArtifactSha256 { get; set; }

    /// <summary>Wallclock at which the artifact expires and the sweeper
    /// will delete it. Default is <see cref="RequestedAt"/> +
    /// <c>Tenancy:Export:RetentionDays</c> (default 7).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Wallclock at which the runner finished writing the
    /// bundle. Null while Pending / Running; set on the transition to
    /// Completed or Failed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Concise machine-friendly reason when
    /// <see cref="Status"/> is <see cref="TenantExportStatus.Failed"/>.
    /// Bounded to 1000 chars; long stack traces stay in structured logs.</summary>
    public string? FailureReason { get; set; }

    /// <summary>How many times the artifact has been downloaded. Surfaced
    /// in the admin dashboard so unusual access patterns are visible.</summary>
    public int DownloadCount { get; set; }

    /// <summary>Wallclock of the last successful download. Null until the
    /// first download.</summary>
    public DateTimeOffset? LastDownloadedAt { get; set; }

    /// <summary>Wallclock the export was revoked, if any. Null otherwise.
    /// Set by <c>ITenantExportService.RevokeExportAsync</c>.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Identity user id of the operator who revoked the export,
    /// if any.</summary>
    public Guid? RevokedByUserId { get; set; }
}

/// <summary>
/// Sprint 25 — bundle format. Picked at request time and immutable for
/// the life of the row.
/// </summary>
public enum TenantExportFormat
{
    /// <summary>Default. Zip archive containing per-table JSON files plus
    /// a top-level <c>manifest.json</c> describing tables, scope, and
    /// versions.</summary>
    JsonBundle = 0,

    /// <summary>Zip archive containing per-table CSV files. Operator-
    /// friendlier for spreadsheet import; loses nested structure (JSON
    /// columns serialised as strings).</summary>
    CsvFlat = 10,

    /// <summary>Zip archive containing one <c>.sql</c> file per database
    /// with INSERT statements (no DROPs). Re-importable to a fresh empty
    /// tenant; useful for migration / test seeding.</summary>
    Sql = 20
}

/// <summary>
/// Sprint 25 — what slice of the tenant's data the bundle should include.
/// Picked at request time and immutable for the life of the row.
/// </summary>
public enum TenantExportScope
{
    /// <summary>Default. Everything the tenant owns across all platform DBs.</summary>
    All = 0,

    /// <summary>Just inspection-module data. Skips finance + identity +
    /// audit. Useful when handing inspection data to a regulator.</summary>
    InspectionOnly = 10,

    /// <summary>Just NickFinance data. Skips inspection + identity +
    /// audit. Useful for finance-team reconciliation exports.</summary>
    FinanceOnly = 20,

    /// <summary>Just platform-side data — identity + audit + tenancy
    /// (no module data). Useful when investigating a security incident
    /// without leaking business records.</summary>
    IdentityAndAudit = 30
}

/// <summary>
/// Sprint 25 — the lifecycle of a <see cref="TenantExportRequest"/>. Gaps
/// in the integer values are intentional so future intermediate states
/// (e.g. Validating, Quarantined) can be inserted without renumbering.
/// </summary>
public enum TenantExportStatus
{
    /// <summary>Default. The runner has not yet picked the row up.</summary>
    Pending = 0,

    /// <summary>The runner is actively building the bundle. Held for the
    /// duration of the export — typically seconds for small tenants,
    /// minutes for large.</summary>
    Running = 10,

    /// <summary>The bundle is on disk, ready to download until
    /// <see cref="TenantExportRequest.ExpiresAt"/>.</summary>
    Completed = 20,

    /// <summary>The runner hit an unrecoverable error.
    /// <see cref="TenantExportRequest.FailureReason"/> carries the
    /// concise reason; full stack lives in structured logs.</summary>
    Failed = 30,

    /// <summary>The retention window passed and the sweeper deleted the
    /// artifact. Row stays in the table for the audit trail.</summary>
    Expired = 40,

    /// <summary>An operator manually revoked the export before its
    /// natural expiry.</summary>
    Revoked = 50
}
