using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.DTOs.Employee;

public class CreateEmployeeDto
{
    // Personal Information
    [Required]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? MiddleName { get; set; }

    [Required]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    public DateTime? DateOfBirth { get; set; }

    public Gender Gender { get; set; }

    public MaritalStatus MaritalStatus { get; set; }

    [MaxLength(100)]
    public string? Nationality { get; set; }

    [MaxLength(50)]
    public string? GhanaCardNumber { get; set; }

    [MaxLength(50)]
    public string? TIN { get; set; }

    [MaxLength(50)]
    public string? SSNITNumber { get; set; }

    // Contact Information
    [Required]
    [Phone]
    [MaxLength(20)]
    public string PrimaryPhone { get; set; } = string.Empty;

    [Phone]
    [MaxLength(20)]
    public string? SecondaryPhone { get; set; }

    [EmailAddress]
    [MaxLength(200)]
    public string? PersonalEmail { get; set; }

    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

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
    [Required]
    public int DepartmentId { get; set; }

    [Required]
    public int DesignationId { get; set; }

    [Required]
    public int GradeId { get; set; }

    [Required]
    public int LocationId { get; set; }

    public int? ReportingManagerId { get; set; }

    public EmploymentType EmploymentType { get; set; }

    [Required]
    public DateTime HireDate { get; set; }

    public DateTime? ProbationEndDate { get; set; }

    // Compensation
    [Required]
    [Range(0, double.MaxValue, ErrorMessage = "Basic salary must be a positive value.")]
    public decimal BasicSalary { get; set; }

    [MaxLength(100)]
    public string? BankName { get; set; }

    [MaxLength(100)]
    public string? BankBranch { get; set; }

    [MaxLength(50)]
    public string? BankAccountNumber { get; set; }

    [MaxLength(20)]
    public string? MobileMoneyNumber { get; set; }
}
