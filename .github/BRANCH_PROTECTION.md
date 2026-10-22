# Branch protection: work by pull requests only

"Changes to `main` must go through a pull request" is a **repository setting**, not something a
workflow can enforce on its own. A workflow (here, `build-check`) provides the CI *check*; the
ruleset below makes PRs mandatory and requires that check to pass before merging.

`rulesets/protect-main.json` defines a ruleset that, on the default branch (`main`):

- **requires a pull request** to merge (no direct pushes),
- **requires the `Build check` CI to pass** (status check `compile`),
- **blocks force-pushes and branch deletion**,
- requires **0 approvals** - so as a solo maintainer you can still merge your own PRs (raise this
  to 1 once you have another reviewer; you can't approve your own PR).

## Apply it (pick one)

**GitHub UI (easiest):** Repo -> Settings -> Rules -> Rulesets -> *New ruleset* -> *Import a ruleset*
-> choose `.github/rulesets/protect-main.json` -> Create. (Or create the same rule by hand under
Settings -> Branches -> Add branch ruleset.)

**GitHub CLI:**

```bash
gh api -X POST repos/RUSTORIGIN/launcher/rulesets --input .github/rulesets/protect-main.json
```

## Important caveats

- **Plan/visibility:** on **GitHub Free, branch protection and rulesets only work on _public_
  repositories**. While this repo is private, enforcement needs GitHub Pro/Team, or wait until you
  make it public. (The ruleset file is safe to commit either way.)
- **Status-check name:** the required check is `compile` (the job in `build-check.yml`). GitHub only
  lets you pick a check name it has seen before, so let the workflow run once on a PR, then confirm
  `compile` (or `Build check / compile`) is the selected required check - adjust the ruleset if the
  displayed name differs.
- After enabling, the flow is: branch -> push -> open PR -> CI runs -> merge when green.

# Release protection: who can ship a launcher to every player

The launcher updates itself on every player's PC, so **whoever can publish a release reaches every
player**. Three layers, from cheapest to strongest:

1. **Tag ruleset** (`rulesets/protect-release-tags.json`): only repository **admins** may create, move or
   delete a `v*` tag - the tag push is what starts the release workflow. A collaborator, a write-scoped
   token or a deploy key can no longer start a release. Apply it like the branch ruleset:

   ```bash
   gh api -X POST repos/RUSTORIGIN/launcher/rulesets --input .github/rulesets/protect-release-tags.json
   ```

2. **`release` environment** (the workflow's `publish` job, the only job with write access): Repo ->
   Settings -> Environments -> *New environment* `release` ->
   - *Required reviewers*: you (the job then waits for your approval before it creates the draft),
   - *Deployment branches and tags*: *Selected* -> add the tag rule `v*`.

   The job runs only GitHub's own actions (pinned by commit SHA) and the preinstalled `gh` CLI; the
   build job, which runs third-party installer tooling, can only read the repo.

3. **Release signature** (the one that does not depend on GitHub at all): CI only ever creates a
   **draft**. Launchers install only a release whose `SHA256SUMS.txt` is signed with the offline release
   key (`UpdatePublicKey=` in `config/launcher.cfg`), so a compromised workflow, action, token or GitHub
   account can at most produce an unsigned draft that no launcher installs. On your own machine:

   ```powershell
   .\scripts\new_signing_key.ps1 -OutFile D:\offline\rustorigin-release-key.xml   # once; commit the printed UpdatePublicKey= line
   .\scripts\sign_release.ps1 -Tag v1.3.0 -KeyFile D:\offline\rustorigin-release-key.xml -Publish   # every release
   ```

   `sign_release.ps1` checks every draft asset against `SHA256SUMS.txt` (and that nothing unlisted is
   attached) and checks its signature with the committed public key before uploading anything.

Caveats: the same plan/visibility limit applies - on GitHub Free, rulesets and required reviewers on
environments only work on **public** repositories. The signature (layer 3) works on any plan. Keep admin
access to the few people who release, and keep their tokens fine-grained and short-lived: an admin's token
bypasses layer 1.
