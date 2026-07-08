using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amt.Core;

/// The result of checking ONE upstream of one fork: what we forked from (Base), what upstream's newest is now
/// (Latest), and the <see cref="VersionCompare.Classify"/> verdict (UPDATE / current / review / …).
public sealed class UpstreamResult
{
    public string Kind { get; init; } = "";        // github | modrinth | curseforge
    public string Id { get; init; } = "";          // owner/repo, or the Modrinth/CF project id
    public string Base { get; init; } = "";        // the version we forked from
    public string Confidence { get; init; } = "";
    public string Note { get; init; } = "";
    public string? Latest { get; init; }
    public string? Url { get; init; }
    public string? Error { get; init; }
    public string Verdict { get; init; } = "";
}

/// One fork and every upstream it tracks, after checking.
public sealed class ForkResult
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Note { get; init; } = "";
    public IReadOnlyList<UpstreamResult> Upstreams { get; init; } = [];
}

/// The "are our forks behind upstream?" checker. Our own forks (Natrium←Sodium, wnllux←ScalableLux, …) aren't on
/// CF/Modrinth as OUR projects, so the normal update scan can't see when THEIR upstream ships a release we should
/// re-port. For each fork in a user-editable registry (`forks.json`) it fetches the upstream's newest version and
/// compares it to the version we forked from. Ported from the Python `fork_watcher.py`.
///
/// (Simplification vs the Python: GitHub calls cascade release → newest tag → gradle.properties `mod_version` on
/// any failure, rather than branching on the exact 404/403 status; a `GITHUB_TOKEN` for the 5000/hr rate limit is
/// a later refinement. The registry format + verdicts are identical.)
public static class ForkWatcher
{
    private const string Mr = "https://api.modrinth.com/v2";

    /// Check every fork in <paramref name="registryJson"/> (the parsed contents of a `forks.json`). Each fork's
    /// upstreams are checked concurrently. Failure-tolerant: a fetch that fails becomes a "no-upstream" verdict
    /// with the error attached, never a throw.
    public static async Task<IReadOnlyList<ForkResult>> CheckAllAsync(
        string registryJson, AppSettings s, string? githubToken = null, CancellationToken ct = default)
    {
        var forks = new List<ForkResult>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(registryJson); }
        catch { return forks; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("forks", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return forks;

            foreach (var fork in arr.EnumerateArray())
            {
                var ups = new List<UpstreamResult>();
                if (fork.TryGetProperty("upstreams", out var upArr) && upArr.ValueKind == JsonValueKind.Array)
                    foreach (var u in upArr.EnumerateArray())
                        ups.Add(await CheckUpstreamAsync(u, s, githubToken, ct));

                forks.Add(new ForkResult
                {
                    Name = Str(fork, "name"),
                    Path = Str(fork, "path"),
                    Note = Str(fork, "note"),
                    Upstreams = ups,
                });
            }
        }
        return forks;
    }

    private static async Task<UpstreamResult> CheckUpstreamAsync(
        JsonElement u, AppSettings s, string? token, CancellationToken ct)
    {
        var kind = Str(u, "kind");
        var id = Str(u, "id");
        var forkBase = Str(u, "base");

        var (latest, url, err) = kind switch
        {
            "github" => await GitHubLatestAsync(id, token, ct),
            "modrinth" => await ModrinthLatestAsync(id, s.McVersion, s.Loader, ct),
            "curseforge" => await CurseForgeLatestAsync(id, s, ct),
            _ => (null, null, $"unknown kind '{kind}'"),
        };

        return new UpstreamResult
        {
            Kind = kind, Id = id, Base = forkBase,
            Confidence = Str(u, "confidence"), Note = Str(u, "note"),
            Latest = latest, Url = url, Error = err,
            Verdict = kind is "github" or "modrinth" or "curseforge" ? VersionCompare.Classify(forkBase, latest) : "bad-kind",
        };
    }

    // --- upstream fetchers ---------------------------------------------------------------------------------

    // GitHub, in order of trust: latest release -> newest tag -> default-branch gradle.properties mod_version.
    private static async Task<(string?, string?, string?)> GitHubLatestAsync(string repo, string? token, CancellationToken ct)
    {
        var headers = new List<(string, string)> { ("Accept", "application/vnd.github+json") };
        if (!string.IsNullOrEmpty(token)) headers.Add(("Authorization", $"Bearer {token}"));

        using (var rel = await HttpJson.GetAsync($"https://api.github.com/repos/{repo}/releases/latest", headers, ct))
            if (rel is not null)
            {
                var tag = Str(rel.RootElement, "tag_name");
                if (tag.Length == 0) tag = Str(rel.RootElement, "name");
                if (tag.Length > 0) return (tag, Str(rel.RootElement, "html_url"), null);
            }

        using (var tags = await HttpJson.GetAsync($"https://api.github.com/repos/{repo}/tags", headers, ct))
            if (tags is not null && tags.RootElement.ValueKind == JsonValueKind.Array && tags.RootElement.GetArrayLength() > 0)
            {
                var name = Str(tags.RootElement[0], "name");
                if (name.Length > 0) return (name, $"https://github.com/{repo}/tags", null);
            }

        // gradle.properties fallback — many small mod repos never tag, so their live version only lives in-tree
        var branch = "main";
        using (var meta = await HttpJson.GetAsync($"https://api.github.com/repos/{repo}", headers, ct))
            if (meta is not null && Str(meta.RootElement, "default_branch") is { Length: > 0 } db) branch = db;

        var raw = await HttpJson.GetStringAsync($"https://raw.githubusercontent.com/{repo}/{branch}/gradle.properties", ct: ct);
        if (raw is not null)
        {
            var m = Regex.Match(raw, @"(?m)^\s*mod_version\s*=\s*(.+?)\s*$");
            if (m.Success) return (m.Groups[1].Value.Trim(), $"https://github.com/{repo}/blob/HEAD/gradle.properties", null);
        }
        return (null, null, "no release, tag, or gradle mod_version (or GitHub rate-limited — a token would help)");
    }

    // Modrinth: newest version for our MC + loader (the API returns newest-first when filtered).
    private static async Task<(string?, string?, string?)> ModrinthLatestAsync(string pid, string mc, string loader, CancellationToken ct)
    {
        var q = $"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}"
              + $"&game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { mc }))}";
        using var doc = await HttpJson.GetAsync($"{Mr}/project/{pid}/version?{q}", ct: ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            return (null, null, "no matching versions");
        var v = doc.RootElement[0];
        return (Str(v, "version_number"), $"https://modrinth.com/mod/{pid}/version/{Str(v, "id")}", null);
    }

    // CurseForge: newest file name for our MC + loader (reuses the update client's plumbing).
    private static async Task<(string?, string?, string?)> CurseForgeLatestAsync(string modId, AppSettings s, CancellationToken ct)
    {
        if (s.CurseForgeApiKey.Length == 0) return (null, null, "no CF key");
        if (!int.TryParse(modId, out var id)) return (null, null, "bad CF id");
        var cf = await CurseForgeClient.CheckAsync([id], s.CurseForgeApiKey, s.McVersion, s.Loader, ct);
        if (!cf.TryGetValue(id, out var m)) return (null, null, "mod not found");
        if (m.LatestFileId == 0) return (null, null, $"no {s.McVersion} {s.Loader} file");
        return (m.LatestFileName, null, null);
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
