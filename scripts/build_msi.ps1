# Builds the RustOrigin Launcher MSI (per-user installer).
#
#   .\scripts\build_msi.ps1                 -> release\RustOriginLauncher-1.0.0.msi
#   .\scripts\build_msi.ps1 -Version 1.1.0  (also stamps the version into the exe + MSI)
#
# Steps: (1) build the fresh single-file release\RustOrigin.exe via make_release.ps1,
#        (2) compile the MSI from scripts\installer\RustOrigin.wxs with the WiX toolset.
#
# Requires the WiX v4+ tool once:  dotnet tool install --global wix
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo   # wxs Source paths (release\..., assets\...) are repo-root-relative

# 1) build the launcher exe (fresh, versioned)
& "$PSScriptRoot\make_release.ps1" -Version $Version
if ($LASTEXITCODE -ne 0) { throw "launcher build failed" }

# 2) locate the WiX tool (PATH, or the default dotnet global-tools folder)
$wix = (Get-Command wix -ErrorAction SilentlyContinue).Source
if (-not $wix) { $wix = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe" }
if (-not (Test-Path $wix)) { throw "WiX not found. Install it with:  dotnet tool install --global wix" }

# 3) compile the MSI
$out = "$repo\release\RustOriginLauncher-$Version.msi"
& $wix build "$PSScriptRoot\installer\RustOrigin.wxs" -arch x64 -o $out
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$msi = Get-Item $out
Write-Host ("built MSI: {0}  ({1:N2} MB)" -f $msi.FullName, ($msi.Length / 1MB))
Write-Host "per-user install (no admin): %LOCALAPPDATA%\Programs\RustOrigin Launcher\ + Start Menu/Desktop shortcuts"
