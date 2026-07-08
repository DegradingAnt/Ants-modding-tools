using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Amt.Core;

/// Microsoft sign-in for Minecraft, done the best-in-class secure way (researched 2026-07-08):
///
/// • **MSAL.NET** drives the Microsoft OAuth half — interactive **authorization-code + PKCE via the system
///   browser** with a loopback redirect. NOT device-code (Microsoft is blocking it as a phishing vector) and
///   NOT an embedded webview (the system browser keeps credentials out of our process entirely). MSAL does PKCE,
///   state, and refresh-token rotation for us.
/// • The **only secret persisted** is MSAL's token cache (which holds the one long-lived secret, the refresh
///   token), encrypted at rest by the **OS-native vault** — DPAPI on Windows, Keychain on macOS, libsecret on
///   Linux — via <c>Extensions.Msal</c>. The app itself never holds the encryption key. If the OS keystore is
///   unavailable (a headless Linux box), we DO NOT fall back to plaintext — the cache stays in memory and the
///   user simply re-signs each launch.
/// • Everything downstream (the MS access token, and the Xbox/XSTS/Minecraft tokens from <see cref="MsaAuth"/>)
///   is re-derived from the cached refresh token every launch and held only in memory.
///
/// Gated on an Azure AD **public-client** application id the author registers (there is no shared one to borrow;
/// the id is not a secret, so it ships in source once registered). Full end-to-end sign-in also needs Mojang's
/// per-client-id Minecraft API approval — until both exist, sign-in returns null with the reason.
public static class MsAccount
{
    // AMT's own registered Azure app id (public client — NOT a secret, safe in source; this is exactly what
    // PrismLauncher/HMCL do). Forks should register their own and substitute it. AppSettings.MsaClientId
    // overrides this if the user wants to use a different app registration.
    private const string DefaultClientId = "5f042910-0608-4b64-9a90-f23bc207bd7e";

    private const string Authority = "https://login.microsoftonline.com/consumers";   // personal MS accounts
    private static readonly string[] Scopes = { "XboxLive.signin", "offline_access" };

    private const string CacheFileName = "msal.cache";
    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools");

    public static string ResolveClientId(AppSettings s) =>
        s.MsaClientId.Trim().Length > 0 ? s.MsaClientId.Trim() : DefaultClientId;

    /// A cached account exists (we can try a silent sign-in). Cheap; no network.
    public static async Task<bool> HasCachedAccountAsync(AppSettings s)
    {
        var app = await BuildAppAsync(s);
        if (app is null) return false;
        var accounts = await app.GetAccountsAsync();
        return accounts.Any();
    }

    /// Silent sign-in from the cached refresh token (no browser). Null if there's no cached account, the token
    /// can't be refreshed, or the game chain fails — the caller then offers interactive sign-in.
    public static async Task<Account?> TrySilentAsync(AppSettings s, CancellationToken ct = default)
    {
        var app = await BuildAppAsync(s);
        if (app is null) return null;
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account is null) return null;
        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            return await MsaAuth.MinecraftFromMicrosoftAsync(result.AccessToken, ct);
        }
        catch (MsalUiRequiredException) { return null; }   // refresh expired/revoked — needs interactive
        catch { return null; }
    }

    /// Interactive sign-in: opens the system browser (PKCE, loopback). Null if no client id is configured or the
    /// user cancels / the chain fails. Run OFF the UI thread; MSAL spins its own loopback listener.
    public static async Task<Account?> SignInAsync(AppSettings s, CancellationToken ct = default)
    {
        var app = await BuildAppAsync(s);
        if (app is null) return null;
        try
        {
            var result = await app.AcquireTokenInteractive(Scopes)
                .WithUseEmbeddedWebView(false)   // system browser, not an embedded view (privacy + SSO)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = "<html><body style='font-family:sans-serif;background:#191919;color:#eee'>"
                        + "<h2>Signed in to Ant's Modding Tools</h2><p>You can close this tab and return to the app.</p></body></html>",
                })
                .ExecuteAsync(ct);
            return await MsaAuth.MinecraftFromMicrosoftAsync(result.AccessToken, ct);
        }
        catch (MsalClientException) { return null; }   // user cancelled the browser / no client id issue
        catch { return null; }
    }

    /// Full purge: forget every cached account (removes the refresh token from the OS vault). The caller also
    /// clears the in-memory session and the non-secret profile, and points the user at the MS consent page.
    public static async Task SignOutAsync(AppSettings s)
    {
        try
        {
            var app = await BuildAppAsync(s);
            if (app is null) return;
            foreach (var a in await app.GetAccountsAsync())
                await app.RemoveAsync(a);
        }
        catch { /* best-effort — the profile purge below still happens in the caller */ }
    }

    // Build the MSAL public client with the OS-encrypted cache registered. Null when no client id is configured.
    private static async Task<IPublicClientApplication?> BuildAppAsync(AppSettings s)
    {
        var clientId = ResolveClientId(s);
        if (clientId.Length == 0) return null;

        var app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(Authority)
            .WithDefaultRedirectUri()   // http://localhost loopback for a public desktop client
            .Build();

        // register the OS-native encrypted token cache; on a platform with no keystore, verify + fall back to
        // in-memory (MSAL's default) rather than ever writing a plaintext token
        try
        {
            var props = new StorageCreationPropertiesBuilder(CacheFileName, CacheDir).Build();
            var helper = await MsalCacheHelper.CreateAsync(props);
            helper.VerifyPersistence();          // throws if the OS keystore can't be used
            helper.RegisterCache(app.UserTokenCache);
        }
        catch
        {
            // no secure persistence available → leave the cache in-memory only (re-sign each launch). We never
            // persist a token unencrypted.
        }
        return app;
    }
}
