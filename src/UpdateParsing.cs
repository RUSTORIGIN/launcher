using System;
using System.Text.RegularExpressions;

// Pure, dependency-free parsing helpers for the launcher self-update (see UpdateCheckWorker in
// WpfLauncher.cs). Kept in their own class with no WPF dependency so scripts\test_updater_parsing.ps1
// can compile and unit-test them against sample GitHub data.
public static class UpdateParsing
{
    // Value of a top-level string field, e.g. JsonStr(json, "tag_name") -> "v1.0.1".
    public static string JsonStr(string json, string key)
    {
        if (json == null) return null;
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(.*?)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    // First browser_download_url whose URL ends with the given asset file name.
    public static string AssetUrl(string json, string assetName)
    {
        if (json == null) return null;
        var ms = Regex.Matches(json,
            "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+?/" + Regex.Escape(assetName) + ")\"");
        return ms.Count > 0 ? ms[0].Groups[1].Value : null;
    }

    // Parse the "<hex>  <name>" line for a file out of a SHA256SUMS.txt body (lowercase hex, or null).
    public static string HashFromSums(string sums, string fileName)
    {
        if (sums == null) return null;
        foreach (string raw in sums.Split('\n'))
        {
            string ln = raw.Trim();
            if (ln.Length == 0) continue;
            if (ln.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                string hash = ln.Split(new[] { ' ' }, 2)[0].Trim();
                if (hash.Length == 64) return hash.ToLowerInvariant();
            }
        }
        return null;
    }

    // True if the release tag (e.g. "v1.0.1") is a newer version than the running build
    // (e.g. "1.0.0.0"). Compares Major.Minor.Build only; bad/empty input returns false.
    public static bool IsNewer(string current, string tag)
    {
        Version cur, lat;
        if (!Version.TryParse((current ?? "").Trim(), out cur)) return false;
        if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out lat)) return false;
        return Norm(lat) > Norm(cur);
    }

    static Version Norm(Version v) { return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build); }
}
