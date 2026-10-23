---
paths:
  - "src/FolderPin.cs"
  - "scripts/test_folder_pin.ps1"
  - "scripts/test_folder_lock.ps1"
---
# Install-folder lock

- The lock itself (`LockFolder`, `NewRecord`, `ResetTree`, `SecureFolderElevated`) is in `src/WpfLauncher.cs`;
  `FolderPin.cs` holds the handle helpers. Behavior: docs/LAUNCHER.md "Security model"
- Permissions only through a pinned handle (`FolderPin.GetSecurity`/`SetSecurity` = `Get`/`SetKernelObjectSecurity`).
  Never `SetAccessControl`, `SetNamedSecurityInfo` or `SetSecurityInfo`: they push a folder's permissions onto what
  is inside it, hard links to files outside the folder included
- Never follow a link: open with `FILE_FLAG_OPEN_REPARSE_POINT`, check `InFolder` before acting on an entry, and
  delete another account's tree with `FolderPin.DeleteTree` - never `Directory.Delete(path, true)`
- Names ending in a dot or space (only creatable through `\\?\`) can't be opened as themselves: they count as unsecured
- Fail closed: every entry that can't be handled increments `failed`, and the folder stays refused. The
  `.rustorigin-lock-incomplete` record is a fresh one of ours (`NewRecord`), put at the name before the folder's own
  permissions are set, and removed only once every entry was secured. A folder planted at the name is moved aside
  first (`FolderPin.MoveAside` -> `.rustorigin-lock-incomplete.<guid>.old`)
- `test_folder_lock.ps1` drives `release\RustoriginLauncher.exe --secure-folder` unelevated: build it first
  (`make_release.ps1`). Tests run as one account, so paths that need another account (its files deleted, not
  re-permissioned) are not covered - say so when reporting
- CI also compiles `FolderPin.cs` with PowerShell 7's `Add-Type` (warnings are errors) through `test_folder_pin.ps1`
- Changes here need the `security-reviewer` agent before they are called done (CLAUDE.md Workflow)
