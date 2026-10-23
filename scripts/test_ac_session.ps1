# Tests for the launcher anti-cheat session (src\AcSession.cs, protocol v1 in docs\CLIENT_ANTICHEAT.md).
# Compiles the real source with Add-Type and checks message building, signing, reply parsing and the
# session client's loop against an in-process fake service. Run: .\scripts\test_ac_session.ps1
#
# Set AC_TEST_URL (e.g. http://127.0.0.1:8092, the dev ac_session.py) to also run the client against a
# real session service; without it that part is skipped, so CI needs no server.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# The client calls back on its own thread, where PowerShell script blocks can't run, so the fake service
# and the callbacks are C#, compiled together with the real source.
$fake = @'
public sealed class FakeService
{
    public readonly string Key = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
    public int SessionCalls, HeartbeatCalls, BadSignatures, FailOpens, RejectBeatNumber = -1;
    public long LastSeq;
    public string LastBody, LastOpenBody;
    public System.Collections.Generic.List<string> Links = new System.Collections.Generic.List<string>();   // "link" codes seen
    public System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public long FirstLinkMs = -1;
    public int SessionCount;
    public string Tools;   // "tools" field of the session reply, or null for none
    public string Post(string url, string body, string sig)
    {
        lock (this)
        {
            if (url.EndsWith("/v1/session"))
            {
                SessionCalls++;
                LastOpenBody = body;
                if (FailOpens > 0) { FailOpens--; return null; }
                SessionCount++;
                LastSeq = 0;
                return "{\"session\":\"" + SessionCount.ToString("x32") + "\",\"key\":\"" + Key + "\",\"heartbeat\":30,\"ttl\":120" +
                       (Tools != null ? ",\"tools\":\"" + Tools + "\"" : "") + "}";
            }
            HeartbeatCalls++;
            LastBody = body;
            var lm = System.Text.RegularExpressions.Regex.Match(body, "\"link\":\"([0-9a-f]{16})\"");
            if (lm.Success) { Links.Add(lm.Groups[1].Value); if (FirstLinkMs < 0) FirstLinkMs = Clock.ElapsedMilliseconds; }
            if (!AcSession.Verify(Key, body, sig)) { BadSignatures++; return "{\"ok\":0,\"reason\":\"bad_sig\"}"; }
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"seq\":([0-9]+)");
            long seq = long.Parse(m.Groups[1].Value);
            if (seq <= LastSeq) return "{\"ok\":0,\"reason\":\"replay\"}";
            LastSeq = seq;
            if (HeartbeatCalls == RejectBeatNumber) return "{\"ok\":0,\"reason\":\"expired\"}";
            return "{\"ok\":1,\"next\":30}";
        }
    }
}

public static class Harness
{
    // A client against the fake service whose game "exits" after stopAfterBeats heartbeats. Steam appears
    // after steamMissingCalls lookups. All delays are shrunk to milliseconds.
    public static AcSessionClient Make(FakeService svc, int stopAfterBeats, int steamMissingCalls)
    {
        int lookups = 0;
        var c = new AcSessionClient("http://test", "1.2.0",
            () => lookups++ < steamMissingCalls ? null : "76561198000000001",
            () => svc.HeartbeatCalls < stopAfterBeats,
            null, svc.Post);
        c.RetryDelayMs = 1; c.MaxRetryDelayMs = 4; c.SecondMs = 1;
        return c;
    }

    // Like Make, with a signal source: fixed labels, or one that throws.
    public static AcSessionClient WithSignals(FakeService svc, int stopAfterBeats, string[] labels, bool throws)
    {
        var c = Make(svc, stopAfterBeats, 0);
        c.Signals = () => { if (throws) throw new System.InvalidOperationException("boom"); return labels; };
        return c;
    }

    // Like Make, with the server's tool rules and a fixed set of "running" processes.
    public static AcSessionClient WithTools(FakeService svc, int stopAfterBeats, string toolSpec, string[] running)
    {
        svc.Tools = toolSpec;
        var c = Make(svc, stopAfterBeats, 0);
        c.ProcessNames = () => running;
        return c;
    }

    // Like Make, with a hardware id source: a fixed value, or one that throws.
    public static AcSessionClient WithHardware(FakeService svc, int stopAfterBeats, string hw, bool throws)
    {
        var c = Make(svc, stopAfterBeats, 0);
        c.HardwareId = () => { if (throws) throw new System.InvalidOperationException("boom"); return hw; };
        return c;
    }

    // A game-link code the test can change from PowerShell; read on the client's own thread.
    public sealed class CodeBox { public volatile string Code; public string Get() { return Code; } }

    // Like Make, with a game-link source and a real-ish second (heartbeat interval 30 x secondMs).
    public static AcSessionClient WithLink(FakeService svc, int stopAfterBeats, CodeBox box, int secondMs)
    {
        var c = Make(svc, stopAfterBeats, 0);
        c.SecondMs = secondMs;
        c.LinkSource = box.Get;
        return c;
    }

    // The real HTTP client against a running service, for the given number of accepted heartbeats.
    public static AcSessionClient Live(string url, string steamId, int beats)
    {
        AcSessionClient c = null;
        c = new AcSessionClient(url, "0.0.0-test", () => steamId, () => c == null || c.HeartbeatsOk < beats, null, null);
        c.SecondMs = 10;
        return c;
    }
}
'@
# -IgnoreWarnings: Add-Type turns compiler warnings into errors, and on PowerShell 7 (.NET 8+, where CI runs
# this) WebRequest/HttpWebRequest are marked obsolete (SYSLIB0014). The launcher itself targets .NET Framework,
# where they are the supported HTTP client, so that warning says nothing about the code under test.
Add-Type -IgnoreWarnings -TypeDefinition ((Get-Content (Join-Path $repo 'src\AcSession.cs') -Raw) + "`n" + $fake)

$script:fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fail++ } }

$sid = '76561198000000001'
$sess = '00112233445566778899aabbccddeeff'
$nonce = 'a1b2c3d4e5f60718293a4b5c6d7e8f90'
$key = '000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f'

# ---- SteamID ----
Check "SteamId64 of account 1"           ([AcSession]::SteamId64FromAccountId(1) -eq '76561197960265729')
Check "SteamId64 of account 0 -> null"   ($null -eq [AcSession]::SteamId64FromAccountId(0))
Check "IsSteamId64 accepts an individual id" ([AcSession]::IsSteamId64($sid))
Check "IsSteamId64 rejects 16 digits"    (-not [AcSession]::IsSteamId64('7656119800000000'))
Check "IsSteamId64 rejects other prefix" (-not [AcSession]::IsSteamId64('12345678901234567'))
Check "IsSteamId64 rejects null"         (-not [AcSession]::IsSteamId64($null))

# ---- session request ----
$req = [AcSession]::BuildSessionRequest($sid, '1.2.0', $nonce, 1760000000)
Check "session request exact body" ($req -eq "{`"v`":1,`"steamid`":`"$sid`",`"launcher`":`"1.2.0`",`"nonce`":`"$nonce`",`"t`":1760000000}")
Check "session request: bad SteamID -> null" ($null -eq [AcSession]::BuildSessionRequest('123', '1.2.0', $nonce, 1))
Check "session request: short nonce -> null" ($null -eq [AcSession]::BuildSessionRequest($sid, '1.2.0', 'abcd', 1))
$evil = [AcSession]::BuildSessionRequest($sid, '1.2"},"x":"y', $nonce, 1)
Check "session request: version cannot break out of its string" ($evil -match '"launcher":"1\.2",' -and $evil -notmatch '"x"')
Check "session request: empty version -> 0" ([AcSession]::BuildSessionRequest($sid, '', $nonce, 1) -match '"launcher":"0"')

# ---- hardware id (expected value computed independently with Python's hashlib) ----
$guid = '6f1e2d3c-4b5a-4968-8776-655443322110'
$hw = '4e66d5084224e1e815223d5682aaafd69662875cbf6deb4d28902f6edd6d08bf'
Check "hardware id matches the reference vector"   ([AcSession]::HardwareIdFromMachineGuid($guid) -eq $hw)
Check "hardware id ignores case and spaces"        ([AcSession]::HardwareIdFromMachineGuid(" $($guid.ToUpperInvariant()) ") -eq $hw)
foreach ($bad in '', 'not-a-guid', '{6f1e2d3c-4b5a-4968-8776-655443322110}', '00000000-0000-0000-0000-000000000000', $null) {
    Check "hardware id refused: '$bad'" ($null -eq [AcSession]::HardwareIdFromMachineGuid($bad))
}
$mine = [AcSession]::ReadHardwareId()
Check "hardware id of this PC is 64 hex and stable" ([AcSession]::IsHex($mine, 64) -and $mine -eq [AcSession]::ReadHardwareId())
$reqHw = [AcSession]::BuildSessionRequest($sid, '1.2.0', $nonce, 1760000000, $hw.ToUpperInvariant())
Check "session request with hardware id exact body" ($reqHw -eq "{`"v`":1,`"steamid`":`"$sid`",`"launcher`":`"1.2.0`",`"nonce`":`"$nonce`",`"t`":1760000000,`"hw`":`"$hw`"}")
Check "session request: bad hardware id left out"   ([AcSession]::BuildSessionRequest($sid, '1.2.0', $nonce, 1760000000, 'abc"}') -eq $req)
Check "session request: no hardware id left out"    ([AcSession]::BuildSessionRequest($sid, '1.2.0', $nonce, 1760000000, $null) -eq $req)

# ---- heartbeat ----
$hb = [AcSession]::BuildHeartbeat($sess, 7, 1760000210, $true, [string[]]@('tool:autohotkey'))
$hbExpected = "{`"v`":1,`"session`":`"$sess`",`"seq`":7,`"t`":1760000210,`"game`":1,`"signals`":[`"tool:autohotkey`"]}"
Check "heartbeat exact body" ($hb -eq $hbExpected)
Check "heartbeat with no signals" ([AcSession]::BuildHeartbeat($sess, 1, 5, $false, $null) -eq "{`"v`":1,`"session`":`"$sess`",`"seq`":1,`"t`":5,`"game`":0,`"signals`":[]}")
Check "heartbeat: seq 0 -> null"       ($null -eq [AcSession]::BuildHeartbeat($sess, 0, 5, $true, $null))
Check "heartbeat: bad session -> null" ($null -eq [AcSession]::BuildHeartbeat('xyz', 1, 5, $true, $null))

$dirty = [string[]]@('ok:one', 'Upper', 'a","b', 'ok:one', '', ('x' * 65), 'ok.two-3_x')
$clean = [AcSession]::CleanSignals($dirty)
Check "signals: invalid, duplicate and over-long labels dropped" (($clean -join '|') -eq 'ok:one|ok.two-3_x')
$many = [string[]](1..40 | ForEach-Object { "s$_" })
Check "signals: capped at MaxSignals" ([AcSession]::CleanSignals($many).Count -eq [AcSession]::MaxSignals)

# ---- signing (expected value computed independently with Python's hmac module) ----
$sig = [AcSession]::Sign($key, $hbExpected)
Check "HMAC-SHA256 matches the reference vector" ($sig -eq '82b9daf011519efd753012a64ec9857c8441142997098d5e23a1053da30b377f')
Check "Verify accepts the signature"           ([AcSession]::Verify($key, $hbExpected, $sig))
Check "Verify accepts an upper-case signature" ([AcSession]::Verify($key, $hbExpected, $sig.ToUpperInvariant()))
Check "Verify rejects a changed body"          (-not [AcSession]::Verify($key, $hbExpected.Replace('"seq":7', '"seq":8'), $sig))
Check "Verify rejects another key"             (-not [AcSession]::Verify(('ff' * 32), $hbExpected, $sig))
Check "Verify rejects a malformed signature"   (-not [AcSession]::Verify($key, $hbExpected, 'abc'))
Check "Sign with a short key -> null"          ($null -eq [AcSession]::Sign('abcd', 'x'))

# ---- session reply ----
$g = [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"$($key.ToUpperInvariant())`",`"heartbeat`":30,`"ttl`":120}")
Check "session reply parsed"            ($g -ne $null -and $g.Session -eq $sess -and $g.KeyHex -eq $key -and $g.Heartbeat -eq 30 -and $g.Ttl -eq 120)
$g = [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"$key`",`"heartbeat`":1,`"ttl`":99999}")
Check "session reply: timings clamped"  ($g.Heartbeat -eq 10 -and $g.Ttl -eq 900)
$g = [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"$key`",`"heartbeat`":200,`"ttl`":60}")
Check "session reply: ttl at least two heartbeats" ($g.Ttl -eq 400)
$g = [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"$key`"}")
Check "session reply: defaults when timings absent" ($g.Heartbeat -eq 30 -and $g.Ttl -eq 120)
$g = [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"$key`",`"heartbeat`":3000000000000}")
Check "session reply: huge number -> default" ($g.Heartbeat -eq 30)
Check "session reply: missing key -> null"  ($null -eq [AcSession]::ParseSessionReply("{`"session`":`"$sess`"}"))
Check "session reply: short key -> null"    ($null -eq [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"abcd`"}"))
Check "session reply: not JSON -> null"     ($null -eq [AcSession]::ParseSessionReply('<html>502 Bad Gateway</html>'))
Check "session reply: null -> null"         ($null -eq [AcSession]::ParseSessionReply($null))

# ---- heartbeat reply ----
$next = 0; $reason = $null
$ok = [AcSession]::ParseHeartbeatReply('{"ok":1,"next":45}', 30, [ref]$next, [ref]$reason)
Check "heartbeat reply ok"               ($ok -and $next -eq 45 -and $null -eq $reason)
$ok = [AcSession]::ParseHeartbeatReply('{"ok":0,"reason":"bad_sig"}', 30, [ref]$next, [ref]$reason)
Check "heartbeat reply rejected + reason" ((-not $ok) -and $reason -eq 'bad_sig' -and $next -eq 30)
$ok = [AcSession]::ParseHeartbeatReply('{"ok":1,"next":2}', 30, [ref]$next, [ref]$reason)
Check "heartbeat reply: next clamped"     ($ok -and $next -eq 10)
$ok = [AcSession]::ParseHeartbeatReply('garbage', 30, [ref]$next, [ref]$reason)
Check "heartbeat reply: garbage -> not ok" ((-not $ok) -and $next -eq 30)

# ---- nonce, clock ----
$n1 = [AcSession]::NewNonceHex(); $n2 = [AcSession]::NewNonceHex()
Check "nonce is 32 hex"     ([AcSession]::IsHex($n1, 32))
Check "nonces are distinct" ($n1 -ne $n2)
Check "UnixNow is the current Unix time" ([Math]::Abs([AcSession]::UnixNow() - [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) -le 1)

# ---- endpoint rule: https anywhere, http only for local / private IP literals ----
$allowed = 'https://ac.rustorigin.com', 'https://ac.rustorigin.com/', 'http://127.0.0.1:8092', 'http://192.168.117.125:8092',
           'http://10.1.2.3', 'http://172.16.0.1', 'http://172.31.255.255', 'http://[::1]:8092', 'http://[fd00::1]'
$refused = 'http://ac.rustorigin.com', 'http://51.195.60.227:8092', 'http://172.32.0.1', 'http://192.169.0.1', 'http://localhost:8092',
           'ftp://127.0.0.1', 'ac.rustorigin.com', '', $null
foreach ($u in $allowed) { Check "endpoint allowed: $u" ([AcSession]::IsAllowedEndpoint($u)) }
foreach ($u in $refused) { Check "endpoint refused: '$u'" (-not [AcSession]::IsAllowedEndpoint($u)) }

# ---- session client loop (fake service) ----
function RunClient($svc, [int]$beats, [int]$steamMissing) {
    $c = [Harness]::Make($svc, $beats, $steamMissing); $c.Start()
    $done = $c.Wait(10000); if (-not $done) { $c.Stop() }
    @{ C = $c; Done = $done }
}
$svc = New-Object FakeService; $r = RunClient $svc 5 0
Check "client: ends by itself when the game exits"    $r.Done
Check "client: one session, five heartbeats accepted" ($r.C.Opened -eq 1 -and $r.C.HeartbeatsOk -eq 5 -and $svc.BadSignatures -eq 0)
Check "client: sequence numbers count up from 1"     ($svc.LastSeq -eq 5)
Check "client: heartbeat says the game is running"   ($svc.LastBody -match '"game":1')

$svc = New-Object FakeService; $svc.FailOpens = 2; $r = RunClient $svc 3 0
Check "client: retries while the service is down" ($r.Done -and $r.C.NetworkErrors -eq 2 -and $r.C.Opened -eq 1 -and $r.C.HeartbeatsOk -eq 3)

$svc = New-Object FakeService; $svc.RejectBeatNumber = 2; $r = RunClient $svc 4 0
Check "client: a refused heartbeat opens a new session" ($r.Done -and $r.C.Opened -eq 2 -and $r.C.HeartbeatsRejected -eq 1 -and $svc.SessionCalls -eq 2)
Check "client: the new session restarts at seq 1"       ($svc.LastSeq -eq 2)

$svc = New-Object FakeService; $r = RunClient $svc 2 3
Check "client: waits for Steam, then opens"   ($r.Done -and $r.C.Opened -eq 1 -and $svc.HeartbeatCalls -eq 2)

$svc = New-Object FakeService; $r = RunClient $svc 0 0
Check "client: does nothing when the game is not running" ($r.Done -and $svc.SessionCalls -eq 0)

$svc = New-Object FakeService
$c = [Harness]::WithHardware($svc, 2, $hw, $false); $c.Start()
Check "client: the hardware id goes with the session request" ($c.Wait(5000) -and $svc.LastOpenBody -match "`"hw`":`"$hw`"}$")
$svc = New-Object FakeService
$c = [Harness]::WithHardware($svc, 2, $null, $true); $c.Start()
Check "client: a throwing hardware id source still opens, without hw" ($c.Wait(5000) -and $c.HeartbeatsOk -eq 2 -and $svc.LastOpenBody -notmatch '"hw"')

$svc = New-Object FakeService
$c = [Harness]::Make($svc, 1000000, 0); $c.SecondMs = 1000; $c.Start()
Start-Sleep -Milliseconds 300
$runningBefore = $c.Running
$c.Stop()
Check "client: Stop() ends it in the middle of a 30 s wait" ($c.Wait(2000) -and $svc.HeartbeatCalls -ge 1)
Check "client: Running is true while the session runs, false once it ended" ($runningBefore -and -not $c.Running)

# ---- evidence signals ----
$svc = New-Object FakeService
$c = [Harness]::WithSignals($svc, 2, [string[]]@('tool:autohotkey', 'client:modified:gameassembly.dll', 'Bad Label'), $false); $c.Start()
Check "signals: sent in the heartbeat (invalid labels dropped)" ($c.Wait(5000) -and $svc.LastBody -match '"signals":\["tool:autohotkey","client:modified:gameassembly.dll"\]')
$svc = New-Object FakeService
$c = [Harness]::WithSignals($svc, 2, $null, $true); $c.Start()
Check "signals: a throwing source still heartbeats, with no labels" ($c.Wait(5000) -and $c.HeartbeatsOk -eq 2 -and $svc.LastBody -match '"signals":\[\]')

# tool rules come from the server ("tools" in the session reply); labels are only the rule's number
$spec = [AcSignals]::ParseToolSpec('1=AutoHotkey.exe, autohotkeyu64 ,autohotkey;12=other;x=bad;3=;4=bad"name;5=ok name')
Check "tool spec parsed: numeric ids, names lowered, .exe and duplicates dropped, bad rules skipped" (
    (($spec | ForEach-Object { $_.Label + '=' + ($_.Names -join ',') }) -join ';') -eq 'tool:1=autohotkey,autohotkeyu64;tool:12=other;tool:5=ok name')
Check "tool spec: missing field -> no rules" ([AcSignals]::ParseToolSpec($null).Count -eq 0)
Check "tool spec: capped at MaxToolRules" ([AcSignals]::ParseToolSpec(((1..100) | ForEach-Object { "$_=p$_" }) -join ';').Count -eq [AcSignals]::MaxToolRules)
Check "tool match: case-insensitive, with or without .exe" ((([AcSignals]::MatchTools([string[]]@('explorer', 'AUTOHOTKEYU64.EXE', 'autohotkey'), $spec)) -join ',') -eq 'tool:1')
Check "tool match: nothing running -> no labels" ([AcSignals]::MatchTools([string[]]@('explorer'), $spec).Count -eq 0)
$g = [AcSession]::ParseSessionReply("{`"session`":`"$sess`",`"key`":`"$key`",`"tools`":`"1=autohotkey;2=notepad`"}")
Check "session reply carries the tool spec" ($g.ToolSpec -eq '1=autohotkey;2=notepad')

$svc = New-Object FakeService
$c = [Harness]::WithTools($svc, 2, '1=autohotkey,autohotkeyu64;2=notepad', [string[]]@('explorer', 'AutoHotkeyU64')); $c.Start()
Check "client: server tool rules matched, only the number is sent" ($c.Wait(5000) -and $svc.LastBody -match '"signals":\["tool:1"\]')
$svc = New-Object FakeService
$c = [Harness]::WithTools($svc, 2, $null, [string[]]@('AutoHotkeyU64')); $c.Start()
Check "client: no tool rules from the server -> no tool labels" ($c.Wait(5000) -and $svc.LastBody -match '"signals":\[\]')
Check "running process names include this one" ([AcSignals]::RunningProcessNames() -contains [Diagnostics.Process]::GetCurrentProcess().ProcessName)

$h = '0' * 64
$f = [AcSignals]::ParseClientFile("RustClient_Data/il2cpp_data/Metadata/global-metadata.dat|12144316|$h")
Check "client file rule parsed (path, size, label name)" ($f.Path -eq 'RustClient_Data\il2cpp_data\Metadata\global-metadata.dat' -and $f.Size -eq 12144316 -and $f.Name -eq 'global-metadata.dat')
foreach ($bad in "C:\x.dll|1|$h", "\x.dll|1|$h", "..\x.dll|1|$h", "a\..\..\x.dll|1|$h", "x.dll:ads|1|$h", "x.dll|-1|$h", "x.dll|1|abc", "x.dll|1", $null) {
    Check "client file rule refused: '$bad'" ($null -eq [AcSignals]::ParseClientFile($bad))
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("ac-signals-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$dir\sub" | Out-Null
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes('original game file')
    [IO.File]::WriteAllBytes("$dir\good.dll", $bytes); [IO.File]::WriteAllBytes("$dir\sub\same-size.dat", $bytes); [IO.File]::WriteAllBytes("$dir\bigger.dll", $bytes + 1)
    $sha = (Get-FileHash -LiteralPath "$dir\good.dll" -Algorithm SHA256).Hash.ToLowerInvariant()
    $frules = [System.Collections.Generic.List[AcSignals+FileRule]]::new()
    foreach ($l in "good.dll|$($bytes.Length)|$sha", "sub\same-size.dat|$($bytes.Length)|$('1' * 64)", "bigger.dll|$($bytes.Length)|$sha", "gone.dll|5|$sha") { $frules.Add([AcSignals]::ParseClientFile($l)) }
    $lock = [IO.File]::Open("$dir\good.dll", 'Open', 'Read', 'Read')   # held open like a loaded DLL
    try { $labels = [AcSignals]::CheckClientFiles($dir, $frules) } finally { $lock.Dispose() }
    Check "client files: modified (hash), modified (size) and missing reported; the good one is not" (($labels -join ',') -eq 'client:modified:same-size.dat,client:modified:bigger.dll,client:missing:gone.dll')
    Check "client files: no folder -> no labels" ([AcSignals]::CheckClientFiles('', $frules).Count -eq 0)
} finally { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }

# ---- loaded modules (docs\AC_MODULE_SIGNAL.md): the classifier with a fake module list and a fake signature check ----
# AcModules.Scan runs on the caller's thread here, so PowerShell script blocks can stand in for the two delegates.
$mdir = Join-Path ([IO.Path]::GetTempPath()) ("ac-modules-test-" + [Guid]::NewGuid().ToString('N'))
$game = Join-Path $mdir 'game'; $other = Join-Path $mdir 'other'
New-Item -ItemType Directory -Force "$game\RustClient_Data\Plugins", $other | Out-Null
try {
    $bytes = [Text.Encoding]::ASCII.GetBytes('module bytes')
    foreach ($f in "$game\RustClient.exe", "$game\GameAssembly.dll", "$game\RustClient_Data\Plugins\steam_api64.dll", "$game\dxgi.dll", "$game\Weird Name (1).DLL",
                   "$other\overlay.dll", "$other\cheat.dll", "$other\broken.dll") { [IO.File]::WriteAllBytes($f, $bytes) }
    $h = (Get-FileHash -LiteralPath "$game\RustClient.exe" -Algorithm SHA256).Hash.ToLowerInvariant()
    $prefix = $h.Substring(0, 32)
    # a module in the user-writable part of the Windows folder must be judged like any other file, not skipped
    $winTemp = Join-Path $env:SystemRoot ("Temp\ac-modules-test-" + [Guid]::NewGuid().ToString('N') + ".dll")
    try { [IO.File]::WriteAllBytes($winTemp, [Text.Encoding]::ASCII.GetBytes('windows temp module')) } catch { $winTemp = $null }
    $tempPrefix = if ($winTemp) { (Get-FileHash -LiteralPath $winTemp -Algorithm SHA256).Hash.ToLowerInvariant().Substring(0, 32) } else { $null }
    $rules = [System.Collections.Generic.List[AcSignals+FileRule]]::new()
    foreach ($l in "RustClient.exe|$($bytes.Length)|$h", "GameAssembly.dll|$($bytes.Length)|$h", "RustClient_Data/Plugins/steam_api64.dll|$($bytes.Length)|$h") { $rules.Add([AcSignals]::ParseClientFile($l)) }
    $signedCalls = New-Object 'System.Collections.Generic.List[string]'
    # "signed" = overlay.dll only; broken.dll makes the check throw
    $signer = [Func[string, bool]]{ param($p) $signedCalls.Add($p); if ($p -like '*broken.dll') { throw 'no' }; $p -like '*overlay.dll' }
    $list = [System.Collections.Generic.List[string]]@("$game\RustClient.exe", "$game\GameAssembly.dll", "$game\RustClient_Data\Plugins\steam_api64.dll",
        "$game\dxgi.dll", "$game\Weird Name (1).DLL", "$env:SystemRoot\System32\ntdll.dll", "$env:SystemRoot\System32\DriverStore\nothing-here.dll",
        "$other\overlay.dll", "$other\cheat.dll", "$other\broken.dll", "$other\gone.dll")
    if ($winTemp) { $list.Add($winTemp) }
    $lister = [Func[int, System.Collections.Generic.List[string]]]{ param($id) $list }
    $mods = New-Object AcModules $game, $rules, 'RustClient.exe', $null
    $mods.IsSigned = $signer; $mods.ListModules = $lister; $mods.FirstScanDelayMs = 0
    $labels = $mods.Scan(1234)
    $expected = "client:extra-module:dxgi.dll,client:extra-module:weird_name__1_.dll,module:unsigned:$prefix,module:missing-file" + $(if ($winTemp) { ",module:unsigned:$tempPrefix" } else { '' })
    Check "modules: listed, Windows-code, signed and unreadable modules give no label; unlisted game-folder, unsigned and vanished ones do" (($labels -join ',') -eq $expected)
    Check "modules: game-folder and Windows code-folder modules never reach the signature check" (@($signedCalls | Where-Object { $_ -like "$game\*" -or $_ -like "$env:SystemRoot\System32\*" }).Count -eq 0)
    if ($winTemp) { Check "modules: a module in the writable Windows\Temp is judged like any other" ($signedCalls -contains $winTemp) } else { Write-Host "  SKIP  Windows\Temp module (could not create a file there)" }
    $empty = New-Object AcModules $null, $null, '', $null
    $judged = 'System32\Tasks', 'System32\com\dmp', 'System32\FxsTmp', 'System32\Microsoft\Crypto\RSA\MachineKeys', 'System32\spool\PRINTERS', 'SysWOW64\com\dmp' |
        Where-Object { $empty.Classify("$env:SystemRoot\$_\nothing-here.dll") -ne 'module:missing-file' }
    Check "modules: Windows code folders are skipped, their user-writable parts are judged ($($judged -join ','))" (
        @($judged).Count -eq 0 -and $null -eq $empty.Classify("$env:SystemRoot\SysWOW64\nothing-here.dll") -and $null -eq $empty.Classify("$env:SystemRoot\System32\DriverStore\nothing-here.dll"))
    Check "modules: every label passes CleanSignals unchanged" (([AcSession]::CleanSignals($labels) -join ',') -eq ($labels -join ','))
    $n = $signedCalls.Count; $c1 = $mods.Classified
    $labels2 = $mods.Scan(1234)
    Check "modules: a second scan classifies nothing again (cache) and keeps the labels" ($signedCalls.Count -eq $n -and $mods.Classified -eq $c1 -and ($labels2 -join ',') -eq ($labels -join ','))
    $late = New-Object AcModules $game, $rules, 'RustClient.exe', $null
    $late.IsSigned = $signer; $late.ListModules = $lister; $late.FirstScanDelayMs = 60000
    Check "modules: nothing is scanned before the first-scan delay" ($late.Scan(1234).Count -eq 0 -and $late.Scans -eq 0)
    $late.FirstScanDelayMs = 0
    Check "modules: pid 0 (no game) -> no scan" ($late.Scan(0).Count -eq 0 -and $late.Scans -eq 0)
    $failed = New-Object AcModules $game, $rules, 'RustClient.exe', $null
    $failed.FirstScanDelayMs = 0; $failed.ListModules = [Func[int, System.Collections.Generic.List[string]]]{ param($id) $null }
    Check "modules: a process that can't be opened -> module:scan-failed only" (($failed.Scan(1234) -join ',') -eq 'module:scan-failed')
    $partial = [System.Collections.Generic.List[AcSignals+FileRule]]::new(); $partial.Add($rules[1])
    $p = New-Object AcModules $game, $partial, 'RustClient.exe', $null
    $p.IsSigned = $signer; $p.ListModules = $lister; $p.FirstScanDelayMs = 0
    Check "modules: without the game exe on the file list, game-folder modules are not judged" (($p.Scan(1) -join ',') -eq ("module:unsigned:$prefix,module:missing-file" + $(if ($winTemp) { ",module:unsigned:$tempPrefix" } else { '' })))
    Check "modules: name cleaning (lower case, [a-z0-9_.-], 40 chars)" ([AcModules]::CleanName('Weird Name (1).DLL') -eq 'weird_name__1_.dll' -and [AcModules]::CleanName(('a' * 50) + '.dll').Length -eq 40)
    Check "modules: hash prefix is the first 32 hex of the file's SHA-256" ([AcModules]::HashPrefix("$other\cheat.dll") -eq $prefix)
    Check "modules: hash prefix of a missing file -> null" ($null -eq [AcModules]::HashPrefix("$other\gone.dll"))
    Check "modules: a file without a signature is not signed" (-not [AcModules]::HasTrustedSignature("$other\cheat.dll"))
} finally { Remove-Item -Recurse -Force $mdir -ErrorAction SilentlyContinue; if ($winTemp) { Remove-Item -Force $winTemp -ErrorAction SilentlyContinue } }

# catalog signatures: Windows' own files carry no signature in the file; the system catalogs vouch for them
$catalogSigned = 'kernel32.dll', 'ntdll.dll', 'user32.dll', 'gdi32.dll' | ForEach-Object { Join-Path $env:SystemRoot "System32\$_" } |
    Where-Object { $s = Get-AuthenticodeSignature -LiteralPath $_; $s.Status -eq 'Valid' -and $s.SignatureType -eq 'Catalog' } | Select-Object -First 1
if ($catalogSigned) {
    # (HasEmbeddedSignature may say yes too: WinVerifyTrust's file check consults the catalogs on its own on some Windows
    # versions; the explicit catalog lookup is for the ones where it doesn't)
    Check "modules: a catalog-signed Windows file is found in the catalogs and counts as signed ($(Split-Path $catalogSigned -Leaf))" ([AcModules]::HasCatalogSignature($catalogSigned) -and [AcModules]::HasTrustedSignature($catalogSigned))
} else { Write-Host "  SKIP  catalog-signature check (no catalog-signed System32 file among the candidates)" }

# live, on this very process: Windows PowerShell and pwsh load only Windows and Microsoft-signed modules
$me = [Diagnostics.Process]::GetCurrentProcess()
$live = [AcModules]::ListProcessModules($me.Id)
Check "modules: this process's module list is read and includes its own exe" ($null -ne $live -and $live.Count -gt 10 -and @($live | Where-Object { $_ -ieq $me.MainModule.FileName }).Count -eq 1)
Check "modules: a process that doesn't exist can't be opened" ($null -eq [AcModules]::ListProcessModules(4000000))
$signedOne = $live | Where-Object { $_ -notlike "$env:SystemRoot\*" } | Where-Object { (Get-AuthenticodeSignature -LiteralPath $_).Status -eq 'Valid' } | Select-Object -First 1
if ($signedOne) { Check "modules: a validly signed module counts as signed ($(Split-Path $signedOne -Leaf))" ([AcModules]::HasTrustedSignature($signedOne)) }
else { Write-Host "  SKIP  signed-module check (every module of this process is under %SystemRoot%)" }
$self = New-Object AcModules $null, $null, 'RustClient.exe', $null
$self.FirstScanDelayMs = 0
$selfLabels = $self.Scan($me.Id)
# this host is .NET, which JITs into private executable memory, so the phase-B mem label may legitimately appear
# here (it would not on the IL2CPP game, which is AOT); the module part must still report nothing
$selfModuleLabels = @($selfLabels | Where-Object { $_ -notlike 'mem:*' })
Check "modules: a live module scan of this process reports no module labels ($($selfModuleLabels -join ','))" ($selfModuleLabels.Count -eq 0 -and $self.Scans -eq 1 -and $self.Classified -gt 0)

# ---- phase B: executable memory no module backs (manually mapped injection) ----
# The bucketing (the floor, the label) is pure and tested with a fake region list; the real VirtualQueryEx walk is
# covered by the live check below. A region list standing in for what RealAnonExecRegions returns:
function MemScan($sizes) {
    $m = New-Object AcModules $null, $null, '', $null
    $m.FirstScanDelayMs = 0
    $m.ListModules = [Func[int, System.Collections.Generic.List[string]]] { param($id) New-Object System.Collections.Generic.List[string] }
    $m.ListAnonExecRegions = [Func[int, System.Collections.Generic.List[long]]] { param($id) $l = New-Object System.Collections.Generic.List[long]; foreach ($s in $sizes) { $l.Add([long]$s) }; $l }
    return $m.Scan(1)
}
Check "mem: nothing unbacked -> no label"                              ((@(MemScan @()) | Where-Object { $_ -like 'mem:*' }).Count -eq 0)
Check "mem: a little, under the floor (overlay trampolines) -> no label" ((@(MemScan @(65536, 131072, 4096)) | Where-Object { $_ -like 'mem:*' }).Count -eq 0)
Check "mem: a mapped image over the floor -> mem:private-exec:large"    (@(MemScan @(30MB)) -contains 'mem:private-exec:large')
Check "mem: many regions summing over the floor -> large"              (@(MemScan @(1MB, 1MB, 1MB)) -contains 'mem:private-exec:large')
$rcProc = Get-Process RustClient -ErrorAction SilentlyContinue | Select-Object -First 1
if ($rcProc) {
    $regions = [AcModules]::RealAnonExecRegions($rcProc.Id)
    $usedMb = if ($regions) { [math]::Round((($regions | Measure-Object -Sum).Sum) / 1MB, 2) } else { 'n/a' }
    Check "mem: a live scan of the running game returns a region list ($usedMb MB unbacked exec)" ($null -ne $regions)
} else { Write-Host "  SKIP  live memory scan (RustClient not running)" }

# ---- optional: the real client against a running session service ----
# ---- game link (#1): the code the server sends to the game, echoed from its log ----
$code = '0123456789abcdef'
Check "link: heartbeat carries the code" ([AcSession]::BuildHeartbeat($sess, 2, 5, $true, $null, $code) -eq "{`"v`":1,`"session`":`"$sess`",`"seq`":2,`"t`":5,`"game`":1,`"link`":`"$code`",`"signals`":[]}")
Check "link: a malformed code is left out" ([AcSession]::BuildHeartbeat($sess, 2, 5, $true, $null, '0123456789ABCDEF') -notmatch 'link')
Check "link: no code, no field" ([AcSession]::BuildHeartbeat($sess, 2, 5, $true, $null, $null) -notmatch 'link')

$logFile = Join-Path ([IO.Path]::GetTempPath()) ("ac-link-" + [guid]::NewGuid().ToString('N') + ".log")
$gl = New-Object AcGameLog $logFile
Check "game log: missing file -> null" ($null -eq $gl.Poll())
[IO.File]::WriteAllText($logFile, "Loading map`r`nrustorigin-link 1111111111111111`r`nsomething else`r`n")
Check "game log: code found (CRLF lines)" ($gl.Poll() -eq '1111111111111111')
[IO.File]::AppendAllText($logFile, "rustorigin-link 2222222222222222`n")
Check "game log: the newest code wins" ($gl.Poll() -eq '2222222222222222')
[IO.File]::AppendAllText($logFile, "rustorigin-link 33333333")
Check "game log: half a line waits for its end" ($gl.Poll() -eq '2222222222222222')
[IO.File]::AppendAllText($logFile, "33333333`n")
Check "game log: completed line read next time" ($gl.Poll() -eq '3333333333333333')
[IO.File]::AppendAllText($logFile, "chat: rustorigin-link 4444444444444444`nrustorigin-link 55555555555555556`n")
Check "game log: only whole lines of the exact form count" ($gl.Poll() -eq '3333333333333333')
[IO.File]::WriteAllText($logFile, "new game`n")
Check "game log: a new (shorter) log starts over" ($null -eq $gl.Poll())
$fs = New-Object IO.FileStream($logFile, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
$w = New-Object IO.StreamWriter($fs); $w.Write("rustorigin-link 6666666666666666`n"); $w.Flush()
Check "game log: read while the game holds it open" ($gl.Poll() -eq '6666666666666666')
$w.Dispose(); Remove-Item $logFile

# the client sends a new code at once, not after the 30 s interval (here 30 x 100 ms = 3 s), and only once
$svc = New-Object FakeService
$box = New-Object 'Harness+CodeBox'
$c = [Harness]::WithLink($svc, 1000000, $box, 100); $c.Start()
Start-Sleep -Milliseconds 500
$box.Code = 'abcdefabcdefabcd'
$t0 = $svc.Clock.ElapsedMilliseconds
Start-Sleep -Milliseconds 1500
$c.Stop(); [void]$c.Wait(3000)
Check "link: code sent within ~1 s, not after the 3 s interval" ($svc.FirstLinkMs -ge 0 -and ($svc.FirstLinkMs - $t0) -lt 1300)
Check "link: each code sent once" ($svc.Links.Count -eq 1 -and $svc.Links[0] -eq 'abcdefabcdefabcd')

if ($env:AC_TEST_URL) {
    $sid = '76561198000000042'
    $c = [Harness]::Live($env:AC_TEST_URL, $sid, 3); $c.Start()
    $done = $c.Wait(20000); if (-not $done) { $c.Stop() }
    Check "live: real HTTP client completed 3 heartbeats against $env:AC_TEST_URL" ($done -and $c.Opened -eq 1 -and $c.HeartbeatsOk -eq 3 -and $c.HeartbeatsRejected -eq 0)
} else { Write-Host "  SKIP  live test (set AC_TEST_URL to a running ac_session.py)" }

if ($script:fail -gt 0) { Write-Host "`n$($script:fail) test(s) FAILED"; exit 1 }
Write-Host "`nall anti-cheat session tests passed"; exit 0
