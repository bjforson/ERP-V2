using System.Text.Json;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.ExternalSystems.Mock;

/// <summary>
/// Synthetic external-system adapter. Returns one fake authority document
/// per fetch (regardless of lookup criteria) and accepts every verdict
/// submission with a stable response. Used to exercise the case
/// pull/submit lifecycle without a real ICUMS endpoint.
/// </summary>
[Plugin("mock-external", Module = "inspection")]
public sealed class MockExternalSystemAdapter : IExternalSystemAdapter
{
    public string TypeCode => "mock-external";

    public ExternalSystemCapabilities Capabilities { get; } = new(
        SupportedDocumentTypes: new[] { "BOE", "CMR", "Manifest" },
        SupportsPushNotifications: false,
        SupportsBulkFetch: true);

    public Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default)
    {
        return Task.FromResult(new ConnectionTestResult(
            Success: true,
            Message: "Mock external system — always reachable.",
            Latency: TimeSpan.FromMilliseconds(1)));
    }

    public Task<IReadOnlyList<AuthorityDocumentDto>> FetchDocumentsAsync(
        ExternalSystemConfig config,
        CaseLookupCriteria lookup,
        CancellationToken ct = default)
    {
        var doc = new AuthorityDocumentDto(
            InstanceId: config.InstanceId,
            DocumentType: "BOE",
            ReferenceNumber: lookup.AuthorityReferenceNumber ?? lookup.ContainerNumber ?? lookup.VehicleVin ?? Guid.NewGuid().ToString("N")[..12],
            ReceivedAt: DateTimeOffset.UtcNow,
            PayloadJson: JsonSerializer.Serialize(new
            {
                source = "mock-external",
                lookup,
                note = "Synthetic BOE — no upstream system was contacted."
            }));
        return Task.FromResult<IReadOnlyList<AuthorityDocumentDto>>(new[] { doc });
    }

    public Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig config,
        OutboundSubmissionRequest request,
        CancellationToken ct = default)
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            accepted = true,
            idempotencyKey = request.IdempotencyKey,
            authorityReference = request.AuthorityReferenceNumber,
            note = "Mock external system accepted the verdict."
        });
        return Task.FromResult(new SubmissionResult(
            Accepted: true,
            AuthorityResponseJson: responseJson,
            Error: null));
    }
}
