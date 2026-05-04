using System.Net.Http.Headers;
using System.Net.Http.Json;
using NickHR.Core.DTOs;

namespace NickHR.WebApp.Services;

public class ApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthStateProvider _authState;

    public ApiClient(IHttpClientFactory httpClientFactory, AuthStateProvider authState)
    {
        _httpClientFactory = httpClientFactory;
        _authState = authState;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("NickHR.API");
        var token = _authState.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return client;
    }

    public async Task<ApiResponse<T>?> GetAsync<T>(string url)
    {
        var client = CreateClient();
        var response = await client.GetAsync(url);
        return await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
    }

    public async Task<ApiResponse<T>?> PostAsync<T>(string url, object data)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(url, data);
        return await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
    }

    public async Task<ApiResponse<T>?> PutAsync<T>(string url, object data)
    {
        var client = CreateClient();
        var response = await client.PutAsJsonAsync(url, data);
        return await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
    }

    public async Task<ApiResponse<bool>?> DeleteAsync(string url)
    {
        var client = CreateClient();
        var response = await client.DeleteAsync(url);
        return await response.Content.ReadFromJsonAsync<ApiResponse<bool>>();
    }
}
