using System.IO;
using System.Text.Json;

namespace Amt.Core;

/// The user's persisted configuration. Everything the backend needs to run is here so nothing is hardcoded: which
/// Minecraft instance to read, the loader + MC version that scope every "newest build" query, the user's own
/// CurseForge API key (we can NOT ship ours — CF forbids it — so the user pastes their own), and where update
/// downloads land. Plain public setters + a flat shape so it serialises to readable JSON and binds trivially to a
/// Settings screen (tab 8 in the app).
public sealed class AppSettings
{
    /// The Minecraft instance root — the folder that holds `mods/` and `minecraftinstance.json`.
    public string InstancePath { get; set; } = "";

    /// The MC version + loader we manage. These scope the CurseForge + Modrinth "latest for this loader/version"
    /// lookups; a mismatch would report the wrong "latest". Defaults match the current dev pack (1.21.1 NeoForge).
    public string McVersion { get; set; } = "1.21.1";
    public string Loader { get; set; } = "neoforge";

    /// The user's OWN CurseForge API key. Empty = CurseForge checks are skipped (Modrinth still works keyless).
    /// Never shipped, never logged; supplied by the user on first run.
    public string CurseForgeApiKey { get; set; } = "";

    /// Where update downloads are written for review (nothing is auto-installed). Empty => a `downloads` folder
    /// next to the settings file (filled in by <see cref="SettingsStore.Load"/>).
    public string DownloadDir { get; set; } = "";

    /// Dock-engine layout (see <see cref="DockRules"/>): the sidebar's user-dragged width and its last
    /// user-chosen state ("expanded" | "rail" | "hidden"). Auto-hide from a narrow window is NOT persisted —
    /// only deliberate user choices are.
    public double SidebarWidth { get; set; } = DockRules.DefaultSidebarWidth;
    public string SidebarState { get; set; } = "expanded";

    /// The tab strip's user-dragged presentation order (indices into the app's tab list). Empty = default
    /// order; the app validates it's a real permutation before using it, so a stale file can't lose tabs.
    public int[] TabOrder { get; set; } = [];

    /// Installed-table column picker (AMT-06): Author + Env ship on, Size + Date added are opt-in.
    public bool ColAuthor { get; set; } = true;
    public bool ColEnv { get; set; } = true;
    public bool ColSize { get; set; }
    public bool ColAdded { get; set; }

    /// Appearance (AMT-12). RowDensity: "compact" | "cozy" (default) | "comfortable" — the installed-table
    /// row height. The value maps to a vertical padding in the app (RowPadFor).
    public string RowDensity { get; set; } = "cozy";

    /// Launcher (AMT-14). The Azure AD application (public client) id AMT signs in with — the user registers
    /// their own; empty = sign-in disabled. The launch command boots the game; placeholders {accessToken}
    /// {username} {uuid} {instance} are filled from the signed-in account (<see cref="GameLauncher"/>).
    public string MsaClientId { get; set; } = "";
    public string LaunchCommand { get; set; } = "";

    /// The mods folder + the CurseForge manifest, derived from <see cref="InstancePath"/>. Empty when no instance
    /// is set yet, which the callers treat as "nothing to scan".
    public string ModsDir => InstancePath.Length == 0 ? "" : Path.Combine(InstancePath, "mods");
    public string InstanceJson => InstancePath.Length == 0 ? "" : Path.Combine(InstancePath, "minecraftinstance.json");
}

/// Loads + saves <see cref="AppSettings"/> as human-readable JSON under the per-user app-data folder
/// (`%APPDATA%/AntsModdingTools/settings.json` on Windows, `~/.config/...` on Linux/macOS via the same BCL call).
/// Load never throws — a missing or corrupt file just yields defaults — so a bad config can never brick startup.
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools");
    private static string ConfigFile => Path.Combine(ConfigDir, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(ConfigFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigFile)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); }   // unreadable / malformed config => fall back to defaults, never crash

        if (s.DownloadDir.Length == 0)
            s.DownloadDir = Path.Combine(ConfigDir, "downloads");
        return s;
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(s, JsonOpts));
    }
}
