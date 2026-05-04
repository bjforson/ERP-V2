namespace NickHR.Core.Interfaces;

public record PolicyDocumentDto(
    int Id,
    string Title,
    string Category,
    string? FilePath,
    string Version,
    DateTime EffectiveDate,
    string? Description,
    bool IsActive,
    bool RequiresAcknowledgement,
    int TotalEmployees,
    int AcknowledgedCount,
    DateTime CreatedAt
);

public record PolicyAcknowledgementDto(
    int Id,
    int PolicyDocumentId,
    string PolicyTitle,
    int EmployeeId,
    string EmployeeName,
    DateTime AcknowledgedAt
);

public interface IPolicyDocumentService
{
    Task<PolicyDocumentDto> CreateAsync(string title, string category, string version, DateTime effectiveDate, string? description, string? filePath, bool requiresAcknowledgement);
    Task<List<PolicyDocumentDto>> GetAllAsync();
    Task<List<PolicyDocumentDto>> GetActiveAsync();
    Task<PolicyDocumentDto?> GetByIdAsync(int id);
    Task<PolicyDocumentDto> UpdateAsync(int id, string title, string category, string version, DateTime effectiveDate, string? description, string? filePath, bool requiresAcknowledgement, bool isActive);
    Task AcknowledgeAsync(int policyDocumentId, int employeeId);
    Task<List<PolicyAcknowledgementDto>> GetAcknowledgementStatusAsync(int policyDocumentId);
    Task<bool> HasAcknowledgedAsync(int policyDocumentId, int employeeId);
}
