# Stop hook (.claude/settings.json): when src\*.cs has uncommitted changes, compile them with scripts\build.bat
# (csc, about a second) and keep Claude from finishing while the compile fails. Exit 2 + stderr = "keep going,
# fix this". When Claude is already continuing because of this hook (stop_hook_active), it only warns, so a
# compile it can't fix never loops.
# Not 'Stop': Windows PowerShell turns a native command's stderr into errors, which would end the hook early.
$ErrorActionPreference = 'Continue'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

$active = $false
try { $active = [bool](([Console]::In.ReadToEnd() | ConvertFrom-Json).stop_hook_active) } catch { }

$changed = git -C $repo status --porcelain -- 'src/*.cs' 2>$null
if (-not $changed) { exit 0 }

# build.bat ends with "pause": stdin from NUL lets it return at once
$out = @(cmd /c "`"$repo\scripts\build.bat`" < NUL" 2>&1 | ForEach-Object { "$_" })
if ($LASTEXITCODE -eq 0) { exit 0 }

$errors = @($out | Where-Object { $_ -match ': error ' }) | Select-Object -First 20
if (-not $errors) { $errors = @($out | Select-Object -Last 10) }
$text = "scripts\build.bat failed on the changed src\*.cs (C# 5 / csc):`n" + ($errors -join "`n")
if ($active) {
    @{ systemMessage = "Compile still failing after a retry: " + ((@($errors) | Select-Object -First 3) -join ' | ') } | ConvertTo-Json -Compress
    exit 0
}
[Console]::Error.WriteLine($text + "`nFix the compile errors before finishing.")
exit 2
