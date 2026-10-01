# Builds the client DELTA pack: what a player on the OLD client build needs to become the NEW build
# without re-downloading the whole client. The launcher (src\ClientPatch.cs) downloads the pack,
# SHA-256-verifies it, rebuilds the changed files from the installed ones + the pack, verifies every
# rebuilt file, and only then swaps them in. See CLAUDE.md "Client updates (delta pack)".
#
# Usage (PowerShell), from the repo root:
#   .\scripts\make_client_patch.ps1 -OldDir E:\Rustorigin -NewDir E:\RustClient-2021-04 -From 2021-01 -To 2021-04
#
#   -OldDir  a folder holding the build players have now (an installed client works: the launcher's
#            own files and personal cfg are filtered out)
#   -NewDir  the new client folder - the SAME folder you run package_client.ps1 on
#   -From/-To  client version labels; -To must equal ClientVersion= in launcher.cfg
#   -OutFile default: <repo>\RustClient-<From>-to-<To>.patchpack
#   -Level   Optimal (default, smaller) or Fastest
#
# Both folders are filtered with scripts\client_filter.ps1, exactly like package_client.ps1.
param(
    [Parameter(Mandatory)][string]$OldDir,
    [Parameter(Mandatory)][string]$NewDir,
    [Parameter(Mandatory)][string]$From,
    [Parameter(Mandatory)][string]$To,
    [string]$OutFile,
    [ValidateSet('Fastest','Optimal')][string]$Level = 'Optimal'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'client_filter.ps1')
# Windows PowerShell needs the two zip assemblies named; PowerShell 7 already references them, and
# naming any there would drop the rest of its default references.
$zipRefs = if ($PSVersionTable.PSEdition -eq 'Core') { @{} } else { @{ ReferencedAssemblies = 'System.IO.Compression', 'System.IO.Compression.FileSystem' } }
Add-Type -Path (Join-Path $repo 'src\ClientPatch.cs'), (Join-Path $PSScriptRoot 'client_patch_builder.cs') @zipRefs

foreach ($d in $OldDir, $NewDir) { if (-not (Test-Path -LiteralPath $d)) { Write-Error "Folder not found: $d"; exit 1 } }
$OldDir = (Resolve-Path -LiteralPath $OldDir).Path.TrimEnd('\')
$NewDir = (Resolve-Path -LiteralPath $NewDir).Path.TrimEnd('\')
if ($From -match '\s' -or $To -match '\s') { Write-Error "-From / -To must not contain spaces."; exit 1 }
if (-not $OutFile) { $OutFile = Join-Path $repo "RustClient-$From-to-$To.patchpack" }
foreach ($d in $OldDir, $NewDir) {
    if ($OutFile.StartsWith($d + '\', [StringComparison]::OrdinalIgnoreCase)) { Write-Error "OutFile must be OUTSIDE the client folders."; exit 1 }
}

$oldRel = @(Get-ClientFiles $OldDir)
$newRel = @(Get-ClientFiles $NewDir)
if (-not $oldRel.Count -or -not $newRel.Count) { Write-Error "A client folder has no shipping files."; exit 1 }

Write-Host ""
Write-Host "Old ($From): $OldDir   ($($oldRel.Count) files)"
Write-Host "New ($To): $NewDir   ($($newRel.Count) files)"
Write-Host "Output    : $OutFile"
Write-Host ""
Write-Host "Indexing the old build, then matching the new one (reads both builds once)..."

$sw  = [System.Diagnostics.Stopwatch]::StartNew()
$log = [Action[string]]{ param($m) Write-Host "  $m" }
$res = [ClientPatchBuilder]::Build($OldDir, [string[]]$oldRel, $NewDir, [string[]]$newRel, $From, $To, $OutFile, ($Level -eq 'Optimal'), $log)
$sw.Stop()

Set-Content -LiteralPath ($OutFile + '.sha256') -Value $res.PackSha256 -Encoding ascii -NoNewline

# A cheap "is this install the build the pack upgrades from?" check for PatchProbe=: the old
# build's GameAssembly.dll (or its game exe) and its size, if that differs in the new build.
$probe = ''
foreach ($cand in 'GameAssembly.dll','RustClient.exe') {
    $o = Join-Path $OldDir $cand; $n = Join-Path $NewDir $cand
    if ((Test-Path -LiteralPath $o) -and -not ((Test-Path -LiteralPath $n) -and (Get-Item -LiteralPath $n).Length -eq (Get-Item -LiteralPath $o).Length)) {
        $probe = "$cand|$((Get-Item -LiteralPath $o).Length)"; break
    }
}

Write-Host ""
Write-Host ("DONE in {0:N1} min." -f $sw.Elapsed.TotalMinutes)
Write-Host ("  unchanged files : {0}" -f $res.UnchangedFiles)
Write-Host ("  rebuilt files   : {0}  ({1:N2} GB)" -f $res.RebuiltFiles, ($res.RebuiltBytes / 1GB))
Write-Host ("    reused from the installed client : {0:N2} GB  (from {1} files)" -f ($res.CopiedBytes / 1GB), $res.Sources)
Write-Host ("    new data in the pack             : {0:N2} GB" -f ($res.DataBytes / 1GB))
Write-Host ("  removed files   : {0}" -f $res.DeletedFiles)
Write-Host ("  pack size       : {0:N2} GB  -> players download this instead of the full client" -f ($res.PackBytes / 1GB))
Write-Host ("  free space a player needs while updating: ~{0:N1} GB" -f (($res.RebuiltBytes + $res.PackBytes) / 1GB + 0.5))
Write-Host ""
Write-Host ("SHA-256: {0}" -f $res.PackSha256)
Write-Host ("        (also written to {0}.sha256)" -f (Split-Path $OutFile -Leaf))
Write-Host ""
Write-Host "Next:"
Write-Host "  1) upload $OutFile to your server (direct download link)"
Write-Host "  2) in launcher.cfg set:"
Write-Host "       ClientVersion=$To"
Write-Host "       PatchUrl=<that link>"
Write-Host "       PatchSha256=$($res.PackSha256)"
if ($probe) { Write-Host "       PatchProbe=$probe" }
Write-Host "     and point DownloadUrl/Sha256 at the FULL zip of the new build (package_client.ps1 on the"
Write-Host "     same -NewDir) - it serves new players and anyone the pack does not fit."
Write-Host "  3) rebuild the launcher (make_release.ps1) so the new values are embedded"
