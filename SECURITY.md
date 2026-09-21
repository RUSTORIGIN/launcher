# Security Policy

The RustOrigin launcher is open source specifically so its security-relevant behavior - network
communication, downloads, SHA-256 verification, installation, and process launching - can be
inspected and verified. We take reports about that behavior seriously.

## Scope

This policy covers the **launcher** in this repository (`WpfLauncher.cs` -> `RustOriginLauncher.exe`, and
the WinForms `RustLauncher.exe`) and its build/release scripts.

It does **not** cover the Rust client itself, which is distributed separately and is not part of
this repository, nor third-party hosting infrastructure (e.g. the download CDN) beyond how the
launcher interacts with it.

Examples of in-scope issues:

- A way to make the launcher install or run a file that does **not** match the expected
  SHA-256, or to bypass the mandatory verification gate.
- TLS downgrade / insecure-transport fallback, or acceptance of an unverified download.
- Path traversal ("zip slip") or writing outside the install directory during extraction.
- Local privilege escalation, arbitrary code execution, or persistence introduced by the launcher.
- Handling of untrusted server/config input that leads to code execution.

## Reporting a vulnerability

**Please report privately - do not open a public issue for an exploitable vulnerability.**

- Email: **rustorigin@proton.me**
- Please include:
  - a description of the issue and its impact,
  - steps to reproduce (proof-of-concept if possible),
  - the launcher version (shown in the Settings tab) and your Windows version,
  - any relevant excerpt from `%LOCALAPPDATA%\RustOrigin\launcher.log`.

You can expect an acknowledgement within a reasonable time. We will investigate, keep you updated
on progress, and coordinate a disclosure timeline with you. Please give us a reasonable window to
release a fix before any public disclosure.

Please act in good faith: only test against your own installation, do not access or modify other
users' data, and avoid service disruption.

## Supported versions

This is a rolling community project; only the **latest release** is supported. Fixes ship in a new
release rather than as patches to older builds. Verify a release against the published source,
checksums, and (where available) build attestation.

## Verifying a download

The launcher enforces integrity itself: it refuses to install unless the downloaded client's
SHA-256 matches the value baked into the release, and rejects (deletes) any mismatch instead of
installing or launching it. If you believe a hosted file does not match its expected hash, treat
it as a potential incident and report it using the channel above.
