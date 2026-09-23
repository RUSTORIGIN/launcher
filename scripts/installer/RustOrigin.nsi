; NSIS installer for the Rustorigin Launcher - a single self-contained setup .exe
; in the same style as electron-builder's NSIS output (e.g. AppName-x.y.z-x64.exe).
;
; Wizard: Welcome -> License -> Choose install folder -> Install (progress) -> Finish (run).
; Per-machine install to C:\Rustorigin (requires admin/UAC), Start Menu + Desktop shortcuts,
; an uninstaller, and an Add/Remove Programs entry.
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
InstallDir "C:\Rustorigin"
InstallDirRegKey HKLM "Software\Rustorigin\Launcher" "InstallDir"
RequestExecutionLevel admin          ; install under C:\ -> elevation (UAC)
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
  SetShellVarContext all              ; per-machine: all-users Start Menu/Desktop, HKLM
  SetRegView 64
  SetOutPath "$INSTDIR"
  File "${REPO}\release\${EXENAME}"

  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXENAME}"
  CreateShortCut "$DESKTOP\${APPNAME}.lnk"    "$INSTDIR\${EXENAME}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  WriteRegStr   HKLM "Software\Rustorigin\Launcher" "InstallDir" "$INSTDIR"
  WriteRegStr   HKLM "${ARPKEY}" "DisplayName"      "${APPNAME}"
  WriteRegStr   HKLM "${ARPKEY}" "DisplayVersion"   "${VERSION}"
  WriteRegStr   HKLM "${ARPKEY}" "Publisher"        "${PUBLISHER}"
  WriteRegStr   HKLM "${ARPKEY}" "DisplayIcon"      "$INSTDIR\${EXENAME}"
  WriteRegStr   HKLM "${ARPKEY}" "InstallLocation"  "$INSTDIR"
  WriteRegStr   HKLM "${ARPKEY}" "UninstallString"  '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKLM "${ARPKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${ARPKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${ARPKEY}" "NoRepair" 1
  WriteRegDWORD HKLM "${ARPKEY}" "EstimatedSize" 7600
SectionEnd

Section "Uninstall"
  SetShellVarContext all
  SetRegView 64
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir  "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKLM "${ARPKEY}"
  DeleteRegKey HKLM "Software\Rustorigin\Launcher"
SectionEnd
