using System.ComponentModel.DataAnnotations;

namespace NickERP.Platform.Identity.Api.Models;

// ---------------------------------------------------------------------------
// Users
// ---------------------------------------------------------------------------

/// <summary>What the API returns when describing a user.</summary>
public sealed record UserDto(
    Guid Id,
    string Email,
    string? DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSeenAt,
    long TenantId,
    IReadOnlyList<UserScopeDto> Scopes);

public sealed record UserScopeDto(
    Guid Id,
    string AppScopeCode,
    DateTimeOffset GrantedAt,
    Guid GrantedByUserId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    Guid? RevokedByUserId,
    string? Notes);

/// <summary>Request body for <c>POST /api/identity/users</c>.</summary>
public sealed class CreateUserRequest
{
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; set; } = string.Empty;

    [StringLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>Tenant id this user belongs to. Defaults to 1.</summary>
    public long TenantId { get; set; } = 1;

    /// <summary>Initial scope codes to grant on creation. Optional.</summary>
    public IReadOnlyList<string>? InitialScopes { get; set; }
}

/// <summary>Request body for <c>PATCH /api/identity/users/{id}/scopes</c>.</summary>
public sealed class UpdateUserScopesRequest
{
    /// <summary>Scope codes to grant.</summary>
    public IReadOnlyList<string> Grant { get; set; } = Array.Empty<string>();

    /// <summary>Scope codes to revoke (case-insensitive match).</summary>
    public IReadOnlyList<string> Revoke { get; set; } = Array.Empty<string>();

    /// <summary>Optional human-readable note recorded on every newly-granted scope row.</summary>
    [StringLength(500)]
    public string? Notes { get; set; }

    /// <summary>Optional future expiry for newly-granted scopes. Null = permanent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

// ---------------------------------------------------------------------------
// App scopes
// ---------------------------------------------------------------------------

public sealed record AppScopeDto(
    Guid Id,
    string Code,
    string AppName,
    string? Description,
    bool IsActive,
    DateTimeOffset CreatedAt,
    long TenantId);

/// <summary>Request body for <c>POST /api/identity/scopes</c>.</summary>
public sealed class CreateAppScopeRequest
{
    // G1 #6 — scope codes must be strictly dot-separated PascalCase
    // segments, each starting with an uppercase letter and using letters
    // only. Underspecified scopes like "admin" or "admin.foo" are
    // rejected at the API boundary; the per-app prefix (Finance / Identity
    // / Inspection) is enforced by convention rather than by code, but
    // the structural rule guarantees a multi-segment, capitalised name.
    [Required, RegularExpression(@"^[A-Z][A-Za-z]+(\.[A-Z][A-Za-z]+)+$",
        ErrorMessage = "Code must be dot-separated PascalCase with at least two segments, each starting with an uppercase letter and containing only letters (e.g. 'Finance.PettyCash.Approver').")]
    [StringLength(128)]
    public string Code { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string AppName { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public long TenantId { get; set; } = 1;
}

// ---------------------------------------------------------------------------
// Service tokens
// ---------------------------------------------------------------------------

public sealed record ServiceTokenDto(
    Guid Id,
    string TokenClientId,
    string DisplayName,
    string? Purpose,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? ExpiresAt,
    long TenantId,
    IReadOnlyList<ServiceTokenScopeDto> Scopes);

public sealed record ServiceTokenScopeDto(
    Guid Id,
    string AppScopeCode,
    DateTimeOffset GrantedAt,
    Guid GrantedByUserId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    Guid? RevokedByUserId);

public sealed class CreateServiceTokenRequest
{
    [Required, StringLength(128)]
    public string TokenClientId { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Purpose { get; set; }

    /// <summary>Optional CF-Access expiry mirror.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public long TenantId { get; set; } = 1;

    public IReadOnlyList<string>? InitialScopes { get; set; }
}

public sealed class UpdateServiceTokenScopesRequest
{
    public IReadOnlyList<string> Grant { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Revoke { get; set; } = Array.Empty<string>();
    public DateTimeOffset? ExpiresAt { get; set; }
}

// ---------------------------------------------------------------------------
// Pagination
// ---------------------------------------------------------------------------

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalItems);
