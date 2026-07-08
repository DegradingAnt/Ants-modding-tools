using System.Net.Http;
using System.Text.Json;

namespace Amt.Core;

/// The Xbox Live → XSTS → Minecraft-services → profile leg of Minecraft sign-in. These are game-service
/// endpoints Microsoft's identity platform (and therefore MSAL) knows nothing about, so this stays hand-rolled;
/// <see cref="MsAccount"/> owns the Microsoft OAuth half (MSAL) and hands the MS access token in here.
///
/// SECURITY: none of the tokens this produces (XBL, XSTS, or the 24h Minecraft token) are ever persisted — they
/// live in memory for the session and are re-derived from the cached refresh token every launch. Nothing here is
/// logged. Failure-tolerant: any step failing yields null (never a partial/duped auth).
public static class MsaAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// MS access token → Account (Minecraft token in memory, plus the public username + uuid). Null on any failure
    /// (incl. the account owning no copy of Minecraft — a 404 from the profile endpoint).
    public static async Task<Account?> MinecraftFromMicrosoftAsync(string msToken, CancellationToken ct = default)
    {
        // Xbox Live
        using var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = $"d={msToken}" },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, ct);
        var xblToken = xbl is null ? "" : Str(xbl.RootElement, "Token");
        var uhs = xbl is null ? "" : FirstUhs(xbl.RootElement);
        if (xblToken.Length == 0 || uhs.Length == 0) return null;

        // XSTS
        using var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT",
        }, ct);
        var xstsToken = xsts is null ? "" : Str(xsts.RootElement, "Token");
        if (xstsToken.Length == 0) return null;

        // Minecraft services login
        using var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" }, ct);
        var mcToken = mc is null ? "" : Str(mc.RootElement, "access_token");
        var expiresIn = mc is null ? 0 : Int(mc.RootElement, "expires_in", 86400);
        if (mcToken.Length == 0) return null;

        // profile (name + uuid) — public info; the token is NOT stored, only held for this session
        using var prof = await GetJsonAsync("https://api.minecraftservices.com/minecraft/profile",
            new[] { ("Authorization", $"Bearer {mcToken}") }, ct);
        if (prof is null) return null;
        var name = Str(prof.RootElement, "name");
        var id = Str(prof.RootElement, "id");
        if (name.Length == 0 || id.Length == 0) return null;   // no profile = the account owns no copy of Minecraft

        return new Account
        {
            Username = name,
            Uuid = id,
            AccessToken = mcToken,
            ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn,
        };
    }

    // --- tiny HTTP helpers (shape-matched to these endpoints; all failure-tolerant) ------------------------

    private static async Task<JsonDocument?> PostJsonAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch { return null; }
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, IEnumerable<(string, string)> headers, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch { return null; }
    }

    private static string FirstUhs(JsonElement root)
    {
        if (root.TryGetProperty("DisplayClaims", out var dc) && dc.TryGetProperty("xui", out var xui)
            && xui.ValueKind == JsonValueKind.Array)
            foreach (var x in xui.EnumerateArray())
                if (x.TryGetProperty("uhs", out var u) && u.ValueKind == JsonValueKind.String)
                    return u.GetString() ?? "";
        return "";
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop, int fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
}
