using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Amt.Core;

/// What Modrinth knows about the newest build of one installed mod. Absent from the result map = a mod Modrinth
/// doesn't recognise (not published there, or a local / forked jar) — those are simply left as-is in the UI.
public sealed class ModUpdate
{
    public string LatestVersion { get; init; } = "";   // Modrinth's version_number for the newest matching build
    public string DownloadUrl { get; init; } = "";     // the primary file's URL (what the installer downloads)
    public string NewFileName { get; init; } = "";     // that file's name (what the installer saves it as)
    public string NewSha1 { get; init; } = "";         // that file's sha1 (the download integrity gate)
    public string ProjectId { get; init; } = "";       // Modrinth project id (used by the cross-version view)
    public string Changelog { get; init; } = "";       // the newest build's changelog (rides the update response free)
    public bool HasUpdate { get; init; }               // the newest build's sha1 differs from the installed jar's
}

/// The keyless half of the updater: checks installed jars against Modrinth for newer builds.
///
/// Modrinth exposes a bulk endpoint that maps a file hash straight to its newest version for a given loader +
/// Minecraft version, so we don't need to know each mod's project id up front — we sha1 every jar and let the
/// hash do the lookup. This mirrors the verified Python tool (`wnl_updater.py`, `mr_update`). CurseForge is the
/// other half and needs the user's own API key (entered on the Settings screen); UpdateChecker merges both.
///
/// Pure .NET base-class-library — `HttpClient` + `System.Text.Json` + `SHA1`, no third-party deps. Fully async
/// and failure-tolerant: a network or parse error on any batch is swallowed and we return whatever we resolved,
/// never a throw into the UI.
public static class ModrinthClient
{
    private const string Base = "https://api.modrinth.com/v2";
    private const int ChunkSize = 300;   // Modrinth accepts large batches; chunk to stay comfortably within limits

    /// Hash every enabled jar in <paramref name="modsDir"/> and return, keyed by file name, the newest Modrinth
    /// build for <paramref name="loader"/> + <paramref name="mcVersion"/>. Runs entirely off the caller's thread.
    public static async Task<IReadOnlyDictionary<string, ModUpdate>> CheckUpdatesAsync(
        string modsDir, string mcVersion, string loader, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ModUpdate>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(modsDir)) return result;

        // 1. sha1 each ENABLED jar (skip *.jar.disabled), remembering which file each hash came from.
        var shaToFile = new Dictionary<string, string>();      // installed sha1 -> file name
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try { shaToFile[await Sha1OfFileAsync(path, ct)] = Path.GetFileName(path); }
            catch { /* unreadable/locked jar — skip it, it just won't get an update row */ }
        }
        if (shaToFile.Count == 0) return result;

        // 2. ask Modrinth, in batches, for the newest matching version of each hash.
        var hashes = shaToFile.Keys.ToList();
        for (var i = 0; i < hashes.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = hashes.GetRange(i, Math.Min(ChunkSize, hashes.Count - i));
            using var doc = await HttpJson.PostAsync($"{Base}/version_files/update",
                new { hashes = chunk, algorithm = "sha1", loaders = new[] { loader }, game_versions = new[] { mcVersion } },
                ct: ct);
            if (doc is not null) ParseUpdateResponse(doc.RootElement, shaToFile, result);   // null = failed batch, skip
        }
        return result;
    }

    /// Bulk environment lookup: for each project id, whether the mod is client-side, server-side, or both —
    /// from Modrinth's project `client_side`/`server_side` fields (required/optional/unsupported). One GET for
    /// all ids; the ENV column's only data source (CurseForge doesn't publish an equivalent). Empty on failure.
    public static async Task<IReadOnlyDictionary<string, string>> ProjectEnvAsync(
        IReadOnlyCollection<string> projectIds, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var ids = projectIds.Where(i => i.Length > 0).Distinct().ToList();
        for (var i = 0; i < ids.Count; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ids.Skip(i).Take(ChunkSize);
            var q = "[" + string.Join(",", chunk.Select(id => $"\"{id}\"")) + "]";
            using var doc = await HttpJson.GetAsync($"{Base}/projects?ids={Uri.EscapeDataString(q)}", ct: ct);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var id = Str(p, "id");
                if (id.Length == 0) continue;
                var client = Str(p, "client_side");
                var server = Str(p, "server_side");
                result[id] = client == "required" && server == "unsupported" ? "client"
                           : server == "required" && client == "unsupported" ? "server"
                           : "both";
            }
        }
        return result;
    }

    /// Every MC version Modrinth has a build of one project for (newest per version), for our loader — the
    /// on-demand cross-version list (port of the Python `/api/modversions`). Empty on any failure.
    public static async Task<IReadOnlyList<(string Mc, string Version)>> ProjectVersionsAsync(
        string projectId, string loader, CancellationToken ct = default)
    {
        var seen = new Dictionary<string, string>();
        if (projectId.Length == 0) return [];
        using var doc = await HttpJson.GetAsync($"{Base}/project/{projectId}/version", ct: ct);   // ALL versions, newest first
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return [];

        foreach (var v in doc.RootElement.EnumerateArray())   // newest-first, so the first build per MC version wins
        {
            if (!v.TryGetProperty("loaders", out var loaders) || loaders.ValueKind != JsonValueKind.Array) continue;
            if (!loaders.EnumerateArray().Any(l => l.ValueKind == JsonValueKind.String && l.GetString() == loader)) continue;
            var ver = Str(v, "version_number");
            if (v.TryGetProperty("game_versions", out var gvs) && gvs.ValueKind == JsonValueKind.Array)
                foreach (var gv in gvs.EnumerateArray())
                    if (gv.ValueKind == JsonValueKind.String && gv.GetString() is { } mc)
                        seen.TryAdd(mc, ver);
        }
        return seen.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    // Modrinth returns a { "<installed sha1>": <version object> } map. Turn it into our file-name-keyed result.
    private static void ParseUpdateResponse(JsonElement root, IReadOnlyDictionary<string, string> shaToFile,
                                            Dictionary<string, ModUpdate> result)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var pair in root.EnumerateObject())
        {
            var installedSha = pair.Name;                          // the hash we asked about
            if (!shaToFile.TryGetValue(installedSha, out var file)) continue;
            var v = pair.Value;

            var latest = Str(v, "version_number");
            var project = Str(v, "project_id");

            // The "primary" file carries the download URL, its file name, and the newest build's own sha1 — the
            // sha1 both decides HasUpdate (differs from installed) and later gates the installer's download.
            string url = "", newFile = "", newestSha = "";
            if (v.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var chosen = default(JsonElement);
                var have = false;
                foreach (var f in files.EnumerateArray())
                {
                    if (!have) { chosen = f; have = true; }        // fall back to the first file if none is primary
                    if (f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True) { chosen = f; break; }
                }
                if (have)
                {
                    url = Str(chosen, "url");
                    newFile = Str(chosen, "filename");
                    if (chosen.TryGetProperty("hashes", out var h)) newestSha = Str(h, "sha1");
                }
            }

            result[file] = new ModUpdate
            {
                LatestVersion = latest,
                DownloadUrl = url,
                NewFileName = newFile,
                NewSha1 = newestSha,
                ProjectId = project,
                Changelog = Str(v, "changelog"),
                HasUpdate = newestSha.Length > 0 && !newestSha.Equals(installedSha, StringComparison.OrdinalIgnoreCase),
            };
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static async Task<string> Sha1OfFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA1.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
