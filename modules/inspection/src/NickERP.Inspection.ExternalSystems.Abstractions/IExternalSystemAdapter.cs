namespace NickERP.Inspection.ExternalSystems.Abstractions;

/// <summary>
/// Plugin contract every external-authority-system adapter implements.
/// Pulls authority documents (BOEs, declarations, manifests) for a case
/// and pushes verdicts back. Concrete classes carry
/// <c>[NickERP.Platform.Plugins.Plugin("type-code")]</c>.
/// </summary>
public interface IExternalSystemAdapter
{
    /// <summary>Stable code matching the <c>[Plugin]</c> attribute on the concrete class.</summary>
    string TypeCode { get; }

    /// <summary>What the adapter supports.</summary>
    ExternalSystemCapabilities Capabilities { get; }

    /// <summary>Test connectivity. Cheap; admin UI calls it.</summary>
    Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default);

    /// <summary>Pull authority documents matching the lookup criteria.</summary>
    Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(
        ExternalSystemConfig config,
        CaseLookupCriteria lookup,
        CancellationToken ct = default);

    /// <summary>
    /// Submit a verdict back to the external system. MUST be idempotent —
    /// the host carries an idempotency key per submission, and the adapter
    /// must guarantee at-most-once semantics per key.
    /// </summary>
    Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig config,
        OutboundSubmissionRequest request,
        CancellationToken ct = default);
}

/// <summary>External-system capabilities surfaced to the admin UI.</summary>
public sealed record ExternalSystemCapabilities(
    IReadOnlyList<string> SupportedDocumentTypes,
    bool SupportsPushNotifications,
    bool SupportsBulkFetch);

/// <summary>
/// Per-instance config the host passes on every call.
/// <para>
/// <c>TenantId</c> is the resolved tenant for this invocation; adapters that
/// keep static / process-wide caches MUST partition them by <c>TenantId</c>
/// so two tenants pointing at the same physical resource don't share state.
/// </para>
/// </summary>
public sealed record ExternalSystemConfig(
    Guid InstanceId,
    long TenantId,
    string ConfigJson);

/// <summary>Lookup criteria for fetching authority documents.</summary>
public sealed record CaseLookupCriteria(
    string? ContainerNumber,
    string? VehicleVin,
    string? AuthorityReferenceNumber);

/// <summary>DTO returned by IExternalSystemAdapter.FetchDocumentsAsync. Renamed from AuthorityDocument in FU-7 to disambiguate from the persistence entity at NickERP.Inspection.Core.Entities.AuthorityDocument.</summary>
public sealed record AuthorityDocumentDto(
    Guid InstanceId,
    string DocumentType,
    string ReferenceNumber,
    DateTimeOffset ReceivedAt,
    string PayloadJson);

/// <summary>Verdict submission payload.</summary>
public sealed record OutboundSubmissionRequest(
    string IdempotencyKey,
    string AuthorityReferenceNumber,
    string PayloadJson);

/// <summary>Submission outcome.</summary>
public sealed record SubmissionResult(
    bool Accepted,
    string? AuthorityResponseJson,
    string? Error);

/// <summary>Outcome of a connectivity test.</summary>
public sealed record ConnectionTestResult(bool Success, string Message, TimeSpan? Latency = null);
