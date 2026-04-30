using Microsoft.EntityFrameworkCore;

namespace NickERP.Platform.Identity;

/// <summary>
/// EF Core context for the <c>identity</c> schema. Owns users, phones,
/// roles, role grants, and the security audit log. The audit log table
/// is append-only and the bootstrap CLI re-applies a Postgres-level
/// trigger to enforce that — see
/// <c>NickFinance.Database.Bootstrap.Program.ApplySchemaTriggersAsync</c>
/// (the trigger lives in <c>SchemaBootstrap</c> alongside the
/// ledger constraints once implemented).
/// </summary>
public sealed class IdentityDbContext : DbContext
{
    public const string SchemaName = "identity";

    private readonly ITenantAccessor? _tenantAccessor;

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options)
    {
    }

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantAccessor? tenantAccessor)
        : base(options)
    {
        _tenantAccessor = tenantAccessor;
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserPhone> UserPhones => Set<UserPhone>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        b.HasDefaultSchema(SchemaName);

        // Tenant scoping is per-context. The ITenantAccessor returns null
        // for the bootstrap CLI / smoke runner so they see every row;
        // returns a real long for the live WebApp so queries are scoped
        // to whoever owns the current circuit.
        var filterEnabled = _tenantAccessor is not null;

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.InternalUserId);
            e.Property(x => x.InternalUserId).HasColumnName("internal_user_id");
            e.Property(x => x.CfAccessSub).HasColumnName("cf_access_sub").HasMaxLength(128);
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(254).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasConversion<short>().IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            // CF Access sub is unique when populated; nullable for system rows.
            e.HasIndex(x => x.CfAccessSub).IsUnique().HasDatabaseName("ux_users_cf_access_sub");
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique().HasDatabaseName("ux_users_tenant_email");

            if (filterEnabled)
            {
                e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
            }
        });

        b.Entity<UserPhone>(e =>
        {
            e.ToTable("user_phones");
            e.HasKey(x => x.UserPhoneId);
            e.Property(x => x.UserPhoneId).HasColumnName("user_phone_id");
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.PhoneE164).HasColumnName("phone_e164").HasMaxLength(32).IsRequired();
            e.Property(x => x.Verified).HasColumnName("verified").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.HasIndex(x => x.PhoneE164).IsUnique().HasDatabaseName("ux_user_phones_e164");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.RoleId);
            // Seeded with deterministic ids 1..6 by the initial migration; never auto-generated.
            e.Property(x => x.RoleId).HasColumnName("role_id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_roles_name");
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => x.UserRoleId);
            e.Property(x => x.UserRoleId).HasColumnName("user_role_id");
            e.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
            e.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
            e.Property(x => x.SiteId).HasColumnName("site_id");
            e.Property(x => x.GrantedByUserId).HasColumnName("granted_by_user_id").IsRequired();
            e.Property(x => x.GrantedAt).HasColumnName("granted_at").IsRequired();
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            // ExternalAuditor grant audit-firm field. Nullable; only set
            // when the role is ExternalAuditor (validated upstream by the
            // SodService and the HR provisioning service).
            e.Property(x => x.AuditFirm).HasColumnName("audit_firm").HasMaxLength(200);
            e.HasIndex(x => new { x.UserId, x.RoleId, x.SiteId }).HasDatabaseName("ix_user_roles_user_role_site");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Permission>(e =>
        {
            e.ToTable("permissions");
            e.HasKey(x => x.PermissionId);
            // Seeded with deterministic ids 1..52 by the
            // Add_Permissions_RolePermissions migration; never auto-generated.
            e.Property(x => x.PermissionId).HasColumnName("permission_id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.Category).HasColumnName("category").HasMaxLength(64);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_permissions_name");
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            // Composite PK — one row per (role, permission). No surrogate id.
            e.HasKey(x => new { x.RoleId, x.PermissionId });
            e.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
            e.Property(x => x.PermissionId).HasColumnName("permission_id").IsRequired();
            e.Property(x => x.GrantedAt).HasColumnName("granted_at").IsRequired();

            e.HasIndex(x => x.PermissionId).HasDatabaseName("ix_role_permissions_permission");

            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SecurityAuditEvent>(e =>
        {
            e.ToTable("security_audit_log");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Action).HasColumnName("action").HasConversion<short>().IsRequired();
            e.Property(x => x.TargetType).HasColumnName("target_type").HasMaxLength(64);
            e.Property(x => x.TargetId).HasColumnName("target_id").HasMaxLength(128);
            e.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
            e.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(500);
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").IsRequired();
            e.Property(x => x.DetailsJson).HasColumnName("details").HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnName("result").HasConversion<short>().IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();

            e.HasIndex(x => new { x.TenantId, x.OccurredAt }).HasDatabaseName("ix_security_audit_tenant_when");
            e.HasIndex(x => new { x.TenantId, x.UserId, x.OccurredAt }).HasDatabaseName("ix_security_audit_tenant_user_when");
            e.HasIndex(x => new { x.TenantId, x.Action }).HasDatabaseName("ix_security_audit_tenant_action");

            // No FK on UserId — audit rows must survive even if a User row
            // is later soft-deleted. The optional join happens at query time.

            if (filterEnabled)
            {
                e.HasQueryFilter(x => x.TenantId == _tenantAccessor!.Current);
            }
        });
    }
}
