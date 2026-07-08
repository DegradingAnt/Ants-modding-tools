using System.IO;

namespace Amt.Core;

/// The complete update picture for one installed mod: what's on disk, and what CurseForge AND Modrinth each say is
/// the newest build, with an update flag per source. This is what the installed table binds to
/// (<see cref="ModrinthClient"/> and <see cref="CurseForgeClient"/> are its two inputs).
public sealed class ModStatus
{
    public string OnDisk { get; init; } = "";           // the jar file name — the key back to a scan row
    public string Name { get; init; } = "";
    public string InstalledVersion { get; init; } = ""; // the installed file's name (best available)
    public bool IsModified { get; init; }               // CF flags a hand-edited jar (never auto-overwrite it)

    // CurseForge side (populated only with a key; falls back to CF's cached-latest when offline)
    public int CfProjectId { get; init; }
    public string CfWebUrl { get; init; } = "";          // the mod's CF page (from the manifest) — row click-through
    public int CfLatestFileId { get; init; }
    public string CfLatestName { get; init; } = "";
    public bool CfLatestCached { get; init; }            // the CF "latest" came from the manifest cache, not the live API
    public bool CfHasUpdate { get; init; }
    public IReadOnlyList<CfCrossVersion> CfCrossVersions { get; init; } = [];
    public IReadOnlyList<int> CfCategoryIds { get; init; } = [];   // primary-first; the UI maps them to sidebar buckets

    // Modrinth side (keyless)
    public string MrProjectId { get; init; } = "";
    public string MrLatestVersion { get; init; } = "";
    public string MrDownloadUrl { get; init; } = "";
    public string MrNewFileName { get; init; } = "";     // the newest primary file's name (what the installer saves)
    public string MrNewSha1 { get; init; } = "";         // its sha1 (the installer's integrity gate)
    public string MrChangelog { get; init; } = "";       // the newest build's changelog (CF's is fetched on demand)
    public bool MrHasUpdate { get; init; }

    /// "client" / "server" / "both" from Modrinth's project metadata; "" when Modrinth doesn't know the mod
    /// (CF publishes no equivalent, so unknown stays honestly blank rather than guessed).
    public string Env { get; init; } = "";

    /// True when EITHER source has a newer build than what's installed.
    public bool HasUpdate => CfHasUpdate || MrHasUpdate;

    /// Which sources recognised this mod — "CF+Modrinth" / "CF" / "Modrinth" / "local" (neither).
    public string Source { get; init; } = "local";
}

/// Runs a full update scan: read the instance manifest for identity, hash the jars for Modrinth, ask BOTH sites in
/// bulk, and merge into one <see cref="ModStatus"/> per installed jar. Folder-driven (every jar on disk becomes a
/// row, so manually-added jars still appear), enriched by `minecraftinstance.json` for the CurseForge project ids.
/// A 1:1 port of the Python `do_scan` combine; reuses <see cref="ModrinthClient"/> + <see cref="CurseForgeClient"/>
/// so there's one source of truth per site. Fully off-thread and failure-tolerant.
public static class UpdateChecker
{
    public static async Task<IReadOnlyList<ModStatus>> ScanAsync(AppSettings s, CancellationToken ct = default)
    {
        var results = new List<ModStatus>();
        if (s.ModsDir.Length == 0 || !Directory.Exists(s.ModsDir)) return results;

        // identity from CF's manifest, keyed by the jar's on-disk name
        var addonByFile = new Dictionary<string, InstalledAddon>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in InstanceReader.Read(s.InstanceJson))
            if (a.OnDisk.Length > 0) addonByFile[a.OnDisk] = a;

        // Modrinth (keyless) — hashes the jars internally, returns keyed by file name
        var mrByFile = await ModrinthClient.CheckUpdatesAsync(s.ModsDir, s.McVersion, s.Loader, ct);

        // CurseForge (keyed) — bulk-check every project id we know from the manifest
        var projectIds = addonByFile.Values.Select(a => a.CfProjectId).Where(id => id != 0).Distinct().ToList();
        var cfById = await CurseForgeClient.CheckAsync(projectIds, s.CurseForgeApiKey, s.McVersion, s.Loader, ct);

        // Modrinth project env (client/server/both) — one bulk GET over every project Modrinth recognised
        var envById = await ModrinthClient.ProjectEnvAsync(
            mrByFile.Values.Select(m => m.ProjectId).ToList(), ct);

        // combine, driven by the jars actually on disk (matches what the installed table scans)
        foreach (var path in Directory.EnumerateFiles(s.ModsDir, "*.jar", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var file = Path.GetFileName(path);
            addonByFile.TryGetValue(file, out var addon);
            mrByFile.TryGetValue(file, out var mr);
            var cf = addon is { CfProjectId: not 0 } && cfById.TryGetValue(addon.CfProjectId, out var c) ? c : null;

            // --- CurseForge verdict (live, else CF's cached-latest offline fallback) -------------------------
            int cfLatestId = 0; var cfLatestName = ""; var cfUpdate = false; var cfCached = false;
            IReadOnlyList<CfCrossVersion> cross = [];
            if (cf is not null)
            {
                cfLatestId = cf.LatestFileId; cfLatestName = cf.LatestFileName; cross = cf.CrossVersions;
                cfUpdate = cfLatestId != 0 && addon!.CfFileId != 0 && cfLatestId > addon.CfFileId;
            }
            else if (addon is { CachedLatestFileId: not 0 })   // no live CF data — use the manifest's cached latest
            {
                // NOTE: the name stays CLEAN (no "(cached)" suffix) — the installer rebuilds a download URL from
                // it, and the UI reads CfLatestCached when it wants to badge the row.
                cfLatestId = addon.CachedLatestFileId;
                cfLatestName = addon.CachedLatestFileName;
                cfCached = true;
                cfUpdate = addon.CfFileId != 0 && cfLatestId > addon.CfFileId;
            }

            var srcs = new List<string>();
            if (cfLatestId != 0) srcs.Add("CF");
            if (mr is not null) srcs.Add("Modrinth");

            results.Add(new ModStatus
            {
                OnDisk = file,
                Name = addon?.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file),
                InstalledVersion = addon?.FileName is { Length: > 0 } iv ? iv : file,
                IsModified = addon?.IsModified ?? false,
                CfProjectId = addon?.CfProjectId ?? 0,
                CfWebUrl = addon?.WebUrl ?? "",
                CfLatestFileId = cfLatestId,
                CfLatestName = cfLatestName,
                CfLatestCached = cfCached,
                CfHasUpdate = cfUpdate,
                CfCrossVersions = cross,
                CfCategoryIds = addon?.CategoryIds ?? [],
                MrProjectId = mr?.ProjectId ?? "",
                MrLatestVersion = mr?.LatestVersion ?? "",
                MrDownloadUrl = mr?.DownloadUrl ?? "",
                MrNewFileName = mr?.NewFileName ?? "",
                MrNewSha1 = mr?.NewSha1 ?? "",
                MrChangelog = mr?.Changelog ?? "",
                MrHasUpdate = mr?.HasUpdate ?? false,
                Env = mr is not null && envById.TryGetValue(mr.ProjectId, out var env) ? env : "",
                Source = srcs.Count > 0 ? string.Join("+", srcs) : "local",
            });
        }

        results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return results;
    }
}
