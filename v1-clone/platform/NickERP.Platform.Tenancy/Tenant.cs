using System.ComponentModel.DataAnnotations;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// A tenant in the NICKSCAN ERP platform. The default tenant (id=1) is
/// "Nick TC-Scan Operations". Additional tenants are created via the
/// tenant management UI in Phase 10.
///
/// This entity lives in the canonical <c>nick_platform</c> database.
/// Modules NEVER store tenants in their own database — they reference
/// <see cref="ITenantOwned.TenantId"/> on each row instead.
/// </summary>
public sealed class Tenant
{
    public long Id { get; set; }

    /// <summary>Stable kebab-case identifier (e.g. "nicktcscan", "ghana-customs", "demo-tenant").</summary>
    [Required, MaxLength(50)]
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name shown in admin UI.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>Billing plan code (e.g. "internal", "starter", "growth", "enterprise").</summary>
    [MaxLength(50)]
    public string BillingPlan { get; set; } = "internal";

    /// <summary>IANA timezone identifier (e.g. "Africa/Accra").</summary>
    [MaxLength(50)]
    public string TimeZone { get; set; } = "Africa/Accra";

    /// <summary>BCP-47 locale (e.g. "en-GH", "en-US", "fr-FR").</summary>
    [MaxLength(20)]
    public string Locale { get; set; } = "en-GH";

    /// <summary>ISO 4217 currency code (e.g. "GHS", "USD", "EUR").</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = "GHS";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    /// <summary>The default tenant id used in single-tenant deployments.</summary>
    public const long DefaultTenantId = 1;

    /// <summary>The well-known code for the default Nick TC-Scan tenant.</summary>
    public const string DefaultTenantCode = "nicktcscan";
}

/// <summary>
/// Maps a platform user to one or more tenants. A user belongs to exactly one
/// "primary" tenant (the one whose data they see by default), but platform-level
/// support staff can have access to multiple tenants for impersonation.
/// </summary>
public sealed class TenantUser
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>The user's id in the central identity provider.</summary>
    [Required, MaxLength(100)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Username at the time of join, cached for display.</summary>
    [MaxLength(100)]
    public string? Username { get; set; }

    /// <summary>True if this is the user's primary/home tenant.</summary>
    public bool IsPrimary { get; set; } = true;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Tracks which platform modules a tenant is subscribed to. Drives the
/// App Launcher tile grid and gates feature access.
/// </summary>
public sealed class TenantModuleSubscription
{
    public long Id { get; set; }
    public long TenantId { get; set; }

    /// <summary>Module name (matches <see cref="Core.Contracts.IPlatformModule.Name"/>).</summary>
    [Required, MaxLength(50)]
    public string ModuleName { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Optional expiry date for trial/lapsed subscriptions.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
