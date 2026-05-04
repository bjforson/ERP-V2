using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace NickHR.Services.Attendance;

public class QrAttendanceService
{
    private readonly IConfiguration _configuration;

    public QrAttendanceService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>Generate a QR token containing location + date + expiry.</summary>
    public string GenerateQrTokenAsync(int locationId, int expiryMinutes = 30)
    {
        var key = _configuration["Jwt:Secret"] ?? "NickHR_Default_Secret_Key_For_QR_2024!";
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("locationId", locationId.ToString()),
            new Claim("date", DateTime.UtcNow.ToString("yyyy-MM-dd")),
            new Claim("purpose", "qr-attendance")
        };

        var token = new JwtSecurityToken(
            issuer: "NickHR",
            audience: "NickHR-Attendance",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Validate a QR token and return the location ID if valid.</summary>
    public int? ValidateQrTokenAsync(string token)
    {
        try
        {
            var key = _configuration["Jwt:Secret"] ?? "NickHR_Default_Secret_Key_For_QR_2024!";
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));

            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "NickHR",
                ValidateAudience = true,
                ValidAudience = "NickHR-Attendance",
                ValidateLifetime = true,
                IssuerSigningKey = securityKey,
                ClockSkew = TimeSpan.FromMinutes(2)
            };

            var principal = handler.ValidateToken(token, parameters, out _);

            var purposeClaim = principal.FindFirst("purpose")?.Value;
            if (purposeClaim != "qr-attendance")
                return null;

            var locationClaim = principal.FindFirst("locationId")?.Value;
            if (int.TryParse(locationClaim, out var locationId))
                return locationId;

            return null;
        }
        catch
        {
            return null;
        }
    }
}
