# Tests for the install-folder link checks (src\FolderPin.cs): a folder that is, or sits under, a junction is
# refused, a plain folder is accepted, and a pinned folder can't be renamed or swapped for a link; permissions
# set through a pin reach that one object only, and hard links are seen and removed as names.
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

    # read-only folders and links can't be removed as they are: DeleteTree makes them writable, never their
    # targets, and removes a read-only hard link without clearing the flag its other name shares
    New-Item -ItemType Directory -Force "$root\rotarget", "$root\rotree\ro\deeper" | Out-Null
    Set-Content "$root\rotarget\keep.txt" 'keep'; Set-Content "$root\rotree\ro\deeper\f.txt" 'x'
    Junction "$root\rotree\ro\out" "$root\rotarget"
    Set-Content "$root\canary\rolink.txt" 'ro'; Set-ItemProperty "$root\canary\rolink.txt" -Name IsReadOnly -Value $true
    cmd /c mklink /H "$root\rotree\ro\hl.txt" "$root\canary\rolink.txt" | Out-Null
    attrib +r "$root\rotarget"; attrib +r "$root\rotree\ro\out" /L; attrib +r "$root\rotree\ro\deeper"; attrib +r "$root\rotree\ro"; attrib +r "$root\rotree"
    Check "read-only junction set up" ((Get-Item "$root\rotree\ro\out" -Force).Attributes -match 'ReadOnly')
    $err = $null
    try { [FolderPin]::DeleteTree("$root\rotree") } catch { $err = $_.Exception.Message }
    Check "DeleteTree removed read-only folders and a read-only junction ($err)" ($err -eq $null -and -not (Test-Path "$root\rotree"))
    Check "the read-only junction's target kept, still read-only" ((Get-Content "$root\rotarget\keep.txt") -eq 'keep' -and (Get-Item "$root\rotarget").Attributes -match 'ReadOnly')
    Check "a hard link's other name kept, still read-only" ((Get-Content "$root\canary\rolink.txt") -eq 'ro' -and (Get-Item "$root\canary\rolink.txt").IsReadOnly)
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
    Set-ItemProperty "$root\canary\target.txt" -Name IsReadOnly -Value $true
    cmd /c mklink /H "$root\plain\Rustorigin\rec.txt" "$root\canary\target.txt" | Out-Null
    Check "hard link created" (Test-Path "$root\plain\Rustorigin\rec.txt")
    [FolderPin]::WriteNewFile("$root\plain\Rustorigin\rec.txt", "ours")
    Check "WriteNewFile wrote a new file at the name" ((Get-Content "$root\plain\Rustorigin\rec.txt") -eq 'ours')
    Check "WriteNewFile left the hard link's other name unchanged, read-only flag included" ((Get-Content "$root\canary\target.txt") -eq 'original' -and
        (Get-Item "$root\canary\target.txt").IsReadOnly)
    Check "IsPlainFile: the file WriteNewFile made"   ([FolderPin]::IsPlainFile("$root\plain\Rustorigin\rec.txt"))
    cmd /c mklink /H "$root\plain\Rustorigin\rec2.txt" "$root\canary\target.txt" | Out-Null
    Check "IsPlainFile: not a hard link, folder, junction or missing name" (-not ([FolderPin]::IsPlainFile("$root\plain\Rustorigin\rec2.txt") -or
        [FolderPin]::IsPlainFile("$root\plain\Rustorigin") -or [FolderPin]::IsPlainFile("$root\via\Rustorigin") -or [FolderPin]::IsPlainFile("$root\nothing")))

    # ---- InheritedAcl: the Windows inheritance rules (pure) ----
    function Acl($sddl) { ,(New-Object Security.AccessControl.RawSecurityDescriptor $sddl).DiscretionaryAcl }   # "," keeps the ACL from being unrolled
    function AclSddl($acl) { (New-Object Security.AccessControl.RawSecurityDescriptor ([Security.AccessControl.ControlFlags]::DiscretionaryAclPresent), $null, $null, $null, $acl).GetSddlForm('Access') }
    $parent = Acl 'D:P(A;OICI;FA;;;SY)(A;CI;0x1200a9;;;BU)(A;OI;FA;;;BA)(A;OICINP;FA;;;WD)(A;OICIIO;FA;;;AU)(A;;FA;;;LS)'
    Check "InheritedAcl: file gets the object-inherit entries, as inherited" ((AclSddl ([FolderPin]::InheritedAcl($parent, $false))) -eq
        'D:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;FA;;;WD)(A;ID;FA;;;AU)')
    Check "InheritedAcl: folder keeps passing them on (no-propagate stops, inherit-only clears)" ((AclSddl ([FolderPin]::InheritedAcl($parent, $true))) -eq
        'D:(A;OICIID;FA;;;SY)(A;CIID;0x1200a9;;;BU)(A;OIIOID;FA;;;BA)(A;ID;FA;;;WD)(A;OICIID;FA;;;AU)')

    # ---- permissions through a pin: this object only, hard links seen ----
    New-Item -ItemType Directory -Force "$root\sec\d\sub" | Out-Null
    Set-Content "$root\sec\d\sub\f.txt" 'x'; Set-Content "$root\canary\shared.txt" 'shared'
    Set-ItemProperty "$root\canary\shared.txt" -Name IsReadOnly -Value $true
    cmd /c mklink /H "$root\sec\d\hl.txt" "$root\canary\shared.txt" | Out-Null
    $shared = (Get-Acl "$root\canary\shared.txt").Sddl; $f = (Get-Acl "$root\sec\d\sub\f.txt").Sddl
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    # not necessarily $me: an administrator's new folders can belong to Administrators (CI runners, servers)
    $owner = (Get-Acl "$root\sec\d").GetOwner([Security.Principal.SecurityIdentifier]).Value
    $h = [FolderPin]::PinForSecurity("$root\sec\d", [ref]$isDir, [ref]$isLink, [ref]$err)
    Check "PinForSecurity: plain folder" ($h -ne $null -and $isDir -and -not $isLink)
    if ($h) {
        Check "GetSecurity reads the owner" ([FolderPin]::GetSecurity($h).Owner.Value -eq $owner)
        [FolderPin]::SetSecurity($h, $null, (Acl "D:(A;OICI;FA;;;$me)(A;OICI;FA;;;SY)"), $true)
        $h.Dispose()
    }
    Check "SetSecurity set the folder (protected)" ((Get-Acl "$root\sec\d").AreAccessRulesProtected -and -not ((icacls "$root\sec\d") -match 'BUILTIN\\Administrators'))
    Check "SetSecurity passed nothing on: entries and the hard-linked file unchanged" ($f -eq (Get-Acl "$root\sec\d\sub\f.txt").Sddl -and $shared -eq (Get-Acl "$root\canary\shared.txt").Sddl)
    $h = [FolderPin]::PinForSecurity("$root\sec\d\hl.txt", [ref]$isDir, [ref]$isLink, [ref]$err)
    Check "LinkCount: hard-linked file has 2 names" ($h -ne $null -and [FolderPin]::LinkCount($h) -eq 2)
    if ($h) { $h.Dispose() }
    $h = [FolderPin]::PinForSecurity("$root\sec\d\sub\f.txt", [ref]$isDir, [ref]$isLink, [ref]$err)
    Check "LinkCount: plain file has 1" ($h -ne $null -and [FolderPin]::LinkCount($h) -eq 1)
    if ($h) { $h.Dispose() }
    $h = [FolderPin]::PinForSecurity("$root\via\Rustorigin", [ref]$isDir, [ref]$isLink, [ref]$err)
    Check "PinForSecurity opens a junction itself" ($h -ne $null -and $isLink)
    if ($h) { $h.Dispose() }
    [FolderPin]::DeleteName("$root\sec\d\hl.txt")
    Check "DeleteName removed the read-only hard link" (-not (Test-Path "$root\sec\d\hl.txt"))
    Check "DeleteName left the other name's content, permissions and read-only flag alone" ((Get-Content "$root\canary\shared.txt") -eq 'shared' -and
        $shared -eq (Get-Acl "$root\canary\shared.txt").Sddl -and (Get-Item "$root\canary\shared.txt").IsReadOnly)
} finally {
    cmd /c rmdir "$root\via\Rustorigin" 2>$null; cmd /c rmdir "$root\parentlink" 2>$null
    foreach ($l in "$root\uninstall\Rustorigin\game\out", "$root\rotree\ro\out") { if (Test-Path $l) { cmd /c rmdir "$l" 2>$null } }
    if (Test-Path "$root\rotarget") { attrib -r "$root\rotarget" }
    foreach ($v in "$root\canary\shared.txt", "$root\canary\target.txt", "$root\canary\rolink.txt") { if (Test-Path $v) { Set-ItemProperty $v -Name IsReadOnly -Value $false } }
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
if ($script:fail) { Write-Host "$script:fail folder-pin test(s) FAILED"; exit 1 }
Write-Host "all folder-pin tests passed"
