using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;

namespace NickHR.WebApp.Services;

public class AuthStateProvider : AuthenticationStateProvider
{
    private readonly ILocalStorageService _localStorage;
    private string? _cachedToken;
    private bool _initialized;
    private readonly Task<AuthenticationState> _anonymousState =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    public AuthStateProvider(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        // If not yet initialized (prerender), try to read token
        if (!_initialized)
        {
            try
            {
                _cachedToken = await _localStorage.GetItemAsync<string>("authToken");
                _initialized = true;
            }
            catch
            {
                // localStorage not available during prerender - return anonymous
                // The MainLayout.OnAfterRenderAsync will call InitializeAsync to retry
                return await _anonymousState;
            }
        }

        if (string.IsNullOrEmpty(_cachedToken))
            return await _anonymousState;

        var claims = ParseClaimsFromJwt(_cachedToken);
        if (claims == null)
        {
            // Token expired or invalid
            _cachedToken = null;
            try { await _localStorage.RemoveItemAsync("authToken"); } catch { }
            return await _anonymousState;
        }

        var identity = new ClaimsIdentity(claims, "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task LoginAsync(string token, string refreshToken)
    {
        _cachedToken = token;
        _initialized = true;
        await _localStorage.SetItemAsync("authToken", token);
        await _localStorage.SetItemAsync("refreshToken", refreshToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task LogoutAsync()
    {
        _cachedToken = null;
        _initialized = true;
        try
        {
            await _localStorage.RemoveItemAsync("authToken");
            await _localStorage.RemoveItemAsync("refreshToken");
        }
        catch { }
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public string? GetToken() => _cachedToken;

    /// <summary>
    /// Called from OnAfterRenderAsync to initialize auth state after prerender.
    /// During prerender, localStorage is unavailable. This method retries once
    /// interactive rendering is active and JS interop works.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            _cachedToken = await _localStorage.GetItemAsync<string>("authToken");
            _initialized = true;

            // Notify so AuthorizeRouteView re-evaluates
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
        catch
        {
            // Still can't access localStorage
        }
    }

    private IEnumerable<Claim>? ParseClaimsFromJwt(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(jwt);

            if (token.ValidTo < DateTime.UtcNow)
                return null;

            return token.Claims;
        }
        catch
        {
            return null;
        }
    }
}
