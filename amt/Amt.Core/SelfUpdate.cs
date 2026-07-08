using System.Text.Json;

namespace Amt.Core;

/// The result of an app self-update check: whether GitHub's latest release is newer than what's running, and
/// where to get it. All fields empty / HasUpdate false when the check couldn't reach GitHub or found nothing
/// newer — so a failed check silently means "you're up to date" rather than nagging.
public sealed class SelfUpdateInfo
{
    public bool HasUpdate { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";     // the release tag, normalised (leading v stripped)
    public string ReleaseUrl { get; init; } = "";        // the human release page
    public string AssetUrl { get; init; } = "";          // the first downloadable asset (installer/zip), if any
    public string AssetName { get; init; } = "";
    public string Notes { get; init; } = "";             // the release body (changelog)
}

/// Checks whether a newer AMT release exists on GitHub (the app's own updater, AMT-13). Keyless: it reads the
/// public releases API. Quiet by design — the caller checks once on launch and only surfaces anything when
/// there's genuinely a newer build. Downloading + applying is deliberately NOT automated here: this returns
/// the release/asset URLs and the app opens them, so the user stays in control of replacing a running binary
/// (self-replacing an in-use exe is platform-fiddly and a later refinement).
public static class SelfUpdate
{
    private const string Repo = "DegradingAnt/Ants-modding-tools";

    public static async Task<SelfUpdateInfo> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var none = new SelfUpdateInfo { CurrentVersion = currentVersion };
        using var doc = await HttpJson.GetAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest",
            new[] { ("Accept", "application/vnd.github+json") }, ct);
        if (doc is null) return none;

        var tag = Str(doc.RootElement, "tag_name");
        if (tag.Length == 0) tag = Str(doc.RootElement, "name");
        if (tag.Length == 0) return none;

        // a prerelease/draft is not an update the user should be pushed to
        if (Bool(doc.RootElement, "prerelease") || Bool(doc.RootElement, "draft")) return none;

        var latest = tag.TrimStart('v', 'V');
        // reuse the same version comparison the fork watcher uses; only a clearly-newer upstream counts
        var newer = VersionCompare.Compare(currentVersion, latest) is { } c && c < 0;

        var (assetUrl, assetName) = FirstAsset(doc.RootElement);
        return new SelfUpdateInfo
        {
            HasUpdate = newer,
            CurrentVersion = currentVersion,
            LatestVersion = latest,
            ReleaseUrl = Str(doc.RootElement, "html_url"),
            AssetUrl = assetUrl,
            AssetName = assetName,
            Notes = Str(doc.RootElement, "body"),
        };
    }

    // the first release asset's browser download URL — the installer/zip the user grabs
    private static (string Url, string Name) FirstAsset(JsonElement release)
    {
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var url = Str(a, "browser_download_url");
                if (url.Length > 0) return (url, Str(a, "name"));
            }
        return ("", "");
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
}
