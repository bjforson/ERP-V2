using NickHR.Core.DTOs.Auth;

namespace NickHR.Core.Interfaces;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request);
    Task<LoginResponse> RegisterAsync(RegisterRequest request);
    Task<LoginResponse> RefreshTokenAsync(string token);
    Task ChangePasswordAsync(int userId, ChangePasswordRequest request);
}
