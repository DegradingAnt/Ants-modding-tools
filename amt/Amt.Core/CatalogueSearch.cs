using System.Text.Json;

namespace Amt.Core;

/// One search result from the full mod catalogue. The same mod can live on both sites, so a hit may carry both a
/// Modrinth identity and (annotated during the merge) a CurseForge one.
public sealed class CatalogueHit
{
    public string Title { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public long Downloads { get; init; }
    public string IconUrl { get; init; } = "";
    public string ModrinthId { get; init; } = "";
    public string ModrinthUrl { get; init; } = "";
    public int CfId { get; set; }             // set during the merge when the same mod is also on CurseForge
    public string CfUrl { get; set; } = "";

    public bool OnModrinth => ModrinthUrl.Length > 0;
    public bool OnCurseForge => CfId != 0 || CfUrl.Length > 0;
}

/// Searches the whole mod catalogue across BOTH sites and returns one merged, deduped list — this is what the
/// Browse tab shows. Modrinth is the DRIVER (keyless, good relevance); CurseForge results are folded in and, when
/// they're the same mod (matched by normalised slug/title), annotate the existing Modrinth hit rather than
/// duplicating it. CF-only mods are appended. If there's no CF key, CF search is skipped and it's Modrinth-only.
/// 1:1 port of the Python `mr_search` / `cf_search` / `catalogue_search`.
public static class CatalogueSearch
{
    private const string Mr = "https://api.modrinth.com/v2";
    private const string Cf = "https://api.curseforge.com";

    public static async Task<IReadOnlyList<CatalogueHit>> SearchAsync(
        string query, AppSettings s, CancellationToken ct = default)
    {
        if (query.Trim().Length == 0) return [];
        var mr = await ModrinthSearchAsync(query, s.McVersion, s.Loader, ct);
        var cf = await CurseForgeSearchAsync(query, s.CurseForgeApiKey, s.McVersion, ct);

        // Modrinth drives; index its hits so a CF hit for the same mod annotates instead of duplicating.
        var bySlug = new Dictionary<string, CatalogueHit>();
        var byTitle = new Dictionary<string, CatalogueHit>();
        foreach (var m in mr)
        {
            bySlug.TryAdd(Norm(m.Slug.Length > 0 ? m.Slug : m.Title), m);
            byTitle.TryAdd(Norm(m.Title), m);
        }

        var merged = new List<CatalogueHit>(mr);
        foreach (var c in cf)
        {
            var hit = bySlug.GetValueOrDefault(Norm(c.Slug.Length > 0 ? c.Slug : c.Title))
                   ?? byTitle.GetValueOrDefault(Norm(c.Title));
            if (hit is not null) { hit.CfId = c.CfId; hit.CfUrl = c.CfUrl; }   // same mod, both sites — annotate
            else merged.Add(c);                                               // CF-only — append after the MR list
        }
        return merged;
    }

    private static async Task<List<CatalogueHit>> ModrinthSearchAsync(string query, string mc, string loader, CancellationToken ct)
    {
        var facets = JsonSerializer.Serialize(new[]
        {
            new[] { "project_type:mod" }, new[] { "versions:" + mc }, new[] { "categories:" + loader },
        });
        var url = $"{Mr}/search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(facets)}&limit=40&index=relevance";
        using var doc = await HttpJson.GetAsync(url, ct: ct);
        var list = new List<CatalogueHit>();
        if (doc is null || !doc.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var h in hits.EnumerateArray())
        {
            var slug = Str(h, "slug");
            list.Add(new CatalogueHit
            {
                Title = Str(h, "title"),
                Slug = slug,
                Author = Str(h, "author"),
                Description = Trim160(Str(h, "description")),
                Downloads = Long(h, "downloads"),
                IconUrl = Str(h, "icon_url"),
                ModrinthId = Str(h, "project_id"),
                ModrinthUrl = slug.Length > 0 ? $"https://modrinth.com/mod/{slug}" : "",
            });
        }
        return list;
    }

    private static async Task<List<CatalogueHit>> CurseForgeSearchAsync(string query, string apiKey, string mc, CancellationToken ct)
    {
        var list = new List<CatalogueHit>();
        if (apiKey.Length == 0) return list;   // CF search needs a key (and is gated for some personal keys → may 403)
        // gameId 432 = Minecraft, classId 6 = Mods; sortField 2 = popularity
        var url = $"{Cf}/v1/mods/search?gameId=432&classId=6&searchFilter={Uri.EscapeDataString(query)}"
                + $"&gameVersion={Uri.EscapeDataString(mc)}&pageSize=40&sortField=2&sortOrder=desc";
        using var doc = await HttpJson.GetAsync(url, new[] { ("x-api-key", apiKey) }, ct);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var m in data.EnumerateArray())
        {
            var author = "";
            if (m.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
                foreach (var a in authors.EnumerateArray()) { author = Str(a, "name"); break; }
            var icon = m.TryGetProperty("logo", out var logo) ? Str(logo, "thumbnailUrl") : "";
            var cfUrl = m.TryGetProperty("links", out var links) ? Str(links, "websiteUrl") : "";

            list.Add(new CatalogueHit
            {
                Title = Str(m, "name"),
                Slug = Str(m, "slug"),
                Author = author,
                Description = Trim160(Str(m, "summary")),
                Downloads = Long(m, "downloadCount"),
                IconUrl = icon,
                CfId = Int(m, "id"),
                CfUrl = cfUrl,
            });
        }
        return list;
    }

    // --- helpers -------------------------------------------------------------------------------------------
    private static string Norm(string s)   // lowercase, letters+digits only — a slug/title match key
    {
        var chars = s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static string Trim160(string s) => s.Length <= 160 ? s : s[..160];

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
