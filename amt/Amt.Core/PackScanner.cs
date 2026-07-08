using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amt.Core;

/// One data/resource pack as found on disk.
public sealed class PackEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";       // "resourcepacks" | "paxi" | "world: <name>"
    public string Description { get; init; } = "";  // pack.mcmeta description (best-effort)
    public int PackFormat { get; init; }            // 0 = unreadable
    public long SizeBytes { get; init; }            // folders: summed lazily is too slow — 0 for folders
    public bool IsZip { get; init; }
    public bool Active { get; init; }               // resource packs only: listed in options.txt resourcePacks
    public int ActiveOrder { get; init; } = -1;     // position in that list (higher = wins overlaps)
}

/// Folder scans for the Data packs + Resource packs pages. Read-only by design: MC silently rewrites its
/// resourcePacks list when it disagrees (see the pack's recovery notes), so v1 OBSERVES and never edits.
/// Every reader is failure-tolerant — an unreadable zip/mcmeta just means fewer details on the row.
public static class PackScanner
{
    /// resourcepacks/ (zips + folders), with active-state + order parsed from options.txt.
    public static IReadOnlyList<PackEntry> ScanResourcePacks(string instancePath)
    {
        var list = new List<PackEntry>();
        var dir = System.IO.Path.Combine(instancePath, "resourcepacks");
        if (instancePath.Length == 0 || !Directory.Exists(dir)) return list;

        // options.txt: resourcePacks:["vanilla","file/Pack.zip",...] — order is priority (later wins)
        var active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = System.IO.Path.Combine(instancePath, "options.txt");
            if (File.Exists(options))
            {
                var line = File.ReadLines(options).FirstOrDefault(l => l.StartsWith("resourcePacks:"));
                if (line is not null)
                {
                    var names = JsonSerializer.Deserialize<List<string>>(line["resourcePacks:".Length..]) ?? [];
                    for (var i = 0; i < names.Count; i++)
                    {
                        var n = names[i];
                        if (n.StartsWith("file/")) n = n["file/".Length..];
                        active[n] = i;
                    }
                }
            }
        }
        catch { /* unreadable options.txt — everything just shows as inactive */ }

        foreach (var path in Directory.EnumerateFileSystemEntries(dir))
        {
            var isZip = File.Exists(path);
            var name = System.IO.Path.GetFileName(path);
            // a pack is a folder or a .zip — loose files in the folder (readme, configs) aren't packs
            if (isZip && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var (desc, fmt) = ReadMcmeta(path, isZip);
            list.Add(new PackEntry
            {
                Name = name,
                Path = path,
                Source = "resourcepacks",
                Description = desc,
                PackFormat = fmt,
                SizeBytes = isZip ? SafeLength(path) : 0,
                IsZip = isZip,
                Active = active.ContainsKey(name),
                ActiveOrder = active.GetValueOrDefault(name, -1),
            });
        }
        // active packs first, in their priority order (highest priority = last in MC's list = shown first)
        return list.OrderByDescending(p => p.Active).ThenByDescending(p => p.ActiveOrder)
                   .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Data packs from every source this instance uses: Paxi's global folder + each world's datapacks/.
    public static IReadOnlyList<PackEntry> ScanDataPacks(string instancePath)
    {
        var list = new List<PackEntry>();
        if (instancePath.Length == 0) return list;

        void ScanDir(string dir, string source)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                var isZip = File.Exists(path);
                var name = System.IO.Path.GetFileName(path);
                if (!isZip && name.StartsWith('.')) continue;
                // loose files (Paxi's datapack_load_order.json etc.) aren't packs — only folders + zips are
                if (isZip && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var (desc, fmt) = ReadMcmeta(path, isZip);
                list.Add(new PackEntry
                {
                    Name = name, Path = path, Source = source, Description = desc,
                    PackFormat = fmt, SizeBytes = isZip ? SafeLength(path) : 0, IsZip = isZip,
                });
            }
        }

        ScanDir(System.IO.Path.Combine(instancePath, "config", "paxi", "datapacks"), "paxi");
        var saves = System.IO.Path.Combine(instancePath, "saves");
        if (Directory.Exists(saves))
            foreach (var world in Directory.EnumerateDirectories(saves))
                ScanDir(System.IO.Path.Combine(world, "datapacks"), $"world: {System.IO.Path.GetFileName(world)}");

        return list.OrderBy(p => p.Source).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // pack.mcmeta {"pack":{"description":..., "pack_format":...}} from a zip or a folder. The description can
    // be a string OR a text component (object/array) — flattened to plain text either way.
    private static (string Desc, int Format) ReadMcmeta(string path, bool isZip)
    {
        try
        {
            string? json = null;
            if (isZip)
            {
                if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ("", 0);
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("pack.mcmeta");
                if (entry is null) return ("", 0);
                using var r = new StreamReader(entry.Open());
                json = r.ReadToEnd();
            }
            else
            {
                var f = System.IO.Path.Combine(path, "pack.mcmeta");
                if (File.Exists(f)) json = File.ReadAllText(f);
            }
            if (json is null) return ("", 0);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("pack", out var pack)) return ("", 0);
            var fmt = pack.TryGetProperty("pack_format", out var pf) && pf.ValueKind == JsonValueKind.Number
                ? pf.GetInt32() : 0;
            var desc = pack.TryGetProperty("description", out var d) ? FlattenText(d) : "";
            // strip § colour codes — they're noise in a plain-text row
            desc = Regex.Replace(desc, "§.", "");
            return (desc.Trim(), fmt);
        }
        catch { return ("", 0); }
    }

    private static string FlattenText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(FlattenText)),
        JsonValueKind.Object => e.TryGetProperty("text", out var t) ? FlattenText(t) : "",
        _ => "",
    };

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
