using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

// The launcher anti-cheat session (protocol v1, see docs\CLIENT_ANTICHEAT.md): AcSession builds, signs
// and parses the messages; AcSessionClient runs the session for one game launch on a background
// thread. No WPF here, and the client's HTTP call is replaceable, so scripts\test_ac_session.ps1 can
// test all of it on its own. Nothing here is secret: the protocol is public by design, and its value
// comes from the server checking it.
public static class AcSession
{
    public const int ProtocolVersion = 1;

    // Bounds the launcher applies to what the server asks for, so a bad reply can't make it spin or stall.
    public const int MinHeartbeat = 10, MaxHeartbeat = 300;
    public const int MinTtl = 30, MaxTtl = 900;
    public const int MaxSignals = 32, MaxSignalLength = 64;

    const ulong SteamId64Base = 76561197960265728UL;   // SteamID64 of account id 0 (individual, public universe)

    public sealed class Grant
    {
        public string Session;     // 32 lowercase hex
        public string KeyHex;      // 64 lowercase hex (32-byte HMAC key)
        public int Heartbeat;      // seconds between heartbeats
        public int Ttl;            // seconds a session stays fresh without a heartbeat
    }

    // Steam's registry stores the logged-in account as a 32-bit account id; the server sees SteamID64s.
    // Returns null for 0 (nobody logged in).
    public static string SteamId64FromAccountId(uint accountId)
    {
        if (accountId == 0) return null;
        return (SteamId64Base + accountId).ToString(CultureInfo.InvariantCulture);
    }

    // A SteamID64 of an individual account: 17 digits starting with 7656119.
    public static bool IsSteamId64(string s)
    {
        return s != null && Regex.IsMatch(s, "^7656119[0-9]{10}$");
    }

    // POST /v1/session body. Null when the SteamID or nonce is unusable, so a broken claim is never sent.
    public static string BuildSessionRequest(string steamId, string launcherVersion, string nonceHex, long unixTime)
    {
        if (!IsSteamId64(steamId) || !IsHex(nonceHex, 32)) return null;
        return "{\"v\":" + ProtocolVersion +
               ",\"steamid\":\"" + steamId + "\"" +
               ",\"launcher\":\"" + Escape(CleanVersion(launcherVersion)) + "\"" +
               ",\"nonce\":\"" + nonceHex.ToLowerInvariant() + "\"" +
               ",\"t\":" + unixTime.ToString(CultureInfo.InvariantCulture) + "}";
    }

    // POST /v1/heartbeat body. Signals are filtered (see CleanSignals) rather than escaped, so the body
    // never carries free text from the player's machine.
    public static string BuildHeartbeat(string session, long seq, long unixTime, bool gameRunning, IList<string> signals)
    {
        if (!IsHex(session, 32) || seq < 1) return null;
        var sb = new StringBuilder();
        sb.Append("{\"v\":").Append(ProtocolVersion)
          .Append(",\"session\":\"").Append(session.ToLowerInvariant()).Append('"')
          .Append(",\"seq\":").Append(seq.ToString(CultureInfo.InvariantCulture))
          .Append(",\"t\":").Append(unixTime.ToString(CultureInfo.InvariantCulture))
          .Append(",\"game\":").Append(gameRunning ? 1 : 0)
          .Append(",\"signals\":[");
        var clean = CleanSignals(signals);
        for (int i = 0; i < clean.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(clean[i]).Append('"');
        }
        return sb.Append("]}").ToString();
    }

    // Distinct, valid labels in their original order, capped at MaxSignals. Anything outside
    // [a-z0-9_.:-] or longer than MaxSignalLength is dropped, not repaired.
    public static List<string> CleanSignals(IList<string> signals)
    {
        var result = new List<string>();
        if (signals == null) return result;
        foreach (var s in signals)
        {
            if (s == null || s.Length == 0 || s.Length > MaxSignalLength) continue;
            if (!Regex.IsMatch(s, "^[a-z0-9_.:-]+$") || result.Contains(s)) continue;
            result.Add(s);
            if (result.Count == MaxSignals) break;
        }
        return result;
    }

    // X-AC-Sig: lowercase hex HMAC-SHA256 of the exact UTF-8 body bytes. Null for an unusable key.
    public static string Sign(string keyHex, string body)
    {
        if (!IsHex(keyHex, 64) || body == null) return null;
        using (var h = new HMACSHA256(FromHex(keyHex)))
            return ToHex(h.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    // Constant-time check of a signature (the server side does the same; this mirror is for tests).
    public static bool Verify(string keyHex, string body, string sigHex)
    {
        string expected = Sign(keyHex, body);
        if (expected == null || !IsHex(sigHex, 64)) return false;
        string given = sigHex.ToLowerInvariant();
        int diff = 0;
        for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ given[i];
        return diff == 0;
    }

    // Reply to POST /v1/session. Null unless the session id and key are well-formed; timings are clamped.
    public static Grant ParseSessionReply(string json)
    {
        string session = JsonStr(json, "session"), key = JsonStr(json, "key");
        if (!IsHex(session, 32) || !IsHex(key, 64)) return null;
        int hb = Clamp(JsonInt(json, "heartbeat", 30), MinHeartbeat, MaxHeartbeat);
        int ttl = Clamp(JsonInt(json, "ttl", 120), MinTtl, MaxTtl);
        if (ttl < 2 * hb) ttl = Math.Min(MaxTtl, 2 * hb);
        return new Grant { Session = session.ToLowerInvariant(), KeyHex = key.ToLowerInvariant(), Heartbeat = hb, Ttl = ttl };
    }

    // Reply to POST /v1/heartbeat: true when accepted. nextSeconds is the clamped interval the server
    // asks for (or fallback when absent); reason is the server's short code on rejection.
    public static bool ParseHeartbeatReply(string json, int fallback, out int nextSeconds, out string reason)
    {
        nextSeconds = Clamp(JsonInt(json, "next", fallback), MinHeartbeat, MaxHeartbeat);
        reason = JsonStr(json, "reason");
        return JsonInt(json, "ok", 0) == 1;
    }

    // 16 random bytes as hex, for the session request nonce.
    public static string NewNonceHex()
    {
        var b = new byte[16];
        using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(b);
        return ToHex(b);
    }

    public static bool IsHex(string s, int length)
    {
        return s != null && s.Length == length && Regex.IsMatch(s, "^[0-9a-fA-F]+$");
    }

    // The session endpoint must be https. Plain http is only accepted for an IP literal on this machine or
    // a private network (127/8, 10/8, 172.16/12, 192.168/16, ::1, fc00::/7) - a dev server, e.g. in WSL -
    // so a released config can never send the session key in the clear over the internet.
    public static bool IsAllowedEndpoint(string url)
    {
        Uri u;
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u) || u.Host.Length == 0) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        if (u.Scheme != Uri.UriSchemeHttp) return false;
        IPAddress ip;
        if (!IPAddress.TryParse(u.Host.Trim('[', ']'), out ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4) return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        return (b[0] & 0xFE) == 0xFC;
    }

    // SteamID64 of the account logged in to the Steam client on this PC (Steam keeps the 32-bit account id
    // in HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser). Null when Steam is not running or nobody is
    // logged in. It is only a claim - the server checks it against the SteamID that actually joins.
    public static string ReadSteamIdFromRegistry()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                object v = k == null ? null : k.GetValue("ActiveUser");
                return v is int ? SteamId64FromAccountId(unchecked((uint)(int)v)) : null;
            }
        }
        catch { return null; }
    }

    public static long UnixNow()
    {
        return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
    }

    static string CleanVersion(string v)
    {
        if (string.IsNullOrEmpty(v)) return "0";
        var m = Regex.Match(v, "^[0-9A-Za-z.+-]{1,32}");
        return m.Success ? m.Value : "0";
    }

    static string Escape(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static string JsonStr(string json, string key)
    {
        if (json == null) return null;
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"\\\\]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static int JsonInt(string json, string key, int fallback)
    {
        if (json == null) return fallback;
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]{1,9})(?![0-9])");
        int v;
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) ? v : fallback;
    }

    static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

    static byte[] FromHex(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(hex.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return b;
    }

    static string ToHex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}

// Runs the session for one game launch on a background thread: opens a session, then sends a heartbeat
// until the game exits or Stop() is called. It never throws and never blocks the caller - every failure
// is logged and retried, because a session problem must never keep anyone from playing.
public sealed class AcSessionClient
{
    // One POST: the reply body (also for an HTTP error status, which carries the reason), or null when no
    // reply came back at all. Replaceable so the loop can be tested without a server.
    public delegate string Poster(string url, string body, string signature);

    readonly string baseUrl, launcherVersion;
    readonly Func<string> steamId;
    readonly Func<bool> gameRunning;
    readonly Action<string> log;
    readonly Poster post;
    readonly ManualResetEvent stopped = new ManualResetEvent(false);
    Thread thread;

    public int Opened, HeartbeatsOk, HeartbeatsRejected, NetworkErrors;
    public int RetryDelayMs = 5000;      // first retry after a failed open; doubles up to MaxRetryDelayMs
    public int MaxRetryDelayMs = 60000;
    public int SecondMs = 1000;          // one second of the server's heartbeat interval (tests shorten it)

    public AcSessionClient(string baseUrl, string launcherVersion, Func<string> steamId, Func<bool> gameRunning, Action<string> log, Poster post)
    {
        this.baseUrl = (baseUrl ?? "").TrimEnd('/');
        this.launcherVersion = launcherVersion;
        this.steamId = steamId;
        this.gameRunning = gameRunning;
        this.log = log ?? (s => { });
        this.post = post ?? HttpPost;
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "ac-session" };
        thread.Start();
    }

    public void Stop() { stopped.Set(); }

    // True once the thread has ended (tests).
    public bool Wait(int ms) { return thread == null || thread.Join(ms); }

    // Sleeps, or returns false at once when Stop() is called.
    bool Sleep(int ms) { return !stopped.WaitOne(Math.Max(0, ms)); }

    void Run()
    {
        try { Loop(); }
        catch (Exception e) { log("session client stopped: " + e.Message); }
    }

    void Loop()
    {
        AcSession.Grant grant = null;
        long seq = 0;
        int retry = RetryDelayMs;
        bool waitingForSteam = false;
        while (!stopped.WaitOne(0) && gameRunning())
        {
            if (grant == null)
            {
                string body = AcSession.BuildSessionRequest(steamId(), launcherVersion, AcSession.NewNonceHex(), AcSession.UnixNow());
                if (body == null)
                {
                    if (!waitingForSteam) log("no Steam account logged in yet - waiting for Steam");
                    waitingForSteam = true;
                    if (!Sleep(RetryDelayMs)) return;
                    continue;
                }
                string reply = post(baseUrl + "/v1/session", body, null);
                grant = AcSession.ParseSessionReply(reply);
                if (grant == null)
                {
                    if (reply == null) Interlocked.Increment(ref NetworkErrors);
                    log(reply == null ? "session service unreachable - retrying" : "session refused: " + Short(reply));
                    if (!Sleep(retry)) return;
                    retry = Math.Min(retry * 2, MaxRetryDelayMs);
                    continue;
                }
                seq = 0; retry = RetryDelayMs;
                Interlocked.Increment(ref Opened);
                log("session opened, heartbeat every " + grant.Heartbeat + " s");
            }

            seq++;
            string hb = AcSession.BuildHeartbeat(grant.Session, seq, AcSession.UnixNow(), gameRunning(), null);
            string r = post(baseUrl + "/v1/heartbeat", hb, AcSession.Sign(grant.KeyHex, hb));
            int next; string reason;
            if (r == null)
            {
                // Keep the session: the server forgives missed heartbeats until its ttl runs out, then answers
                // "expired" and a new session is opened.
                Interlocked.Increment(ref NetworkErrors);
                if (!Sleep(Math.Min(grant.Heartbeat, 15) * SecondMs)) return;
            }
            else if (AcSession.ParseHeartbeatReply(r, grant.Heartbeat, out next, out reason))
            {
                Interlocked.Increment(ref HeartbeatsOk);
                if (!Sleep(next * SecondMs)) return;
            }
            else
            {
                Interlocked.Increment(ref HeartbeatsRejected);
                log("heartbeat refused (" + (reason ?? Short(r)) + ") - opening a new session");
                grant = null;
                if (!Sleep(RetryDelayMs)) return;
            }
        }
    }

    static string Short(string s)
    {
        s = Regex.Replace(s ?? "", "[^\\x20-\\x7E]", " ").Trim();
        return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
    }

    static string HttpPost(string url, string body, string signature)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = "RustoriginLauncher";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            if (signature != null) req.Headers["X-AC-Sig"] = signature;
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            using (var resp = req.GetResponse()) return ReadReply(resp);
        }
        catch (WebException e)
        {
            if (e.Response == null) return null;
            try { return ReadReply(e.Response); }
            catch { return null; }
            finally { e.Response.Close(); }
        }
        catch { return null; }
    }

    // At most 4 KB of a reply: the real ones are under 200 bytes.
    static string ReadReply(WebResponse resp)
    {
        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
        {
            var buf = new char[4096];
            int n = sr.ReadBlock(buf, 0, buf.Length);
            return new string(buf, 0, n);
        }
    }
}
