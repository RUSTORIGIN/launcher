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
!include "LogicLib.nsh"

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
!define GAMEDIR     "C:\Rustorigin"          ; the launcher's default client folder (InstallDir in launcher.cfg)
!define GAMEEXE     "RustClient.exe"
!define GAMEMARKER  ".rustorigin-installed"  ; written by the launcher after a completed client install

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
!define MUI_UNCONFIRMPAGE_TEXT_TOP "This removes Rustorigin completely: the launcher, the game files, the shortcuts, and your launcher settings, logs and download cache."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Section "Install"
  SetShellVarContext all              ; per-machine: all-users Start Menu/Desktop, HKLM
  SetRegView 64
  SetOutPath "$INSTDIR"
  File "${REPO}\release\${EXENAME}"

  ; explicit icon (not the implicit ",0"), so a reinstall to the same path never keeps a stale blank icon
  CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0
  CreateShortCut "$DESKTOP\${APPNAME}.lnk"    "$INSTDIR\${EXENAME}" "" "$INSTDIR\${EXENAME}" 0

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

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'   ; SHCNE_ASSOCCHANGED: Explorer refreshes icons
SectionEnd

; ---- uninstaller helpers ----
; $R0 = folder (left unchanged). Sets $R1 = "1" if it is safe to delete recursively and $R6 = the
; normalized path; "0" for anything that isn't a plain absolute folder, a drive root, or a
; system/profile folder (e.g. someone typed C:\ or C:\Program Files as the install location).
Function un.IsSafeToWipe
  StrCpy $R1 "0"
  StrCpy $R6 ""
  ; must be absolute: "X:\..." or UNC "\\server\share\..." (a bare "C:" means "current folder of C:")
  StrCpy $R2 $R0 2 1
  StrCpy $R3 $R0 2
  ${If} $R2 != ":\"
  ${AndIf} $R3 != "\\"
    Return
  ${EndIf}
  ; normalize so odd spellings ("C:\\Windows", "C:\Windows\", "C:\x\..\Windows") can't slip past;
  ; if Windows can't resolve it, don't wipe
  ClearErrors
  GetFullPathName $R6 $R0
  ${If} ${Errors}
  ${OrIf} $R6 == ""
    StrCpy $R6 ""
    Return
  ${EndIf}
  StrCpy $R2 $R6 1 -1
  ${If} $R2 == "\"
    StrLen $R2 $R6
    ${If} $R2 > 3
      StrCpy $R6 $R6 -1
    ${EndIf}
  ${EndIf}
  StrLen $R2 $R6
  ${If} $R2 < 4
    Return                                   ; drive root such as "C:\"
  ${EndIf}
  StrCpy $R3 $WINDIR 2                       ; system drive, e.g. "C:"
  ${If} $R6 == $WINDIR
  ${OrIf} $R6 == $SYSDIR
  ${OrIf} $R6 == $PROGRAMFILES
  ${OrIf} $R6 == $PROGRAMFILES64
  ${OrIf} $R6 == $COMMONFILES
  ${OrIf} $R6 == $PROFILE
  ${OrIf} $R6 == $DESKTOP
  ${OrIf} $R6 == $DOCUMENTS
  ${OrIf} $R6 == $APPDATA
  ${OrIf} $R6 == $LOCALAPPDATA
  ${OrIf} $R6 == $TEMP
  ${OrIf} $R6 == "$R3\Users"
  ${OrIf} $R6 == "$R3\ProgramData"
  ${OrIf} $R6 == "$R3\Program Files"
  ${OrIf} $R6 == "$R3\Program Files (x86)"
    Return
  ${EndIf}
  StrCpy $R1 "1"
FunctionEnd

; $R0 = folder. Deletes it completely when safe; otherwise removes only the launcher's own files.
Function un.WipeDir
  Call un.IsSafeToWipe
  ${If} $R1 == "1"
    RMDir /r /REBOOTOK "$R6"
  ${Else}
    Delete "$R0\${EXENAME}"
    Delete "$R0\${EXENAME}.old"
    Delete "$R0\${EXENAME}.new"
    Delete "$R0\Uninstall.exe"
  ${EndIf}
FunctionEnd

; $R0 = folder. Refuses to continue while the game is running from it (its files would be locked).
Function un.EnsureGameClosed
  ${IfNot} ${FileExists} "$R0\${GAMEEXE}"
    Return
  ${EndIf}
  retry:
    ClearErrors
    FileOpen $R4 "$R0\${GAMEEXE}" a          ; a running exe cannot be opened for writing
    ${If} ${Errors}
      MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Rustorigin is still running.$\r$\n$\r$\nClose the game, then click Retry." /SD IDCANCEL IDRETRY retry
      Abort
    ${EndIf}
    FileClose $R4
FunctionEnd

Function un.onInit
  StrCpy $R0 "$INSTDIR"
  Call un.EnsureGameClosed
  StrCpy $R0 "${GAMEDIR}"
  Call un.EnsureGameClosed
FunctionEnd

Section "Uninstall"
  SetRegView 64

  ; 1) close the launcher so none of its files are locked
  nsExec::Exec '"$SYSDIR\taskkill.exe" /F /IM ${EXENAME}'
  Pop $0
  Sleep 800

  ; 2) shortcuts: the installer's (all users) and the Desktop one the launcher creates (current user)
  SetShellVarContext all
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"
  SetShellVarContext current
  Delete "$SMPROGRAMS\${APPNAME}.lnk"
  Delete "$DESKTOP\${APPNAME}.lnk"

  ; 3) the game client, when the launcher put it somewhere other than the install folder
  ;    (only if it really is a Rustorigin install)
  ${If} "${GAMEDIR}" != "$INSTDIR"
    ${If} ${FileExists} "${GAMEDIR}\${GAMEMARKER}"
    ${OrIf} ${FileExists} "${GAMEDIR}\${GAMEEXE}"
    ${OrIf} ${FileExists} "${GAMEDIR}\${EXENAME}"
      StrCpy $R0 "${GAMEDIR}"
      Call un.WipeDir
    ${EndIf}
  ${EndIf}

  ; 4) launcher data: settings, logs, unpacked assets and the download cache
  RMDir /r /REBOOTOK "$LOCALAPPDATA\Rustorigin"

  ; 5) the install folder itself (launcher, uninstaller, self-update leftovers, and the game when
  ;    it lives here - the default)
  StrCpy $R0 "$INSTDIR"
  Call un.WipeDir

  ; 6) registry
  DeleteRegKey HKLM "${ARPKEY}"
  DeleteRegKey HKLM "Software\Rustorigin\Launcher"
  DeleteRegKey /ifempty HKLM "Software\Rustorigin"   ; drop the parent too if nothing else lives there

  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'   ; refresh Explorer (removed shortcuts/icons)
SectionEnd
