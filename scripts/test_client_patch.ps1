# End-to-end tests for the client delta update: builds a pack from two small synthetic client
# folders with the real builder (scripts\client_patch_builder.cs), applies it with the real launcher
# code (src\ClientPatch.cs), and checks the result byte-for-byte - plus the ways an update must
# refuse to apply. Run: .\scripts\test_client_patch.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'client_filter.ps1')
# Windows PowerShell needs the two zip assemblies named; PowerShell 7 already references them, and
# naming any there would drop the rest of its default references.
$zipRefs = if ($PSVersionTable.PSEdition -eq 'Core') { @{} } else { @{ ReferencedAssemblies = 'System.IO.Compression', 'System.IO.Compression.FileSystem' } }
Add-Type -Path (Join-Path $repo 'src\ClientPatch.cs'), (Join-Path $PSScriptRoot 'client_patch_builder.cs') @zipRefs

$script:fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fail++ } }
# True when the script block throws the given exception type (unwrapping PowerShell's method wrapper).
function Throws([type]$type, [scriptblock]$block) {
    try { & $block | Out-Null; return $false }
    catch { $e = $_.Exception; while ($e.InnerException -and $e -isnot $type) { $e = $e.InnerException }; return ($e -is $type) }
}
function Sha($p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }
function Put($path, [byte[]]$bytes) { New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null; [IO.File]::WriteAllBytes($path, $bytes) }

$rng = New-Object Random 42
function Rand([int]$n) { $b = New-Object byte[] $n; $rng.NextBytes($b); ,$b }

$root = Join-Path ([IO.Path]::GetTempPath()) ("rustorigin-patch-test-" + [Guid]::NewGuid().ToString('N'))
$old = "$root\old"; $new = "$root\new"; $pack = "$root\test.patchpack"
try {
    # ---- two synthetic builds ----
    $A = Rand 300000; $B = Rand 300000; $C = Rand 300000; $X = Rand 50000
    $same = Rand 200000; $keys = [Text.Encoding]::ASCII.GetBytes("bind w forward`n")
    $oldAsm = Rand 10000; $newAsm = Rand 12000; $extra = Rand 20000

    Put "$old\same.bin" $same;                           Put "$new\same.bin" $same
    Put "$old\cfg\keys_default.cfg" $keys;               Put "$new\cfg\keys_default.cfg" $keys
    Put "$old\Bundles\shared\big.bundle" ($A + $B + $C); Put "$new\Bundles\shared\big.bundle" ($A + $X + $C + $B)   # insert + move
    Put "$old\gone.bin" (Rand 5000)                                                                                # dropped by the new build
    Put "$old\GameAssembly.dll" $oldAsm;                 Put "$new\GameAssembly.dll" $newAsm                       # fully new
    Put "$new\RustClient_Data\new file.dat" ($B + $extra)                                                          # new file reusing old data
    Put "$new\empty.txt" (New-Object byte[] 0)
    Put "$new\cfg\client.cfg" (Rand 100)                 # personal cfg in the source folder: must never ship

    $res = [ClientPatchBuilder]::Build($old, [string[]]@(Get-ClientFiles $old), $new, [string[]]@(Get-ClientFiles $new), '2021-01', '2021-04', $pack, $true, $null)
    Check "builder: 2 unchanged files"         ($res.UnchangedFiles -eq 2)
    Check "builder: 4 rebuilt files"           ($res.RebuiltFiles -eq 4)
    Check "builder: 1 removed file"            ($res.DeletedFiles -eq 1)
    # 82,000 bytes are truly new ($X, $extra, the new GameAssembly.dll); the rest exists in the old build
    Check "builder: reuses the installed data" ($res.DataBytes -ge 82000 -and $res.CopiedBytes -gt 2 * $res.DataBytes)
    Check "builder: pack hash matches file"    ($res.PackSha256 -eq (Sha $pack))

    # A player's install: the old build plus the launcher's files and personal settings.
    function New-Install($name) {
        $d = "$root\$name"; Copy-Item $old $d -Recurse
        Put "$d\RustoriginLauncher.exe" (Rand 100); Put "$d\cfg\client.cfg" (Rand 64)
        [IO.File]::WriteAllText("$d\.rustorigin-installed", '2026-09-29T23:10:14.7204057+02:00')
        $d
    }

    # ---- happy path ----
    $inst = New-Install 'install'
    $personal = Sha "$inst\cfg\client.cfg"; $launcher = Sha "$inst\RustoriginLauncher.exe"
    Check "probe: old build matches"           ([ClientPatch]::ProbeMatches($inst, "GameAssembly.dll|$($oldAsm.Length)"))
    Check "probe: other size does not"         (-not [ClientPatch]::ProbeMatches($inst, "GameAssembly.dll|$($newAsm.Length)"))
    Check "probe: blank always matches"        ([ClientPatch]::ProbeMatches($inst, ''))
    Check "marker: legacy has no client"       ([ClientPatch]::ReadMarkerClient("$inst\.rustorigin-installed") -eq '')

    $recipe = [ClientPatch]::Stage($pack, $inst, [Func[bool]]{ $false }, $null)
    Check "stage: recipe versions"             ($recipe.From -eq '2021-01' -and $recipe.To -eq '2021-04')
    Check "stage: pending commit"              ([ClientPatch]::HasPendingCommit($inst))
    Check "stage: install still the old build" ((Sha "$inst\Bundles\shared\big.bundle") -eq (Sha "$old\Bundles\shared\big.bundle"))

    $to = [ClientPatch]::Commit($inst, "$inst\.rustorigin-installed")
    Check "commit: returns the new version"    ($to -eq '2021-04')
    Check "commit: marker records the client"  ([ClientPatch]::ReadMarkerClient("$inst\.rustorigin-installed") -eq '2021-04')
    $allMatch = $true
    foreach ($rel in Get-ClientFiles $new) { $p = Join-Path $inst $rel; if (-not (Test-Path -LiteralPath $p) -or (Sha $p) -ne (Sha (Join-Path $new $rel))) { $allMatch = $false; Write-Host "    differs: $rel" } }
    Check "commit: every file equals the new build" $allMatch
    Check "commit: dropped file removed"       (-not (Test-Path "$inst\gone.bin"))
    Check "commit: personal cfg untouched"     ((Sha "$inst\cfg\client.cfg") -eq $personal)
    Check "commit: launcher untouched"         ((Sha "$inst\RustoriginLauncher.exe") -eq $launcher)
    Check "commit: staging removed"            (-not (Test-Path "$inst\_update"))
    Check "commit: nothing pending"            (-not [ClientPatch]::HasPendingCommit($inst))
    $mk = "$root\marker-test.txt"; [IO.File]::WriteAllText($mk, [ClientPatch]::MarkerText('2021-07'))
    Check "marker: text round-trips"           ([ClientPatch]::ReadMarkerClient($mk) -eq '2021-07')

    # ---- an install that is not the expected build is refused and left untouched ----
    $bad = New-Install 'modified'
    $bytes = [IO.File]::ReadAllBytes("$bad\Bundles\shared\big.bundle"); $bytes[1000] = $bytes[1000] -bxor 0xFF
    [IO.File]::WriteAllBytes("$bad\Bundles\shared\big.bundle", $bytes); $before = Sha "$bad\Bundles\shared\big.bundle"
    Check "modified install: refused"          (Throws ([PatchException]) { [ClientPatch]::Stage($pack, $bad, [Func[bool]]{ $false }, $null) })
    Check "modified install: untouched"        ((Sha "$bad\Bundles\shared\big.bundle") -eq $before -and (Test-Path "$bad\gone.bin"))
    Check "modified install: no staging left"  (-not (Test-Path "$bad\_update"))

    $short = New-Install 'wrongsize'
    Put "$short\Bundles\shared\big.bundle" (Rand 1234)
    Check "wrong-size source: refused"         (Throws ([PatchException]) { [ClientPatch]::Stage($pack, $short, [Func[bool]]{ $false }, $null) })
    Remove-Item "$short\Bundles\shared\big.bundle"
    Check "missing source: refused"            (Throws ([PatchException]) { [ClientPatch]::Stage($pack, $short, [Func[bool]]{ $false }, $null) })

    # ---- cancel and interruption ----
    $c = New-Install 'cancel'
    Check "cancel: throws cancelled"           (Throws ([OperationCanceledException]) { [ClientPatch]::Stage($pack, $c, [Func[bool]]{ $true }, $null) })
    Check "cancel: no staging left"            (-not (Test-Path "$c\_update"))

    $i = New-Install 'interrupted'
    [ClientPatch]::Stage($pack, $i, [Func[bool]]{ $false }, $null) | Out-Null
    Remove-Item "$i\GameAssembly.dll"; Move-Item "$i\_update\files\GameAssembly.dll" "$i\GameAssembly.dll"   # a swap cut off after one file
    [ClientPatch]::Commit($i, "$i\.rustorigin-installed") | Out-Null
    $resumed = $true
    foreach ($rel in Get-ClientFiles $new) { if ((Sha (Join-Path $i $rel)) -ne (Sha (Join-Path $new $rel))) { $resumed = $false } }
    Check "interrupted commit: finishes"       ($resumed -and -not (Test-Path "$i\_update"))
    Check "interrupted commit: marker written" ([ClientPatch]::ReadMarkerClient("$i\.rustorigin-installed") -eq '2021-04')
    Check "commit without staging: refused"    (Throws ([PatchException]) { [ClientPatch]::Commit($i, "$i\.rustorigin-installed") })

    # Something else (Explorer, antivirus) holding the staging folder open must not turn a finished
    # update into a failure, nor leave it unlabelled.
    $l = New-Install 'locked'
    [ClientPatch]::Stage($pack, $l, [Func[bool]]{ $false }, $null) | Out-Null
    $hold = [IO.File]::Open("$l\_update\held-by-something.tmp", 'Create', 'ReadWrite', 'None')
    try {
        $lockedOk = -not (Throws ([Exception]) { [ClientPatch]::Commit($l, "$l\.rustorigin-installed") })
        Check "locked staging: commit still succeeds" $lockedOk
        Check "locked staging: client labelled"    ([ClientPatch]::ReadMarkerClient("$l\.rustorigin-installed") -eq '2021-04')
        Check "locked staging: nothing pending"    (-not [ClientPatch]::HasPendingCommit($l))
        $lockedSame = $true
        foreach ($rel in Get-ClientFiles $new) { if ((Sha (Join-Path $l $rel)) -ne (Sha (Join-Path $new $rel))) { $lockedSame = $false } }
        Check "locked staging: files are the new build" $lockedSame
    } finally { $hold.Dispose() }

    # A crash after the marker was written but before "ready" was removed: the next run just finishes.
    $r2 = New-Install 'recommit'
    [ClientPatch]::Stage($pack, $r2, [Func[bool]]{ $false }, $null) | Out-Null
    [ClientPatch]::Commit($r2, "$r2\.rustorigin-installed") | Out-Null
    New-Item -ItemType Directory -Force "$r2\_update" | Out-Null; [IO.File]::WriteAllText("$r2\_update\ready", '2021-04')
    Check "re-commit: pending again"           ([ClientPatch]::HasPendingCommit($r2))
    Check "re-commit: finishes cleanly"        (([ClientPatch]::Commit($r2, "$r2\.rustorigin-installed") -eq '2021-04') -and -not [ClientPatch]::HasPendingCommit($r2))

    # ---- a damaged pack ----
    $cut = "$root\cut.patchpack"; $pb = [IO.File]::ReadAllBytes($pack); [IO.File]::WriteAllBytes($cut, $pb[0..([int]($pb.Length / 2))])
    $d = New-Install 'damaged'
    Check "truncated pack: refused"            (Throws ([PatchException]) { [ClientPatch]::Stage($cut, $d, [Func[bool]]{ $false }, $null) })

    # ---- recipe validation ----
    function Parse($text) { [ClientPatch]::ParseRecipe((New-Object IO.StringReader $text)) }
    $z = '0' * 64
    Check "recipe: minimal parses"             ((Parse "rustorigin-patch-v1`nfrom a`nto b`nend`n").To -eq 'b')
    Check "recipe: wrong header refused"       (Throws ([PatchException]) { Parse "something else`nend`n" })
    Check "recipe: truncated refused"          (Throws ([PatchException]) { Parse "rustorigin-patch-v1`nfrom a`nto b`n" })
    Check "recipe: path traversal refused"     (Throws ([PatchException]) { Parse "rustorigin-patch-v1`nfile 0 $z ..\evil.dll`nend`n" })
    Check "recipe: absolute path refused"      (Throws ([PatchException]) { Parse "rustorigin-patch-v1`nfile 0 $z C:\Windows\evil.dll`nend`n" })
    Check "recipe: launcher file refused"      (Throws ([PatchException]) { Parse "rustorigin-patch-v1`ndelete RustoriginLauncher.exe`nend`n" })
    Check "recipe: ops must add up"            (Throws ([PatchException]) { Parse "rustorigin-patch-v1`nfile 10 $z a.bin`nd 4`nend`n" })
    Check "recipe: unknown source refused"     (Throws ([PatchException]) { Parse "rustorigin-patch-v1`nfile 4 $z a.bin`nc 0 0 4`nend`n" })
    Check "recipe: copy past source refused"   (Throws ([PatchException]) { Parse "rustorigin-patch-v1`nsource 3 s.bin`nfile 4 $z a.bin`nc 0 0 4`nend`n" })
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }

if ($script:fail -gt 0) { Write-Host "`n$($script:fail) test(s) FAILED"; exit 1 }
Write-Host "`nall client-patch tests passed"; exit 0
