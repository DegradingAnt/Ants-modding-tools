using System.IO;

namespace Amt.Core;

/// The outcome of one mod's update attempt — the UI shows these in its results summary.
public sealed class InstallResult
{
    public string Name { get; init; } = "";
    public string NewFile { get; init; } = "";
    public bool Ok { get; init; }
    public string Message { get; init; } = "";   // "updated …" / why it was skipped / the error
}

/// "Update all": download each newer build (sha1-gated) and swap it into mods/. Deliberate boundaries:
/// - The old jar is MOVED to a timestamped backup folder in the same step the new one lands — never left beside
///   it (two builds of one mod in mods/ breaks the game's boot), and never deleted (a run stays reversible by
///   moving the backup files back).
/// - A jar CurseForge flags as locally modified is SKIPPED — hand-patched work is never auto-overwritten.
/// - A failed download / hash mismatch leaves the installed jar untouched.
/// - Minecraft must be closed: Windows holds mod jars open while it runs, which surfaces here as a per-mod
///   "swap failed" result rather than anything corrupting.
public static class UpdateInstaller
{
    public static async Task<IReadOnlyList<InstallResult>> UpdateAllAsync(
        IEnumerable<ModStatus> mods, AppSettings s, Action<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<InstallResult>();
        var dl = new DownloadManager(s.DownloadDir);
        var backupDir = Path.Combine(s.DownloadDir, "backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var todo = mods.Where(m => m.HasUpdate).ToList();

        for (var i = 0; i < todo.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var m = todo[i];
            progress?.Invoke($"updating {i + 1}/{todo.Count}: {m.Name}");

            if (m.IsModified)
            {
                results.Add(new InstallResult { Name = m.Name, Ok = false, Message = "skipped — locally modified jar (never auto-overwritten)" });
                continue;
            }

            var (url, fileName, sha1, err) = await ResolveAsync(m, s, ct);
            if (url.Length == 0)
            {
                results.Add(new InstallResult { Name = m.Name, Ok = false, Message = err });
                continue;
            }

            var task = new DownloadTask { Name = m.Name, Url = url, FileName = fileName, ExpectedSha1 = sha1 };
            await dl.RunAsync(task);
            if (task.Status != "done")
            {
                results.Add(new InstallResult { Name = m.Name, NewFile = fileName, Ok = false, Message = task.Error });
                continue;
            }

            try
            {
                Directory.CreateDirectory(backupDir);
                var oldPath = Path.Combine(s.ModsDir, m.OnDisk);
                if (File.Exists(oldPath)) File.Move(oldPath, Path.Combine(backupDir, m.OnDisk));
                File.Move(Path.Combine(s.DownloadDir, fileName), Path.Combine(s.ModsDir, fileName), overwrite: true);
                results.Add(new InstallResult
                {
                    Name = m.Name, NewFile = fileName, Ok = true,
                    Message = task.Verified ? "updated (sha1 verified)" : "updated (source gave no hash to verify)",
                });
            }
            catch (Exception e)
            {
                results.Add(new InstallResult { Name = m.Name, NewFile = fileName, Ok = false, Message = $"swap failed: {e.Message} (is the game running?)" });
            }
        }
        return results;
    }

    // Where the newer build comes from, in order of trust: Modrinth's primary file when Modrinth has the update
    // (direct URL + sha1); the CF file endpoint when a key exists (URL + sha1); else the forgecdn edge URL rebuilt
    // from the CACHED file id + name — that path carries no hash, so it downloads unverified (flagged in the result).
    private static async Task<(string Url, string FileName, string Sha1, string Err)> ResolveAsync(
        ModStatus m, AppSettings s, CancellationToken ct)
    {
        if (m.MrHasUpdate && m.MrDownloadUrl.Length > 0)
        {
            var name = m.MrNewFileName.Length > 0 ? m.MrNewFileName : FileNameFromUrl(m.MrDownloadUrl);
            if (name.Length > 0) return (m.MrDownloadUrl, name, m.MrNewSha1, "");
        }
        if (m.CfHasUpdate && m.CfProjectId != 0 && m.CfLatestFileId != 0)
        {
            if (s.CurseForgeApiKey.Length > 0)
            {
                var f = await CurseForgeClient.GetFileAsync(m.CfProjectId, m.CfLatestFileId, s.CurseForgeApiKey, ct);
                if (f is not null) return (f.Url, f.FileName, f.Sha1, "");
            }
            if (m.CfLatestName.Length > 0)   // keyless: rebuild the edge URL (file names often contain spaces — escape)
            {
                var fid = m.CfLatestFileId.ToString();
                if (fid.Length > 4)
                    return ($"https://edge.forgecdn.net/files/{fid[..4]}/{fid[4..]}/{Uri.EscapeDataString(m.CfLatestName)}",
                            m.CfLatestName, "", "");
            }
        }
        return ("", "", "", "no downloadable source for the newer build");
    }

    private static string FileNameFromUrl(string url)
    {
        try { return Path.GetFileName(new Uri(url).LocalPath); }
        catch { return ""; }
    }
}
