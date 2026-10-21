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
Add-Type -TypeDefinition ((Get-Content (Join-Path $repo 'src\AcSession.cs') -Raw) + "`n" + $fake)

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

# ---- optional: the real client against a running session service ----
if ($env:AC_TEST_URL) {
    $sid = '76561198000000042'
    $c = [Harness]::Live($env:AC_TEST_URL, $sid, 3); $c.Start()
    $done = $c.Wait(20000); if (-not $done) { $c.Stop() }
    Check "live: real HTTP client completed 3 heartbeats against $env:AC_TEST_URL" ($done -and $c.Opened -eq 1 -and $c.HeartbeatsOk -eq 3 -and $c.HeartbeatsRejected -eq 0)
} else { Write-Host "  SKIP  live test (set AC_TEST_URL to a running ac_session.py)" }

if ($script:fail -gt 0) { Write-Host "`n$($script:fail) test(s) FAILED"; exit 1 }
Write-Host "`nall anti-cheat session tests passed"; exit 0
