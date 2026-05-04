using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

public class PolicyDocument : BaseEntity
{
    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Category { get; set; } = "Company Policy";

    public string? FilePath { get; set; }

    [MaxLength(50)]
    public string Version { get; set; } = "1.0";

    public DateTime EffectiveDate { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public bool RequiresAcknowledgement { get; set; }

    public List<PolicyAcknowledgement> Acknowledgements { get; set; } = new();
}
