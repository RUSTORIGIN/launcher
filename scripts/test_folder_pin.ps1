# Tests for the install-folder link checks (src\FolderPin.cs): a folder that is, or sits under, a junction is
# refused, a plain folder is accepted, and a pinned folder can't be renamed or swapped for a link.
# Builds junctions in a temp folder (no admin needed). Run: .\scripts\test_folder_pin.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $repo 'src\FolderPin.cs')

$script:fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fail++ } }
function Junction($link, $target) { cmd /c mklink /J "$link" "$target" | Out-Null; if (-not (Test-Path $link)) { throw "could not create junction $link" } }
function PinFolder($dir) { $why = $null; $h = [FolderPin]::PinFolder($dir, [ref]$why); @{ h = $h; why = $why } }

$root = Join-Path ([IO.Path]::GetTempPath()) ('folderpin-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force "$root\target\Rustorigin", "$root\plain\Rustorigin", "$root\via" | Out-Null
Junction "$root\via\Rustorigin" "$root\target\Rustorigin"   # the folder itself is a junction
Junction "$root\parentlink" "$root\target"                  # a folder above it is a junction
try {
    $r = PinFolder "$root\plain\Rustorigin"
    Check "plain folder pins" ($r.h -ne $null)
    if ($r.h) {
        $renamed = $true
        try { [IO.Directory]::Move("$root\plain\Rustorigin", "$root\plain\moved") } catch { $renamed = $false }
        Check "pinned folder can't be renamed (no link swap mid-lock)" (-not $renamed)
        $r.h.Dispose()
        if ($renamed) { [IO.Directory]::Move("$root\plain\moved", "$root\plain\Rustorigin") }
        Rename-Item "$root\plain\Rustorigin" "moved"; Rename-Item "$root\plain\moved" "Rustorigin"
        Check "rename works again once unpinned" (Test-Path "$root\plain\Rustorigin")
    }
    $r = PinFolder "$root\via\Rustorigin"
    Check "junction folder refused" ($r.h -eq $null -and $r.why -eq [FolderPin]::LinkReason)
    $r = PinFolder "$root\parentlink\Rustorigin"
    Check "folder under a junction refused" ($r.h -eq $null -and $r.why -like '*reached through a link*')
    $r = PinFolder "$root\missing"
    Check "missing folder refused" ($r.h -eq $null)

    Check "UnderLink: plain folder"        (-not [FolderPin]::UnderLink("$root\plain\Rustorigin"))
    Check "UnderLink: not-yet-created leaf" (-not [FolderPin]::UnderLink("$root\plain\new\Rustorigin"))
    Check "UnderLink: junction"            ([FolderPin]::UnderLink("$root\via\Rustorigin"))
    Check "UnderLink: under a junction"    ([FolderPin]::UnderLink("$root\parentlink\Rustorigin\sub"))
    Check "UnderLink: drive root"          (-not [FolderPin]::UnderLink([IO.Path]::GetPathRoot($root)))

    $isDir = $false; $isLink = $false
    $h = [FolderPin]::Pin("$root\via\Rustorigin", [ref]$isDir, [ref]$isLink)
    Check "Pin opens the junction itself, not its target" ($h -ne $null -and $isDir -and $isLink)
    if ($h) { $h.Dispose() }
    $h = [FolderPin]::Pin("$root\plain\Rustorigin", [ref]$isDir, [ref]$isLink)
    Check "Pin: plain folder is no link" ($h -ne $null -and $isDir -and -not $isLink)
    if ($h) { $h.Dispose() }
} finally {
    cmd /c rmdir "$root\via\Rustorigin" 2>$null; cmd /c rmdir "$root\parentlink" 2>$null
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
if ($script:fail) { Write-Host "$script:fail folder-pin test(s) FAILED"; exit 1 }
Write-Host "all folder-pin tests passed"
