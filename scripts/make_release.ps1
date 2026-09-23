# Builds the SINGLE-FILE RustoriginLauncher.exe: the video, logo, Montserrat fonts (+OFL license)
# and launcher.cfg are embedded as resources and unpacked at first run.
#
#   .\scripts\make_release.ps1                 -> release\RustoriginLauncher.exe
#   .\scripts\make_release.ps1 -Version 1.1.0  (also stamps the version into the exe)
#
# Ship / host ONLY release\RustoriginLauncher.exe. Edit config\launcher.cfg (servers, URL, install
# dir) and rebuild to change the embedded defaults; a launcher.cfg placed next to the exe
# by an admin still overrides them at runtime.
#
# Run from anywhere: paths resolve against the repo root (the parent of scripts\).
param([string]$Version = "1.0.0")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo   # relative paths below are repo-root-relative

# stamp version into the source's assembly attributes
$src = Get-Content "$repo\src\WpfLauncher.cs" -Raw -Encoding UTF8
$src = [regex]::Replace($src, 'AssemblyVersion\("[\d.]+"\)',     ('AssemblyVersion("{0}.0")' -f $Version))
$src = [regex]::Replace($src, 'AssemblyFileVersion\("[\d.]+"\)', ('AssemblyFileVersion("{0}.0")' -f $Version))
[System.IO.File]::WriteAllText("$repo\src\WpfLauncher.cs", $src, (New-Object System.Text.UTF8Encoding($false)))

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) { $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319" }
$refs = @("/r:$fw\WPF\PresentationFramework.dll","/r:$fw\WPF\PresentationCore.dll","/r:$fw\WPF\WindowsBase.dll",
          "/r:$fw\System.Xaml.dll","/r:System.dll","/r:System.Core.dll","/r:System.Xml.dll",
          "/r:System.IO.Compression.dll","/r:System.IO.Compression.FileSystem.dll","/r:System.Windows.Forms.dll","/r:System.Drawing.dll")
# NOTE: the resource NAME (after the comma) must stay "assets/..." - that is what the code
# reads at runtime (Assets.Ensure / ConfigLines). Only the source PATH (before the comma) moved.
$res = @("/resource:assets\1.jpg,assets/1.jpg",
         "/resource:assets\2.jpg,assets/2.jpg",
         "/resource:assets\3.jpg,assets/3.jpg",
         "/resource:assets\main.jpg,assets/main.jpg",
         "/resource:assets\train.jpg,assets/train.jpg",
         "/resource:assets\logo.png,assets/logo.png",
         "/resource:config\launcher.cfg,assets/launcher.cfg",
         "/resource:assets\fonts\Montserrat-Regular.ttf,assets/fonts/Montserrat-Regular.ttf",
         "/resource:assets\fonts\Montserrat-Medium.ttf,assets/fonts/Montserrat-Medium.ttf",
         "/resource:assets\fonts\Montserrat-SemiBold.ttf,assets/fonts/Montserrat-SemiBold.ttf",
         "/resource:assets\fonts\Montserrat-Bold.ttf,assets/fonts/Montserrat-Bold.ttf",
         "/resource:assets\fonts\OFL.txt,assets/fonts/OFL.txt",
         "/resource:assets\fonts\Poppins-Regular.ttf,assets/fonts/Poppins-Regular.ttf",
         "/resource:assets\fonts\Poppins-Medium.ttf,assets/fonts/Poppins-Medium.ttf",
         "/resource:assets\fonts\Poppins-SemiBold.ttf,assets/fonts/Poppins-SemiBold.ttf",
         "/resource:assets\fonts\Poppins-Bold.ttf,assets/fonts/Poppins-Bold.ttf",
         "/resource:assets\fonts\Poppins-OFL.txt,assets/fonts/Poppins-OFL.txt")

Get-Process RustoriginLauncher -ErrorAction SilentlyContinue | Stop-Process -Force
& "$fw\csc.exe" /nologo /nowarn:0108 /target:winexe /optimize+ `
    /win32icon:assets\release_icon.ico /win32manifest:src\app.manifest `
    /out:RustoriginLauncher.exe $refs $res src\WpfLauncher.cs src\UpdateParsing.cs src\A2S.cs src\DiscordRpc.cs
if ($LASTEXITCODE -ne 0) { throw "BUILD FAILED" }

New-Item -ItemType Directory -Force "$repo\release" | Out-Null
Copy-Item "$repo\RustoriginLauncher.exe" "$repo\release\RustoriginLauncher.exe" -Force
$exe = Get-Item "$repo\release\RustoriginLauncher.exe"
Write-Host ("built single-file launcher: {0}  ({1:N2} MB, v{2})" -f $exe.FullName, ($exe.Length / 1MB), $Version)
Write-Host "embedded: 3 night background screenshots, 2 server covers, logo.png, launcher.cfg, 4x Montserrat + 4x Poppins + OFL"
