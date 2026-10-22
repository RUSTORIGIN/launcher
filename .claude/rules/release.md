---
paths:
  - ".github/workflows/**"
  - "scripts/make_release.ps1"
  - "scripts/sign_release.ps1"
  - "scripts/new_signing_key.ps1"
  - "scripts/package_client.ps1"
  - "config/launcher.cfg"
  - "config/piece-hashes.txt"
  - "scripts/make_piece_hashes.ps1"
---
# Release chain

- Order matters (docs/LAUNCHER.md "Release / publish checklist"): package client -> set `DownloadUrl`, `Sha256`,
  `ClientSize` -> for a new client build also `ClientVersion`, regenerated `AcClientFile=` lines, `PatchUrl`,
  `PatchSha256`, `PatchSize`, `PatchProbe` -> PR to main -> tag `vX.Y.Z` -> CI draft -> `sign_release.ps1` -> publish
- Whenever the hosted zip or pack changes, regenerate `config/piece-hashes.txt` with `scripts\make_piece_hashes.ps1`
  on the exact uploaded files (piece repair); its `file` lines give the exact `ClientSize` / `PatchSize`
- Any re-package changes the hash: the zip, `Sha256`/`ClientSize` and the uploaded file move together; a new client
  build gets a new file name in `DownloadUrl` (CDN caching)
- `UpdatePublicKey=` must be set before tagging (release.yml refuses otherwise). Never put the private key, a token
  or a CDN credential in the repo, a workflow or a log
- `release.yml`: `permissions: {}` at the top, build job read-only, only `publish` writes (environment `release`);
  pin actions to full commit SHAs (`gh api repos/<owner>/<action>/commits/<tag> --jq .sha`); keep NSIS/WiX pinned
- CI only creates drafts. Publishing = `scripts\sign_release.ps1 -Publish` on the maintainer's machine: outward-facing,
  never run it (or tag, or upload to the CDN) unasked
