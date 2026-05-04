using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.DTOs.Employee;

/// <summary>
/// All fields are optional to support partial updates (PATCH semantics).
/// Only non-null fields will be applied by the service layer.
/// </summary>
public class UpdateEmployeeDto
{
    // Personal Information
    [MaxLength(100)]
    public string? FirstName { get; set; }

    [MaxLength(100)]
    public string? MiddleName { get; set; }

    [MaxLength(100)]
    public string? LastName { get; set; }

    public DateTime? DateOfBirth { get; set; }

    public Gender? Gender { get; set; }

    public MaritalStatus? MaritalStatus { get; set; }

    [MaxLength(100)]
    public string? Nationality { get; set; }

    [MaxLength(50)]
    public string? GhanaCardNumber { get; set; }

    [MaxLength(50)]
    public string? TIN { get; set; }

    [MaxLength(50)]
    public string? SSNITNumber { get; set; }

    [MaxLength(500)]
    public string? PhotoUrl { get; set; }

    // Contact Information
    [Phone]
    [MaxLength(20)]
    public string? PrimaryPhone { get; set; }

    [Phone]
    [MaxLength(20)]
    public string? SecondaryPhone { get; set; }

    [EmailAddress]
    [MaxLength(200)]
    public string? PersonalEmail { get; set; }

    [EmailAddress]
    [MaxLength(200)]
    public string? WorkEmail { get; set; }

    // Address Information
    [MaxLength(500)]
    public string? ResidentialAddress { get; set; }

    [MaxLength(500)]
    public string? PostalAddress { get; set; }

    [MaxLength(200)]
    public string? Hometown { get; set; }

    [MaxLength(100)]
    public string? Region { get; set; }

    // Employment Information
    public int? DepartmentId { get; set; }
    public int? DesignationId { get; set; }
    public int? GradeId { get; set; }
    public int? LocationId { get; set; }
    public int? ReportingManagerId { get; set; }
    public EmploymentType? EmploymentType { get; set; }
    public EmploymentStatus? EmploymentStatus { get; set; }
    public DateTime? HireDate { get; set; }
    public DateTime? ConfirmationDate { get; set; }
    public DateTime? ProbationEndDate { get; set; }

    // Compensation
    [Range(0, double.MaxValue, ErrorMessage = "Basic salary must be a positive value.")]
    public decimal? BasicSalary { get; set; }

    [MaxLength(100)]
    public string? BankName { get; set; }

    [MaxLength(100)]
    public string? BankBranch { get; set; }

    [MaxLength(50)]
    public string? BankAccountNumber { get; set; }

    [MaxLength(20)]
    public string? MobileMoneyNumber { get; set; }
}
