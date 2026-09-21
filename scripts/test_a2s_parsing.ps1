# Tests for the live server-status query (src\A2S.cs). Compiles the real source with Add-Type,
# unit-tests the A2S_INFO reply parser against crafted bytes, and runs one end-to-end query against
# a local UDP responder that speaks the challenge->info handshake. Run: .\scripts\test_a2s_parsing.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $repo 'src\A2S.cs')

$script:fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fail++ } }

# --- craft a valid A2S_INFO reply: header, 'I', protocol, 4 strings, appid short, players, max, bots
function New-InfoPacket([int]$players, [int]$max) {
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([byte[]](0xFF,0xFF,0xFF,0xFF)); $bw.Write([byte]0x49); $bw.Write([byte]17)
    foreach ($s in @("RustOrigin Mock","Procedural","rust","Rust")) { $bw.Write([System.Text.Encoding]::ASCII.GetBytes($s)); $bw.Write([byte]0) }
    $bw.Write([UInt16]0); $bw.Write([byte]$players); $bw.Write([byte]$max); $bw.Write([byte]0); $bw.Flush()
    return $ms.ToArray()
}

# --- parser unit tests (no network) ---
$pl = 0; $mx = 0
$ok = [A2S]::ParseInfo((New-InfoPacket 42 100), [ref]$pl, [ref]$mx)
Check "ParseInfo valid -> true"      ($ok)
Check "ParseInfo players = 42"       ($pl -eq 42)
Check "ParseInfo max = 100"          ($mx -eq 100)

$pl = 0; $mx = 0
Check "ParseInfo 0/200"              ([A2S]::ParseInfo((New-InfoPacket 0 200), [ref]$pl, [ref]$mx) -and $pl -eq 0 -and $mx -eq 200)

$pl = 0; $mx = 0
Check "ParseInfo garbage -> false"   (-not [A2S]::ParseInfo(([byte[]](1,2,3,4,5,6,7,8)), [ref]$pl, [ref]$mx))
Check "ParseInfo null -> false"      (-not [A2S]::ParseInfo($null, [ref]$pl, [ref]$mx))
Check "ParseInfo truncated -> false" (-not [A2S]::ParseInfo(([byte[]](0xFF,0xFF,0xFF,0xFF,0x49,17)), [ref]$pl, [ref]$mx))

# --- request builder ---
$req = [A2S]::BuildInfoRequest($null)
Check "Request header 0xFFFFFFFF"    ($req[0] -eq 0xFF -and $req[1] -eq 0xFF -and $req[2] -eq 0xFF -and $req[3] -eq 0xFF)
Check "Request type 'T' (0x54)"      ($req[4] -eq 0x54)
Check "Request length (no challenge)" ($req.Length -eq 25)
Check "Request length (challenge)"    (([A2S]::BuildInfoRequest([byte[]](1,2,3,4))).Length -eq 29)

# --- end-to-end query against a local responder (challenge -> info) ---
$port = 28016
$job = Start-Job -ArgumentList $port -ScriptBlock {
    param($port)
    $udp = New-Object System.Net.Sockets.UdpClient($port)
    $udp.Client.ReceiveTimeout = 400   # unblock the loop periodically so Stop-Job can end it cleanly
    $ep  = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        try { $data = $udp.Receive([ref]$ep) } catch { continue }   # timeout -> re-check deadline
        if ($data.Length -ge 5 -and $data[4] -eq 0x54) {
            if ($data.Length -lt 29) {
                $resp = [byte[]](0xFF,0xFF,0xFF,0xFF,0x41,0x11,0x22,0x33,0x44)   # challenge
            } else {
                $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter($ms)
                $bw.Write([byte[]](0xFF,0xFF,0xFF,0xFF)); $bw.Write([byte]0x49); $bw.Write([byte]17)
                foreach ($s in @("RustOrigin Mock","Procedural","rust","Rust")) { $bw.Write([System.Text.Encoding]::ASCII.GetBytes($s)); $bw.Write([byte]0) }
                $bw.Write([UInt16]0); $bw.Write([byte]42); $bw.Write([byte]100); $bw.Write([byte]0); $bw.Flush()
                $resp = $ms.ToArray()
            }
            [void]$udp.Send($resp, $resp.Length, $ep)
        }
    }
}
try {
    Start-Sleep -Milliseconds 800
    $pl = 0; $mx = 0
    $ok = [A2S]::TryQueryInfo("127.0.0.1", $port, 2500, [ref]$pl, [ref]$mx)
    Check "TryQueryInfo online -> true" ($ok)
    Check "TryQueryInfo players = 42"   ($pl -eq 42)
    Check "TryQueryInfo max = 100"      ($mx -eq 100)

    # a port nobody answers on -> offline (false), quickly
    $pl = 0; $mx = 0
    Check "TryQueryInfo dead port -> false" (-not [A2S]::TryQueryInfo("127.0.0.1", 28017, 800, [ref]$pl, [ref]$mx))
} finally {
    Stop-Job $job -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $job -Force -ErrorAction SilentlyContinue | Out-Null
}

if ($script:fail -gt 0) { Write-Host "`n$($script:fail) test(s) FAILED"; exit 1 }
Write-Host "`nall A2S tests passed"; exit 0
