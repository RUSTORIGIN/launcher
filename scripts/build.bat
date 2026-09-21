@echo off
REM Quick DEV compile check of the WPF launcher using the .NET Framework C# compiler
REM present on all Windows 10/11. This does NOT embed resources/icon/manifest - the
REM resulting RustOriginLauncher.exe needs assets beside it to run. For a shippable single-file
REM build use scripts\make_release.ps1 instead.
REM
REM Run from anywhere: it switches to the repo root (the parent of scripts\).

pushd "%~dp0.."

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo Could not find the .NET Framework C# compiler at %FW%\csc.exe
  popd
  pause
  exit /b 1
)

set REFS=/r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:RustOriginLauncher.exe %REFS% src\WpfLauncher.cs src\UpdateParsing.cs src\A2S.cs src\DiscordRpc.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  popd
  pause
  exit /b 1
)

echo.
echo Built RustOriginLauncher.exe (dev build - assets not embedded).
echo For a shippable single-file exe, run scripts\make_release.ps1.
popd
pause
