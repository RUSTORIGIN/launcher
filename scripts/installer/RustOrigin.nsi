; NSIS installer for the Rustorigin Launcher - a single self-contained setup .exe
; in the same style as electron-builder's NSIS output (e.g. AppName-x.y.z-x64.exe).
;
; Wizard: Welcome -> License -> Choose install folder -> Install (progress) -> Finish (run).
; Per-user install to %LOCALAPPDATA%\Programs (NO admin / no UAC), Start Menu + Desktop shortcuts,
; an uninstaller, and a per-user Add/Remove Programs entry.
;
; Built by scripts\build_installer_exe.ps1, which passes:
;   /DVERSION=x.y.z  /DVERSION4=x.y.z.0  /DREPO=<repo root>  /DOUTFILE=<output .exe path>
; Installs ONLY the ~7 MB launcher; it downloads + SHA-256-verifies the game client at runtime.

Unicode true
!include "MUI2.nsh"

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef VERSION4
  !define VERSION4 "${VERSION}.0"
!endif
!ifndef REPO
  !define REPO ".."
!endif

!define APPNAME     "Rustorigin Launcher"
!define PUBLISHER   "Rustorigin"
!define EXENAME     "RustoriginLauncher.exe"
!define ARPKEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\RustoriginLauncher"

Name "${APPNAME}"
!ifdef OUTFILE
  OutFile "${OUTFILE}"
!else
  OutFile "RustoriginLauncher-${VERSION}-x64.exe"
!endif
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "Software\Rustorigin\Launcher" "InstallDir"
RequestExecutionLevel user           ; per-user LocalAppData install -> no elevation (no UAC)
SetCompressor /SOLID lzma

; ---- installer exe file properties (Properties -> Details) ----
VIProductVersion "${VERSION4}"
VIAddVersionKey "ProductName"     "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} Setup"
VIAddVersionKey "CompanyName"     "${PUBLISHER}"
VIAddVersionKey "LegalCopyright"  "Copyright (c) 2026 kaveOO"
VIAddVersionKey "FileVersion"     "${VERSION4}"
VIAddVersionKey "ProductVersion"  "${VERSION4}"
VIAddVersionKey "Comments"        "Installs the Rustorigin game launcher."

; ---- Modern UI look ----
!define MUI_ICON   "${REPO}\assets\release_icon.ico"
!define MUI_UNICON "${REPO}\assets\release_icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APPNAME}"

; ---- installer pages ----
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${REPO}\scripts\installer\license.rtf"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; ---- uninstaller pages ----
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetShellVarContext current          ; per-user: LocalAppData, current-user Start Menu/Desktop, HKCU
  SetRegView 64
  SetOutPath "$INSTDIR"
  File "${REPO}\release\${EXENAME}"

  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortCut "$DESKTOP\${APPNAME}.lnk"    "$INSTDIR\${EXENAME}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr   HKCU "Software\Rustorigin\Launcher" "InstallDir" "$INSTDIR"
  WriteRegStr   HKCU "${ARPKEY}" "DisplayName"      "${APPNAME}"
  WriteRegStr   HKCU "${ARPKEY}" "DisplayVersion"   "${VERSION}"
  WriteRegStr   HKCU "${ARPKEY}" "Publisher"        "${PUBLISHER}"
  WriteRegStr   HKCU "${ARPKEY}" "DisplayIcon"      "$INSTDIR\${EXENAME}"
  WriteRegStr   HKCU "${ARPKEY}" "InstallLocation"  "$INSTDIR"
  WriteRegStr   HKCU "${ARPKEY}" "UninstallString"  '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKCU "${ARPKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${ARPKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${ARPKEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${ARPKEY}" "EstimatedSize" 7600
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKCU "${ARPKEY}"
  DeleteRegKey HKCU "Software\Rustorigin\Launcher"
SectionEnd
