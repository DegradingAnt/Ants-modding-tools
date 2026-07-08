using System.Text.Json;

namespace Amt.Core;

/// The list of Minecraft RELEASE versions, for the Settings dropdown — fetched keyless from Mojang's own
/// launcher manifest so the list is always current, with a static fallback so the dropdown still has sensible
/// entries offline. The field stays writable in the UI, so a missing entry never blocks anyone.
public static class MojangVersions
{
    // fallback when Mojang is unreachable — recent releases, newest first (update occasionally; the live
    // fetch supersedes this whenever the network is up)
    private static readonly string[] Fallback =
    [
        "1.21.4", "1.21.3", "1.21.1", "1.21", "1.20.6", "1.20.4", "1.20.1", "1.19.4", "1.19.2", "1.18.2", "1.16.5", "1.12.2",
    ];

    public static async Task<IReadOnlyList<string>> ReleasesAsync(CancellationToken ct = default)
    {
        using var doc = await HttpJson.GetAsync("https://launchermeta.mojang.com/mc/game/version_manifest.json", ct: ct);
        if (doc is null || !doc.RootElement.TryGetProperty("versions", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Fallback;

        var list = new List<string>();
        foreach (var v in arr.EnumerateArray())
        {
            if (!v.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "release")
                continue;
            if (v.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s)
                list.Add(s);   // manifest is newest-first already
        }
        return list.Count > 0 ? list : Fallback;
    }
}
