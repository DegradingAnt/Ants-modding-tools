using System.Text.Json;

namespace Amt.Core;

/// A signed-in Minecraft account. The <see cref="AccessToken"/> is the 24h Minecraft session token — it lives
/// ONLY in memory for the session and is re-derived from MSAL's cached refresh token each launch (see
/// <see cref="MsAccount"/>); it is never persisted or logged. Passwords never touch this app at all — sign-in is
/// Microsoft's system-browser flow.
public sealed class Account
{
    public string Username { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string AccessToken { get; init; } = "";   // in-memory only, never persisted
    public long ExpiresUnix { get; init; }

    public bool Expired => DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= ExpiresUnix;
}

/// The non-secret display profile — just the username + uuid — cached in plaintext so the app can show
/// "signed in as X" instantly at launch, before the silent token refresh completes. There is NO secret here: a
/// username and uuid are public (they appear on every server's player list). The actual secret (the refresh
/// token) is held only by MSAL's OS-encrypted cache; this app never persists a token.
public sealed class ProfileCache
{
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
}

/// Loads/saves the non-secret <see cref="ProfileCache"/>. Load never throws.
public static class AccountStore
{
    private static string ProfileFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools", "profile.json");

    public static void SaveProfile(Account a)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileFile)!);
            File.WriteAllText(ProfileFile, JsonSerializer.Serialize(new ProfileCache { Username = a.Username, Uuid = a.Uuid }));
        }
        catch { /* best-effort — a missed profile cache just means the UI waits for the silent refresh */ }
    }

    public static ProfileCache? LoadProfile()
    {
        try
        {
            return File.Exists(ProfileFile)
                ? JsonSerializer.Deserialize<ProfileCache>(File.ReadAllText(ProfileFile))
                : null;
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(ProfileFile)) File.Delete(ProfileFile); } catch { /* ignore */ }
    }
}
