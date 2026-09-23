# Builds the single-file NSIS setup .exe (electron-builder style):
#   RustoriginLauncher-<version>-x64.exe  (Welcome -> License -> Choose folder -> Install -> Finish)
#
#   .\scripts\build_installer_exe.ps1                 -> release\RustoriginLauncher-1.0.0-x64.exe
#   .\scripts\build_installer_exe.ps1 -Version 1.1.0
#
# Steps: (1) build the fresh single-file release\RustoriginLauncher.exe via make_release.ps1,
#        (2) compile scripts\installer\RustOrigin.nsi with NSIS (makensis).
#
# Requires NSIS once:  winget install NSIS.NSIS
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# 1) build the launcher exe (fresh, versioned)
& "$PSScriptRoot\make_release.ps1" -Version $Version
if ($LASTEXITCODE -ne 0) { throw "launcher build failed" }

# 2) locate makensis
$makensis = "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
if (-not (Test-Path $makensis)) { $makensis = "$env:ProgramFiles\NSIS\makensis.exe" }
if (-not (Test-Path $makensis)) { throw "NSIS not found. Install it with:  winget install NSIS.NSIS" }

# 3) compile the setup .exe
$out = "$repo\release\RustoriginLauncher-$Version-x64.exe"
& $makensis "/DVERSION=$Version" "/DVERSION4=$Version.0" "/DREPO=$repo" "/DOUTFILE=$out" "$PSScriptRoot\installer\RustOrigin.nsi"
if ($LASTEXITCODE -ne 0) { throw "NSIS build failed" }

$exe = Get-Item $out
Write-Host ("built setup: {0}  ({1:N2} MB)" -f $exe.FullName, ($exe.Length / 1MB))
Write-Host "per-user install to %LOCALAPPDATA%\Programs (no admin) with folder-choose wizard, shortcuts, and uninstaller"
