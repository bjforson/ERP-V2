namespace NickHR.Core.Interfaces;

public record UpcomingBirthdayDto(int EmployeeId, string FullName, DateTime DateOfBirth, int DaysUntil, int Age);
public record UpcomingAnniversaryDto(int EmployeeId, string FullName, DateTime HireDate, int DaysUntil, int Years);
public record ExpiringDocumentDto(int EmployeeId, string FullName, string DocumentType, DateTime ExpiryDate, int DaysUntil);
public record ExpiringContractDto(int EmployeeId, string FullName, DateTime ContractEndDate, int DaysUntil);

public interface IAlertService
{
    Task<List<UpcomingBirthdayDto>> GetUpcomingBirthdaysAsync(int days = 30);
    Task<List<UpcomingAnniversaryDto>> GetUpcomingAnniversariesAsync(int days = 30);
    Task<List<ExpiringDocumentDto>> GetExpiringDocumentsAsync(int days = 60);
    Task<List<ExpiringContractDto>> GetExpiringContractsAsync(int days = 60);
}
