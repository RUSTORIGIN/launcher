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

    // First browser_download_url whose URL ends with the given asset file name (case-insensitive, so a
    // rename that only changes letter case does not break self-update).
    public static string AssetUrl(string json, string assetName)
    {
        if (json == null) return null;
        var ms = Regex.Matches(json,
            "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]+?/" + Regex.Escape(assetName) + ")\"",
            RegexOptions.IgnoreCase);
        return ms.Count > 0 ? ms[0].Groups[1].Value : null;
    }

    // Parse the "<hex>  <name>" line for a file out of a SHA256SUMS.txt body (lowercase hex, or null).
    // The name must match exactly (a "*" binary-mode marker is allowed), so "evil-<name>" is not taken for <name>.
    public static string HashFromSums(string sums, string fileName)
    {
        if (sums == null) return null;
        foreach (string raw in sums.Split('\n'))
        {
            string[] p = raw.Trim().Split(new[] { ' ', '\t' }, 2);
            if (p.Length != 2) continue;
            string name = p[1].Trim().TrimStart('*');
            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(p[0], "^[0-9a-fA-F]{64}$"))
                return p[0].ToLowerInvariant();
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

    // ---------- release signature ----------
    // A release's SHA256SUMS.txt only proves the exe matches the sums file next to it - whoever can publish a
    // release controls both. So the sums file is also SIGNED, offline, with the maintainer's RSA key
    // (scripts\sign_release.ps1), and the launcher only takes an update whose signature verifies against the
    // public key embedded in it (UpdatePublicKey= in its embedded launcher.cfg). The signed message binds
    // the release tag too, so an old signed release can't be re-published under a newer version:
    //   "rustorigin-launcher-release\n" + tag + "\n" + <exact bytes of SHA256SUMS.txt>
    // Signature: RSA (>= 3072-bit key) PKCS#1 v1.5 over SHA-256, base64, in the asset SHA256SUMS.txt.sig.
    public const string SignatureAsset = "SHA256SUMS.txt.sig";
    public const int MinKeyBits = 3072;

    public static byte[] SignedMessage(string tag, byte[] sums)
    {
        byte[] head = System.Text.Encoding.UTF8.GetBytes("rustorigin-launcher-release\n" + (tag ?? "") + "\n");
        var msg = new byte[head.Length + (sums == null ? 0 : sums.Length)];
        Buffer.BlockCopy(head, 0, msg, 0, head.Length);
        if (sums != null) Buffer.BlockCopy(sums, 0, msg, head.Length, sums.Length);
        return msg;
    }

    // "rsa:<base64 modulus>:<base64 exponent>" (what scripts\new_signing_key.ps1 prints), or null when malformed
    // or shorter than MinKeyBits.
    public static System.Security.Cryptography.RSAParameters? ParsePublicKey(string key)
    {
        try
        {
            string[] p = (key ?? "").Trim().Split(':');
            if (p.Length != 3 || p[0] != "rsa") return null;
            byte[] mod = Convert.FromBase64String(p[1]), exp = Convert.FromBase64String(p[2]);
            if (mod.Length * 8 < MinKeyBits || exp.Length == 0 || exp.Length > 8) return null;
            return new System.Security.Cryptography.RSAParameters { Modulus = mod, Exponent = exp };
        }
        catch { return null; }
    }

    // True only when sigBase64 is a valid signature by publicKey over SignedMessage(tag, sums). Never throws.
    public static bool VerifyRelease(string publicKey, string tag, byte[] sums, string sigBase64)
    {
        try
        {
            var k = ParsePublicKey(publicKey);
            if (k == null || string.IsNullOrEmpty(tag) || sums == null || sums.Length == 0 || string.IsNullOrEmpty(sigBase64)) return false;
            byte[] sig = Convert.FromBase64String(sigBase64.Trim());
            if (sig.Length != k.Value.Modulus.Length) return false;
            using (var rsa = System.Security.Cryptography.RSA.Create())
            {
                rsa.ImportParameters(k.Value);
                return rsa.VerifyData(SignedMessage(tag, sums), sig,
                    System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            }
        }
        catch { return false; }
    }
}
