using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// A configured external authority system — e.g. a specific ICUMS endpoint,
/// a GRA e-VAT integration, etc. Bound to one or more <see cref="Location"/>s
/// via <see cref="ExternalSystemBinding"/>.
///
/// <para>
/// Three binding scopes are supported (Sprint 16 / LA1 extension,
/// locked 2026-05-02):
/// <list type="bullet">
///   <item><description><see cref="ExternalSystemBindingScope.PerLocation"/> — exactly one binding row.</description></item>
///   <item><description><see cref="ExternalSystemBindingScope.SubsetOfLocations"/> — two or more binding rows, fewer than the tenant's full location count.</description></item>
///   <item><description><see cref="ExternalSystemBindingScope.Shared"/> — zero binding rows; the instance covers every location in the tenant implicitly. Used for national/centralised authority systems.</description></item>
/// </list>
/// Validation lives in <see cref="ExternalSystemBindingScopeValidation"/>.
/// </para>
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
/// <remarks>
/// Sprint 16 / LA1 extension (locked 2026-05-02): the original binary
/// PerLocation/Shared distinction was extended with a third value,
/// <see cref="SubsetOfLocations"/>. Storage is unchanged — bindings
/// already lived in the <c>ExternalSystemBinding</c> junction table; only
/// the discriminator + per-scope validation rules grew. Existing rows
/// keep their integer values (PerLocation = 0, Shared = 1) so no data
/// migration is required.
/// </remarks>
public enum ExternalSystemBindingScope
{
    /// <summary>Bound to exactly one location. Requires exactly one binding row.</summary>
    PerLocation = 0,

    /// <summary>Shared across every location in the tenant (covers all implicitly). Requires zero binding rows.</summary>
    Shared = 1,

    /// <summary>
    /// Bound to a chosen subset of locations within the tenant. Requires
    /// at least two binding rows — fewer collapses to <see cref="PerLocation"/>
    /// (1 row) or <see cref="Shared"/> (0 rows) and is rejected by
    /// <see cref="ExternalSystemBindingScopeValidation"/>. Sprint 16 extension.
    /// </summary>
    SubsetOfLocations = 2
}

/// <summary>
/// Sprint 16 — pure-function validation of the binding-row count against
/// the declared <see cref="ExternalSystemBindingScope"/>. Centralised so
/// every write path (admin UI, programmatic seed, future API) applies the
/// same rule and renders the same error.
/// </summary>
public static class ExternalSystemBindingScopeValidation
{
    /// <summary>
    /// Validate that <paramref name="bindingCount"/> matches what
    /// <paramref name="scope"/> requires. Returns null on success or an
    /// English error message on failure.
    /// </summary>
    /// <param name="scope">Declared scope of the instance.</param>
    /// <param name="bindingCount">Number of <c>ExternalSystemBinding</c> rows attached to the instance.</param>
    public static string? Validate(ExternalSystemBindingScope scope, int bindingCount)
    {
        if (bindingCount < 0)
        {
            return "Binding count cannot be negative.";
        }

        return scope switch
        {
            ExternalSystemBindingScope.PerLocation when bindingCount != 1 =>
                $"PerLocation scope requires exactly 1 binding (got {bindingCount}). Pick one location.",
            ExternalSystemBindingScope.SubsetOfLocations when bindingCount < 2 =>
                $"SubsetOfLocations scope requires at least 2 bindings (got {bindingCount}). " +
                "If only 1 location applies, use PerLocation; if every location applies, use Shared.",
            ExternalSystemBindingScope.Shared when bindingCount != 0 =>
                $"Shared scope requires 0 bindings (got {bindingCount}). " +
                "Shared instances cover every location implicitly; remove the per-location bindings.",
            _ => null
        };
    }
}
