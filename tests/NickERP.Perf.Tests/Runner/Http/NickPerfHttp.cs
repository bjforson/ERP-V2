using System.Net.Http.Headers;
using System.Text;

namespace NickERP.Perf.Tests.Runner.Http;

/// <summary>
/// Sprint 58 — thin HTTP helpers used by perf scenarios. Replaces the
/// NBomber.Http <c>Http.CreateRequest(...).WithHeader(...)</c> +
/// <c>Http.Send(http, request)</c> surface with direct
/// <see cref="HttpClient"/> calls; the per-step return is a
/// <see cref="NickPerfStepResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ok-criterion.</b> 2xx responses are <see cref="NickPerfStepResult.OkResult"/>;
/// non-2xx + transport errors map to
/// <see cref="NickPerfStepResult.Fail"/>. Scenarios that need a custom
/// ok-criterion (e.g. edge-replay-backlog wants 429s tallied separately)
/// can use <see cref="SendAsync"/> with their own classifier.
/// </para>
/// <para>
/// <b>Why no HttpRequestMessage builder.</b> The scenarios use a small
/// fixed shape — GET path, POST path with JSON body — so static helpers
/// keep the call-site readable. If a third pattern shows up we add a
/// new helper.
/// </para>
/// </remarks>
public static class NickPerfHttp
{
    /// <summary>
    /// Issue a GET against <paramref name="url"/>. Returns ok on 2xx;
    /// fail otherwise. The optional <paramref name="acceptHeader"/>
    /// defaults to <c>application/json</c>.
    /// </summary>
    public static async Task<NickPerfStepResult> GetAsync(
        HttpClient http,
        string url,
        string acceptHeader = "application/json",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(acceptHeader))
            req.Headers.Accept.ParseAdd(acceptHeader);

        return await SendAsync(http, req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// POST a JSON body against <paramref name="url"/>. Returns ok on
    /// 2xx; fail otherwise.
    /// </summary>
    public static async Task<NickPerfStepResult> PostJsonAsync(
        HttpClient http,
        string url,
        string jsonBody,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentNullException.ThrowIfNull(jsonBody);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return await SendAsync(http, req, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Low-level send. Translates 2xx → ok and everything else → fail.
    /// Scenarios needing a different classifier should call this and
    /// wrap.
    /// </summary>
    public static async Task<NickPerfStepResult> SendAsync(
        HttpClient http,
        HttpRequestMessage request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var resp = await http.SendAsync(request, ct).ConfigureAwait(false);
            // Drain so connection can be reused.
            _ = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? NickPerfStepResult.OkResult((int)resp.StatusCode)
                : NickPerfStepResult.Fail($"http {(int)resp.StatusCode}", (int)resp.StatusCode);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return NickPerfStepResult.Fail($"timeout: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return NickPerfStepResult.Fail($"transport: {ex.Message}");
        }
    }
}
