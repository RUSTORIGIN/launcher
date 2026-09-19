@echo off
REM Builds RustOrigin.exe (WPF, video-background launcher) using the .NET Framework
REM C# compiler present on all Windows 10/11. No runtime install needed on end-user PCs.
REM Output: RustOrigin.exe  (ship it with background.mp4 + launcher.cfg)

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo Could not find the .NET Framework C# compiler at %FW%\csc.exe
  pause
  exit /b 1
)

set REFS=/r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll

"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:RustOrigin.exe %REFS% WpfLauncher.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)

echo.
echo Built RustOrigin.exe
echo Ship RustOrigin.exe together with background.mp4 and launcher.cfg.
pause
