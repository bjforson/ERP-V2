namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// What kind of thing is being inspected. Polymorphic on
/// <see cref="InspectionCase.SubjectType"/>; type-specific fields ride
/// in <see cref="InspectionCase.SubjectPayloadJson"/> as a jsonb blob.
/// </summary>
public enum CaseSubjectType
{
    /// <summary>Shipping container (the v1 case — TEU/FEU, container number).</summary>
    Container = 0,
    /// <summary>Truck or trailer; subject identifier is a plate or VIN.</summary>
    Truck = 1,
    /// <summary>Hand-carried or courier parcel.</summary>
    Parcel = 2,
    /// <summary>Loose bag or suitcase.</summary>
    Bag = 3,
    /// <summary>Anything not in the other buckets — payload describes shape.</summary>
    Other = 99
}
