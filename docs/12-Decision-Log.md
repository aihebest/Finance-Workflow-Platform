# 12 — Decision Log (Steps 3–5)

Condensed record of decisions made in the architecture conversation that are
not otherwise captured in the repo. Full reasoning lived in docs 08-11, which
were never committed.

## Closed decisions

- **Procurement module: removed.** Desicon uses Dynamics 365 BC for PR -> PO ->
  vendor. BC has no expense/advance approval workflow, so no overlap.
- **Retirement clock: WORKING hours, starting at CASH RELEASE.** On a 9-hour day
  that is 2.67 working days (in-station) and 8 working days (out-of-station).
  Confirm the 72h figure with Finance — a Friday release is not overdue for ~12
  calendar days.
- **Numbering:** EXP-{yyyy}-{000000}, ADV-{yyyy}-{000000}, TR-{yyyy}-{00000},
  JV-{yyyy}-{00000}. Annual reset, SQL SEQUENCE, gaps accepted.
- **One expense claim per advance** (assumed pending Finance confirmation).
  Partial retirement across multiple claims produces spurious REFUND_DUE, because
  the paper form's netting arithmetic cannot express it.
- **Bank details** come from the Employee record. Never auto-vivify a blank
  Beneficiary. Changes write a SecurityEvent; maker-checker enforced.
- **Payment method is derived**, not chosen — NGN 30,000 threshold from the
  policy table with effective dating.
- **Environments:** dev has no private endpoints (IP-restricted public access
  instead) so it deploys from a workstation. UAT is the faithful prod mirror
  with full private endpoints and needs a self-hosted runner in the app subnet.

## Open decisions

- JV number: mint locally and push to BC as External Document No. (recommended),
  or receive from BC's No. Series
- Does BC hold Projects, Cost Centres, Employees — if so, sync read-only from BC
- Is NGN 30,000 still current at Rev 05
- Consequence of an overdue advance (payroll deduction / block / escalation)
- Repo ownership: currently a personal GitHub account, should move to a Desicon
  org. No longer blocks any control (see step 7 part 2) but the IP belongs to
  Desicon, and moving changes the OIDC federated-credential subject.
- Multi-entity scope
- Migrate the historical backlog or start clean at cut-over

## Recurring failure patterns found

1. **Guard-field seam** — five instances of a workflow guard referencing a field
   nothing exposed or wrote. All failed closed. Fixed by the guard-field schema
   validator plus CI drift check.
2. **Captures/records seam** — a transition declares captures/records in JSON;
   the endpoint must supply them. ACKNOWLEDGE silently recorded neither, and
   that one failed OPEN. Needs the same validator treatment.
3. **Security checks that check nothing** — conftest evaluating 0 rules,
   covered_by_diagnostics matching nothing, has_private_endpoint never finding a
   match. Every policy rule needs verification in BOTH directions.
4. **Comments asserting what nothing enforces.** The pattern behind most of
   step 5b. `create-app-user.sql` stated migrations run under a separate
   higher-privileged identity — no such principal existed and nothing applied
   migrations. `modules/app-service` said "registry pull uses the app's managed
   identity" while `container_registry_use_managed_identity` was false.
   `Provision-AlwaysEncryptedKeys.ps1` documented a `-ConnectionString`
   parameter the cmdlet does not have. The migration attributed its own failure
   to a missing `Column Encryption Setting`. Each was a true statement of
   intent and a false statement of fact, and none had a test.
5. **Two faults, one symptom.** Repeatedly, fixing a real defect changed
   nothing observable because a second independent defect produced the same
   result. Managed-identity pull was broken *and* Easy Auth returned 401 to
   the probe. The SQL VNet rule was missing *and* the NSG denied the traffic
   *and* the Redirect port range was closed. Each fix looked ineffective. The
   lesson is to instrument first: `CanConnectAsync` returning a bare `false`
   cost two rounds of investigation into networking that was already correct.

## Step 5b — making dev actually run (2026-08-03)

Step 5 ended with infrastructure applied and no application on it. Closing
that gap surfaced eleven defects, none of which any test would have caught,
because nothing had ever executed the deployed path.

**Structural changes**

- **Container registry moved from GHCR to ACR** (`modules/acr`). A private
  GHCR package requires App Service to store a PAT as a registry password —
  the only stored credential in a system that otherwise authenticates to SQL,
  Storage and Key Vault by managed identity. ACR + `AcrPull` keeps that
  property. ~$5/month Basic.
- **`deploy-app.yml`**: build in ACR → migrate → deploy → smoke test. OIDC
  federation, no client secret. Migrations run as
  `github-desicon-finance-workflow` (`db_ddladmin`), never as the app
  identity, which finally makes the separation `create-app-user.sql` claimed
  real rather than aspirational.
- **`/health/live` and `/health/ready`.** Readiness opens a connection, checks
  for pending migrations, and reads the Always Encrypted column — the last of
  those exercises the Key Vault role assignment and the network path in one
  go. Every defect below is one this probe reports directly.
- **`global.json` pins the SDK.** CI had been red for reasons no local build
  reproduced: `setup-dotnet` floated `8.0.x` while `AnalysisLevel` is
  `latest-recommended` and warnings are errors, so a newer runner SDK
  extended IDE0040 to interface members. The SDK was the last floating input
  in a repo that otherwise pins action SHAs and checks generated files for
  drift.

**Defects found and fixed**

| # | Defect | Presented as |
|---|--------|--------------|
| 1 | No Key Vault key for the Always Encrypted CMK | migration `THROW` (guard worked) |
| 2 | `deployer_ip_addresses` stale; apply rewrites the ACL it is reaching through | 403 mid-apply, looks like RBAC |
| 3 | `Provision-AlwaysEncryptedKeys.ps1` used a non-existent parameter | never run successfully |
| 4 | CEK wrap needs a separate Key Vault audience token | AKV10000 401 |
| 5 | In-place `ALTER COLUMN ... ENCRYPTED WITH` needs a secure enclave | Msg 33543 |
| 6 | `ConnectionStrings__Default` vs `GetConnectionString("WorkflowDb")` | silent fallback to `Server=(local)` |
| 7 | Connection string missing `Column Encryption Setting=Enabled` | would decrypt nothing |
| 8 | `Key Vault Secrets User` does not cover key unwrap | 403 at first encrypted read |
| 9 | `container_registry_use_managed_identity` false | pull 401, reads as bad credentials |
| 10 | Easy Auth 401s every probe, including `health_check_path` | healthy instances evicted |
| 11 | Plain chiselled image has no ICU; SqlClient asks for en-US | "invalid culture identifier" |
| 12 | App subnet had no path to SQL or Key Vault: no VNet rule, NSG denied the service tags, and the SQL Redirect range 11000–11999 was closed | 85s timeout, error 40 |

**Controls that existed but never ran**

Two were found switched off rather than absent, which is worse than missing —
the repo reads as if both were enforced.

- `scripts/check-action-pinning.mjs` was never referenced by any workflow, so
  both workflows had drifted to mutable tag pins while holding an OIDC token
  for the subscription. Now a CI step, and every action is SHA-pinned.
- `EnclaveFreeMigrationRunner` made the integration suite green against a
  schema the shipped migration could not produce. Deleted;
  `WorkflowApiFixture` now calls `Database.MigrateAsync()` so the tests
  exercise exactly what deploys.
- `AlwaysEncryptedTestKeyProvisioner` used `MSSQL_CERTIFICATE_STORE`, whose
  driver implementation is the Windows certificate store and throws
  `PlatformNotSupportedException` on Linux. The integration suite could
  therefore never have run on `ubuntu-latest` — masked for as long as CI
  failed earlier, at the build step. Replaced with
  `TestColumnEncryptionKeyStoreProvider`, an in-memory RSA key with no
  platform dependency. `Program.cs` skips its own Key Vault provider
  registration under the `IntegrationTests` environment, since
  `RegisterColumnEncryptionKeyStoreProviders` is process-wide and may only
  be called once.

**Positions now committed**

- **Enclave-free encryption.** The Always Encrypted migration drops and
  re-adds the column rather than encrypting in place, guarded by an emptiness
  assertion. Deterministic equality still works; range queries and `LIKE` on
  that column would need real enclave infrastructure and a revisit.
- **Sequence provisioning is owned by the pipeline**, covering the current
  year plus two. `SqlSequenceRequestNumberGenerator` creates sequences lazily
  at runtime, which cannot succeed under `db_datawriter` — and
  `db_datawriter` does not cover sequences at all, so `NEXT VALUE FOR` needs
  an explicit `UPDATE` grant. The window lapses in 2029.
- **Health detail is environment-gated.** The probe paths are excluded from
  Easy Auth so the platform and the smoke test can reach them, which makes
  them reachable through Front Door; per-check diagnostics are suppressed
  outside development.

**Still open from this work**

- `FRONT_DOOR_HOSTNAME` is hardcoded in `deploy-app.yml`; it should come from
  a Terraform output.
- The Entra SQL admin is a person, not a group — a single point of failure.
- Sequence rollover past 2028 has no owner. A December timer function is the
  natural fit and lands squarely in step 6.

## Step 6 (part 1) — RetirementSweep

`src/Desicon.Workflow.Functions`, isolated worker, deployed and verified.

**Decisions**

- **Singleton by SQL application lock**, not the WebJobs `[Singleton]`
  attribute. `sp_getapplock` puts the lock in the same database as the work
  it guards and uses the same mechanism as the action pipeline's UPDLOCK,
  rather than a blob lease in a storage account with its own identity,
  network and lifecycle. Session-scoped and non-blocking: an instance that
  loses the race skips the tick rather than queueing behind the winner and
  sweeping again immediately.
- **The sweep compares against the stored `RetirementDueDate`**, which
  `ReleaseCash` computed from the working calendar at release, rather than
  recomputing the window each run. The build plan says the sweep "must use
  the working calendar, not AddHours"; comparing against a calendar-derived
  stored instant satisfies that, and recomputing would not — it would
  silently re-date every outstanding advance whenever the holiday table
  changed, including ones already flagged overdue. Nigerian holidays are
  declared days ahead, so that would happen in practice.
- **One audit event per crossing into Overdue**, keyed on the due date
  rather than the sweep date. A daily sweep that appended per run would put
  one entry per day per overdue advance into the hash chain.
- **Run-from-package over blob, not Kudu zip deploy.**
  `az functionapp deployment source config-zip` authenticates to the SCM
  site with basic auth, which `webdeploy_publish_basic_authentication_enabled
  = false` disables; it receives a 401 with an empty body and fails parsing
  it as JSON. CI uploads the package with its own OIDC identity and the
  Function App reads it with its managed identity, so the credential-free
  property holds through deployment as well as runtime.

**Measured, not assumed**

An out-of-station advance released Friday 16:00 is overdue **exactly twelve
calendar days later** — asserted in `RetirementSweepTests`, not stated in
prose. A public holiday inside the window makes it thirteen. That second
figure is correct behaviour and worth knowing: an unmaintained holiday table
does not only cause false overdue flags, it also quietly grants extra time
nobody agreed to.

`RetirementStatus.NotDue` means "no due date set", not "not yet late" — an
advance inside its window is `Due`. Easy to misread, and now asserted.

**Pattern 6 — deny-by-default with an allow-list nobody added the app to**

Four resources today, identical shape, each reported as something else:

| Resource | Missing | Reported as |
|---|---|---|
| Key Vault | app subnet in `network_acls` | 403, looks like RBAC |
| SQL | VNet rule, NSG rule, Redirect ports 11000-11999 | 85s timeout, looks like SQL down |
| Functions storage | subnet rule, service endpoint, NSG rule | `InternalServerError from host runtime` |
| ACR | `container_registry_use_managed_identity` | 401 pull, looks like bad credentials |

All four exist only because dev has no private endpoints. uat and prd use
private endpoints and a self-hosted runner inside the VNet, so none of these
exceptions apply there — which is exactly why someone will copy this
workflow to prod and wonder why it opens firewalls. Every one is gated on
`use_private_endpoints`.

A service endpoint is also required for a subnet to be *nameable* in another
resource's rules. A rule referencing a subnet without the matching endpoint
is accepted by Azure and silently never matches.

## Step 6 (part 2) — Reminder, escalation, audit verification

**Escalation transfers authority, and that is now asserted.** `sla.escalateTo`
names a *state*, so moving the request there hands authority to that state's
actor — authorisation derives from current state, not from a field naming an
approver. The test that matters is not that `CurrentState` changed but that
the Department Head can subsequently `VERIFY`; asserting the column alone
would pass for a notification-only escalation, which is exactly the failure
the build plan warns about.

**Escalation deliberately bypasses `RequestActionService`.** That service
resolves a declared transition, evaluates its guards and checks the caller is
an authorised actor. Escalation is none of those: it is not in the
transitions list, no human performs it, and it must succeed *because* the
authorised actor did not act. The cost is that `EscalationSweep` maintains
by hand the invariants that service normally maintains — current actor, SLA,
`StateEnteredAt`, `ReminderCount`, audit chain, outbox. A future field
joining that set must be added in two places. Named here because it is
precisely the seam this log keeps recording.

**Reminders count calendar hours, not working hours** — unlike the SLA
deadline itself. A working-hours cadence goes quiet over a weekend, which is
when a Friday submission is most likely to be forgotten. Cadence is derived
from `StateEnteredAt + ReminderCount x reminderEveryHours`, so a missed tick
(deploy, outage, lock contention) catches up rather than pushing every later
reminder back.

**`AddWorkflowPlatform` extracted.** The API and Functions host had each
registered the database context, calendar, clock, definitions and actor
resolver independently — including two copies of the holiday seeding, where
a silent divergence moves SLA deadlines and retirement due dates without any
code looking wrong.

### The audit hash did not cover what it needed to

`AuditChainVerification` was written with a tamper test as well as a
happy-path test, on the reasoning that a checker which always returns "fine"
passes the happy path — and this log already records three controls that did
exactly that. The tamper test failed, and the checker was right.

`ComputeHash` covered `RequestId`, `EventType`, `FromState`, `ToState`,
`ActorId`, `ActorRole`, `OccurredAtUtc` and `PayloadJson`. It did **not**
cover `Reason`, `OnBehalfOfUserId`, `ClientIpAddress` or `CorrelationId`.

So an approver's stated reason for a rejection could be edited directly in
the database: no behaviour changes, no hash breaks, nothing detects it. And
`OnBehalfOfUserId` records both delegation and — since this step — the actor
who failed to act on an escalated request. Claims about people, outside the
tamper-evidence.

All four are now hashed. `IdempotencyKey` remains excluded: it is a
de-duplication mechanism rather than a record of what happened, and it is
already protected by a unique index.

**This invalidates chains sealed under the old field set.** Free now, before
the platform holds real approvals. Once it does, the answer is a version
marker on each event and a verifier that checks it against the rules in
force when it was sealed — a materially larger piece of work, which is why
it was worth doing today.

`AuditChainVerification` logs at Critical and then throws, so a failed
invocation raises an alert. A nightly job reporting success quietly is how
this class of problem stays invisible. It is a full scan, which is correct
while the table is small and will not stay affordable; incremental
verification is deferred deliberately, because a checker that skips the rows
an attacker edited is worse than no checker.

## Step 7 (part 1) — Notifications

The outbox had three producers and no consumer. Rows accumulated as Pending,
which looks healthy from the writing side and means nobody was told anything.

**Decisions**

- **Deep link, no action token.** The architecture promises "a deep link and
  an action token, so an approver can act in two clicks". Only the link ships.
  A token that authorises a state change from a mailbox is a bearer
  credential sitting in an inbox and a mail relay — forwardable, loggable,
  and outside maker-checker. On a finance system, possession of a forwarded
  email would be possession of approval authority. That deserves its own
  design, not an afternoon alongside a dispatcher.
- **`INotificationSender` takes a rendered message, not an `OutboxMessage`.**
  The scaffolded interface would have made every transport responsible for
  recipient resolution and templating as well as sending. Nothing implemented
  it, so narrowing it was free.
- **Two senders, selected by explicit configuration.** Graph for real, and a
  logging sender until Exchange provisions the shared mailbox. `UseGraph` is
  stated per environment rather than inferred from whether a mailbox is set:
  inference would let a deployment that lost its configuration silently
  downgrade to sending nothing while reporting success — this repo's
  signature failure, and the one a notification system can least afford.
- **Raw HTTP to Graph, not the SDK.** sendMail is one POST. The SDK's
  transitive graph is a large scanning surface for a Function App that needs
  one operation.
- **Recipient resolution reuses `IActorResolver`.** A person authorised to
  act and a person told to act must not drift apart, which they would
  immediately with two copies of the rules. It also means notifications
  follow delegations the same way authority does, for free.

**The role-recipient gap, surfaced rather than papered over**

`FinanceManager` appears in the workflow definitions as a notification
recipient and there is no role-membership store to resolve it against.
`EmployeeActorResolver` returns an empty set for role-only specs and the
engine reads that as "everyone in the role" — correct for authorisation,
where it widens access, and exactly wrong for notification, where it means
"send to nobody".

So unresolved specifiers come back named, and the dispatcher parks those
messages as Failed with the specifier in `LastError` after one attempt, not
five. `LastError` reading "could not resolve FinanceManager" points at the
missing role store; "no recipients" would have sent someone to inspect the
mail transport instead.

Either a role-membership store or a per-module configured address is needed
before the definitions can rely on role recipients. Until then those
notifications fail loudly, which is the correct behaviour for a finance
approval nobody was told about.

**Still outstanding in step 7** — all repository and tenant configuration,
none of it code:

- Shared mailbox, Graph `Mail.Send` consent, and an Exchange **application
  access policy scoping it to that one mailbox**. Without the policy the
  platform can email as anyone in the tenant. Nothing in this repository can
  enforce or verify that, which is why it is written in three places.
- Entra app registrations per environment with federated OIDC scoped so a dev
  workflow cannot obtain a production token. Currently one registration
  serves dev.
- Branch protection per `docs/06-DevSecOps-Maturity.md`, including
  **Include administrators**.
- A `production` GitHub Environment with required reviewers.

## Step 7 (part 2) — Repository configuration

The maturity document's checklist could not be applied as written, for three
reasons found only by trying:

- It requires a **`Security Gate`** status check. The CI job is called
  `build-test-validate`. Requiring a check that does not exist blocks every
  merge permanently.
- It requires **two approvals, one from CODEOWNERS**, on a repository with one
  contributor. Any non-zero count freezes `main` for the only person who can
  work on it.
- It requires **signed commits**, and commits are unsigned. Enabling it would
  reject every push until a signing key is configured.

A checklist applied without reading it against the repository produces
controls nobody can satisfy, which get waived, which is worse than not having
them.

**Repository made public.** Branch protection, environment protection rules,
secret scanning and push protection are all unavailable on a private
repository on a free personal account — four controls blocked by the plan,
not by the GHAS licence. Public makes every one of them free, and closes the
GHAS licensing question outright rather than answering it: CodeQL, SARIF
upload, secret scanning and push protection cost nothing here.

What that discloses is resource naming — the SQL FQDN, registry, storage
accounts and Front Door hostname, all from `deploy-app.yml`, all dev. That is
reconnaissance value, not access. Verified before publishing with gitleaks
over the full history (29 commits, no leaks) and confirmed `*.tfvars` is
gitignored, so no subscription id, object id or allow-listed IP is published.

**Branch protection applied** — see `scripts/README-branch-protection.md` for
the configuration and reasoning, kept in the repository so what is enforced is
reviewable and reproducible on the org repo later.

On: required `build-test-validate` (strict), linear history, no force pushes
or deletions, required conversation resolution, and **`enforce_admins: true`**
— the item the build plan names as most often waived, and the only one that
matters while the owner is the only contributor.

Off, with the trigger recorded: approvals stay at `0` until a second engineer
can approve (raise to 2 and enable `require_code_owner_reviews`;
`.github/CODEOWNERS` already exists so it is one setting). Signatures stay off
until a signing key is configured and one commit verifies as `G`.

**Consequence: direct pushes to `main` are now blocked, including the owner's.**
Work moves to branch, PR, wait for CI, merge. Until today the pipeline could
be bypassed by the person most able to bypass it.

**`production` environment created** with a ten-minute wait timer and required
reviewer. The reviewer is currently the same person who deploys, which is
theatre; the wait timer is not, and nothing deploys to it yet, which is the
right time to have set it up. `prevent_self_review` should be enabled when a
second reviewer exists.

**Dependabot** tracks `github-actions` and `nuget`. The action ecosystem
matters most here: every action is SHA-pinned and enforced in CI, and an
immutable pin never picks up a security fix on its own — pinning without a
bump mechanism just trades one risk for another.

## Step 8 (part 1) — Frontend scaffold, auth and the approval path

`src/Desicon.Workflow.Web`: React 18, TypeScript, Vite, Tailwind core
utilities, deployed behind the same Front Door as the API.

**Decisions**

- **One origin, not two.** Front Door routes `/api/*` and `/health/*` to the
  API and everything else to the SPA. Same-origin means no preflight, no CORS
  allow-list to get wrong, and the SPA's CSP keeps `connect-src 'self'`.
- **The SPA is served anonymously**, so `modules/web-app` is a separate module
  rather than a reuse of `modules/app-service`. Easy Auth in front of static
  files would return 401 to the very request that loads MSAL.
- **Tokens in `sessionStorage`, not `localStorage`.** A shared site machine is
  the normal case; a token that outlives the browser session means the next
  person to open it is still signed in as the last.
- **No action tokens** — see step 7. Email carries a deep link only.
- **Vite build args, not runtime config.** Vite inlines `import.meta.env`, so
  a static bundle has no runtime configuration. The Entra client and tenant
  ids are build args and are not secrets: they are visible to anyone who opens
  the sign-in page, and the boundary is the redirect-URI allow-list plus token
  validation.

**The 401 that took an hour: two auth layers, one configured**

Sign-in succeeded, the SPA rendered, and every API call returned a bare 401.
Three separate faults, each individually plausible:

1. **Token version.** `modules/app-service` pointed Easy Auth at the v2 issuer
   (`tenant_auth_endpoint .../v2.0`) while the registration defaulted to v1
   tokens. A v1 token carries `aud = api://<client-id>` — which matched — and
   `iss = sts.windows.net/<tenant>/` — which did not. The audience being
   right is what made this hard to see.
2. **The API validates tokens too**, independently of Easy Auth, reading
   `AzureAd:TenantId` and `AzureAd:Audience`. Terraform set neither, so the
   deployed API used `appsettings.json`'s `REPLACE_WITH_TENANT_ID` and
   rejected everything. Easy Auth accepted the token; ASP.NET refused it;
   the response named neither layer.
3. **`?? throw` checks presence, not validity.** A placeholder is not null,
   so every guard passed. `Program.cs` now rejects `REPLACE_WITH` values as
   well, on the reasoning that absent and present-but-wrong are the same
   defect to a caller.

`WorkflowApiFactory` had a comment stating that the tests relied on
"placeholder values that satisfy Program.cs's `?? throw` checks" — accurate
documentation of the weakness, recorded as a convenience. Fixing the guard
broke 30 of 33 integration tests, which is the correct outcome: the tests now
supply real-shaped values rather than being exempted, so the guard stays on
the path they exercise.

**Pattern 7 — a control that only fails at the boundary it does not cover**

`/health/ready` is excluded from Easy Auth so the platform probe and the
deploy smoke test can reach it. That exclusion is necessary, and it meant the
readiness check passed throughout while every authenticated call failed. A
probe proving the app can reach its database says nothing about whether a
user can reach the app. Worth an authenticated smoke test in the deploy job.

**Form layout extracted** into `docs/13-Form-Layout-Reference.md` from the
controlled spreadsheets. Two details are absent from `docs/01`'s field
mapping and change the capture screens materially: the cash advance form has
**six** line rows rather than eleven, and carries a **separate minor-unit
column** (`k/¢/p`) — amounts are entered as naira and kobo in two boxes, not
as a decimal.

Both forms also print policy in their body text: the NGN 30,000 transfer
threshold and the 24/72-hour retirement note. Those values live in the system
with effective dating, and the retirement window is counted in *working*
hours. Rendering the printed wording as a literal would eventually contradict
the engine — the disagreement already open with Finance, made visible to a
user.

**Still open in step 8**

- Capture forms for DEL-AC-FRM-002 and 003, to be reviewed by someone from
  Accounts against the paper rather than against `docs/13`.
- Playwright across Chrome, Edge and mobile Safari — the step's stated
  acceptance test, none of which exists yet.
- My Requests, My Advances, dashboards and admin screens.
- Employee data has no source. `scripts/seed-dev-employee.sql` inserts one
  person for dev; a directory or HR feed is a step 10 concern.

## Status after step 5b

Dev is deployed and verified end to end: `/health/ready` returns Healthy with
`connection: ok`, `migrations: up to date`, `alwaysEncrypted: ok`. Image is
built in ACR and pulled by managed identity; schema is applied by CI under a
DDL principal; the API reads an Always Encrypted column through Key Vault.

UAT topology written, not applied — needs the self-hosted runner first. Note
that uat/prd use private endpoints, so the dev-only NSG rules, VNet rules and
Key Vault ACL subnet entries added here are all correctly gated off there.

Step 6 complete. `RetirementSweep`, `ReminderSweep`, `EscalationSweep` and
`AuditChainVerification` are implemented, tested against a real SQL Server,
and deployed; the deploy job verifies the host registered them rather than
assuming it. 107 unit and 30 integration tests pass.

Both of step 6's acceptance criteria are now numbers the build checks: an
out-of-station advance released Friday 16:00 is overdue exactly twelve
calendar days later, and an SLA breach transfers authority to the escalation
target — proven by that target successfully actioning the request, not by a
column changing.

Step 7 part 1 done: the outbox has a consumer. `OutboxDispatchSweep` drains
it every five minutes, resolving recipients through the same actor resolver
that decides authority, rendering eleven templates with a deep link, and
sending via Graph or logging depending on explicit configuration. 107 unit
and 33 integration tests pass.

Remaining in step 7 is entirely repository and tenant configuration — shared
mailbox and the Exchange application access policy, per-environment Entra
registrations, branch protection, and the production environment with
required reviewers. None of it is code, and none of it can be verified from
inside this repository.
