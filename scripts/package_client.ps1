# Packages the Rust client folder into a single RustClient.zip for hosting.
# - Streams via .NET ZipArchive (Zip64) so it handles the full ~16 GB.
# - Client files sit at the zip ROOT (RustClient.exe at top level).
# - Automatically EXCLUDES things that must never ship, even if the game
#   recreated them after a test launch:
#     temp\            (video cache)          maps\   (map cache, fetched from the server)
#     cfg\*            except keys_default.cfg (personal keybinds/settings)
#     *.bak *.log *.dmp *.tmp *.old *.orig *.py *.vdf *.bat *.before*  Thumbs.db desktop.ini
#
# Usage (PowerShell), from the repo root:
#   .\scripts\package_client.ps1
#   .\scripts\package_client.ps1 -SourceDir "C:\path\to\RustClient" -OutFile "D:\RustClient.zip" -Level Fastest
#
# Level: Optimal (default, smaller download, slower) or Fastest (quick, a bit larger).
#
# Defaults: the client folder is the repo's sibling (..\RustClient relative to the repo root,
# i.e. two levels up from this script), and the zip lands at the repo root.

param(
    [string]$SourceDir = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'RustClient'),
    [string]$OutFile   = (Join-Path (Split-Path $PSScriptRoot -Parent) 'RustClient.zip'),
    [ValidateSet('Fastest','Optimal','NoCompression')]
    [string]$Level     = 'Optimal'
)

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $SourceDir)) { Write-Error "Source folder not found: $SourceDir"; exit 1 }
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
if ($OutFile.StartsWith($SourceDir, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "OutFile must be OUTSIDE the source folder."; exit 1
}
if (-not (Test-Path -LiteralPath 'C:\' )) { }  # no-op guard

function Test-Excluded([string]$rel) {
    $r = $rel.ToLowerInvariant()
    if ($r.StartsWith('temp/') -or $r.StartsWith('maps/')) { return $true }
    if ($r.StartsWith('cfg/') -and $r -ne 'cfg/keys_default.cfg') { return $true }
    $name = [System.IO.Path]::GetFileName($r)
    if ($name -match '\.(bak|log|dmp|tmp|old|orig|py|vdf|bat)$') { return $true }
    if ($name -like '*.before*') { return $true }
    if ($name -in 'thumbs.db','desktop.ini','.ds_store') { return $true }
    return $false
}

# ---- enumerate + filter ----
$all  = Get-ChildItem -LiteralPath $SourceDir -Recurse -File -Force
$keep = New-Object System.Collections.Generic.List[object]
$skip = New-Object System.Collections.Generic.List[string]
foreach ($f in $all) {
    $rel = $f.FullName.Substring($SourceDir.Length + 1).Replace('\', '/')
    if (Test-Excluded $rel) { $skip.Add($rel) } else { $keep.Add([pscustomobject]@{ File = $f; Rel = $rel }) }
}
$total = ($keep | ForEach-Object { $_.File.Length } | Measure-Object -Sum).Sum
if (-not $total) { Write-Error "Nothing to package."; exit 1 }

# ---- free-space check on the output drive (zip can't exceed source size) ----
$drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($OutFile))
if ($drive.AvailableFreeSpace -lt ($total + 1GB)) {
    Write-Error ("Not enough free space on {0}: need ~{1:N1} GB, have {2:N1} GB" -f $drive.Name, (($total+1GB)/1GB), ($drive.AvailableFreeSpace/1GB)); exit 1
}

Write-Host ""
Write-Host "Source : $SourceDir"
Write-Host "Output : $OutFile"
Write-Host ("Files  : {0} included, {1} excluded   Size: {2:N2} GB   Level: {3}" -f $keep.Count, $skip.Count, ($total/1GB), $Level)
if ($skip.Count) { Write-Host "Excluded:"; $skip | ForEach-Object { Write-Host "   - $_" } }
Write-Host ""
Write-Host "Zipping..."

if (Test-Path -LiteralPath $OutFile) { [System.IO.File]::Delete($OutFile) }
$cl  = [System.IO.Compression.CompressionLevel]::$Level
$sw  = [System.Diagnostics.Stopwatch]::StartNew()
$fs  = [System.IO.File]::Create($OutFile)
$zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
$done = 0L; $n = 0; $nextReport = 0L
try {
    foreach ($k in $keep) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $k.File.FullName, $k.Rel, $cl)
        $done += $k.File.Length; $n++
        if ($done -ge $nextReport -or $n -eq $keep.Count) {
            $pct  = [math]::Round(100.0 * $done / $total, 1)
            $rate = if ($sw.Elapsed.TotalSeconds -gt 0) { $done / 1MB / $sw.Elapsed.TotalSeconds } else { 0 }
            Write-Host ("  {0,5}%  {1,4}/{2} files  {3:N2} GB read  {4:N0} MB/s  {5:mm\:ss} elapsed" -f $pct, $n, $keep.Count, ($done/1GB), $rate, $sw.Elapsed)
            $nextReport = $done + 512MB
        }
    }
}
finally { $zip.Dispose(); $fs.Dispose() }
$sw.Stop()

$zipGb = (Get-Item -LiteralPath $OutFile).Length / 1GB
Write-Host ""
Write-Host ("DONE in {0:N1} min.  Zip: {1:N2} GB  (source {2:N2} GB, {3:N0}% of original)" -f $sw.Elapsed.TotalMinutes, $zipGb, ($total/1GB), (100*$zipGb*1GB/$total))

# ---- SHA-256 for integrity verification (paste into launcher.cfg -> Sha256=) ----
Write-Host ""
Write-Host "Hashing (SHA-256)..."
$hash = (Get-FileHash -LiteralPath $OutFile -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($OutFile + '.sha256') -Value $hash -Encoding ascii -NoNewline
Write-Host ("SHA-256: {0}" -f $hash)
Write-Host ("        (also written to {0}.sha256)" -f (Split-Path $OutFile -Leaf))
Write-Host ""
Write-Host "Next:"
Write-Host "  1) upload $OutFile to your server (direct download link)"
Write-Host "  2) in launcher.cfg set   DownloadUrl=<that link>   and   Sha256=$hash"
Write-Host "  3) rebuild the launcher (make_release.ps1) so the new hash is embedded"
