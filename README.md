# Desicon Finance Workflow Platform

Digital replacement for Desicon Engineering Limited's paper financial approval
forms. Phase 1 covers Expense Reimbursement, Cash Advance and Procurement
Requisition, built on a generic workflow engine so later processes are added as
data rather than code.

Modelled directly on `DEL-AC-FRM-002 Rev 05` and `DEL-AC-FRM-003 Rev 05`.

---

## Status

This is a Sprint 0 scaffold, not a running system. What exists:

| Area | State |
|---|---|
| Workflow engine (guards, transitions, SLA) | Written, unit-tested |
| Definition validator | Written, unit-tested, wired into a CLI check |
| Domain model (Expense, Cash Advance, Audit) | Written, unit-tested |
| Workflow definitions (Expense, Cash Advance) | Written and validating clean |
| PR pipeline | 11 jobs: all gates from the DevSecOps spec, plus supply-chain, policy-as-code and SBOM |
| Release pipeline | Keyless signing, SBOM attestation, build provenance, digest-based deploy |
| Scheduled security | Weekly re-scan, Terraform drift detection, OpenSSF Scorecard |
| Policy as code | OPA/Rego over the Terraform plan |
| Docker | API and web images, chiselled runtime, non-root |
| Terraform | App Service module only; network/SQL/KV/storage modules still to write |
| API project | Not yet written |
| Infrastructure/EF layer | Not yet written |
| Frontend | Not yet written |
| Procurement module | Blocked — awaiting the paper form |

**Nothing here has been compiled.** It was authored without a .NET toolchain
available, so expect to fix a handful of compile errors on first build. The
workflow JSON definitions *have* been validated (see below) and the pipeline
YAML parses.

---

## First run

```bash
# 1. Create the solution and wire up the projects
./bootstrap.sh          # or: pwsh ./bootstrap.ps1 on Windows

# 2. Build and test
dotnet build
dotnet test

# 3. Validate the workflow definitions
node tools/validate-definitions.mjs modules

# 4. Security housekeeping checks (both run in CI too)
node scripts/check-exceptions.mjs
node scripts/check-action-pinning.mjs     # will FAIL until step 5

# 5. Pin every GitHub Action to a commit SHA — do this once, then commit
npx ratchet pin .github/workflows/*.yml
```

Step 5 is not optional. Actions are currently on version tags, which are mutable
pointers: `actions/checkout@v4` runs whatever the tag owner last pushed, inside a
job holding an OIDC token for your Azure subscription. SHA pinning could not be
done in the scaffold because commit SHAs have to be resolved against the real
repositories rather than invented.

Expected output from step 3:

```
cash-advance.workflow.json  (CASH_ADVANCE — DEL-AC-FRM-003 Rev 05)
  states: 14  transitions: 28
  OK — no structural errors

expense-reimbursement.workflow.json  (EXPENSE — DEL-AC-FRM-002 Rev 05)
  states: 13  transitions: 23
  WARN   NO_NEGATIVE_PATH: REFUND_DUE offers no REJECT or RETURN
  OK — no structural errors
```

That remaining warning is known and accepted: `REFUND_DUE` is a state waiting
for the employee to return over-drawn cash, and there is no sensible "reject"
from it — the money is owed either way. Revisit if Finance defines a write-off
path for small balances.

---

## Layout

```
src/
  Desicon.Workflow.Core/           Engine. Knows nothing about expenses.
    Guards/                        Restricted expression language
    Definitions/                   Definition model
    Engine/                        Transition execution
    Validation/                    Structural validation of definitions
  Desicon.Workflow.Domain/         Entities, modelled on the paper forms
  Desicon.Workflow.Infrastructure/ EF Core, blob, Graph  (to write)
  Desicon.Workflow.Api/            ASP.NET Core Web API  (to write)
  Desicon.Workflow.Functions/      Timer jobs: reminders, escalation, retirement sweep  (to write)
  Desicon.Workflow.Web/            React + TypeScript  (to write)
modules/                           Workflow definitions — data, not code
tools/                             Definition validator CLI
infra/terraform/                   Modules and per-environment roots
docs/                              Design package
```

---

## The design rule that matters

`Desicon.Workflow.Core` must never gain knowledge of a specific module. The test
is concrete: **adding a Leave Request module should require no change to that
project and no schema migration.** If a change to Core is needed to add a
module, the abstraction is wrong — fix the abstraction rather than special-casing.

The engine project deliberately has zero package references. Keep it that way.

---

## Security notes for contributors

- **No secrets, ever.** The platform authenticates to Azure by Managed Identity.
  A connection string in this repo should carry no password. Gitleaks runs on
  full history in CI.
- **Every state change goes through `WorkflowEngine.ExecuteAsync`.** There is no
  second path, and that is what guarantees an audit event exists for every
  transition. Do not add a shortcut.
- **Guards are parsed, never compiled or evaluated dynamically.** Functions come
  from a fixed allowlist. Do not add a function that touches IO, reflection or
  the process.
- **Maker–checker is not configurable.** `PostedByUserId != AuthorisedByUserId`
  is enforced in the guard, in the domain, and as a database check constraint.
- **`AUDIT_EVENT` is insert-only** and hash-chained. The application identity
  holds no UPDATE or DELETE grant on it.

---

## Open questions blocking work

Listed in full in `docs/00-Phase1-README.md`. The two that block code:

1. **Is the 24h/72h retirement window calendar or working hours, and does the
   clock start at approval, cash release, or recipient acknowledgement?** The
   scaffold assumes calendar hours from acknowledgement. This drives every
   overdue figure on the dashboard.
2. **Does a procurement form exist?** If so, send it. The two supplied forms
   changed the design substantially; a third probably would too.

---

## DevSecOps

See `docs/06-DevSecOps-Maturity.md`. The short version: scanner coverage is the
entry ticket, not the claim. The claim rests on supply-chain integrity (signed
images, provenance, SBOM, pinned actions), policy-as-code over the Terraform
plan, exceptions that expire, continuous rather than point-in-time scanning, and
segregation of duties enforced by branch protection with *Include administrators*
on.

## Documentation

`docs/` holds the full design package — form analysis and field mapping,
solution architecture, data model, security architecture, DevSecOps, API and
frontend design, and the production readiness checklist.
