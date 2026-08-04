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
- GHAS licensing: private repo means CodeQL, Dependency Review and every
  upload-sarif step fail. ~$30/mo Code Security for this repo, or restructure
  scanners to fail-on-exit-code with artefacts. Decide before step 7.
- Repo ownership: currently a personal GitHub account, should move to a Desicon org
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

**Remaining in step 6**

`ReminderSweep`, `EscalationSweep`, `AuditChainVerification`. Escalation is
the substantive one: it must transfer authority, not merely notify, or the
SLA is advisory and the delay stays hidden.

## Status after step 5b

Dev is deployed and verified end to end: `/health/ready` returns Healthy with
`connection: ok`, `migrations: up to date`, `alwaysEncrypted: ok`. Image is
built in ACR and pulled by managed identity; schema is applied by CI under a
DDL principal; the API reads an Always Encrypted column through Key Vault.

UAT topology written, not applied — needs the self-hosted runner first. Note
that uat/prd use private endpoints, so the dev-only NSG rules, VNet rules and
Key Vault ACL subnet entries added here are all correctly gated off there.

Step 6 part 1 done: RetirementSweep deployed and registered with the host,
proven by the deploy job rather than assumed. Next: ReminderSweep,
EscalationSweep, AuditChainVerification.
