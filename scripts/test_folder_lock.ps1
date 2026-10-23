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
        Check "entry in use: not secured, busy (3)"         ((Secure $dir) -eq 3)
        Check "unfinished-lock record left in the folder"   (Test-Path "$dir\.rustorigin-lock-incomplete")
        Check "other accounts can't remove the record"      (-not ((icacls "$dir\.rustorigin-lock-incomplete") -match 'S-1-5-11|Authenticated Users'))
        # the record as another account could have prepared it: marked, its own permissions letting
        # Authenticated Users change or delete it, and a handle held open on it from before the next lock
        Set-Content "$dir\.rustorigin-lock-incomplete" 'prepared'
        icacls "$dir\.rustorigin-lock-incomplete" /grant '*S-1-5-11:F' | Out-Null
        $old = [IO.File]::Open("$dir\.rustorigin-lock-incomplete", 'Open', 'Read', 'ReadWrite, Delete')
        try {
            Check "still refused next time, though the folder itself is private now (3)" ((Secure $dir) -eq 3)
            Check "the record was replaced by a fresh one, never kept" ((Get-Content "$dir\.rustorigin-lock-incomplete") -ne 'prepared')
            Check "the fresh record: no rights for other accounts"   (-not ((icacls "$dir\.rustorigin-lock-incomplete") -match 'S-1-5-11|Authenticated Users'))
            $buf = New-Object byte[] 8; $n = $old.Read($buf, 0, 8)
            Check "a handle held on the old one reaches only the old file" ([Text.Encoding]::ASCII.GetString($buf, 0, $n) -eq 'prepared')
            # -like, not -Filter: Windows wildcards would also match the record itself ("name.*" matches "name")
            Check "no temporary record left"                     (-not (Get-ChildItem $dir -Force | Where-Object { $_.Name -like '.rustorigin-lock-incomplete.*.new' }))
        } finally { $old.Dispose() }
    } finally { $fs.Dispose() }
    Check "entry released: secured (0)"                     ((Secure $dir) -eq 0)
    Check "record removed once everything is secured"       (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- busy only when every failure is a file in use: one that isn't (a trailing-dot name) makes it fixable (1) ----
    $dir = "$root\mixed\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content "$dir\busy.dll" 'x'
    [IO.File]::WriteAllText("\\?\$dir\odd.", 'x')
    Expose $dir
    $fs = [IO.File]::Open("$dir\busy.dll", 'Open', 'Read', 'None')
    try { Check "an entry in use plus one that can't be opened: not secured, not busy (1)" ((Secure $dir) -eq 1) } finally { $fs.Dispose() }
    [IO.File]::Delete("\\?\$dir\odd.")

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
    Check "nothing moved aside left"                        (-not (Get-ChildItem $dir -Force | Where-Object { $_.Name -like '.rustorigin-lock-incomplete.*' }))
    Check "link inside it removed, its target kept and unchanged" ((Test-Path "$root\canary\keep.txt") -and $canary -eq (Sddl "$root\canary"))

    # ---- a planted folder made undeletable with read-only entries is still removed ----
    $dir = "$root\rosquat\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\.rustorigin-lock-incomplete\sub" | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete\sub\x.txt" 'x'
    attrib +r "$dir\.rustorigin-lock-incomplete\sub"; attrib +r "$dir\.rustorigin-lock-incomplete"
    Expose $dir
    Check "read-only folder at the record's name: secured, then removed (0)" ((Secure $dir) -eq 0)
    Check "no record left"                                  (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- a planted folder at the record's name that can't be secured is moved aside: a record of ours takes the name ----
    $dir = "$root\oddsquat\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\.rustorigin-lock-incomplete" | Out-Null
    [IO.File]::WriteAllText("\\?\$dir\.rustorigin-lock-incomplete\x.", 'x')   # can't be opened: the folder can't be secured or removed
    Expose $dir
    function Aside { Get-ChildItem $dir -Force -Directory | Where-Object { $_.Name -like '.rustorigin-lock-incomplete.*.old' } }
    function OurRecord { (Test-Path "$dir\.rustorigin-lock-incomplete" -PathType Leaf) -and
        ((Get-Content "$dir\.rustorigin-lock-incomplete") -like '*could not be secured*') -and
        -not ((icacls "$dir\.rustorigin-lock-incomplete") -match 'S-1-5-11|Authenticated Users') }
    Check "unsecurable folder at the record's name: not secured (1)" ((Secure $dir) -eq 1)
    Check "the folder itself was still locked"              (-not ((icacls $dir) -match 'S-1-5-11|Authenticated Users'))
    Check "the planted folder was moved aside"              (@(Aside).Count -eq 1)
    Check "a record of ours, a file, is at the name"        (OurRecord)
    Check "still refused next time (1)"                     ((Secure $dir) -eq 1)
    Check "and our record is still at the name"             (OurRecord)
    foreach ($a in @(Aside)) { [IO.File]::Delete("\\?\$($a.FullName)\x.") }
    Check "once it can be secured: secured (0)"             ((Secure $dir) -eq 0)
    Check "record and moved-aside folder removed"           (-not (Test-Path "$dir\.rustorigin-lock-incomplete") -and -not (Aside))

    # ---- a planted folder that can't be moved aside (something in it held open) aborts the lock: the folder stays exposed ----
    $dir = "$root\heldsquat\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\.rustorigin-lock-incomplete" | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete\x.txt" 'x'
    Expose $dir
    $fs = [IO.File]::Open("$dir\.rustorigin-lock-incomplete\x.txt", 'Open', 'Read', 'ReadWrite')
    try {
        Check "planted folder that can't be moved: refused as busy (3), no fix offered" ((Secure $dir) -eq 3)
        Check "the folder was left exposed, not locked"     ((icacls $dir) -match 'S-1-5-11|Authenticated Users')
        Check "the planted folder still at the name, nothing moved aside" ((Test-Path "$dir\.rustorigin-lock-incomplete" -PathType Container) -and
            -not (Get-ChildItem $dir -Force | Where-Object { $_.Name -like '.rustorigin-lock-incomplete.*' }))
    } finally { $fs.Dispose() }
    Check "released: moved aside, secured, removed (0)"     ((Secure $dir) -eq 0)
    Check "nothing left at or beside the record's name"     (-not (Get-ChildItem $dir -Force | Where-Object { $_.Name -like '.rustorigin-lock-incomplete*' }))

    # ---- a file another account could have left at the record's name is replaced by ours ----
    $dir = "$root\fileplant\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete" 'planted'
    Expose $dir
    Check "file at the record's name: folder secured (0)"   ((Secure $dir) -eq 0)
    Check "and no record left"                              (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

    # ---- a file at the record's name held open without delete sharing can't be replaced: the lock aborts as busy ----
    $dir = "$root\heldrecord\Rustorigin"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content "$dir\.rustorigin-lock-incomplete" 'planted'
    Expose $dir
    $fs = [IO.File]::Open("$dir\.rustorigin-lock-incomplete", 'Open', 'Read', 'ReadWrite')
    try {
        Check "record held without delete sharing: refused as busy (3), not as fixable" ((Secure $dir) -eq 3)
        Check "the folder was left exposed, not locked"     ((icacls $dir) -match 'S-1-5-11|Authenticated Users')
        Check "what was at the name is untouched, no temporary record left" ((Get-Content "$dir\.rustorigin-lock-incomplete") -eq 'planted' -and
            -not (Get-ChildItem $dir -Force | Where-Object { $_.Name -like '.rustorigin-lock-incomplete.*' }))
    } finally { $fs.Dispose() }
    Check "released: replaced, folder secured (0)"          ((Secure $dir) -eq 0)
    Check "and no record left either"                       (-not (Test-Path "$dir\.rustorigin-lock-incomplete"))

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

    # ---- a folder nobody can remove at a guessable temporary name doesn't stop the lock or end it without a record ----
    $dir = "$root\tempsquat\Rustorigin"
    New-Item -ItemType Directory -Force "$dir\.rustorigin-lock-incomplete.new" | Out-Null
    [IO.File]::WriteAllText("\\?\$dir\.rustorigin-lock-incomplete.new\x.", 'x')   # can't be opened: the folder can't be secured or removed
    Set-Content "$dir\.rustorigin-lock-incomplete" 'planted'   # something at the record's name, so it is replaced through a temporary name
    Expose $dir
    Check "undeletable folder at the old temporary name: not secured (1)" ((Secure $dir) -eq 1)
    Check "the folder itself was still locked"              (-not ((icacls $dir) -match 'S-1-5-11|Authenticated Users'))
    Check "a fresh record is at the name"                   ((Test-Path "$dir\.rustorigin-lock-incomplete" -PathType Leaf) -and (Get-Content "$dir\.rustorigin-lock-incomplete") -ne 'planted')
    Check "still refused next time (1)"                     ((Secure $dir) -eq 1)
    Check "and a record is still at the name"               (Test-Path "$dir\.rustorigin-lock-incomplete" -PathType Leaf)
    [IO.File]::Delete("\\?\$dir\.rustorigin-lock-incomplete.new\x.")
} finally {
    foreach ($l in "$root\via\Rustorigin", "$root\parentlink", "$root\lock\Rustorigin\link") { if (Test-Path $l) { cmd /c rmdir "$l" 2>$null } }
    foreach ($v in "$root\canary\victim.txt", "$root\canary\victim2.txt") { if (Test-Path $v) { Set-ItemProperty $v -Name IsReadOnly -Value $false } }
    try { [IO.File]::Delete("\\?\$root\oddname\Rustorigin\game.dll.") } catch { }
    try { [IO.File]::Delete("\\?\$root\mixed\Rustorigin\odd.") } catch { }
    foreach ($o in @(Get-ChildItem "$root\oddsquat\Rustorigin" -Force -Directory -ErrorAction SilentlyContinue)) { try { [IO.File]::Delete("\\?\$($o.FullName)\x.") } catch { } }
    try { [IO.File]::Delete("\\?\$root\tempsquat\Rustorigin\.rustorigin-lock-incomplete.new\x.") } catch { }
    cmd /c rmdir /s /q "$root" 2>$null
}
if ($script:fail) { Write-Host "$script:fail folder-lock test(s) FAILED"; exit 1 }
Write-Host "all folder-lock tests passed"
