---
paths:
  - "src/ClientPatch.cs"
  - "scripts/client_patch_builder.cs"
  - "scripts/make_client_patch.ps1"
  - "scripts/client_filter.ps1"
  - "scripts/test_client_patch.ps1"
---
# Client delta update

- `Stage` only reads the install: it rebuilds into `_update\files`, verifies every file's SHA-256, and writes the
  manifest (`%LOCALAPPDATA%\Rustorigin\update-manifest.txt`) before the `ready` flag
- `Commit` moves only manifest-listed files of the right size (`rehash: true` when resuming after a restart), is
  idempotent, and writes the marker before removing `ready`
- Keep the exception split: `PatchException` = pack doesn't fit this install -> full-download fallback;
  `IOException` = retryable (no space, locked file) -> error, keep the verified pack
- Every recipe path goes through `SafeRelPath`; nothing may be written outside the install folder or touch the
  launcher's own files
- `client_filter.ps1` is shared by `package_client.ps1` and the builder: the zip and the pack ship the same file set
- Run `scripts\test_client_patch.ps1` after any change. Details: docs/LAUNCHER.md "Client updates (delta pack)"
