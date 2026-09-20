# Contributing

Changes to `main` go through a **pull request**. `main` is never committed to directly - it only
changes when a PR is merged. This keeps every change reviewed and CI-checked.

For architecture and build details, see [CLAUDE.md](CLAUDE.md).

## Prerequisites

- **Windows 10/11** (the launcher is C#/.NET Framework 4.x WPF; `csc.exe` ships with Windows).
- **Git**, and optionally the **GitHub CLI** (`gh`) for creating PRs from the terminal.
- Only needed to build the installers: **WiX v5** (`dotnet tool install --global wix --version 5.0.2`
  + `wix extension add -g WixToolset.UI.wixext/5.0.2`) and **NSIS** (`winget install NSIS.NSIS`).

## The pull request workflow

### 1. Branch off the latest `main`

```bash
git checkout main
git pull
git checkout -b short-descriptive-name    # e.g. fix-resume-offset
```

### 2. Make your change and check it builds

A clean compile is the only gate (there are no automated tests):

```powershell
.\scripts\build.bat            # quick compile check of src\WpfLauncher.cs
# or a full shippable build:
.\scripts\make_release.ps1 -Version 0.0.0
```

If you touched the **download / verify** path, also run the integrity smoke test: with `Sha256`
blank INSTALL is refused; with the correct hash it downloads -> verifies -> extracts; with a wrong
hash the download is rejected and nothing is installed.

### 3. Commit and push the branch

```bash
git add -A
git commit -m "Fix resume offset off-by-one"
git push -u origin short-descriptive-name
```

This pushes your **branch**, not `main` - nothing on `main` changes yet.

### 4. Open the pull request into `main`

- On GitHub, click **"Compare & pull request"** after pushing (base = `main`), or
- from the terminal: `gh pr create --base main --fill`.

Fill in the PR template (summary, what changed, how you tested).

### 5. Let CI run, then merge

Opening the PR automatically triggers the **Build check** workflow
([.github/workflows/build-check.yml](.github/workflows/build-check.yml)); it compiles
`RustOrigin.exe` and the WinForms project on a Windows runner and reports a green check or red X on
the PR. **Merge only when it's green** (GitHub "Merge pull request", or `gh pr merge`).

### 6. Clean up

```bash
git checkout main
git pull                 # main now includes your merged change
git branch -d short-descriptive-name
```

## Commit messages

- Imperative present tense, concise subject line ("Add ...", "Fix ...", "Reword ...").
- Add a short body when the *why* isn't obvious from the subject.

## Releasing (maintainers)

Merging to `main` publishes nothing. To ship a version, first complete the client-hash steps in the
**Release / publish checklist** in [CLAUDE.md](CLAUDE.md) (package the client, set the matching
`Sha256`, upload the zip), then tag `main`:

```bash
git tag v1.0.0 && git push origin v1.0.0
```

The tag fires the **Release** workflow ([.github/workflows/release.yml](.github/workflows/release.yml)),
which builds the exe + NSIS setup + MSI, generates checksums, and creates the GitHub Release.

## Enforcing PR-only

Requiring PRs (blocking direct pushes to `main`) is a repository setting, not something a workflow
enforces. See [.github/BRANCH_PROTECTION.md](.github/BRANCH_PROTECTION.md) for the importable
ruleset and how to enable it. Note: on GitHub Free, branch protection only applies to **public**
repositories.
