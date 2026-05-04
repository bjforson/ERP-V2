namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 14 / VP6 Phase A.5 — host-side flag indicating whether the
/// Inspection module's DbContext + <c>IAnalysisServiceBootstrap</c> are
/// registered for this deployment. Mirrors the NickFinance pattern:
/// <c>ConnectionStrings:Inspection</c> being unset means the portal is
/// running in a tenancy-only mode with no inspection DB to write to, and
/// <c>Tenants.razor</c> skips the bootstrap call accordingly.
/// </summary>
public sealed record InspectionFeatureFlag(bool Enabled);
