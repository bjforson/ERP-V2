using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NickHR.Core.Interfaces;

namespace NickHR.Services.Auth;

/// <summary>
/// HTTP client that validates credentials against the NSCIS central auth endpoint.
/// POST /api/auth/validate-credentials on the NSCIS API.
/// </summary>
public class CentralAuthClient : ICentralAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CentralAuthClient> _logger;

    public CentralAuthClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<CentralAuthClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CentralAuthResult> ValidateCredentialsAsync(string usernameOrEmail, string password)
    {
        try
        {
            var apiKey = Environment.GetEnvironmentVariable("NICKSCAN_SERVICE_API_KEY")
                ?? _configuration["CentralAuth:ServiceApiKey"]
                ?? string.Empty;

            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogError("CentralAuth: NICKSCAN_SERVICE_API_KEY not configured");
                return new CentralAuthResult { IsValid = false };
            }

            var request = new
            {
                username = usernameOrEmail,
                password,
                serviceApiKey = apiKey
            };

            var timeoutSeconds = _configuration.GetValue<int>("CentralAuth:TimeoutSeconds", 10);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

            var response = await _httpClient.PostAsJsonAsync("/api/auth/validate-credentials", request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CentralAuth: HTTP {StatusCode} for {Identifier}",
                    response.StatusCode, usernameOrEmail);
                return new CentralAuthResult { IsValid = false };
            }

            var result = await response.Content.ReadFromJsonAsync<CentralAuthResponseDto>(cancellationToken: cts.Token);

            if (result == null || !result.IsValid)
            {
                _logger.LogInformation("CentralAuth: Invalid credentials for {Identifier}", usernameOrEmail);
                return new CentralAuthResult { IsValid = false };
            }

            _logger.LogInformation("CentralAuth: Validated user {Username} (NSCIS ID: {NscisUserId})",
                result.Username, result.NscisUserId);

            return new CentralAuthResult
            {
                IsValid = true,
                Username = result.Username,
                Email = result.Email,
                FullName = result.FullName,
                NscisUserId = result.NscisUserId
            };
        }
        catch (TaskCanceledException)
        {
            _logger.LogError("CentralAuth: Request timed out for {Identifier}", usernameOrEmail);
            return new CentralAuthResult { IsValid = false };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "CentralAuth: HTTP error for {Identifier}", usernameOrEmail);
            return new CentralAuthResult { IsValid = false };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CentralAuth: Unexpected error for {Identifier}", usernameOrEmail);
            return new CentralAuthResult { IsValid = false };
        }
    }

    public async Task<List<NscisRole>> GetRolesAsync()
    {
        try
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey)) return new List<NscisRole>();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(GetTimeout()));
            var response = await _httpClient.GetAsync($"/api/roles/service/list?apiKey={Uri.EscapeDataString(apiKey)}", cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CentralAuth: GetRoles HTTP {StatusCode}", response.StatusCode);
                return new List<NscisRole>();
            }

            var roles = await response.Content.ReadFromJsonAsync<List<NscisRoleDto>>(cancellationToken: cts.Token);
            return roles?.Select(r => new NscisRole
            {
                Id = r.Id,
                Name = r.Name,
                DisplayName = r.DisplayName,
                Description = r.Description,
                IsSystemRole = r.IsSystemRole
            }).ToList() ?? new List<NscisRole>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CentralAuth: GetRoles failed");
            return new List<NscisRole>();
        }
    }

    public async Task<ProvisionResult> ProvisionUserAsync(ProvisionUserRequest request)
    {
        try
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                return new ProvisionResult { Success = false, ErrorMessage = "Service API key not configured" };
            }

            var body = new
            {
                username = request.Username,
                email = request.Email,
                firstName = request.FirstName,
                lastName = request.LastName,
                roleId = request.RoleId,
                isActive = request.IsActive,
                serviceApiKey = apiKey
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(GetTimeout()));
            var response = await _httpClient.PostAsJsonAsync("/api/users/service/provision", body, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cts.Token);
                _logger.LogWarning("CentralAuth: Provision HTTP {StatusCode} for {Username}: {Error}",
                    response.StatusCode, request.Username, errorText);
                return new ProvisionResult
                {
                    Success = false,
                    ErrorMessage = $"NSCIS returned {response.StatusCode}: {errorText}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<ProvisionResponseDto>(cancellationToken: cts.Token);
            if (result == null)
            {
                return new ProvisionResult { Success = false, ErrorMessage = "Empty response from NSCIS" };
            }

            _logger.LogInformation("CentralAuth: Provisioned user {Username} (NSCIS ID {Id}, Created: {Created})",
                result.Username, result.NscisUserId, result.Created);

            return new ProvisionResult
            {
                Success = result.Success,
                NscisUserId = result.NscisUserId,
                Username = result.Username,
                Email = result.Email,
                IsActive = result.IsActive,
                Created = result.Created,
                TemporaryPassword = result.TemporaryPassword
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CentralAuth: ProvisionUser failed for {Username}", request.Username);
            return new ProvisionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> DeactivateUserAsync(string username)
    {
        try
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey)) return false;

            var body = new { serviceApiKey = apiKey };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(GetTimeout()));
            var response = await _httpClient.PutAsJsonAsync(
                $"/api/users/service/{Uri.EscapeDataString(username)}/deactivate", body, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("CentralAuth: Deactivate HTTP {StatusCode} for {Username}",
                    response.StatusCode, username);
                return false;
            }

            _logger.LogInformation("CentralAuth: Deactivated user {Username}", username);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CentralAuth: DeactivateUser failed for {Username}", username);
            return false;
        }
    }

    private string GetApiKey() =>
        Environment.GetEnvironmentVariable("NICKSCAN_SERVICE_API_KEY")
        ?? _configuration["CentralAuth:ServiceApiKey"]
        ?? string.Empty;

    private int GetTimeout() => _configuration.GetValue<int>("CentralAuth:TimeoutSeconds", 10);

    private class CentralAuthResponseDto
    {
        public bool IsValid { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public int NscisUserId { get; set; }
    }

    private class NscisRoleDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsSystemRole { get; set; }
    }

    private class ProvisionResponseDto
    {
        public bool Success { get; set; }
        public int NscisUserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public bool Created { get; set; }
        public string? TemporaryPassword { get; set; }
    }
}
