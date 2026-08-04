# Branch protection on `main`

Applied with:

```bash
gh api -X PUT repos/aihebest/Finance-Workflow-Platform/branches/main/protection \
  --input scripts/github-branch-protection.json
```

Kept in the repository rather than applied as a one-off command so that what
is enforced is reviewable, diffable, and reproducible on the Desicon org repo
when ownership moves.

## What is on, and why

| Setting | Reason |
|---|---|
| `required_status_checks: build-test-validate` | The actual CI job name. `docs/06-DevSecOps-Maturity.md` names a `Security Gate` check that does not exist in `ci.yml` — requiring it would have blocked every merge permanently, which is the failure mode of applying a checklist without reading it against the repository. |
| `strict: true` | A branch must be up to date with `main` before merging, so the checks that passed were run against what will actually be on `main`. |
| `enforce_admins: true` | The item the build plan says is "most often waived". Protection that the owner can bypass protects against accidents, not against the owner having a bad day, and the owner is the only contributor. |
| `required_linear_history` | Keeps the audit story simple: one ordered sequence of commits, matching how the audit chain itself is reasoned about. |
| `allow_force_pushes: false`, `allow_deletions: false` | History on `main` is evidence. |
| `required_conversation_resolution` | A review comment cannot be merged past silently. |
| `required_approving_review_count: 0` | See below. |

## What is deliberately off, and when to turn it on

**Approving reviews (`0`, not `2`).** The maturity document asks for two
approvals with one from CODEOWNERS. This repository has one contributor, so
any non-zero count freezes `main` completely — nobody else can approve. Zero
still requires a pull request, which is the part that carries the value today:
CI runs before merge, the diff is reviewable, and the history is linear.

Raise this to `2` and set `require_code_owner_reviews: true` the day a second
engineer joins. `.github/CODEOWNERS` already exists so that is one setting,
not another round of decisions.

**Signed commits.** Current commits are unsigned (`git log --pretty=%G?`
reports `N`). Turning this on before configuring a signing key would reject
every push. Configure GPG or SSH signing first, verify one commit shows `G`,
then add `required_signatures`.

## Consequence

Direct pushes to `main` stop working. The workflow becomes:

```bash
git switch -c feature/whatever
git commit -am "..."
git push -u origin HEAD
gh pr create --fill
# wait for build-test-validate, then:
gh pr merge --squash --delete-branch
```

This is a real change from pushing straight to `main`, and it is the point:
until now the pipeline could be bypassed by the person most able to bypass it.

## Not yet applied

- **Secret scanning and push protection** — return HTTP 422 "not available for
  this repository". Private repositories need GitHub Advanced Security. This is
  now the third control behind that licensing decision, alongside CodeQL and
  SARIF upload. See the open decisions in `docs/12-Decision-Log.md`.
- **Private vulnerability reporting** — public repositories only. Not
  applicable.
- **Actions restricted to verified creators** — every action is already pinned
  to a full commit SHA and enforced by `scripts/check-action-pinning.mjs` in
  CI, which is the stronger control. Worth adding as defence in depth.
