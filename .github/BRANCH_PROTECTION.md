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
