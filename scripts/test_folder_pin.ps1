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
    $err = 0
    $busy = [IO.File]::Open("$root\plain\Rustorigin\busy.txt", 'Create', 'ReadWrite', 'None')
    try {
        $h = [FolderPin]::Pin("$root\plain\Rustorigin\busy.txt", [ref]$isDir, [ref]$isLink, [ref]$err)
        Check "Pin: file open in another program -> null, error 32" ($h -eq $null -and $err -eq 32)
    } finally { $busy.Dispose() }

    # ---- DeleteTree: removes a tree, never what a link inside points at ----
    New-Item -ItemType Directory -Force "$root\canary", "$root\tree\a\b" | Out-Null
    Set-Content "$root\canary\keep.txt" 'keep'; Set-Content "$root\tree\a\b\f.txt" 'x'
    Set-ItemProperty "$root\tree\a\b\f.txt" -Name IsReadOnly -Value $true
    Junction "$root\tree\a\out" "$root\canary"
    [FolderPin]::DeleteTree("$root\tree")
    Check "DeleteTree removed the tree (read-only file included)" (-not (Test-Path "$root\tree"))
    Check "DeleteTree left a junction's target alone" ((Get-Content "$root\canary\keep.txt") -eq 'keep')

    # read-only folders and links can't be removed as they are: DeleteTree makes them writable, never their targets
    New-Item -ItemType Directory -Force "$root\rotarget", "$root\rotree\ro\deeper" | Out-Null
    Set-Content "$root\rotarget\keep.txt" 'keep'; Set-Content "$root\rotree\ro\deeper\f.txt" 'x'
    Junction "$root\rotree\ro\out" "$root\rotarget"
    attrib +r "$root\rotarget"; attrib +r "$root\rotree\ro\out" /L; attrib +r "$root\rotree\ro\deeper"; attrib +r "$root\rotree\ro"; attrib +r "$root\rotree"
    Check "read-only junction set up" ((Get-Item "$root\rotree\ro\out" -Force).Attributes -match 'ReadOnly')
    $err = $null
    try { [FolderPin]::DeleteTree("$root\rotree") } catch { $err = $_.Exception.Message }
    Check "DeleteTree removed read-only folders and a read-only junction ($err)" ($err -eq $null -and -not (Test-Path "$root\rotree"))
    Check "the read-only junction's target kept, still read-only" ((Get-Content "$root\rotarget\keep.txt") -eq 'keep' -and (Get-Item "$root\rotarget").Attributes -match 'ReadOnly')
    attrib -r "$root\rotarget"

    # ---- DeleteDirContents (Uninstall): kept files and folders stay whole, the rest goes, links aren't followed ----
    $u = "$root\uninstall\Rustorigin"
    New-Item -ItemType Directory -Force "$u\keepdir\inner", "$u\game\sub" | Out-Null
    Set-Content "$u\keepdir\inner\x.txt" 'x'; Set-Content "$u\keep.txt" 'k'; Set-Content "$u\game\sub\g.txt" 'g'; Set-Content "$u\top.txt" 't'
    Junction "$u\game\out" "$root\canary"
    $keep = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [void]$keep.Add([IO.Path]::GetFullPath("$u\keepdir")); [void]$keep.Add([IO.Path]::GetFullPath("$u\KEEP.txt"))
    [FolderPin]::DeleteDirContents($u, $keep)
    Check "DeleteDirContents left a kept folder whole"   (Test-Path "$u\keepdir\inner\x.txt")
    Check "DeleteDirContents left a kept file"           (Test-Path "$u\keep.txt")
    Check "DeleteDirContents removed everything else"    (-not (Test-Path "$u\game\sub") -and -not (Test-Path "$u\top.txt"))
    Check "DeleteDirContents left a junction's target alone" ((Get-Content "$root\canary\keep.txt") -eq 'keep')

    # ---- WriteNewFile: replaces what is at the name, never writes through a hard link ----
    Set-Content "$root\canary\target.txt" 'original'
    cmd /c mklink /H "$root\plain\Rustorigin\rec.txt" "$root\canary\target.txt" | Out-Null
    Check "hard link created" (Test-Path "$root\plain\Rustorigin\rec.txt")
    [FolderPin]::WriteNewFile("$root\plain\Rustorigin\rec.txt", "ours")
    Check "WriteNewFile wrote a new file at the name" ((Get-Content "$root\plain\Rustorigin\rec.txt") -eq 'ours')
    Check "WriteNewFile left the hard link's other name unchanged" ((Get-Content "$root\canary\target.txt") -eq 'original')
} finally {
    cmd /c rmdir "$root\via\Rustorigin" 2>$null; cmd /c rmdir "$root\parentlink" 2>$null; foreach ($l in "$root\uninstall\Rustorigin\game\out", "$root\rotree\ro\out") { if (Test-Path $l) { cmd /c rmdir "$l" 2>$null } }
    if (Test-Path "$root\rotarget") { attrib -r "$root\rotarget" }
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
if ($script:fail) { Write-Host "$script:fail folder-pin test(s) FAILED"; exit 1 }
Write-Host "all folder-pin tests passed"
