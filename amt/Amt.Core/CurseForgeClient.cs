using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amt.Core;

/// The newest CurseForge build of one installed mod, plus every MC version CF lists a build for (the "cross
/// version" matrix — free, because CF's `latestFilesIndexes` already carries one entry per game version).
public sealed class CfMod
{
    public int ProjectId { get; init; }
    public int LatestFileId { get; init; }          // newest file id for our loader + MC version (0 = none listed)
    public string LatestFileName { get; init; } = "";
    public IReadOnlyList<CfCrossVersion> CrossVersions { get; init; } = [];
}

/// One (MC version → newest CF file) entry for the cross-version view.
public sealed class CfCrossVersion
{
    public string Mc { get; init; } = "";
    public int FileId { get; init; }
    public string FileName { get; init; } = "";
}

/// A resolved CurseForge download: where to fetch it, what to call it, and (when CF provides it) the sha1 to
/// verify the finished file against.
public sealed class CfFile
{
    public string Url { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha1 { get; init; } = "";
}

/// The keyed half of the updater — talks to the CurseForge API. Unlike Modrinth, CF requires an API key; we can
/// NOT ship ours (CF forbids redistribution), so the key is user-supplied (Settings) and an empty key means every
/// CF call is simply skipped. Ported 1:1 from the verified Python (`cf_post_mods`, `cf_pick_latest`, the
/// `latestFilesIndexes` cross-version grouping, `cf_file_download_url`). Every call tolerates failure (a bad key,
/// a 403, a network blip) by returning empty/no result rather than throwing.
public static class CurseForgeClient
{
    private const string Base = "https://api.curseforge.com";
    private const int ChunkSize = 200;   // CF caps the bulk /v1/mods body; chunk to stay under it

    /// CurseForge modLoader TYPE ids. 0 = unknown (we then match only entries CF left loader-agnostic).
    public static int LoaderId(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0,
    };

    /// For each CF project id, the newest build for <paramref name="loader"/> + <paramref name="mcVersion"/> plus
    /// its cross-version matrix. Keyed by project id; projects CF doesn't return are just absent. No key => empty.
    public static async Task<IReadOnlyDictionary<int, CfMod>> CheckAsync(
        IReadOnlyList<int> projectIds, string apiKey, string mcVersion, string loader, CancellationToken ct = default)
    {
        var result = new Dictionary<int, CfMod>();
        if (apiKey.Length == 0 || projectIds.Count == 0) return result;
        var loaderId = LoaderId(loader);
        var headers = new[] { ("x-api-key", apiKey) };

        for (var i = 0; i < projectIds.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = projectIds.Skip(i).Take(ChunkSize).ToArray();
            using var doc = await HttpJson.PostAsync($"{Base}/v1/mods", new { modIds = chunk }, headers, ct);
            if (doc is null) continue;   // 403 (personal keys are gated for some endpoints), network error, etc.
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;

            foreach (var mod in data.EnumerateArray())
                if (Int(mod, "id") is var pid && pid != 0 && mod.TryGetProperty("latestFilesIndexes", out var idxs)
                    && idxs.ValueKind == JsonValueKind.Array)
                    result[pid] = BuildCfMod(pid, idxs, mcVersion, loaderId);
        }
        return result;
    }

    // Pick the newest build for our MC version + loader, and collect the newest build per MC version (loader-matched).
    private static CfMod BuildCfMod(int projectId, JsonElement indexes, string mcVersion, int loaderId)
    {
        var bestFileId = 0;
        var bestName = "";
        var cross = new Dictionary<string, CfCrossVersion>();

        foreach (var idx in indexes.EnumerateArray())
        {
            if (!LoaderMatches(idx, loaderId)) continue;
            var gv = Str(idx, "gameVersion");
            var fileId = Int(idx, "fileId");
            var fileName = Str(idx, "filename");   // NOTE: latestFilesIndexes uses lowercase "filename"
            if (gv.Length == 0) continue;

            // newest build for the version we actually run
            if (gv == mcVersion && fileId > bestFileId) { bestFileId = fileId; bestName = fileName; }

            // cross-version matrix: keep the newest file id per MC version
            if (!cross.TryGetValue(gv, out var have) || fileId > have.FileId)
                cross[gv] = new CfCrossVersion { Mc = gv, FileId = fileId, FileName = fileName };
        }

        var ordered = cross.Values.OrderByDescending(c => McKey(c.Mc)).ToList();
        return new CfMod { ProjectId = projectId, LatestFileId = bestFileId, LatestFileName = bestName, CrossVersions = ordered };
    }

    /// CF's official category list (id → name) for Minecraft, fetched once per run (the UI caches it). Used to
    /// turn the manifest's numeric `categoryIds` into names, which map onto the sidebar buckets — REAL tags
    /// instead of the keyword guess. Name-based downstream (never hardcoded ids) so CF renumbering can't break it.
    public static async Task<IReadOnlyDictionary<int, string>> CategoriesAsync(string apiKey, CancellationToken ct = default)
    {
        var map = new Dictionary<int, string>();
        if (apiKey.Length == 0) return map;
        using var doc = await HttpJson.GetAsync($"{Base}/v1/categories?gameId=432", new[] { ("x-api-key", apiKey) }, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return map;
        foreach (var c in data.EnumerateArray())
            if (Int(c, "id") is var id && id != 0 && Str(c, "name") is { Length: > 0 } name)
                map[id] = name;
        return map;
    }

    /// Resolve a file's download URL, name, and sha1. Ports the Python fallback: when CF nulls `downloadUrl`,
    /// reconstruct the forgecdn edge URL from the file id + name. Returns null if the file can't be resolved.
    public static async Task<CfFile?> GetFileAsync(int projectId, int fileId, string apiKey, CancellationToken ct = default)
    {
        if (apiKey.Length == 0) return null;
        using var doc = await HttpJson.GetAsync($"{Base}/v1/mods/{projectId}/files/{fileId}",
            new[] { ("x-api-key", apiKey) }, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var d)) return null;

        var fileName = Str(d, "fileName");
        var url = Str(d, "downloadUrl");
        if (url.Length == 0)   // CF sometimes nulls it — rebuild the edge URL: files/<first 4 of id>/<rest>/<name>
        {
            var fid = fileId.ToString();
            url = fid.Length > 4 ? $"https://edge.forgecdn.net/files/{fid[..4]}/{fid[4..]}/{fileName}" : "";
        }
        if (url.Length == 0) return null;
        return new CfFile { Url = url, FileName = fileName, Sha1 = Sha1FromHashes(d) };
    }

    /// A file's changelog, fetched on demand (CF returns it as HTML — stripped to plain text here, since the
    /// UI shows changelogs in a plain flyout). Empty on any failure or without a key.
    public static async Task<string> ChangelogAsync(int projectId, int fileId, string apiKey, CancellationToken ct = default)
    {
        if (apiKey.Length == 0 || projectId == 0 || fileId == 0) return "";
        using var doc = await HttpJson.GetAsync($"{Base}/v1/mods/{projectId}/files/{fileId}/changelog",
            new[] { ("x-api-key", apiKey) }, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.String)
            return "";
        // crude but sufficient HTML → text: <br>/<p>/<li> become line breaks, all other tags drop, entities decode
        var raw = d.GetString() ?? "";
        raw = Regex.Replace(raw, @"<(br|/p|/li)\s*/?>", "\n", RegexOptions.IgnoreCase);
        raw = Regex.Replace(raw, "<[^>]+>", "");
        return WebUtility.HtmlDecode(raw).Trim();
    }

    // --- helpers -------------------------------------------------------------------------------------------

    // An index matches when CF tags it for our loader OR leaves the loader unset (loader-agnostic entry).
    private static bool LoaderMatches(JsonElement idx, int loaderId)
    {
        if (!idx.TryGetProperty("modLoader", out var ml) || ml.ValueKind != JsonValueKind.Number) return true;
        return ml.TryGetInt32(out var id) && (id == loaderId || loaderId == 0);
    }

    // CF file hash list is [{value, algo}]; algo 1 = sha1.
    private static string Sha1FromHashes(JsonElement file)
    {
        if (!file.TryGetProperty("hashes", out var hs) || hs.ValueKind != JsonValueKind.Array) return "";
        foreach (var h in hs.EnumerateArray())
            if (Int(h, "algo") == 1) return Str(h, "value");
        return "";
    }

    // Sort key for an MC version string ("1.21.1" -> (1,21,1)) so versions order numerically, not as text.
    private static (int, int, int, int) McKey(string s)
    {
        var p = s.Split('.');
        int G(int i) => i < p.Length && int.TryParse(p[i], out var n) ? n : 0;
        return (G(0), G(1), G(2), G(3));
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
