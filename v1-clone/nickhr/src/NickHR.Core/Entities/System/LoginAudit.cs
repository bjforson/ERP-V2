using System.ComponentModel.DataAnnotations;

namespace NickHR.Core.Entities.System;

/// <summary>
/// Logs every login attempt with GPS location data.
/// </summary>
public class LoginAudit
{
    public long Id { get; set; }

    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    public int? EmployeeId { get; set; }

    [MaxLength(200)]
    public string? EmployeeName { get; set; }

    public bool Success { get; set; }

    public DateTime LoginTime { get; set; } = DateTime.UtcNow;

    // GPS Data
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Accuracy { get; set; } // meters

    // Resolved location info
    [MaxLength(200)]
    public string? NearestLocationName { get; set; }
    public double? DistanceFromNearestLocation { get; set; } // meters
    public bool? WithinGeoFence { get; set; }

    // Device info
    [MaxLength(50)]
    public string? IPAddress { get; set; }

    [MaxLength(500)]
    public string? UserAgent { get; set; }
}
