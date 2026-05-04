using NickHR.Core.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NickHR.Core.Entities.Core;

public class Employee : BaseEntity
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

    [MaxLength(500)]
    public string? PhotoUrl { get; set; }

    /// <summary>Portrait photo stored in database as byte array.</summary>
    public byte[]? PhotoData { get; set; }

    [MaxLength(100)]
    public string? PhotoContentType { get; set; }

    [MaxLength(500)]
    public string? DigitalSignatureUrl { get; set; }

    // Ghana-specific fields
    [MaxLength(50)]
    public string? GhanaPostGPS { get; set; } // Digital address e.g. GA-123-4567

    [MaxLength(50)]
    public string? Tier2PensionNumber { get; set; }

    [MaxLength(200)]
    public string? Tier2Provider { get; set; } // e.g. Enterprise Trustees, GLICO Pensions

    [MaxLength(50)]
    public string? Tier3PensionNumber { get; set; }

    [MaxLength(200)]
    public string? Tier3Provider { get; set; }

    [MaxLength(20)]
    public string? BloodGroup { get; set; } // A+, A-, B+, B-, AB+, AB-, O+, O-

    [MaxLength(50)]
    public string? Religion { get; set; }

    [MaxLength(100)]
    public string? EthnicGroup { get; set; }

    [MaxLength(200)]
    public string? PlaceOfBirth { get; set; }

    [MaxLength(100)]
    public string? MothersMaidenName { get; set; }

    [MaxLength(100)]
    public string? SpouseName { get; set; }

    [MaxLength(20)]
    public string? SpousePhone { get; set; }

    [MaxLength(200)]
    public string? SpouseEmployer { get; set; }

    public int NumberOfChildren { get; set; }

    [MaxLength(200)]
    public string? MedicalConditions { get; set; } // Known allergies/conditions for emergencies

    [MaxLength(200)]
    public string? DriversLicenseNumber { get; set; }

    public DateTime? DriversLicenseExpiry { get; set; }

    [MaxLength(200)]
    public string? PassportNumber { get; set; }

    public DateTime? PassportExpiry { get; set; }

    // Contact Information
    [MaxLength(20)]
    public string? PrimaryPhone { get; set; }

    [MaxLength(20)]
    public string? SecondaryPhone { get; set; }

    [MaxLength(200)]
    public string? PersonalEmail { get; set; }

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
    [Required]
    [MaxLength(50)]
    public string EmployeeCode { get; set; } = string.Empty;

    public DateTime? HireDate { get; set; }

    public DateTime? ConfirmationDate { get; set; }

    public EmploymentType EmploymentType { get; set; }

    public EmploymentStatus EmploymentStatus { get; set; }

    public DateTime? ProbationEndDate { get; set; }

    // Foreign Keys
    public int? DepartmentId { get; set; }

    public int? DesignationId { get; set; }

    public int? GradeId { get; set; }

    public int? LocationId { get; set; }

    public int? ReportingManagerId { get; set; }

    // Compensation & Banking
    [Column(TypeName = "decimal(18,2)")]
    public decimal BasicSalary { get; set; }

    [MaxLength(100)]
    public string? BankName { get; set; }

    [MaxLength(100)]
    public string? BankBranch { get; set; }

    [MaxLength(50)]
    public string? BankAccountNumber { get; set; }

    [MaxLength(20)]
    public string? MobileMoneyNumber { get; set; }

    // Navigation Properties
    public Department? Department { get; set; }

    public Designation? Designation { get; set; }

    public Grade? Grade { get; set; }

    public Location? Location { get; set; }

    public Employee? ReportingManager { get; set; }

    public ICollection<Employee> Subordinates { get; set; } = new List<Employee>();

    public ICollection<EmergencyContact> EmergencyContacts { get; set; } = new List<EmergencyContact>();

    public ICollection<Dependent> Dependents { get; set; } = new List<Dependent>();

    public ICollection<EmployeeDocument> EmployeeDocuments { get; set; } = new List<EmployeeDocument>();

    public ICollection<EmployeeQualification> EmployeeQualifications { get; set; } = new List<EmployeeQualification>();

    public ICollection<EmploymentHistoryRecord> EmploymentHistory { get; set; } = new List<EmploymentHistoryRecord>();

    public ICollection<Leave.LeaveBalance> LeaveBalances { get; set; } = new List<Leave.LeaveBalance>();

    public ICollection<Beneficiary> Beneficiaries { get; set; } = new List<Beneficiary>();
}
