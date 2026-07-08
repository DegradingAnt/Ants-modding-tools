using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Amt.Core;

/// One shared HttpClient plus tiny JSON GET/POST helpers for the CurseForge / Modrinth / catalogue / fork clients.
///
/// A single long-lived client (a fresh HttpClient per request exhausts sockets) and a descriptive User-Agent so
/// the mod sites can identify — and if needed contact — the tool. Every call returns a parsed JsonDocument or
/// <c>null</c> and never throws, so a network blip degrades a feature instead of crashing the app. The caller
/// OWNS the returned document and must dispose it (`using`).
public static class HttpJson
{
    private static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AntsModdingTools/0.1 (github.com/DegradingAnt/Ant-s-modding-tools)");
        return c;
    }

    public static Task<JsonDocument?> GetAsync(
        string url, IEnumerable<(string Key, string Value)>? headers = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, url, null, headers, ct);

    /// GET raw text (not JSON) — e.g. a repo's `gradle.properties`. Null on any non-2xx / error.
    public static async Task<string?> GetStringAsync(
        string url, IEnumerable<(string Key, string Value)>? headers = null, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers is not null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await Http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
        }
        catch { return null; }
    }

    /// POST <paramref name="body"/> serialised as JSON. Anonymous types serialise property-name-as-is, which is
    /// what the mod-site APIs expect (they use snake_case keys like `game_versions`, so name your fields to match).
    public static Task<JsonDocument?> PostAsync(
        string url, object body, IEnumerable<(string Key, string Value)>? headers = null, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, url, JsonSerializer.Serialize(body), headers, ct);

    private static async Task<JsonDocument?> SendAsync(HttpMethod method, string url, string? jsonBody,
        IEnumerable<(string Key, string Value)>? headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, url);
            if (jsonBody is not null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            if (headers is not null)
                foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            // ParseAsync fully reads the stream into an independent document, so disposing the stream/response
            // here is safe — the returned JsonDocument outlives them.
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch { return null; }
    }
}
