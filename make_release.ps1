# Builds the SINGLE-FILE RustOrigin.exe: the video, logo, Montserrat fonts (+OFL license)
# and launcher.cfg are embedded as resources and unpacked at first run.
#
#   .\make_release.ps1                 -> release\RustOrigin.exe
#   .\make_release.ps1 -Version 1.1.0  (also stamps the version into the exe)
#
# Ship / host ONLY release\RustOrigin.exe. Edit launcher.cfg here (servers, URL, install
# dir) and rebuild to change the embedded defaults; a launcher.cfg placed next to the exe
# by an admin still overrides them at runtime.
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root   # relative paths below keep csc's /resource syntax simple

# stamp version into the source's assembly attributes
$src = Get-Content "$root\WpfLauncher.cs" -Raw -Encoding UTF8
$src = [regex]::Replace($src, 'AssemblyVersion\("[\d.]+"\)',     ('AssemblyVersion("{0}.0")' -f $Version))
$src = [regex]::Replace($src, 'AssemblyFileVersion\("[\d.]+"\)', ('AssemblyFileVersion("{0}.0")' -f $Version))
[System.IO.File]::WriteAllText("$root\WpfLauncher.cs", $src, (New-Object System.Text.UTF8Encoding($false)))

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
$refs = @("/r:$fw\WPF\PresentationFramework.dll","/r:$fw\WPF\PresentationCore.dll","/r:$fw\WPF\WindowsBase.dll",
          "/r:$fw\System.Xaml.dll","/r:System.dll","/r:System.Core.dll","/r:System.Xml.dll",
          "/r:System.IO.Compression.dll","/r:System.IO.Compression.FileSystem.dll","/r:System.Windows.Forms.dll","/r:System.Drawing.dll")
$res = @("/resource:background.mp4,assets/background.mp4",
         "/resource:logo.png,assets/logo.png",
         "/resource:server-cover.png,assets/server-cover.png",
         "/resource:launcher.cfg,assets/launcher.cfg",
         "/resource:fonts\Montserrat-Regular.ttf,assets/fonts/Montserrat-Regular.ttf",
         "/resource:fonts\Montserrat-Medium.ttf,assets/fonts/Montserrat-Medium.ttf",
         "/resource:fonts\Montserrat-SemiBold.ttf,assets/fonts/Montserrat-SemiBold.ttf",
         "/resource:fonts\Montserrat-Bold.ttf,assets/fonts/Montserrat-Bold.ttf",
         "/resource:fonts\OFL.txt,assets/fonts/OFL.txt")

Get-Process RustOrigin -ErrorAction SilentlyContinue | Stop-Process -Force
& "$fw\csc.exe" /nologo /nowarn:0108 /target:winexe /optimize+ `
    /win32icon:release_icon.ico /win32manifest:app.manifest `
    /out:RustOrigin.exe $refs $res WpfLauncher.cs
if ($LASTEXITCODE -ne 0) { throw "BUILD FAILED" }

New-Item -ItemType Directory -Force "$root\release" | Out-Null
Copy-Item "$root\RustOrigin.exe" "$root\release\RustOrigin.exe" -Force
$exe = Get-Item "$root\release\RustOrigin.exe"
Write-Host ("built single-file launcher: {0}  ({1:N2} MB, v{2})" -f $exe.FullName, ($exe.Length / 1MB), $Version)
Write-Host "embedded: background.mp4, logo.png, launcher.cfg, 4x Montserrat + OFL"
