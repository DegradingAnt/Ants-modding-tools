using System.IO;
using System.Text.Json;

namespace Amt.Core;

/// One cached row of the last completed scan — just what the table needs to look alive at launch.
public sealed class CachedMod
{
    public string File { get; set; } = "";
    public string Latest { get; set; } = "";
    public string Status { get; set; } = "";
    public bool HasUpdate { get; set; }
    public string Env { get; set; } = "";   // client/server/both — cached so the ENV column fills instantly
}

/// Persists the LAST completed scan so the app opens showing the last-known state immediately (author call:
/// "show the last known info before updating it") — the live scan then overwrites it. Stored beside the
/// settings file; load never throws (a missing/corrupt cache just means a cold start).
public static class ScanCache
{
    private static string CacheFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AntsModdingTools", "lastscan.json");

    public static IReadOnlyDictionary<string, CachedMod> Load()
    {
        try
        {
            if (!File.Exists(CacheFile)) return new Dictionary<string, CachedMod>();
            var list = JsonSerializer.Deserialize<List<CachedMod>>(File.ReadAllText(CacheFile)) ?? [];
            var map = new Dictionary<string, CachedMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in list) if (m.File.Length > 0) map[m.File] = m;
            return map;
        }
        catch { return new Dictionary<string, CachedMod>(); }
    }

    public static void Save(IEnumerable<CachedMod> mods)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(mods.ToList()));
        }
        catch { /* cache is best-effort — a failed save just means a cold next start */ }
    }
}
