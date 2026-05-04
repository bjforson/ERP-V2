using NickHR.Core.Enums;

namespace NickHR.Core.DTOs.Employee;

public class EmployeeDetailDto
{
    // Identity
    public int Id { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;

    // Personal Information
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {MiddleName} {LastName}".Replace("  ", " ").Trim();
    public DateTime? DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    public MaritalStatus MaritalStatus { get; set; }
    public string? Nationality { get; set; }
    public string? GhanaCardNumber { get; set; }
    public string? TIN { get; set; }
    public string? SSNITNumber { get; set; }
    public string? PhotoUrl { get; set; }
    public string? DigitalSignatureUrl { get; set; }

    // Contact Information
    public string? PrimaryPhone { get; set; }
    public string? SecondaryPhone { get; set; }
    public string? PersonalEmail { get; set; }
    public string? WorkEmail { get; set; }

    // Address Information
    public string? ResidentialAddress { get; set; }
    public string? PostalAddress { get; set; }
    public string? Hometown { get; set; }
    public string? Region { get; set; }

    // Employment Information
    public DateTime? HireDate { get; set; }
    public DateTime? ConfirmationDate { get; set; }
    public EmploymentType EmploymentType { get; set; }
    public EmploymentStatus EmploymentStatus { get; set; }
    public DateTime? ProbationEndDate { get; set; }

    // Department / Role
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public int? DesignationId { get; set; }
    public string? Designation { get; set; }
    public int? GradeId { get; set; }
    public string? Grade { get; set; }
    public int? LocationId { get; set; }
    public string? Location { get; set; }
    public int? ReportingManagerId { get; set; }
    public string? ReportingManagerName { get; set; }

    // Compensation (bank details only visible to admin roles - filter at service layer)
    public decimal BasicSalary { get; set; }
    public string? BankName { get; set; }
    public string? BankBranch { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? MobileMoneyNumber { get; set; }

    // Nested Collections
    public List<EmergencyContactDto> EmergencyContacts { get; set; } = new();
    public List<DependentDto> Dependents { get; set; } = new();
    public List<EmployeeQualificationDto> Qualifications { get; set; } = new();
    public List<EmployeeDocumentDto> Documents { get; set; } = new();
}

public class EmergencyContactDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Relationship { get; set; }
    public string PrimaryPhone { get; set; } = string.Empty;
    public string? SecondaryPhone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
}

public class DependentDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? Relationship { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public Gender Gender { get; set; }
    public bool IsActive { get; set; }
}

public class EmployeeQualificationDto
{
    public int Id { get; set; }
    public string QualificationType { get; set; } = string.Empty;
    public string? Institution { get; set; }
    public string Qualification { get; set; } = string.Empty;
    public string? FieldOfStudy { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Grade { get; set; }
    public bool IsHighest { get; set; }
}

public class EmployeeDocumentDto
{
    public int Id { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? ContentType { get; set; }
    public string? Description { get; set; }
    public DateTime UploadedAt { get; set; }
    public int Version { get; set; }
}
