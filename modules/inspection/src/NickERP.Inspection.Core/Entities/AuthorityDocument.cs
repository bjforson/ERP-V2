using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Evidence pulled from an <see cref="ExternalSystemInstance"/> attached
/// to an <see cref="InspectionCase"/>. The vendor-neutral shape — concrete
/// authority types (BOE, CMR, IM in CustomsGh; future GRA / GCNet
/// document types) live in <see cref="DocumentType"/> as a free-form
/// string and the adapter-shaped payload sits in <see cref="PayloadJson"/>.
/// </summary>
public sealed class AuthorityDocument : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public InspectionCase? Case { get; set; }

    public Guid ExternalSystemInstanceId { get; set; }
    public ExternalSystemInstance? ExternalSystemInstance { get; set; }

    /// <summary>Vendor / authority document type — "BOE", "CMR", "IM", "Manifest", "WaybillDeclaration", etc.</summary>
    public string DocumentType { get; set; } = string.Empty;

    /// <summary>Authority-side reference number (BOE number, CMR number, etc.).</summary>
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>Adapter-shaped payload as JSON (jsonb in Postgres).</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>When the adapter received the document from the external system.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    public long TenantId { get; set; }
}
