using System.IO;
using System.Text.Json;

namespace Amt.Core;

/// One mod as CurseForge's own manifest (`minecraftinstance.json`) records it. This is the RICH identity the
/// filename scan can't give us: the CurseForge project + file ids (so we can ask CF for the newest build), the
/// real display name, and CF's cached "latest file" (an offline fallback when the API is unreachable / keyless).
public sealed class InstalledAddon
{
    public string Name { get; init; } = "";
    public int CfProjectId { get; init; }        // addonID — the CurseForge project
    public int CfFileId { get; init; }           // installedFile.id — the exact installed file
    public string FileName { get; init; } = "";  // the installed file's name
    public string OnDisk { get; init; } = "";    // the jar's actual name in mods/ (used to key back to a scan row)
    public bool IsModified { get; init; }        // CF flags a hand-edited jar — we surface it, never auto-overwrite
    public string WebUrl { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public int CachedLatestFileId { get; init; }         // CF's cached newest file id (offline "is there an update")
    public string CachedLatestFileName { get; init; } = "";

    /// CF category ids for this addon, PRIMARY FIRST — mapped to sidebar buckets via the official id→name list
    /// (<see cref="CurseForgeClient.CategoriesAsync"/>) so mods get their real tags instead of a keyword guess.
    public IReadOnlyList<int> CategoryIds { get; init; } = [];

    /// The mod's CF avatar thumbnail (forgecdn URL) and primary author — both ride the manifest for free, so
    /// the installed table gets icons + an AUTHOR column with no API key and no extra network calls.
    public string ThumbnailUrl { get; init; } = "";
    public string PrimaryAuthor { get; init; } = "";
}

/// Reads CurseForge's `minecraftinstance.json` into <see cref="InstalledAddon"/> rows. A direct port of the
/// verified Python `read_instance_mods` — same field reads, same fallbacks. Returns an empty list (never throws)
/// if the file is missing or malformed, so a bad/absent manifest degrades to "Modrinth-only" rather than crashing.
public static class InstanceReader
{
    public static IReadOnlyList<InstalledAddon> Read(string instanceJsonPath)
    {
        var list = new List<InstalledAddon>();
        if (instanceJsonPath.Length == 0 || !File.Exists(instanceJsonPath)) return list;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(instanceJsonPath)); }
        catch { return list; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("installedAddons", out var addons) ||
                addons.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var a in addons.EnumerateArray())
            {
                var f = a.TryGetProperty("installedFile", out var file) && file.ValueKind == JsonValueKind.Object
                    ? file : default;
                var cached = a.TryGetProperty("latestFile", out var lf) && lf.ValueKind == JsonValueKind.Object
                    ? lf : default;

                // name: the addon name, else the file name, else a placeholder (mirrors the Python fallbacks)
                var name = Str(a, "name");
                if (name.Length == 0) name = Str(f, "fileName");
                if (name.Length == 0) name = "?";

                // the on-disk jar name — the addon's own field first, then the file's (port of the Python `onDisk`)
                var onDisk = Str(a, "fileNameOnDisk");
                if (onDisk.Length == 0) onDisk = Str(f, "fileNameOnDisk");
                if (onDisk.Length == 0) onDisk = Str(f, "fileName");

                var fileName = Str(f, "fileNameOnDisk");
                if (fileName.Length == 0) fileName = Str(f, "fileName");

                // CF category ids, reordered so primaryCategoryId leads — the first mappable id wins the bucket
                var cats = new List<int>();
                if (a.TryGetProperty("categoryIds", out var ci) && ci.ValueKind == JsonValueKind.Array)
                    foreach (var c in ci.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) && n != 0)
                            cats.Add(n);
                var prim = Int(a, "primaryCategoryId");
                if (prim != 0) { cats.Remove(prim); cats.Insert(0, prim); }

                list.Add(new InstalledAddon
                {
                    Name = name,
                    CfProjectId = Int(a, "addonID"),
                    CfFileId = Int(f, "id"),
                    FileName = fileName,
                    OnDisk = onDisk,
                    IsModified = Bool(a, "isModified"),
                    WebUrl = Str(a, "webSiteURL"),
                    GameVersion = Str(f, "gameVersion"),
                    CachedLatestFileId = Int(cached, "id"),
                    CachedLatestFileName = Str(cached, "fileName"),
                    CategoryIds = cats,
                    ThumbnailUrl = Str(a, "thumbnailUrl"),
                    PrimaryAuthor = Str(a, "primaryAuthor"),
                });
            }
        }
        return list;
    }

    // small tolerant readers — a missing/wrong-typed property yields "" / 0 / false rather than throwing
    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
}
