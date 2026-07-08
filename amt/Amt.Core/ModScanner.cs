using System.IO;
using System.Text.RegularExpressions;

namespace Amt.Core;

/// Reads a mods folder into ModEntry rows — the C# replacement for the old Rust ants_core scan, no
/// interop. v1 works off the filename (name + a version guess); reading each jar's real metadata
/// (mods.toml) comes next; the update check itself now lives in <see cref="UpdateChecker"/>.
public static class ModScanner
{
    public static IReadOnlyList<ModEntry> Scan(string modsDir)
    {
        if (!Directory.Exists(modsDir))
            return [];

        var list = new List<ModEntry>();
        // *.jar = enabled, *.jar.disabled = off — the pack's convention
        foreach (var path in Directory.EnumerateFiles(modsDir, "*.jar*", SearchOption.TopDirectoryOnly))
        {
            var file = Path.GetFileName(path);
            var name = PrettyName(file);
            list.Add(new ModEntry
            {
                FileName = file,
                DisplayName = name,
                Version = VersionFromName(file),
                ModId = Slug(name),
                Enabled = file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase),
            });
        }
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    // A rough display name from the filename, up to where the version starts, until we read the jar's
    // real display name.
    private static string PrettyName(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file.Replace(".disabled", ""));
        var cut = Regex.Match(stem, @"[-_ ]\d");           // first "-1", "_2" … = the version boundary
        if (cut.Success) stem = stem[..cut.Index];
        return stem.Replace('_', ' ').Replace('-', ' ').Trim();
    }

    // First version-looking token in the filename (e.g. 2.3.0-b, 0.8.12).
    private static string VersionFromName(string file)
    {
        var m = Regex.Match(file, @"\d+\.\d+(\.\d+)*([-+][A-Za-z0-9.]+)?");
        return m.Success ? m.Value : "";
    }

    private static string Slug(string name) =>
        new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
