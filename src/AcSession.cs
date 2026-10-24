using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
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
        public string ToolSpec;    // tool rules from the server ("1=autohotkey,autohotkeyu64;2=..."), or null - see AcSignals.ParseToolSpec
        public long MemFloorBytes; // phase-B floor from the server ("memfloormb"), in bytes; 0 = the server didn't send one
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
        return BuildSessionRequest(steamId, launcherVersion, nonceHex, unixTime, null);
    }

    // The same, with the PC's hardware id (see HardwareIdFromMachineGuid). An unusable id is left out
    // rather than failing the request: the id is review evidence, never a condition for a session.
    public static string BuildSessionRequest(string steamId, string launcherVersion, string nonceHex, long unixTime, string hardwareId)
    {
        if (!IsSteamId64(steamId) || !IsHex(nonceHex, 32)) return null;
        return "{\"v\":" + ProtocolVersion +
               ",\"steamid\":\"" + steamId + "\"" +
               ",\"launcher\":\"" + Escape(CleanVersion(launcherVersion)) + "\"" +
               ",\"nonce\":\"" + nonceHex.ToLowerInvariant() + "\"" +
               ",\"t\":" + unixTime.ToString(CultureInfo.InvariantCulture) +
               (IsHex(hardwareId, 64) ? ",\"hw\":\"" + hardwareId.ToLowerInvariant() + "\"" : "") + "}";
    }

    // Hardware id of this PC for the alt-account signal: lowercase hex SHA-256 of "rustorigin-hw-v1:" + the
    // Windows MachineGuid. The prefix makes it a Rustorigin-only value, so it can't be matched against the
    // raw MachineGuid other software may report. MachineGuid is random per Windows install, so two PCs never
    // share one; the SMBIOS UUID is not used because many boards ship a generic one, which would link
    // strangers. Null unless the input is a GUID.
    public static string HardwareIdFromMachineGuid(string machineGuid)
    {
        Guid g;
        if (string.IsNullOrEmpty(machineGuid) || !Guid.TryParseExact(machineGuid.Trim(), "D", out g) || g == Guid.Empty) return null;
        using (var sha = SHA256.Create())
            return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes("rustorigin-hw-v1:" + g.ToString("D"))));
    }

    // HardwareIdFromMachineGuid of HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (readable without admin
    // rights; read from the 64-bit registry view, which is where it lives even for a 32-bit process). Null
    // when it can't be read.
    public static string ReadHardwareId()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Default })
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var k = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    string id = HardwareIdFromMachineGuid(k == null ? null : k.GetValue("MachineGuid") as string);
                    if (id != null) return id;
                }
            }
            catch { }
        }
        return null;
    }

    // POST /v1/heartbeat body. Signals are filtered (see CleanSignals) rather than escaped, so the body
    // never carries free text from the player's machine.
    public static string BuildHeartbeat(string session, long seq, long unixTime, bool gameRunning, IList<string> signals)
    {
        return BuildHeartbeat(session, seq, unixTime, gameRunning, signals, null);
    }

    // The same, echoing a game-link code (see AcGameLog): 16 lowercase hex the server sent to this PC's game. An unusable
    // code is left out, never sent half-formed.
    public static string BuildHeartbeat(string session, long seq, long unixTime, bool gameRunning, IList<string> signals, string link)
    {
        if (!IsHex(session, 32) || seq < 1) return null;
        var sb = new StringBuilder();
        sb.Append("{\"v\":").Append(ProtocolVersion)
          .Append(",\"session\":\"").Append(session.ToLowerInvariant()).Append('"')
          .Append(",\"seq\":").Append(seq.ToString(CultureInfo.InvariantCulture))
          .Append(",\"t\":").Append(unixTime.ToString(CultureInfo.InvariantCulture))
          .Append(",\"game\":").Append(gameRunning ? 1 : 0);
        if (IsLinkCode(link)) sb.Append(",\"link\":\"").Append(link).Append('"');
        sb.Append(",\"signals\":[");
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
        // memfloormb: the phase-B floor in whole MB, server-sent like the tool list so the number stays off the client.
        // Absent or <= 0 means the server didn't enable phase B, and the client reports no memory label. Clamped so a
        // bad reply can't make the floor absurd.
        int memFloorMb = JsonInt(json, "memfloormb", 0);
        long memFloorBytes = memFloorMb > 0 ? (long)Math.Min(memFloorMb, 4096) * 1024 * 1024 : 0;
        return new Grant { Session = session.ToLowerInvariant(), KeyHex = key.ToLowerInvariant(), Heartbeat = hb, Ttl = ttl, ToolSpec = JsonStr(json, "tools"), MemFloorBytes = memFloorBytes };
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
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);   // not RNGCryptoServiceProvider: obsolete (an error) where CI runs the tests
        return ToHex(b);
    }

    // A game-link code: exactly 16 lowercase hex.
    public static bool IsLinkCode(string s)
    {
        return s != null && Regex.IsMatch(s, "^[0-9a-f]{16}$");
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

// Follows the game's own log (Player.log) for the game-link code the server sends to the connected client: a console
// line "rustorigin-link <16 hex>". Echoing it in a heartbeat shows the server that this session runs on the PC whose game
// it is talking to. Only that code is taken from the log; nothing else is read into memory beyond the line search, and
// nothing else leaves the PC. The game rewrites the file at every start, so a shorter file means "read from the start".
public sealed class AcGameLog
{
    public const int MaxReadPerPoll = 4 << 20;   // bytes read per poll at most (the log can grow fast while loading)
    static readonly Regex CodeLine = new Regex("^rustorigin-link ([0-9a-f]{16})\\s*$", RegexOptions.Multiline);

    readonly string path;
    long offset;
    string carry = "";   // an unfinished last line, kept for the next poll
    string latest;

    public AcGameLog(string path) { this.path = path; }

    // %USERPROFILE%\AppData\LocalLow\Facepunch Studios LTD\Rust\Player.log, where the Rust client writes its log.
    public static string DefaultPath()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(Path.Combine(Path.GetDirectoryName(local), "LocalLow"), Path.Combine("Facepunch Studios LTD", Path.Combine("Rust", "Player.log")));
    }

    // The newest code seen in the log so far, or null. Never throws: a missing or locked file just finds nothing.
    public string Poll()
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return latest;
            if (fi.Length < offset) { offset = 0; carry = ""; latest = null; }   // the game started again: a new log
            if (fi.Length == offset) return latest;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[(int)Math.Min(MaxReadPerPoll, fi.Length - offset)];
                int n = fs.Read(buf, 0, buf.Length);
                offset += n;
                string text = carry + Encoding.UTF8.GetString(buf, 0, n);
                int cut = text.LastIndexOf('\n');
                carry = cut < 0 ? text : text.Substring(cut + 1);
                if (carry.Length > 4096) carry = "";                            // no real line is that long
                foreach (Match m in CodeLine.Matches(cut < 0 ? "" : text.Substring(0, cut).Replace("\r", "")))
                    latest = m.Groups[1].Value;
            }
        }
        catch { }
        return latest;
    }
}

// Evidence labels for the heartbeat. Only labels ever leave the PC, never a process list or file contents:
//   tool:<id>               a running process matches tool rule <id>. The rules come from the server in
//                           the session reply, so the list of tools is not in this repo, the exe or the
//                           player's config, and the label is only a number.
//   client:modified:<file>  a game file listed in AcClientFile= differs from the hosted build
//   client:missing:<file>   that file is not there at all
// They are evidence for staff review on the server, never a reason to block anything here.
public static class AcSignals
{
    public sealed class ToolRule { public string Label; public string[] Names; }
    public sealed class FileRule { public string Path; public long Size; public string Sha256; public string Name; }

    public const int MaxToolRules = 64;

    // The "tools" field of the session reply: "<id>=<process>,<process>;<id>=..." with numeric ids and lower-case
    // process names without ".exe". Malformed rules are dropped one by one; a missing field gives no rules.
    public static List<ToolRule> ParseToolSpec(string spec)
    {
        var rules = new List<ToolRule>();
        if (string.IsNullOrEmpty(spec) || spec.Length > 8192) return rules;
        foreach (var part in spec.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string id = part.Substring(0, eq).Trim();
            if (!Regex.IsMatch(id, "^[0-9]{1,4}$")) continue;
            var names = new List<string>();
            foreach (var raw in part.Substring(eq + 1).Split(','))
            {
                string n = ProcessKey(raw);
                if (Regex.IsMatch(n, "^[a-z0-9_.+ -]{1,64}$") && !names.Contains(n)) names.Add(n);
            }
            if (names.Count > 0) rules.Add(new ToolRule { Label = "tool:" + id, Names = names.ToArray() });
            if (rules.Count == MaxToolRules) break;
        }
        return rules;
    }

    // AcClientFile=<path relative to the client folder>|<size>|<sha256>. Rooted paths and ".." are refused.
    public static FileRule ParseClientFile(string v)
    {
        var p = (v ?? "").Split('|');
        if (p.Length != 3) return null;
        string path = p[0].Trim().Replace('/', '\\');
        long size;
        if (path.Length == 0 || System.IO.Path.IsPathRooted(path) || path.Contains(":") || ("\\" + path + "\\").Contains("\\..\\")) return null;
        if (!long.TryParse(p[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out size) || !AcSession.IsHex(p[2].Trim(), 64)) return null;
        string name = Regex.Replace(System.IO.Path.GetFileName(path).ToLowerInvariant(), "[^a-z0-9_.-]", "_");
        if (name.Length > 48) name = name.Substring(0, 48);
        return new FileRule { Path = path, Size = size, Sha256 = p[2].Trim().ToLowerInvariant(), Name = name };
    }

    // Labels of the rules whose process names appear among the running ones (rule order, each once).
    public static List<string> MatchTools(IEnumerable<string> running, IList<ToolRule> rules)
    {
        var have = new HashSet<string>();
        if (running != null) foreach (var r in running) have.Add(ProcessKey(r));
        var labels = new List<string>();
        if (rules != null)
            foreach (var rule in rules)
                foreach (var n in rule.Names)
                    if (have.Contains(n)) { if (!labels.Contains(rule.Label)) labels.Add(rule.Label); break; }
        return labels;
    }

    // Labels for listed game files that are missing or differ from the expected size + SHA-256. Reads the
    // files with full sharing, so it works while the game has them open. A file that can't be read at
    // all is left out rather than reported (a locked file is not evidence of anything).
    public static List<string> CheckClientFiles(string clientDir, IList<FileRule> rules)
    {
        var labels = new List<string>();
        if (string.IsNullOrEmpty(clientDir) || rules == null) return labels;
        foreach (var r in rules)
        {
            string full = System.IO.Path.Combine(clientDir, r.Path);
            try
            {
                var fi = new FileInfo(full);
                if (!fi.Exists) { labels.Add("client:missing:" + r.Name); continue; }
                if (fi.Length != r.Size) { labels.Add("client:modified:" + r.Name); continue; }
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
                using (var sha = SHA256.Create())
                {
                    var sb = new StringBuilder(64);
                    foreach (byte x in sha.ComputeHash(fs)) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                    if (sb.ToString() != r.Sha256) labels.Add("client:modified:" + r.Name);
                }
            }
            catch { }
        }
        return labels;
    }

    // Names of the running processes (no paths, nothing else).
    public static List<string> RunningProcessNames()
    {
        var names = new List<string>();
        try { foreach (var p in Process.GetProcesses()) { try { names.Add(p.ProcessName); } catch { } finally { p.Dispose(); } } }
        catch { }
        return names;
    }

    static string ProcessKey(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s.EndsWith(".exe") ? s.Substring(0, s.Length - 4) : s;
    }
}

// Code loaded into the game (docs\AC_MODULE_SIGNAL.md, phase A): lists the modules of the RustClient.exe process the
// launcher started - read-only, as the same user, without a driver or admin rights; nothing is written, suspended or
// injected - and reports code that is neither part of the hosted client build, nor Windows, nor signed by a trusted
// publisher. As everywhere in the session, only labels leave the PC, never a path or the module list:
//   client:extra-module:<name>  a module in the game folder that is not in the hosted build's file list (AcClientFile=):
//                               the proxy-DLL case (version.dll, dxgi.dll ...). <name> is the file name, cleaned
//   module:unsigned:<32 hex>    a module from anywhere else without a valid Authenticode signature: the first 32 hex
//                               characters of its SHA-256, so the server can translate known ones and nothing else
//   module:missing-file         a loaded module whose file is no longer where Windows says it was loaded from (a DLL
//                               renamed away after injection); no name, no hash
//   module:scan-failed          the game process could not be opened at all (expected only when the game runs elevated)
//   debugger:attached           a debugger is attached to the game - a memory editor or trainer (Cheat Engine, a
//                               trainer) attaches as one; read-only, nothing is traced or changed
//   handle:external-rw:<32 hex> another process (not the game, not the launcher, not Windows, not signed) holds an
//                               open handle to the game with memory read/write/operation access: the external-cheat
//                               case that injects no module at all. The 32 hex are that program's SHA-256 prefix, so
//                               the server can translate known ones; its path and contents never leave the PC
// The last two look beyond the game's own modules (an external reader or debugger injects nothing, so phase A/B miss
// it). A program that reads the game's memory is judged like a module: Windows' own processes and trusted-signed
// programs (overlays, recorders, antivirus) are not reported, only unsigned ones, by hash.
// Listed game files are the file check's job (client:modified). Windows' own code folders (System32, SysWOW64, WinSxS,
// assembly, Microsoft.NET) are skipped: writing there needs admin rights, and their files are signed through catalogs,
// not in the file. The rest of %SystemRoot% is NOT skipped: Temp, Tasks and tracing are writable by any user. The first
// scan waits a minute (Unity loads plugins lazily); after that only modules not seen before are classified, so a scan
// costs almost nothing. Evidence for staff, never a block here.
public sealed class AcModules
{
    public const int DefaultFirstScanDelayMs = 60000;
    public const int MaxNameLength = 40;
    public const int HashPrefixLength = 32;
    public const int MaxLabels = 64;   // the heartbeat sends at most MaxSignals anyway; this bounds the memory
    public const string MemLargeLabel = "mem:private-exec:large";   // the phase-B label; sent once, then kept for the launch
    public const string DebuggerLabel = "debugger:attached";        // a debugger is attached to the game

    // Phase B (docs\AC_MODULE_SIGNAL.md): a manually mapped DLL - code an injector writes into the game's memory
    // without the Windows loader - never appears in the module list, so phase A can't see it. It does leave
    // executable memory that no loaded module accounts for. A clean game already has a little of that (an overlay
    // or recorder leaves small hook trampolines - measured at ~0.2 MB on the 2021-04 client, no single region over
    // ~0.06 MB), so the label is sent only when the total clears MemFloorBytes, where a whole mapped image would sit.
    // The floor is NOT a constant here: the server sends it in the session reply (like the tool list), so the exact
    // number stays off the client and out of this open-source repo; AcSessionClient copies it here each session.
    // 0 means the server didn't enable phase B, so no memory label is produced. The signal ships measure-only (the
    // panel keeps the label but opens no case), because legitimate software makes executable memory too.
    public long MemFloorBytes;

    // Replaceable, so the classifier can be tested with a fake module list, signature check and region list.
    public Func<int, List<string>> ListModules = ListProcessModules;
    public Func<string, bool> IsSigned = HasTrustedSignature;
    public Func<int, List<long>> ListAnonExecRegions = RealAnonExecRegions;   // sizes of exec regions no module backs
    public Func<int, bool> IsDebugged = RealIsDebugged;                       // a debugger attached to the game
    public Func<int, List<string>> ListExternalReaders = RealExternalReaders; // exes holding a r/w handle to the game
    public int FirstScanDelayMs = DefaultFirstScanDelayMs;
    public int Scans, Classified, MemScans, HandleScans;   // counters for tests and the log

    readonly string clientDir;              // the game folder, full path with a trailing separator, or null
    readonly HashSet<string> listed = new HashSet<string>();   // relative paths (lower case) of the hosted build's listed files
    readonly bool clientListComplete;
    readonly string[] systemRoots;
    readonly Action<string> log;
    readonly Stopwatch since = Stopwatch.StartNew();
    readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // path|size|mtime -> label or ""
    readonly List<string> labels = new List<string>();
    bool scanFailedLogged;

    // clientDir is the folder of the game exe; rules are the AcClientFile= lines; gameExe is the game's exe name
    // (LaunchExe). The extra-module label is only produced when the rules list the game's own exe: a list without
    // it (the three key files of older configs) is not a list of the build's code files, and flagging every
    // unlisted DLL of the game itself would be noise, not evidence.
    public AcModules(string clientDir, IList<AcSignals.FileRule> rules, string gameExe, Action<string> log)
    {
        this.log = log ?? (s => { });
        try { if (!string.IsNullOrEmpty(clientDir)) this.clientDir = WithSeparator(Path.GetFullPath(clientDir)); } catch { }
        string exe = Path.GetFileName(gameExe ?? "").ToLowerInvariant();
        bool complete = false;
        if (rules != null)
            foreach (var r in rules)
            {
                string rel = r.Path.ToLowerInvariant();
                listed.Add(rel);
                if (exe.Length > 0 && Path.GetFileName(rel) == exe) complete = true;
            }
        clientListComplete = complete;
        var roots = new List<string>();
        try
        {
            string w = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(w))
                foreach (var sub in new[] { "System32", "SysWOW64", "WinSxS", "assembly", "Microsoft.NET" })
                    roots.Add(WithSeparator(Path.Combine(Path.GetFullPath(w), sub)));
        }
        catch { }
        systemRoots = roots.ToArray();
    }

    // Folders under the trusted Windows subtrees that a standard user can still write to (the well-known AppLocker-bypass
    // list), so a module there is judged like any other file instead of being skipped.
    static readonly string[] writableUnderSystem =
    {
        "System32\\Tasks\\", "System32\\Tasks_Migrated\\", "System32\\spool\\drivers\\color\\", "System32\\spool\\PRINTERS\\", "System32\\spool\\SERVERS\\",
        "System32\\com\\dmp\\", "System32\\FxsTmp\\", "System32\\Microsoft\\Crypto\\RSA\\MachineKeys\\",
        "SysWOW64\\Tasks\\", "SysWOW64\\com\\dmp\\", "SysWOW64\\FxsTmp\\",
    };

    // True for a path inside Windows' own code folders (see the class comment), which the scan skips.
    bool IsTrustedWindowsPath(string full)
    {
        foreach (var root in systemRoots)
        {
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            string windows = root.Substring(0, root.TrimEnd('\\').LastIndexOf('\\') + 1);
            foreach (var w in writableUnderSystem)
                if (full.StartsWith(windows + w, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        return false;
    }

    // The labels gathered so far for the game process pid, after classifying any module not seen before. Labels
    // stay for the rest of the launch once seen (an injector may unload again). Never throws; a failure to list
    // or read something adds no label, except "module:scan-failed" when the process can't be opened at all.
    public List<string> Scan(int pid)
    {
        try
        {
            if (pid > 0 && since.ElapsedMilliseconds >= FirstScanDelayMs)
            {
                List<string> modules = ListModules(pid);
                Scans++;
                if (modules == null)
                {
                    if (!scanFailedLogged) { log("module scan: the game process can't be opened - reporting module:scan-failed"); scanFailedLogged = true; }
                    Add("module:scan-failed");
                }
                else
                {
                    foreach (var m in modules) Add(Classify(m));
                    // Phase B only: skip the memory walk entirely when the server didn't enable it (no floor), and
                    // once the label is already sent - it stays for the launch, so re-walking can't change the output.
                    if (MemFloorBytes > 0 && !labels.Contains(MemLargeLabel)) Add(MemoryBucket(pid));
                    // A debugger or an external memory reader can attach after the game started, so these are checked
                    // every scan (until the label is up); an external reader classifies like a module (signed/Windows
                    // skipped), so each distinct reader is hashed once per launch.
                    if (!labels.Contains(DebuggerLabel) && Debugged(pid)) Add(DebuggerLabel);
                    foreach (var reader in ExternalReaders(pid)) Add(ClassifyExternalReader(reader));
                }
            }
        }
        catch (Exception e) { log("module scan failed: " + e.Message); }
        return new List<string>(labels);
    }

    // The phase-B label for the executable memory in the game that no loaded module backs (a manually mapped
    // injector lives here), or null when the total is under the benign floor or the scan can't run. Point-in-time;
    // Scan re-checks it each scan until the label is sent (it stays for the launch) and only while the server set a
    // floor, so the memory walk is skipped once the label is up or phase B is off. Measure-only.
    public string MemoryBucket(int pid)
    {
        try
        {
            if (MemFloorBytes <= 0) return null;   // no floor from the server: phase B is off - don't walk memory
            List<long> sizes = ListAnonExecRegions(pid);
            if (sizes == null) return null;
            MemScans++;
            long total = 0;
            foreach (long s in sizes) total += s;
            return total >= MemFloorBytes ? MemLargeLabel : null;
        }
        catch { return null; }
    }

    // The label for one module file, or null when it is fine (or can't be judged). Cached by path, size and
    // last-write time, so each file is hashed or signature-checked once per launch. A module whose file is gone
    // from the path it was loaded from is reported as such: a DLL can be renamed while it is mapped, and hiding the
    // file that way is what an injector does after loading.
    public string Classify(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("\\\\?\\")) path = path.Substring(4);
            if (IsTrustedWindowsPath(Path.GetFullPath(path))) return null;
            var fi = new FileInfo(path);
            if (!fi.Exists) return "module:missing-file";
            string key = fi.FullName + "|" + fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            string label;
            if (cache.TryGetValue(key, out label)) return label.Length == 0 ? null : label;
            label = ClassifyFile(fi) ?? "";
            cache[key] = label;
            Classified++;
            return label.Length == 0 ? null : label;
        }
        catch { return null; }
    }

    string ClassifyFile(FileInfo fi)
    {
        string full = fi.FullName;
        if (clientDir != null && full.StartsWith(clientDir, StringComparison.OrdinalIgnoreCase))
        {
            string rel = full.Substring(clientDir.Length).ToLowerInvariant();
            if (listed.Contains(rel) || !clientListComplete) return null;   // listed: CheckClientFiles reports client:modified
            return "client:extra-module:" + CleanName(fi.Name);
        }
        bool signed;
        try { signed = IsSigned(full); } catch { return null; }   // a check that fails says nothing about the file
        if (signed) return null;
        string hash = HashPrefix(full);
        return hash == null ? null : "module:unsigned:" + hash;
    }

    // True when a debugger is attached to the game; never throws. HandleScans doubles as "the beyond-modules checks
    // ran" for the log and tests.
    bool Debugged(int pid)
    {
        HandleScans++;
        try { return IsDebugged(pid); } catch { return false; }
    }

    // The exe paths of processes holding a read/write handle to the game, or empty; never throws or returns null.
    List<string> ExternalReaders(int pid)
    {
        try { List<string> r = ListExternalReaders(pid); return r ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    // The label for one process that holds a read/write handle to the game, or null when it is fine (or can't be
    // judged). Cached by path, size and last-write time, like a module. Windows' own processes and trusted-signed
    // programs are not reported (an overlay, a recorder or antivirus legitimately reads game memory); an unsigned one
    // is reported by the first 32 hex of its SHA-256, so the server can translate known cheats and nothing else.
    public string ClassifyExternalReader(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (path.StartsWith("\\\\?\\")) path = path.Substring(4);
            string full = Path.GetFullPath(path);
            if (IsTrustedWindowsPath(full)) return null;   // OS processes legitimately hold handles to everything
            var fi = new FileInfo(full);
            if (!fi.Exists) return null;
            string key = "ext|" + fi.FullName + "|" + fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            string label;
            if (cache.TryGetValue(key, out label)) return label.Length == 0 ? null : label;
            bool signed;
            try { signed = IsSigned(full); } catch { signed = true; }   // a check that fails must not flag a legit tool
            label = "";
            if (!signed) { string hash = HashPrefix(full); if (hash != null) label = "handle:external-rw:" + hash; }
            cache[key] = label;
            Classified++;
            return label.Length == 0 ? null : label;
        }
        catch { return null; }
    }

    void Add(string label)
    {
        if (label == null || labels.Contains(label) || labels.Count >= MaxLabels) return;
        labels.Add(label);
    }

    // A file name as a label part: lower case, [a-z0-9_.-] only, at most MaxNameLength characters.
    public static string CleanName(string fileName)
    {
        string n = Regex.Replace((fileName ?? "").ToLowerInvariant(), "[^a-z0-9_.-]", "_");
        return n.Length > MaxNameLength ? n.Substring(0, MaxNameLength) : n;
    }

    // The first HashPrefixLength hex characters of the file's SHA-256, read with full sharing (it is a loaded module).
    // Null when it can't be read.
    public static string HashPrefix(string path)
    {
        try
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (byte x in sha.ComputeHash(fs)) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString().Substring(0, HashPrefixLength);
            }
        }
        catch { return null; }
    }

    static string WithSeparator(string dir)
    {
        return dir.EndsWith("\\") || dir.EndsWith("/") ? dir : dir + "\\";
    }

    // ---- the module list: psapi, read-only ----
    const uint ProcessQueryInformation = 0x0400, ProcessVmRead = 0x0010;
    const uint ListModulesAll = 0x03;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("psapi.dll", SetLastError = true)] static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint size, out uint needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetModuleFileNameExW(IntPtr process, IntPtr module, StringBuilder name, uint size);

    // Full paths of the modules loaded in process pid (64-bit and 32-bit alike), as Windows reports them. Null when
    // the process can't be opened (another user's or an elevated process, or one that has exited); an empty list when
    // it was opened but the list couldn't be read this time (a process still starting up), to be retried later.
    public static List<string> ListProcessModules(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
            if (h == IntPtr.Zero) return null;
            var result = new List<string>();
            uint needed;
            if (!EnumProcessModulesEx(h, new IntPtr[0], 0, out needed, ListModulesAll)) return result;
            var mods = new IntPtr[needed / IntPtr.Size + 16];
            if (!EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out needed, ListModulesAll)) return result;
            int count = Math.Min(mods.Length, (int)(needed / IntPtr.Size));
            var name = new StringBuilder(1024);
            for (int i = 0; i < count; i++)
            {
                name.Length = 0;
                uint cap = (uint)name.Capacity;
                uint n = GetModuleFileNameExW(h, mods[i], name, cap);
                // n == cap means the path was truncated to fit; a truncated path points nowhere and would read as
                // module:missing-file, so grow once (Windows long paths) and retry, then skip if still truncated.
                if (n >= cap)
                {
                    name.Capacity = 32768;
                    name.Length = 0;
                    cap = (uint)name.Capacity;
                    n = GetModuleFileNameExW(h, mods[i], name, cap);
                }
                if (n > 0 && n < cap) result.Add(name.ToString(0, (int)n));
            }
            return result;
        }
        catch { return null; }
        finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch { } }
    }

    // ---- executable memory with no backing module: VirtualQueryEx, read-only (phase B) ----
    const uint MemCommit = 0x1000, MemImage = 0x1000000;
    const uint PageExecuteAny = 0xF0;   // PAGE_EXECUTE | _READ | _READWRITE | _WRITECOPY

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, IntPtr length);

    // 64-bit MEMORY_BASIC_INFORMATION. The launcher runs 64-bit (anycpu, no 32-bit preference), so this is the one
    // the game and this process both use.
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryBasicInformation
    {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, Alignment1;
        public ulong RegionSize;
        public uint State, Protect, Type, Alignment2;
    }

    // Sizes of the committed, executable regions of process pid that no loaded module backs: private or mapped
    // executable memory, or an executable image whose allocation base is not a loaded module. Null when the process
    // can't be opened, or when its module list can't be read (every image would then look unbacked - better no
    // answer than a false one); an empty list when it is clean. Nothing is read from the regions - only the memory
    // map is walked - and nothing is written.
    public static List<long> RealAnonExecRegions(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
            if (h == IntPtr.Zero) return null;
            var bases = ModuleBases(h);
            if (bases.Count == 0) return null;   // a live process always has modules: an empty set means the list
                                                 // couldn't be read, so we can't tell backed images apart - don't guess
            var sizes = new List<long>();
            var mbi = new MemoryBasicInformation();
            IntPtr len = new IntPtr(Marshal.SizeOf(typeof(MemoryBasicInformation)));
            ulong addr = 0;
            const ulong max = 0x7FFFFFFF0000UL;   // top of the user-mode address space on x64
            int guard = 0;
            while (addr < max && guard++ < 2000000)
            {
                if (VirtualQueryEx(h, new IntPtr((long)addr), out mbi, len).ToInt64() == 0) break;
                if (mbi.RegionSize == 0) break;
                if (mbi.State == MemCommit && (mbi.Protect & PageExecuteAny) != 0)
                {
                    bool backedImage = mbi.Type == MemImage && bases.Contains(mbi.AllocationBase);
                    if (!backedImage) sizes.Add((long)mbi.RegionSize);
                }
                ulong next = mbi.BaseAddress + mbi.RegionSize;
                if (next <= addr) break;
                addr = next;
            }
            return sizes;
        }
        catch { return null; }
        finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch { } }
    }

    // Base addresses of the modules loaded in the open process handle (the module handle is the load address).
    static HashSet<ulong> ModuleBases(IntPtr h)
    {
        var set = new HashSet<ulong>();
        try
        {
            uint needed;
            if (!EnumProcessModulesEx(h, new IntPtr[0], 0, out needed, ListModulesAll)) return set;
            var mods = new IntPtr[needed / IntPtr.Size + 16];
            if (!EnumProcessModulesEx(h, mods, (uint)(mods.Length * IntPtr.Size), out needed, ListModulesAll)) return set;
            int count = Math.Min(mods.Length, (int)(needed / IntPtr.Size));
            for (int i = 0; i < count; i++) set.Add((ulong)mods[i].ToInt64());
        }
        catch { }
        return set;
    }

    // ---- a debugger attached to the game: CheckRemoteDebuggerPresent, read-only ----
    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint ProcessVmWrite = 0x0020, ProcessVmOperation = 0x0008;   // ProcessVmRead is 0x0010 above

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CheckRemoteDebuggerPresent(IntPtr process, out bool present);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    // True when a debugger is attached to process pid. A memory editor or trainer attaches as a debugger; this only
    // asks Windows whether one is attached - nothing is traced, read or changed. False on any failure (we can't open
    // the process, say): the signal is evidence that a debugger is attached, never proof that none is.
    public static bool RealIsDebugged(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(ProcessQueryInformation, false, pid);
            if (h == IntPtr.Zero) return false;
            bool present;
            return CheckRemoteDebuggerPresent(h, out present) && present;
        }
        catch { return false; }
        finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch { } }
    }

    // ---- processes holding a read/write handle to the game: NtQuerySystemInformation, read-only ----
    const int SystemExtendedHandleInformation = 0x40;
    static readonly int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);

    // 64-bit SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX. The launcher runs 64-bit, so the pointer-sized fields are 8 bytes.
    [StructLayout(LayoutKind.Sequential)]
    struct SystemHandleEntryEx
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    // The exe paths of the processes that hold an open handle to the game (gamePid) with memory read, write or
    // operation access, and are not the game, the launcher, Windows' System/Idle, or a process we can't name. An
    // external cheat that reads or writes the game's memory without injecting a module lives here. Null when the game
    // can't be opened or the handle table can't be read (no answer beats a wrong one); an empty list when nothing
    // holds such a handle. The whole system handle table is a snapshot taken once; nothing is written anywhere.
    public static List<string> RealExternalReaders(int gamePid)
    {
        IntPtr game = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            game = OpenProcess(ProcessQueryLimitedInformation, false, gamePid);
            if (game == IntPtr.Zero) return null;   // can't anchor the game's kernel object -> no answer
            int ourPid = Process.GetCurrentProcess().Id;
            int length = 1 << 20;
            for (int tries = 0; tries < 8; tries++)
            {
                buffer = Marshal.AllocHGlobal(length);
                int needed;
                int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out needed);
                if (status == StatusInfoLengthMismatch)
                {
                    Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero;
                    length = Math.Max(needed + (1 << 20), length * 2);   // grow past what it asked for; it keeps changing
                    continue;
                }
                if (status != 0) return null;
                break;
            }
            if (buffer == IntPtr.Zero) return null;

            // The table can hold hundreds of thousands of entries and is re-read every heartbeat, so the fixed fields
            // we need are read by offset (Object 0, UniqueProcessId 8, HandleValue 16, GrantedAccess 24) rather than
            // marshaling a whole SystemHandleEntryEx per entry. SizeOf gives the stride; the struct documents the layout.
            int entrySize = Marshal.SizeOf(typeof(SystemHandleEntryEx));
            long count = Marshal.ReadIntPtr(buffer).ToInt64();   // NumberOfHandles, first field of the header
            long maxByBuf = (length - IntPtr.Size * 2L) / entrySize;
            if (count < 0 || count > maxByBuf) count = maxByBuf;   // never read past the buffer we sized
            long first = buffer.ToInt64() + IntPtr.Size * 2;      // header is { NumberOfHandles, Reserved }
            long gameHandle = game.ToInt64();

            // First the game's kernel object, found through our own handle to it; then every other handle on that
            // same object with memory access, by owner process.
            long gameObject = 0;
            for (long i = 0; i < count; i++)
            {
                long entry = first + i * entrySize;
                if ((int)Marshal.ReadInt64(new IntPtr(entry + 8)) == ourPid && Marshal.ReadInt64(new IntPtr(entry + 16)) == gameHandle)
                { gameObject = Marshal.ReadInt64(new IntPtr(entry)); break; }
            }
            if (gameObject == 0) return null;   // a live handle's object is never null: not finding it means no answer

            const uint rw = ProcessVmRead | ProcessVmWrite | ProcessVmOperation;
            var seen = new HashSet<int>();
            var paths = new List<string>();
            for (long i = 0; i < count; i++)
            {
                long entry = first + i * entrySize;
                if (Marshal.ReadInt64(new IntPtr(entry)) != gameObject) continue;
                int owner = (int)Marshal.ReadInt64(new IntPtr(entry + 8));
                if (owner == gamePid || owner == ourPid || owner <= 4) continue;   // the game, us, System (4) / Idle (0)
                if (((uint)Marshal.ReadInt32(new IntPtr(entry + 24)) & rw) == 0) continue;
                if (!seen.Add(owner)) continue;   // one label per reader, whatever access rights it took
                string p = ProcessPath(owner);
                if (p != null) paths.Add(p);
            }
            return paths;
        }
        catch { return null; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (game != IntPtr.Zero) try { CloseHandle(game); } catch { }
        }
    }

    // The full path of process pid's exe, or null when it can't be opened or named.
    static string ProcessPath(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (h == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return null;
            return sb.ToString(0, (int)Math.Min(size, (uint)sb.Capacity));
        }
        catch { return null; }
        finally { if (h != IntPtr.Zero) try { CloseHandle(h); } catch { } }
    }

    // ---- Authenticode: wintrust, offline ----
    [StructLayout(LayoutKind.Sequential)]
    struct WintrustFileInfo { public uint Size; public IntPtr FilePath; public IntPtr File; public IntPtr KnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustData
    {
        public uint Size; public IntPtr PolicyCallbackData; public IntPtr SipClientData; public uint UiChoice; public uint RevocationChecks;
        public uint UnionChoice; public IntPtr File; public uint StateAction; public IntPtr StateData; public IntPtr UrlReference;
        public uint ProvFlags; public uint UiContext; public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1, WtdStateActionIgnore = 0;
    const uint WtdRevocationCheckNone = 0x10, WtdCacheOnlyUrlRetrieval = 0x1000;
    const int CertExpired = unchecked((int)0x800B0101);

    // True when the file carries an Authenticode signature that verifies to a trusted root, in the file itself or in one
    // of the system's security catalogs (how Windows and many Microsoft components are signed). No revocation check and
    // no network fetch, so it is quick and works offline. An intact signature whose certificate has expired counts
    // as signed (the signer is known; the label is meant for code with no publisher at all). A file with no
    // signature, a bad hash, or a root this Windows user does not trust is not signed. "Trusted" is Windows' own
    // judgement for the player's account: a root the player added to their own certificate store counts, which is
    // one more way a determined cheater hides from this signal (docs\AC_MODULE_SIGNAL.md, "What it cannot see").
    // Throws only on a marshaling failure.
    public static bool HasTrustedSignature(string path)
    {
        return HasEmbeddedSignature(path) || HasCatalogSignature(path);
    }

    public static bool HasEmbeddedSignature(string path)
    {
        IntPtr pathPtr = IntPtr.Zero, filePtr = IntPtr.Zero;
        try
        {
            pathPtr = Marshal.StringToCoTaskMemUni(path);
            var fileInfo = new WintrustFileInfo { Size = (uint)Marshal.SizeOf(typeof(WintrustFileInfo)), FilePath = pathPtr };
            filePtr = Marshal.AllocCoTaskMem((int)fileInfo.Size);
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            return Trusted(WtdChoiceFile, filePtr);
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(filePtr);
            if (pathPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    static bool Trusted(uint unionChoice, IntPtr unionPtr)
    {
        var data = new WintrustData
        {
            Size = (uint)Marshal.SizeOf(typeof(WintrustData)), UiChoice = WtdUiNone, RevocationChecks = WtdRevokeNone,
            UnionChoice = unionChoice, File = unionPtr, StateAction = WtdStateActionIgnore,
            ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
        };
        Guid action = GenericVerifyV2;
        int rc = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        return rc == 0 || rc == CertExpired;
    }

    // ---- catalog signatures: the file's hash looked up in the system catalogs, then verified like a signature ----
    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, ref Guid subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)] static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint hashSize, byte[] hash, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)] static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint hashSize, uint flags, ref IntPtr previous);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern bool CryptCATCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll")] static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CatalogInfo { public uint Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string CatalogFile; }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustCatalogInfo
    {
        public uint Size; public uint CatalogVersion; public IntPtr CatalogFilePath; public IntPtr MemberTag; public IntPtr MemberFilePath;
        public IntPtr MemberFile; public IntPtr CalculatedFileHash; public uint CalculatedFileHashSize; public IntPtr CatalogContext; public IntPtr CatAdmin;
    }

    static readonly Guid DriverActionVerify = new Guid("F750E6C3-38EE-11D1-85E5-00C04FC295EE");
    const uint WtdChoiceCatalog = 2;

    // SHA-256 catalogs first (Windows 10+), then SHA-1 for older ones. Any failure along the way means "not found".
    public static bool HasCatalogSignature(string path)
    {
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            try { if (InCatalog(path, algorithm)) return true; }
            catch { }
        }
        return false;
    }

    static bool InCatalog(string path, string algorithm)
    {
        IntPtr admin = IntPtr.Zero, catalog = IntPtr.Zero, catalogPath = IntPtr.Zero, memberTag = IntPtr.Zero, memberPath = IntPtr.Zero, infoPtr = IntPtr.Zero;
        try
        {
            Guid subsystem = DriverActionVerify;
            if (!CryptCATAdminAcquireContext2(out admin, ref subsystem, algorithm, IntPtr.Zero, 0)) return false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle.DangerousGetHandle(), ref size, null, 0);
                if (size == 0 || size > 128) return false;
                var hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle.DangerousGetHandle(), ref size, hash, 0)) return false;
                IntPtr previous = IntPtr.Zero;
                catalog = CryptCATAdminEnumCatalogFromHash(admin, hash, size, 0, ref previous);
                if (catalog == IntPtr.Zero) return false;
                var info = new CatalogInfo { Size = (uint)Marshal.SizeOf(typeof(CatalogInfo)) };
                if (!CryptCATCatalogInfoFromContext(catalog, ref info, 0) || string.IsNullOrEmpty(info.CatalogFile)) return false;
                var tag = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) tag.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                catalogPath = Marshal.StringToCoTaskMemUni(info.CatalogFile);
                memberTag = Marshal.StringToCoTaskMemUni(tag.ToString());
                memberPath = Marshal.StringToCoTaskMemUni(path);
                var ci = new WintrustCatalogInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(WintrustCatalogInfo)), CatalogFilePath = catalogPath, MemberTag = memberTag,
                    MemberFilePath = memberPath, MemberFile = fs.SafeFileHandle.DangerousGetHandle()
                };
                infoPtr = Marshal.AllocCoTaskMem((int)ci.Size);
                Marshal.StructureToPtr(ci, infoPtr, false);
                return Trusted(WtdChoiceCatalog, infoPtr);
            }
        }
        finally
        {
            if (infoPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(infoPtr);
            if (memberPath != IntPtr.Zero) Marshal.FreeCoTaskMem(memberPath);
            if (memberTag != IntPtr.Zero) Marshal.FreeCoTaskMem(memberTag);
            if (catalogPath != IntPtr.Zero) Marshal.FreeCoTaskMem(catalogPath);
            if (catalog != IntPtr.Zero) try { CryptCATAdminReleaseCatalogContext(admin, catalog, 0); } catch { }
            if (admin != IntPtr.Zero) try { CryptCATAdminReleaseContext(admin, 0); } catch { }
        }
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
    // The phase-B memory floor the server sent with the current session, in bytes (0 = none). The caller reads it
    // from here to drive AcModules, so the number never has to live in the client. Set each time a session opens.
    public long MemFloorBytes;
    // Evidence labels to send with each heartbeat (see AcSignals), asked for on the client thread before
    // every heartbeat. Null = none. A throwing source just sends no labels that time. The tool labels are
    // added by the client itself, from the rules the server sent with the session.
    public Func<IList<string>> Signals;
    public Func<IEnumerable<string>> ProcessNames = AcSignals.RunningProcessNames;   // replaceable for tests
    // The PC's hardware id sent with each session request (AcSession.ReadHardwareId), read once. Null = none.
    public Func<string> HardwareId;
    // The newest game-link code from the game's log (AcGameLog.Poll), null = none. When a new one appears the client
    // sends a heartbeat at once instead of waiting out the interval, so the server sees the link within seconds.
    public Func<string> LinkSource;
    string sentLink;
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

    // True while the session thread runs (it ends when the game exits or Stop() is called).
    public bool Running { get { var t = thread; return t != null && t.IsAlive; } }

    // Sleeps, or returns false at once when Stop() is called.
    bool Sleep(int ms) { return !stopped.WaitOne(Math.Max(0, ms)); }

    // Like Sleep, but wakes early (true) when the game log holds a code not sent yet. False only when stopped.
    bool SleepOrLink(int ms)
    {
        if (LinkSource == null) return Sleep(ms);
        int left = Math.Max(0, ms);
        while (left > 0)
        {
            int step = Math.Min(left, SecondMs);
            if (stopped.WaitOne(step)) return false;
            left -= step;
            if (NewLink() != null) return true;
        }
        return true;
    }

    // A code from the game log that has not been sent with this session yet, or null.
    string NewLink()
    {
        string code = null;
        try { if (LinkSource != null) code = LinkSource(); } catch { }
        return AcSession.IsLinkCode(code) && code != sentLink ? code : null;
    }

    void Run()
    {
        try { Loop(); }
        catch (Exception e) { log("session client stopped: " + e.Message); }
    }

    void Loop()
    {
        AcSession.Grant grant = null;
        List<AcSignals.ToolRule> tools = null;
        long seq = 0;
        int retry = RetryDelayMs;
        bool waitingForSteam = false;
        string lastLabels = "";
        string hw = null;
        try { if (HardwareId != null) hw = HardwareId(); } catch { }
        while (!stopped.WaitOne(0) && gameRunning())
        {
            if (grant == null)
            {
                string body = AcSession.BuildSessionRequest(steamId(), launcherVersion, AcSession.NewNonceHex(), AcSession.UnixNow(), hw);
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
                seq = 0; retry = RetryDelayMs; sentLink = null;   // a new session must link again
                tools = AcSignals.ParseToolSpec(grant.ToolSpec);
                MemFloorBytes = grant.MemFloorBytes;   // phase-B floor for this session (0 = the server did not enable it)
                Interlocked.Increment(ref Opened);
                log("session opened, heartbeat every " + grant.Heartbeat + " s, " + tools.Count + " tool rule(s) from the server, " +
                    (AcSession.IsHex(hw, 64) ? "hardware id sent" : "no hardware id"));
            }

            seq++;
            var gathered = new List<string>();
            try { if (Signals != null) { var s = Signals(); if (s != null) gathered.AddRange(s); } } catch { }
            try { if (tools.Count > 0 && ProcessNames != null) gathered.AddRange(AcSignals.MatchTools(ProcessNames(), tools)); } catch { }
            IList<string> labels = AcSession.CleanSignals(gathered);
            // Tell the player's own log what is being reported, whenever it changes.
            string joined = string.Join(",", labels);
            if (joined != lastLabels) { log(joined.Length > 0 ? "reporting: " + joined : "reporting nothing"); lastLabels = joined; }
            string link = NewLink();
            if (link != null) log("game link code found - sending it with the heartbeat");
            string hb = AcSession.BuildHeartbeat(grant.Session, seq, AcSession.UnixNow(), gameRunning(), labels, link);
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
                if (link != null) sentLink = link;
                if (!SleepOrLink(next * SecondMs)) return;
            }
            else
            {
                Interlocked.Increment(ref HeartbeatsRejected);
                log("heartbeat refused (" + Short(reason ?? r) + ") - opening a new session");   // the server's text, made one short line
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
            req.AllowAutoRedirect = false;   // a redirect could lead off the checked endpoint (IsAllowedEndpoint), e.g. to plain http
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
