using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.Core;

public class Location : BaseEntity
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }

    [MaxLength(100)]
    public string? Region { get; set; }

    [MaxLength(100)]
    public string Country { get; set; } = "Ghana";

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>Radius in meters for GPS-based attendance check-in.</summary>
    public int GeoFenceRadiusMeters { get; set; } = 200;

    public bool IsHeadOffice { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation Properties
    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}
