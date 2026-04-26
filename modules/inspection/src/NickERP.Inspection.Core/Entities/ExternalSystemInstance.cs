using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// A configured external authority system — e.g. a specific ICUMS endpoint,
/// a GRA e-VAT integration, etc. Bound to one or more <see cref="Location"/>s
/// via <see cref="ExternalSystemBinding"/>; per-location bindings cover
/// the case where each location has its own authority endpoint, shared
/// bindings cover national systems.
/// </summary>
public sealed class ExternalSystemInstance : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Plugin type code, matches an <c>IExternalSystemAdapter</c>'s <c>[Plugin]</c>. E.g. "icums-gh", "mock".</summary>
    public string TypeCode { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>How the instance binds to locations. Chosen at onboarding.</summary>
    public ExternalSystemBindingScope Scope { get; set; } = ExternalSystemBindingScope.PerLocation;

    /// <summary>Instance config (JSON). Schema = plugin's <c>configSchema</c>.</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }

    public List<ExternalSystemBinding> Bindings { get; set; } = new();
}

/// <summary>
/// Many-to-many join between <see cref="ExternalSystemInstance"/> and
/// <see cref="Location"/>. Lets one shared instance serve multiple
/// locations (national systems) or pin an instance to a single location
/// (per-port deployments).
/// </summary>
public sealed class ExternalSystemBinding : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExternalSystemInstanceId { get; set; }
    public ExternalSystemInstance? Instance { get; set; }

    public Guid LocationId { get; set; }
    public Location? Location { get; set; }

    /// <summary>"primary" or "secondary" — informational; module logic decides what to do with multiples.</summary>
    public string Role { get; set; } = "primary";

    public DateTimeOffset CreatedAt { get; set; }
    public long TenantId { get; set; }
}

/// <summary>How an <see cref="ExternalSystemInstance"/> binds to locations.</summary>
public enum ExternalSystemBindingScope
{
    /// <summary>Bound to exactly one location. Most common.</summary>
    PerLocation = 0,

    /// <summary>Shared across many locations via the binding join table. Used for national/centralised authority systems.</summary>
    Shared = 1
}
