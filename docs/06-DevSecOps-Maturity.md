# 06 — DevSecOps Maturity

The brief asks the project to demonstrate enterprise DevSecOps maturity. This
document is the argument for that claim, and it is worth being precise about
what the claim rests on.

**Running scanners is not maturity.** Any team can add Trivy and Checkov to a
pipeline in an afternoon, and most do. What separates a mature practice is
whether the controls hold when they are inconvenient: whether findings can be
silently suppressed, whether the artefact that reaches production is the one
that was scanned, whether a control that quietly disappears gets noticed, and
whether anyone can produce evidence six months later.

Six capabilities below are where that distinction lives. The scanner coverage
your brief specifies is present in full — it is the entry ticket, listed last.

---

## 1. Supply chain integrity

**The gap this closes:** a clean Trivy report proves the image *you scanned* was
clean. It says nothing about whether the image *running in production* is that
image.

| Control | Implementation | Evidence |
|---|---|---|
| Actions pinned to commit SHA | `scripts/check-action-pinning.mjs`, enforced in `supply-chain` job | Build log |
| Pins kept current | Dependabot `github-actions` ecosystem | PR history |
| Keyless image signing | `cosign sign` via OIDC, no private key exists | Rekor transparency log |
| Build provenance | `actions/attest-build-provenance`, SLSA Build L3 | Attestation in registry |
| SBOM per build | CycloneDX via Syft, attached and signed | 90-day artefact retention |
| Deploy by digest, never tag | `release.yml` deployment step | Deployment record |
| Verification before deploy | `cosign verify` gate between build and deploy | Build log |

A tag is a mutable pointer. `actions/checkout@v4` runs whatever the tag owner
last pushed, inside a job holding an OIDC token for your Azure subscription.
That is the same category of risk as `docker pull ...:latest`, and it is the
route used in most recent CI compromises.

> **Action required before this is real.** The scaffold's actions are on version
> tags, not SHAs, because commit SHAs cannot be invented — they have to be
> resolved against the real repositories. Run `npx ratchet pin
> .github/workflows/*.yml` once and commit the result. Until then the
> `supply-chain` job will fail, which is the correct behaviour.

## 2. Policy as code

**The gap this closes:** Checkov and tfsec enforce generic best practice. They
do not know that *this* platform's storage account holds receipts containing
bank details, or that its Key Vault holds the SQL TDE key.

`policy/terraform/azure_security.rego` encodes the decisions specific to this
system — public network access disabled on all four data services, ACR admin
user off, Key Vault purge protection on, required tags, production-only zone
redundancy, and a check for inline credentials in app settings.

Two design points worth defending:

- **It evaluates the plan, not the source.** `terraform show -json tfplan`
  resolves variables, module outputs and computed references. Scanning `.tf`
  files alone misses anything whose value is not literal in source — which, in a
  properly modularised codebase, is most of it.
- **Custom rules live beside the code they govern**, versioned and reviewed via
  `CODEOWNERS`. A rule in a vendor console that nobody in the team can read is a
  rule that will be worked around.

## 3. Exceptions with expiry

**The gap this closes:** every scanner supports suppression. Almost no team
manages suppressions, and a suppression file is where security debt goes to
become permanent.

`security-exceptions.yml` requires, per entry: owner, justification, linked
issue, acceptance date and **expiry date**. `scripts/check-exceptions.mjs` runs
on every PR and weekly, and fails the build when an exception lapses. Critical
and High are capped at 30 days; everything else at 180.

The script caught its own sample entry at 184 days on first run. That is the
mechanism working — the cap held rather than being adjusted to fit.

A suppression with no expiry is not an exception. It is a decision to accept the
risk permanently, usually made by whoever was in a hurry rather than whoever
owns the risk.

## 4. Continuous, not point-in-time

**The gap this closes:** a PR gate only ever sees code at merge. A CVE published
next month against a dependency shipped last month generates no PR, so no gate
runs, and nobody finds out.

`scheduled-security.yml`, weekly:

- **Re-scan of the running production image**, with `ignore-unfixed: false` —
  deliberately stricter than the PR gate, because at this point you want the
  full picture rather than only actionable items
- **Terraform drift detection across all three environments**, raising an issue
  automatically. Drift is where controls silently die: a firewall opened during
  a 2am incident and never closed, with state still insisting the rule is there
- **OpenSSF Scorecard** — an external, standardised assessment of the repo's own
  posture, useful precisely because it is not our opinion of ourselves
- **Exception register review**

## 5. Segregation of duties, enforced by the pipeline

Security controls in this platform are not only application logic. Several are
enforced by the delivery process itself:

| Control | Enforcement |
|---|---|
| No one merges their own security-relevant change | `CODEOWNERS` requires `@desicon/security` on `Core/`, `infra/`, `.github/`, `.config/` |
| No one deploys to production unreviewed | GitHub Environment `production` with required reviewers |
| Administrators are not exempt | Branch protection with *Include administrators* enabled |
| No stored cloud credentials | OIDC federated identity to Azure; no service principal secret exists to leak |
| History cannot be rewritten | Linear history, signed commits, no force push |

The *Include administrators* setting deserves the emphasis. It is the single
most commonly waived control, and waiving it makes every control above it
advisory.

## 6. Measurable

Maturity that cannot be measured cannot be defended in an audit or improved on
purpose. Metrics to publish from day one:

| Metric | Source | Target |
|---|---|---|
| Mean time to remediate Critical | Issue open→close, `security` label | < 7 days |
| Mean time to remediate High | Same | < 30 days |
| Open exceptions | `security-exceptions.yml` | < 10, none expired |
| Actions pinned | `check-action-pinning.mjs` | 100% |
| Pipeline pass rate first attempt | Actions API | > 80% |
| Drift incidents per month | Auto-raised issues | 0 in prd |
| Change failure rate | Deployments requiring rollback | < 15% |
| Deployment frequency | Release workflow runs | Weekly or better |
| Coverage on `Workflow.Core` | Coverlet | 100% |

---

## Scanner coverage — the brief's checklist

Every item specified, mapped to where it runs. This is table stakes, which is
why it comes last rather than first.

| Requirement | Job in `pr-validation.yml` | Gate |
|---|---|---|
| Terraform fmt | `terraform-security` | Blocks |
| Terraform validate | `terraform-security` | Blocks |
| Terraform plan | `terraform-security` | Blocks |
| TFLint | `terraform-security` | Blocks on error severity |
| Checkov | `terraform-security` | `soft_fail: false` — blocks |
| tfsec | `terraform-security` | SARIF to code scanning |
| CodeQL | `codeql` (C# + TypeScript, `security-extended`) | Blocks |
| Dependency Review | `dependency-review` | Blocks on High; denies GPL/AGPL |
| OWASP Dependency-Check | `dependency-scan` | `--failOnCVSS 7` |
| npm audit | `dependency-scan` | `--audit-level=high` |
| dotnet audit | `dependency-scan` | Blocks on High/Critical |
| Docker build | `container-security` | Blocks |
| Trivy filesystem | `container-security` | `exit-code: 1` on High/Critical |
| Trivy image | `container-security` | `exit-code: 1` on High/Critical |
| Gitleaks | `secret-detection` | Full history, blocks |
| GitHub Secret Scanning | Repository setting + push protection | Blocks push |
| Unit tests | `build-and-test` | Coverage thresholds enforced |
| Integration tests | `build-and-test` | Real SQL container |
| Build validation | `build-and-test` | `/warnaserror` |

All eleven jobs converge on a single `security-gate` job that fails if any
upstream job failed or was cancelled. `security-gate` is the only required
status check on `main`, so adding a job to the pipeline automatically extends
the gate without a branch-protection change — and removing one is visible in the
diff.

---

## Repository settings to apply

The pipeline is only half of it. These are configured in GitHub, not in code,
and without them the pipeline is decoration:

**Branch protection on `main`:**
☐ Require `Security Gate` status check · ☐ 2 approving reviews, 1 from CODEOWNERS ·
☐ Dismiss stale approvals on push · ☐ Require conversation resolution ·
☐ Require signed commits · ☐ Require linear history · ☐ Block force push and deletion ·
☐ **Include administrators**

**Repository security:**
☐ Secret scanning enabled · ☐ Push protection enabled · ☐ Dependabot alerts and
security updates on · ☐ Private vulnerability reporting on · ☐ Code scanning
default setup disabled (the workflow handles it) · ☐ Actions restricted to
verified creators and explicitly allowed actions

**Environments:**
☐ `production` with required reviewers and a wait timer · ☐ Federated OIDC
credentials scoped per environment, so a dev workflow cannot obtain a
production token

---

## What is deliberately not claimed

Honesty about the boundary is itself part of maturity, and a reviewer will find
these anyway:

- **DAST is not yet wired in.** OWASP ZAP against an ephemeral environment is
  the right next addition; it needs a disposable environment first.
- **No penetration test has been run.** Listed in the production readiness
  checklist as a go-live gate.
- **SLSA Build L3 is targeted, not verified.** The provenance is generated; the
  verification policy at admission still needs enforcing on the Azure side.
- **The pipeline has never executed.** It is syntactically valid and every job
  is wired to the gate, but it has not run against a real repository, so expect
  to fix action versions and permissions on the first few runs.
