---
paths:
  - "scripts/installer/**"
  - "scripts/build_installer_exe.ps1"
  - "scripts/build_msi.ps1"
---
# Installers

- The NSIS setup is per-user (`RequestExecutionLevel user`, `%LOCALAPPDATA%\Programs`, HKCU). Never go back to
  admin/HKLM: an elevated install into a user-writable folder lets any account swap files an admin later runs
- `icacls` locks only a folder the setup created or that already holds the launcher; the uninstaller deletes
  recursively only a folder named `Rustorigin` / `Rustorigin Launcher`
- MSI: per-user, WiX **v5** only (v6+ needs a paid EULA); `UpgradeCode` in `RustOrigin.wxs` never changes
- Check NSIS edits compile: `makensis /V2 /DVERSION=1.2.0 /DVERSION4=1.2.0.0 /DREPO=<repo> /DOUTFILE=<scratch exe>
  scripts\installer\RustOrigin.nsi`. Never run a built setup's uninstaller on a dev machine
