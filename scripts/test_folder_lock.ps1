# End-to-end tests for the install-folder lock (LockFolder / ResetTree / SecureFolderElevated in
# src\WpfLauncher.cs), run through the built exe's --secure-folder helper on folders in a temp folder. Run as a
# normal user it uses the same code as the elevated fix. Needs release\RustoriginLauncher.exe
# (scripts\make_release.ps1). Run: .\scripts\test_folder_lock.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'release\RustoriginLauncher.exe'
if (-not (Test-Path $exe)) { throw "build the launcher first: .\scripts\make_release.ps1 -Version <x.y.z>" }

$script:fail = 0
function Check($name, $cond) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name"; $script:fail++ } }
function Junction($link, $target) { cmd /c mklink /J "$link" "$target" | Out-Null; if (-not (Test-Path $link)) { throw "could not create junction $link" } }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
function Secure($dir) { (Start-Process $exe -ArgumentList "--secure-folder `"$dir`" $sid" -Wait -PassThru -WindowStyle Hidden).ExitCode }
function Expose($dir) { icacls $dir /grant '*S-1-5-11:(OI)(CI)M' | Out-Null }   # Authenticated Users: Modify
function Sddl($p) { (Get-Acl $p).Sddl }

$root = Join-Path ([IO.Path]::GetTempPath()) ('folderlock-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root | Out-Null
try {
    # ---- links: never re-permissioned through ----
    New-Item -ItemType Directory -Force "$root\target\Rustorigin", "$root\via" | Out-Null
    Expose "$root\target\Rustorigin"
    Junction "$root\via\Rustorigin" "$root\target\Rustorigin"
    Junction "$root\parentlink" "$root\target"
    $before = Sddl "$root\target\Rustorigin"
    Check "install folder that is a junction: refused (2)"  ((Secure "$root\via\Rustorigin") -eq 2)
    Check "install folder under a junction: refused (2)"    ((Secure "$root\parentlink\Rustorigin") -eq 2)
    Check "junction target's permissions unchanged"         ($before -eq (Sddl "$root\target\Rustorigin"))

    # ---- an exposed folder is locked; links inside are removed, their target untouched ----
    $dir = "$root\lock\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\sub", "$root\elsewhere" | Out-Null
    Set-Content "$dir\sub\a.txt" 'x'; Set-Content "$dir\sub\b.txt" 'x'; Set-Content "$root\elsewhere\keep.txt" 'keep'
    Expose $dir
    icacls "$dir\sub\a.txt" /inheritance:d | Out-Null
    Junction "$dir\link" "$root\elsewhere"
    $elsewhere = Sddl "$root\elsewhere"
    Check "exposed folder locked (0)"                       ((Secure $dir) -eq 0)
    Check "other accounts can no longer change it"          (-not ((icacls $dir) -match 'S-1-5-11|Authenticated Users'))
    Check "link inside removed"                             (-not (Test-Path "$dir\link"))
    Check "link target kept, permissions unchanged"         ((Test-Path "$root\elsewhere\keep.txt") -and $elsewhere -eq (Sddl "$root\elsewhere"))
    Check "entry with its own permissions inherits again"   (-not (Get-Acl "$dir\sub\a.txt").AreAccessRulesProtected)
    # nothing is passed on by Windows any more: each entry must have been set by the walk itself
    Check "entries inside lost what they inherited before"  (-not ((icacls "$dir\sub") + (icacls "$dir\sub\a.txt") + (icacls "$dir\sub\b.txt") -match 'S-1-5-11|Authenticated Users'))
    Check "private folder: nothing to do (0)"               ((Secure $dir) -eq 0)

    # ---- an entry that can't be secured keeps the folder refused, across runs ----
    $dir = "$root\busy\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content "$dir\busy.dll" 'x'
    Expose $dir
    icacls "$dir\busy.dll" /inheritance:d | Out-Null
    $fs = [IO.File]::Open("$dir\busy.dll", 'Open', 'Read', 'None')   # held open without sharing: can't be pinned
    try {
        Check "entry in use: not secured (1)"               ((Secure $dir) -eq 1)
        Check "unfinished-lock record left in the folder"   (Test-Path "$dir\.rustorigin-lock-incomplete")
        Check "still refused next time, though the folder itself is private now (1)" ((Secure $dir) -eq 1)
    } finally { $fs.Dispose() }
    Check "entry released: secured (0)"                     ((Secure $dir) -eq 0)
    Check "record removed once everything is secured"       (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- a record left by a walk that was cut short keeps a private folder refused until it is walked again ----
    Set-Content "$dir\.rustorigin-lock-incomplete" 'x'
    Check "leftover record: walked again and secured (0)"   ((Secure $dir) -eq 0)
    Check "leftover record removed"                         (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- a folder planted under the record's name doesn't defeat it ----
    $dir = "$root\squat\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\.rustorigin-lock-incomplete" | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete\x.txt" 'x'
    Expose $dir
    New-Item -ItemType Directory -Force "$root\canary" | Out-Null
    Set-Content "$root\canary\keep.txt" 'keep'
    Junction "$dir\.rustorigin-lock-incomplete\out" "$root\canary"   # a link inside the planted folder
    $canary = Sddl "$root\canary"
    Check "folder at the record's name: secured, then removed (0)" ((Secure $dir) -eq 0)
    Check "no record (and no planted folder) left"          (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))
    Check "link inside it removed, its target kept and unchanged" ((Test-Path "$root\canary\keep.txt") -and $canary -eq (Sddl "$root\canary"))

    # ---- a planted folder made undeletable with read-only entries is still removed ----
    $dir = "$root\rosquat\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\.rustorigin-lock-incomplete\sub" | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete\sub\x.txt" 'x'
    attrib +r "$dir\.rustorigin-lock-incomplete\sub"; attrib +r "$dir\.rustorigin-lock-incomplete"
    Expose $dir
    Check "read-only folder at the record's name: secured, then removed (0)" ((Secure $dir) -eq 0)
    Check "no record left"                                  (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- a file another account could have left at the record's name is replaced by ours ----
    $dir = "$root\fileplant\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete" 'planted'
    Expose $dir
    Check "file at the record's name: folder secured (0)"   ((Secure $dir) -eq 0)
    Check "and no record left"                              (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- a hard link at the record's name: the record is never written into the file it shares ----
    $dir = "$root\hardlink\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    New-Item -ItemType Directory -Force "$dir\sub" | Out-Null
    Set-Content "$root\canary\victim.txt" 'victim'; Set-Content "$root\canary\victim2.txt" 'victim2'
    Set-ItemProperty "$root\canary\victim.txt", "$root\canary\victim2.txt" -Name IsReadOnly -Value $true
    Expose $dir   # first: icacls would otherwise propagate onto the linked files themselves
    cmd /c mklink /H "$dir\.rustorigin-lock-incomplete" "$root\canary\victim.txt" | Out-Null
    cmd /c mklink /H "$dir\sub\game.dll" "$root\canary\victim2.txt" | Out-Null
    $victim = Sddl "$root\canary\victim.txt"; $victim2 = Sddl "$root\canary\victim2.txt"
    Check "hard links: folder secured (0)"                  ((Secure $dir) -eq 0)
    Check "the linked file's content, permissions and read-only flag unchanged" ((Get-Content "$root\canary\victim.txt") -eq 'victim' -and
        $victim -eq (Sddl "$root\canary\victim.txt") -and (Get-Item "$root\canary\victim.txt").IsReadOnly)
    Check "a hard link deeper in: removed"                  (-not (Test-Path "$dir\sub\game.dll"))
    Check "its file's content, permissions and read-only flag unchanged" ((Get-Content "$root\canary\victim2.txt") -eq 'victim2' -and
        $victim2 -eq (Sddl "$root\canary\victim2.txt") -and (Get-Item "$root\canary\victim2.txt").IsReadOnly)

    # ---- a name Windows trims (trailing dot) can't be pinned as itself: not secured ----
    $dir = "$root\oddname\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Expose $dir
    [IO.File]::WriteAllText("\\?\$dir\game.dll.", 'x')   # only a \\?\ path keeps the trailing dot
    Check "entry named with a trailing dot created"         ((cmd /c dir /b "$dir") -contains 'game.dll.')
    Check "trailing-dot entry: not secured (1)"             ((Secure $dir) -eq 1)
    [IO.File]::Delete("\\?\$dir\game.dll.")
} finally {
    foreach ($l in "$root\via\Rustorigin", "$root\parentlink", "$root\lock\Rustorigin\link") { if (Test-Path $l) { cmd /c rmdir "$l" 2>$null } }
    foreach ($v in "$root\canary\victim.txt", "$root\canary\victim2.txt") { if (Test-Path $v) { Set-ItemProperty $v -Name IsReadOnly -Value $false } }
    try { [IO.File]::Delete("\\?\$root\oddname\Rustorigin\game.dll.") } catch { }
    cmd /c rmdir /s /q "$root" 2>$null
}
if ($script:fail) { Write-Host "$script:fail folder-lock test(s) FAILED"; exit 1 }
Write-Host "all folder-lock tests passed"
