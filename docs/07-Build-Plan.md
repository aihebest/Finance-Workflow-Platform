# 07 — Build Plan

From the current green build to production rollout, in twelve steps.

**Baseline verified 31 July 2026:** `dotnet build` and `dotnet test` clean —
0 warnings, 0 errors, 60/60 tests. Core engine, domain model, workflow
definitions, DevSecOps pipeline and docs complete. Infrastructure, API,
Functions, frontend and most Terraform not started.

Each step below is sized to be one Claude Code session, and each ends with an
acceptance check you can actually run. Do not move on until the check passes —
the whole point of the ordering is that every step stands on verified ground.

---

## How to use this

Each step gives you three things:

- **Prompt** — paste into Claude Code as-is
- **Watch for** — the decisions Claude Code will get wrong without being told.
  These are the parts worth reading before you start the session.
- **Done when** — the command that proves it

Work on a branch per step (`feat/01-persistence`), open a PR, let the pipeline
run. From step 7 onward the pipeline is real and will block you, which is the
intended behaviour.

---

# Phase A — Walking skeleton

The goal of Phase A is one expense claim travelling end-to-end through a
deployed system. Everything after it is thickening.

## Step 1 — Persistence layer

**Goal:** EF Core context, entity configurations, first migration. Nothing can
be saved until this exists.

**Prompt:**
> Create `src/Desicon.Workflow.Infrastructure` targeting net8.0, referencing
> Domain and Core. Add `WorkflowDbContext` with EF Core 8 for SQL Server.
> Configure Request as table-per-type with ExpenseRequest and
> CashAdvanceRequest as derived tables sharing the primary key. Add entity
> configurations in `Persistence/Configurations/` for every entity. Implement
> all check constraints and indexes described in `docs/03-Data-Model-ERD.md`.
> Generate the initial migration.

**Watch for:**

- **Money precision.** Every `decimal` needs `.HasPrecision(18, 2)` explicitly.
  EF's default is `decimal(18,2)` on SQL Server but it warns, and
  `TreatWarningsAsErrors` is on. `FxRate` needs `(18, 6)`.
- **Guid clustered keys fragment badly.** `Guid.NewGuid()` produces random
  values; as a clustered primary key on a table taking thousands of inserts
  that means constant page splits. Either default the column to
  `NEWSEQUENTIALID()` in SQL, or keep the Guid PK non-clustered and cluster on
  `RequestNumber`. Decide now — changing it later is a table rebuild.
- **Temporal tables.** `.IsTemporal()` on `Request` and the module tables. Note
  that EF migrations for temporal tables are awkward to reverse; test the down
  migration once before you rely on it.
- **`AuditEvent` must be insert-only.** Add a `SaveChangesInterceptor` that
  throws if any `AuditEvent` is in `Modified` or `Deleted` state. The database
  grant enforces it in production, but the interceptor catches the mistake at
  development time where it is cheap to fix.
- **Do not seed reference data in the migration.** Projects, cost centres and
  expense categories change; a data migration that runs on every deploy is a
  future incident. Seed via a separate idempotent script.

**Done when:**
```bash
dotnet ef migrations add InitialCreate -p src/Desicon.Workflow.Infrastructure -s src/Desicon.Workflow.Api
dotnet ef migrations script          # inspect the SQL; check constraints present
dotnet build
```

---

## Step 2 — The action pipeline

**Goal:** the single transaction that executes a transition, persists it, seals
an audit event and queues a notification. This is the most important step in
the project.

**Prompt:**
> Create `RequestActionService` in Infrastructure. It executes a workflow
> transition in one database transaction: load the request with its rowversion,
> set ActorId, call `WorkflowEngine.ExecuteAsync`, apply the resulting state
> change, compute the new SLA due date via the working calendar, seal an
> `AuditEvent` chained to the previous event for that request, and write a
> notification outbox row. Add an `OutboxMessage` entity and a dispatcher.
> Handle idempotency keys so a retried request does not double-execute.

**Watch for:**

- **The hash chain has a concurrency hazard.** Sealing requires the previous
  event's hash for that request. Two concurrent transitions on one request will
  both read the same previous hash and produce a forked chain, which the
  verification job will report as tampering. Take the request row with
  `UPDLOCK` in the same transaction so the second caller blocks, then fails
  cleanly on rowversion. Test this with two concurrent calls — it will not show
  up any other way.
- **Never send the notification inside the transaction.** If the transaction
  rolls back you have already emailed an approver about an approval that did not
  happen; if Service Bus is slow you have coupled your database transaction to a
  network call. Write an outbox row inside the transaction and dispatch it
  afterwards.
- **Set `ActorId` before evaluating guards.** Maker–checker guards read
  `ActorId != PostedByUserId`. If it is null when the guard runs, the guard
  throws and correctly blocks — but the error message will be confusing.
- **Idempotency must cover the whole transition,** not just the HTTP request.
  Store the key with the resulting audit event id and return the original
  outcome on replay.

**Done when:** an integration test proves that no state change is possible
without a corresponding audit row, and that two concurrent transitions produce
one success and one clean concurrency failure.

---

## Step 3 — API

**Goal:** the endpoints from `docs/05-API-Frontend-and-Operations.md`, with
authentication and three-layer authorisation.

**Prompt:**
> Create `src/Desicon.Workflow.Api` as an ASP.NET Core 8 Web API. Add Entra ID
> JWT bearer authentication validating against the tenant JWKS. Implement the
> generic engine endpoints and the expense and cash advance module endpoints
> from `docs/05-API-Frontend-and-Operations.md`. Add an authorisation filter
> that scopes every read by requester, reporting line or role. Use
> problem+json for errors, ETag/If-Match mapped to rowversion, and the
> ASP.NET Core rate limiter at the documented thresholds.

**Watch for:**

- **IDOR is the likeliest real vulnerability in this system.** Every read path —
  including attachments, comments and history — must be scoped in the query
  itself, not filtered after loading. `GET /requests/{id}` for someone else's
  claim returns 404, not 403; a 403 confirms the record exists.
- **`POST /actions` is the only mutation path for workflow state.** If a later
  session adds a convenience endpoint that changes state directly, the audit
  guarantee is gone. Note it in the PR template.
- **Health endpoints must not leak.** `/health/ready` should check SQL and Key
  Vault reachability but return only healthy/unhealthy — no connection strings,
  no exception detail. It is unauthenticated by necessity.
- **Do not enable Swagger UI in production.** Generate the OpenAPI document for
  the contract test, but gate the UI behind the dev environment.

**Done when:**
```bash
dotnet test tests/Desicon.Workflow.IntegrationTests
```
with tests covering every endpoint and, specifically, an authorisation test per
role proving each cannot reach what it should not.

---

## Step 4 — Integration test suite

**Goal:** prove steps 1–3 against real SQL, including full workflow traversal.

**Prompt:**
> Create `tests/Desicon.Workflow.IntegrationTests` using Testcontainers with
> `mcr.microsoft.com/mssql/server:2022-latest` and `WebApplicationFactory`.
> Write tests that drive an expense claim through every path in
> `modules/expense-reimbursement.workflow.json` including return, rejection,
> escalation, and the negative-net-payable refund path. Do the same for cash
> advance including partial and full retirement. Add a test asserting every
> state and transition in both definitions is exercised.

**Watch for:**

- **Test the definitions, not just the engine.** A correct engine running a
  definition with an unreachable state is a live incident. The coverage
  assertion over states and transitions is the point of this step.
- **The refund path is the one nobody tests.** Negative net payable →
  `REFUND_DUE` → refund confirmed → `POSTING`. It is where money leaks on paper
  and it will be where the bug is.
- Seed a deterministic clock. `TimeProvider` is already injectable in
  `WorkflowClock`; use `FakeTimeProvider` from
  `Microsoft.Extensions.TimeProvider.Testing`.

**Done when:** `dotnet test` runs both suites green, and the coverage gate
(80% overall, 100% on `Workflow.Core`) passes.

---

## Step 5 — Terraform and first deployment

**Goal:** a deployed dev environment, and a real target for the OPA policy.

**Prompt:**
> Write Terraform modules for network, sql, keyvault, storage, monitoring,
> functions and frontdoor in `infra/terraform/modules/`, matching the style and
> security posture of the existing app-service module. Then write
> `infra/terraform/environments/dev/` composing them per
> `docs/02-Solution-Architecture.md`. All data services must have public network
> access disabled and private endpoints from the app subnet.

**Watch for:**

- **State backend is a chicken-and-egg problem.** The storage account holding
  Terraform state cannot be created by the Terraform that uses it. Write a
  one-off `scripts/bootstrap-state.sh` using Azure CLI, run it once per
  subscription, and commit it so the step is reproducible.
- **Private endpoints need private DNS zones *and* virtual network links.**
  Forgetting the link is the classic failure: the endpoint exists, DNS still
  resolves to the public IP, and connections fail in a way that looks like a
  firewall problem.
- **Terraform cannot grant Managed Identity access inside SQL.** Creating the
  contained database user requires `CREATE USER [app-name] FROM EXTERNAL
  PROVIDER` executed against the database by an Entra admin. Script it and run
  it as a post-apply step; do not leave it as a manual note.
- **Run `conftest` locally before pushing.** `policy/terraform/azure_security.rego`
  will fail the build on missing tags and public access, which is what it is for.

**Done when:**
```bash
cd infra/terraform/environments/dev && terraform plan
terraform show -json tfplan > plan.json
conftest test --policy ../../../../policy/terraform --all-namespaces plan.json
```
both clean, then `terraform apply` and the API's `/health/ready` returns 200
through Front Door.

**At this point the walking skeleton is complete.** One request can travel from
submission to closure on deployed infrastructure. Everything below thickens it.

---

# Phase B — The promises to management

## Step 6 — Timer functions

**Goal:** reminders, escalation and the retirement sweep. Without these, the
platform digitises paper but does not fix delay — which is the specific thing
your correspondence promised.

**Prompt:**
> Create `src/Desicon.Workflow.Functions` as an isolated-worker Azure Functions
> project. Implement `ReminderSweep` (hourly), `EscalationSweep` (hourly) and
> `RetirementSweep` (daily 06:00 WAT) per `docs/02-Solution-Architecture.md`.
> Escalation must transfer authority to the escalation target and write an
> `Escalated` audit event naming the person who did not act. Add an
> `AuditChainVerification` function running nightly.

**Watch for:**

- **Timer triggers fire once per instance without a singleton.** On a scaled-out
  plan that means duplicate reminders and, worse, duplicate escalations. Use
  `[Singleton]` or a distributed lock.
- **Escalation transfers authority, it does not merely notify.** If the
  Department Head cannot actually action the escalated item, the SLA is
  advisory and the delay stays hidden.
- **The retirement sweep must use the working calendar,** not `AddHours`. This
  is the whole point of the 31 July decision, and it is easy to lose here.
- **Populate the holiday table before this runs in anger.** Eid al-Fitr, Eid
  al-Adha and Maulud are declared annually and cannot be computed. An empty
  holiday table will produce false overdue flags and false breach alerts, and
  the first people to notice will be the ones being wrongly chased.

**Done when:** tests with a fake clock prove a reminder fires, an SLA breach
escalates and records the non-actor, and an advance released Friday 16:00
out-of-station is not overdue until twelve calendar days later.

---

## Step 7 — Notifications and pipeline activation

**Goal:** email out, and the DevSecOps pipeline made real.

**Prompt (part 1):**
> Implement the outbox dispatcher sending email via Microsoft Graph as the
> platform service principal from a shared mailbox. Add the notification
> templates referenced in both workflow definitions. Every notification carries
> a deep link to the request and an action token.

**Then, manually — this is repository configuration, not code:**

1. `npx ratchet pin .github/workflows/*.yml` and commit. Until this runs the
   `supply-chain` job fails, correctly.
2. Create Entra app registrations for dev/uat/prd and configure **federated
   OIDC credentials scoped per environment**, so a dev workflow cannot obtain a
   production token.
3. Apply every branch protection and repository setting listed at the end of
   `docs/06-DevSecOps-Maturity.md`. **Include administrators** is the one that
   matters most and the one most often waived.
4. Create the `production` GitHub Environment with required reviewers.

**Watch for:**

- The shared mailbox approach lines up with the NRS e-Invoicing work already in
  motion, so request both at once from whoever administers Exchange.
- Graph `Mail.Send` as application permission is broad — scope it with an
  application access policy limited to the single shared mailbox, or the
  platform can email as anyone in the tenant.

**Done when:** a PR runs all eleven jobs green and `security-gate` passes; a
tagged release produces a signed image and `cosign verify` succeeds.

---

# Phase C — Usable by humans

## Step 8 — Frontend

**Goal:** the form-faithful UI. This is the adoption step.

**Prompt:**
> Create `src/Desicon.Workflow.Web` with React 18, TypeScript and Vite. Use
> MSAL.js for Entra ID auth. Build the screens in
> `docs/05-API-Frontend-and-Operations.md`. The expense and cash advance capture
> forms must reproduce the layout, field order, section headings and terminology
> of DEL-AC-FRM-002 and DEL-AC-FRM-003 exactly, including "FOR ACF DEPT USE
> ONLY" and the three signature blocks. Buttons read Verify, Approve and Endorse.

**Watch for:**

- **Fidelity to the paper form is the requirement, not a preference.** A clerk
  who has filled this form for eight years should recognise it in one second.
  Resist the urge to improve the layout.
- **Mobile-first for the approval path only.** A department head approving from
  a site is the normal case. Capture forms can assume desktop.
- **No browser storage in this platform's artifacts** is a Claude.ai constraint,
  not a real one — but do keep drafts server-side. Losing a half-finished
  eleven-line expense form to a dropped connection is exactly the friction that
  sends people back to paper.
- Only Tailwind core utilities; no arbitrary values if you want the build to
  stay simple.

**Done when:** Playwright covers raise → approve → post → pay → acknowledge on
Chrome, Edge and mobile Safari, and someone from Accounts looks at the form and
recognises it without prompting.

---

## Step 9 — PDF generation

**Goal:** the printed form, because it gets filed, photocopied and attached to
bank instructions.

**Prompt:**
> Add PDF rendering of a completed request reproducing the paper form layout,
> using the FormRevision stored on the request so a historical request prints in
> its own layout. Include the auto-generated Amount in Words, approval names
> with timestamps in the signature blocks, and the TREAS. and JV numbers.

**Watch for:**

- **Amount in Words must be correct for Naira and Kobo**, including the edge
  cases: zero kobo, exact hundreds, and values above a million. Auditors read
  this field.
- Render from the stored revision, not the current template. That is the ISO
  9001 requirement from `docs/01`.

**Done when:** ACF reviews a printed PDF against the paper original and signs
it off.

---

# Phase D — Completing scope and going live

## Step 10 — Procurement Requisition

**Blocked.** Send `DEL-AC-FRM-004` or whatever the procurement form is
numbered. The two forms you did send changed the design substantially — the
advance/expense linkage, the 24/72 rule and the acknowledgement-closes-the-loop
finding all came from reading them rather than from the brief. Designing this
module from the brief alone would repeat exactly the mistake the first two forms
corrected.

If no form exists, say so and it gets designed in house style for document
control.

## Step 11 — Hardening

- OWASP ZAP baseline scan against an ephemeral environment, wired into CI
  (this closes the DAST gap named in `docs/06`)
- External penetration test; close all high and critical findings
- k6 performance test: 200 concurrent users, 1,000 requests/day, p95 < 2s
- Restore an actual backup into a scratch database. A backup you have never
  restored is a hypothesis.
- Rehearse the rollback slot swap under time pressure
- Verify maker–checker and the audit chain in the production environment

## Step 12 — Pilot and rollout

Work `docs/05-API-Frontend-and-Operations.md`'s readiness checklist to zero,
then pilot with ICT for four weeks running parallel with paper. Then
department-by-department, four weeks each, with paper stopping for new
submissions per department as it goes live.

**Set the parallel-running end date at the start.** Indefinite parallel running
means paper wins, because paper is what people already know and there is always
one urgent case that justifies an exception.

---

## The governance item that is not a build step

The controls in this design make delay visible and attributable. Visibility only
converts into speed if management policy attaches a consequence to sitting on an
item — which is your own point from the correspondence, that technology can only
enforce the process so far.

Get that policy issued **before** go-live, while the project still has
attention. After go-live it competes with everything else, and a dashboard full
of red items that nobody is required to act on is worse than no dashboard: it
proves the problem is real and tolerated.

---

## Still open

From `docs/00-Phase1-README.md`, in order of how much rework a wrong guess
costs. Two are now answered.

| # | Question | Status |
|---|---|---|
| 1 | Procurement form | **Blocking step 10** |
| 2 | Retirement clock definition | **Answered** — working hours, from cash release |
| 3 | Consequence of an overdue advance | Open — designed as a configurable block on new advances |
| 4 | NGN 30,000 threshold still current at Rev 05 | Open — needs the live figure |
| 5 | TREAS. / JV number formats and owners | Open — needed for step 3 |
| 6 | Maker–checker exception path | Open — currently no override |
| 7 | Multi-entity scope | Open — far cheaper to build in now |
| 8 | Migrate the historical backlog, or start clean | Open — affects step 12 sequencing |

Question 4 is worth chasing this week; it is a one-line answer and it is baked
into every payment routed by the system.
