namespace NickERP.Platform.Identity;

/// <summary>
/// Append-only audit log for privileged actions. Called from kernel
/// services (PettyCashService, ArService, LedgerWriter) and the
/// authorization handler when a policy denies a request. The interface
/// lives in the platform Identity project so kernel modules can wire to
/// it without taking a dependency on any specific WebApp host.
/// </summary>
/// <remarks>
/// Implementation registered in the WebApp DI block writes rows to
/// <c>identity.security_audit_log</c>. Tests + the bootstrap CLI register
/// a no-op so they can run without a security context.
/// </remarks>
public interface ISecurityAuditService
{
    Task RecordAsync(
        SecurityAuditAction action,
        string? targetType = null,
        string? targetId = null,
        SecurityAuditResult result = SecurityAuditResult.Allowed,
        object? details = null,
        CancellationToken ct = default);
}

/// <summary>
/// Default no-op for environments without a real audit context (tests,
/// the bootstrap CLI smoke runner, design-time DbContext factories).
/// Keeps every kernel call-site compilable even when the WebApp host
/// isn't in the DI container.
/// </summary>
public sealed class NoopSecurityAuditService : ISecurityAuditService
{
    public Task RecordAsync(
        SecurityAuditAction action,
        string? targetType = null,
        string? targetId = null,
        SecurityAuditResult result = SecurityAuditResult.Allowed,
        object? details = null,
        CancellationToken ct = default)
        => Task.CompletedTask;
}
