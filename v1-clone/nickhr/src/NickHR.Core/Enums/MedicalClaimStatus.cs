namespace NickHR.Core.Enums;

public enum MedicalClaimStatus
{
    Submitted,
    UnderReview,
    Approved,
    PartiallyApproved,
    Rejected,
    Paid
}

public enum MedicalClaimCategory
{
    Consultation,
    Medication,
    Surgery,
    Dental,
    Optical,
    Lab,
    Hospitalization,
    Other
}

public enum MedicalPaymentMethod
{
    Payroll,
    BankTransfer,
    Cash
}
