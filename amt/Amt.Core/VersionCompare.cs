using System.Text.RegularExpressions;

namespace Amt.Core;

/// A messy mod-version string parsed into a comparable key: the dotted numbers, a pre-release RANK (alpha &lt;
/// beta &lt; rc &lt; release &lt; hotfix), and the qualifier's number (e.g. the 2 in "beta.2").
public readonly record struct ParsedVersion(int[] Numbers, int Rank, int QualifierNumber);

/// Best-effort comparison of the deliberately-messy version strings mods ship. No parser is perfect, so the
/// design stance (from the fork watcher) is: when a comparison is UNCERTAIN, say so (null / "review") rather than
/// ever silently claim "up to date" — a false "you're current" is the dangerous error, a false "check this" only
/// costs a glance. 1:1 port of the verified Python `parse_version` / `compare_versions` / `classify`.
public static class VersionCompare
{
    // Rank of a pre-release qualifier: a plain release (3) is newer than its alpha/beta/rc; a hotfix/patch (4) is
    // newer than the plain release of the same numbers; snapshot is the oldest.
    private static int PreRank(string q) => q switch
    {
        "snapshot" => -1,
        "alpha" => 0,
        "beta" => 1,
        "rc" or "pre" => 2,
        "hotfix" or "patch" => 4,
        _ => 3,
    };

    /// Parse a raw version string, or null if no version core is found. Strips only UNAMBIGUOUS noise (`+build`
    /// metadata and an `mcX.Y.Z` marker) — a bare "1.21.1" is left intact because for some mods that IS the whole
    /// version, and silently losing it would be worse than a slightly-noisy compare.
    public static ParsedVersion? Parse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var t = s.ToLowerInvariant().Trim();
        t = Regex.Replace(t, @"\+[0-9a-z.\-]*$", "");   // drop +build metadata (0.8.12-alpha.2+mc1.21.1 -> …alpha.2)
        t = Regex.Replace(t, @"mc\d+(?:\.\d+)+", "");    // drop an explicit mcX.Y.Z marker

        var m = Regex.Match(t, @"\d+(?:\.\d+)+");         // first dotted-number run = the version core
        if (!m.Success) return null;
        var nums = m.Value.Split('.').Select(int.Parse).ToArray();

        // The qualifier must IMMEDIATELY follow the core (^-anchored on the tail) so a stray letter in a trailing
        // word like "-fabric" can't be misread as an alpha tag; full qualifier words only, no bare a/b aliases.
        var tail = t[(m.Index + m.Length)..];
        var pm = Regex.Match(tail, @"^[-_.]?(alpha|beta|rc|pre|snapshot|hotfix|patch)\.?-?(\d+)?");
        if (!pm.Success) return new ParsedVersion(nums, 3, 0);
        var pnum = pm.Groups[2].Success && pm.Groups[2].Value.Length > 0 ? int.Parse(pm.Groups[2].Value) : 0;
        return new ParsedVersion(nums, PreRank(pm.Groups[1].Value), pnum);
    }

    /// Compare two raw version strings: -1 (a &lt; b), 0 (equal), 1 (a &gt; b), or null when uncertain (either
    /// side unparseable). Numbers first (zero-padded to equal length), then the pre-release rank, then its number.
    public static int? Compare(string? a, string? b)
    {
        if (Parse(a) is not { } pa || Parse(b) is not { } pb) return null;

        var cmp = CompareNumbers(pa.Numbers, pb.Numbers);
        if (cmp != 0) return cmp;
        if (pa.Rank != pb.Rank) return pa.Rank < pb.Rank ? -1 : 1;
        if (pa.QualifierNumber != pb.QualifierNumber) return pa.QualifierNumber < pb.QualifierNumber ? -1 : 1;
        return 0;
    }

    /// Turn (our fork base, upstream latest) into a verdict for the Forks report. A false "current" is the
    /// dangerous error, so anything unrankable is flagged "review" rather than assumed fine.
    public static string Classify(string? forkBase, string? upstreamLatest)
    {
        if (string.IsNullOrEmpty(upstreamLatest)) return "no-upstream";   // couldn't fetch upstream at all
        if (string.IsNullOrEmpty(forkBase)) return "set-base";            // we don't know what we forked from
        return Compare(forkBase, upstreamLatest) switch
        {
            null => "review",     // shapes we can't rank -> a human looks
            < 0 => "UPDATE",      // upstream newer than our base -> re-port candidate
            0 => "current",
            _ => "ahead",         // our base parses newer than upstream (odd -> check)
        };
    }

    // Lexicographic compare of two number runs, zero-padded to equal length (1.21 vs 1.21.1 -> 1.21.0 vs 1.21.1).
    private static int CompareNumbers(int[] a, int[] b)
    {
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }
}
