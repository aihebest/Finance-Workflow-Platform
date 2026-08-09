# Go-live checklist

Things that are deliberately wrong in dev and must be put right before real
money moves through this platform. Each one is here because it is invisible —
nothing fails, no test goes red, and the system keeps reporting success.

Ordered by consequence, not by effort.

---

## 1. Revoke the temporary role assignments

**Why this is first.** Dev role assignments were made to accounts that were
simply available at the time, because the real approvers were not. That is fine
for a walkthrough. Carried into production it means a shared administrative
account can authorise payments — which is exactly the control the Director of
Finance gate exists to be.

The danger is that go-live *adds* the real people and nobody *removes* the test
ones. Both hold the role, everything works, and the weakness is silent.

**Assignments made for testing, to be revoked:**

| Account | Role | Reason it was assigned |
|---|---|---|
| `wazuhalerts@desicongroup.com` | `FinanceManager` | Accounts Manager unavailable during testing |
| `ictadmin@desicongroup.com` | `DirectorOfFinance` | DMD unavailable during testing |
| `olanrewaju.atanda@desicongroup.com` | `FinanceOfficer` | Confirmed 9 Aug 2026 as **Cost Control**. Reassign to `CostControlOfficer` and revoke `FinanceOfficer` once version-2 requests drain |
| `best.aihebholoria@desicongroup.com` | `FinanceManager` | Assigned during the 9 August walkthrough because the Accounts Manager was unavailable |
| `ictadmin@desicongroup.com` | `DirectorOfFinance` | Assigned during the 9 August walkthrough because the DMD was unavailable |

Note that `ictadmin` is also the seeded Department Head, so during that
walkthrough one account both approved at department level and authorised the
payment. The workflow permitted it — `DMD_APPROVAL`'s guard only forbids the
requester — so it proved the gate functions, not that it separates anyone.

**To list who currently holds what:**

```powershell
$appId = "8deb5019-590d-4ef3-bb61-f5d450d341b5"   # dev; use the target environment's
$sp = (az ad sp list --filter "appId eq '$appId'" --query "[0].id" -o tsv)

az rest --method GET `
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignedTo" `
  --query "value[].{principal:principalDisplayName, roleId:appRoleId, assignmentId:id}" -o table
```

**To revoke one:**

```powershell
az rest --method DELETE `
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$sp/appRoleAssignedTo/<assignmentId>"
```

Revoking does not take effect until the holder's current access token expires
or they sign out. A revoked person can keep acting for the remainder of their
token lifetime.

**Verify afterwards** that each role has exactly the intended holders, and that
no one person holds two of `CostControlOfficer`, `TreasuryOfficer`,
`FinanceManager` and `DirectorOfFinance`. The maker-checker guards compare
`Employee.Id`, so one human with two accounts would satisfy them while
providing no separation at all.

### 1b. Retire `FinanceOfficer`

Workflow version 3 replaced `FinanceOfficer` with `CostControlOfficer` and
`TreasuryOfficer`. The old role is still defined and still assignable, and it
grants everything both new roles grant — at `COST_CONTROL_VERIFY`,
`AWAITING_POSTING`, `AWAITING_PAYMENT` and `CASH_RELEASE` — but only on
requests pinned to version 2.

It exists solely so those requests can be finished. Leaving it assigned after
they drain restores exactly the collapse version 3 removed, and it will not
announce itself: everything keeps working.

**Roles created in dev 9 August 2026.** `CostControlOfficer` and
`TreasuryOfficer` now exist in the directory. Creating them before version 3
deploys is the right order: version 2 does not reference them, but a version-3
request raised before they exist reaches `COST_CONTROL_VERIFY` and stops with
an empty action list and no error.

- [ ] Assign `CostControlOfficer` — Olanrewaju Atanda, confirmed 9 Aug 2026
- [ ] Assign `TreasuryOfficer` — the Accounts Officer who posts in Business
      Central

`costcontrol@desicongroup.com` and `treasury@desicongroup.com` are **shared
mailboxes**, not sign-in accounts. They are where notifications go; the app
roles belong to the individual people who work those desks. Assigning a role to
a shared mailbox that nobody signs into would make the directory look correct
and leave the queue unworkable.

That distinction also matters for section 2: the Exchange application access
policy scopes `Mail.Send` to the *sender* mailbox, which is a third address
again — not either of these two.
- [ ] Do **not** assign `FinanceOfficer` to anyone new
- [ ] When the version-2 query in section 3 returns zero open rows: revoke every
      `FinanceOfficer` assignment, then disable and remove the role definition
      (a role cannot be deleted while `isEnabled` is true — two PATCHes)
- [ ] Drop the `FinanceOfficer` key from `notifications_role_mailboxes` at the
      same time, and delete `modules/*.v2.workflow.json`

---

## 2. Turn notifications on

`notifications_use_graph = false` in dev, so `LoggingNotificationSender` writes
each message to Application Insights instead of sending it. Nothing has ever
been emailed to a real person from this platform.

Before go-live:

- [ ] Provision the shared sender mailbox and set `notifications_sender_mailbox`
- [ ] Grant `Mail.Send` application permission to the Function App's managed
      identity, with admin consent
- [ ] **Scope it with an Exchange application access policy** to that one
      mailbox. Without this, `Mail.Send` as an application permission allows
      sending as *any* mailbox in the tenant. Nothing in this repository can
      enforce or detect that, which is why it is called out separately rather
      than left as part of the step above.
- [ ] Set `notifications_use_graph = true`
- [ ] Confirm `notifications_role_mailboxes` names the real people

Verify by raising one request and checking it arrives, before anyone relies on
it. A notification system that silently sends nothing is worse than none,
because people stop checking the queue.

---

## 3. Never delete a definition file while requests are open under it

**Done 9 August 2026** — requests now carry `Request.DefinitionVersion`,
stamped at creation, and every path that acts on an existing request resolves
by it. A new version applies to requests raised after it and to nothing already
moving.

That creates a standing operational rule. A definition file must stay published
for as long as anything is still open under it. Removing one does not fail
quietly — `IWorkflowDefinitionProvider.GetAsync` throws and names the versions
that *are* published — but a request whose definition cannot be loaded cannot
be actioned by anybody until the file comes back.

Before retiring any version:

```sql
SELECT DefinitionVersion, ModuleKey, COUNT(*) AS StillOpen
FROM Requests
WHERE ClosedAt IS NULL
GROUP BY DefinitionVersion, ModuleKey
ORDER BY ModuleKey, DefinitionVersion;
```

Zero rows for that version is the only safe answer.

`DefinitionVersion = 0` means nobody stamped it. Every creation path does, so a
0 indicates a row written outside the application — a data fix, an import — and
it will refuse to transition until corrected. That is intended: the alternative
is guessing which process it belongs to.

### What pinning does not fix

It holds the process still. It does not hold the org chart still.

`EXP-2026-000004` was stranded not by a definition change but by an employee's
line manager changing: `CurrentActorId` is stamped at each transition while the
actor resolver runs live, so the two disagreed and the request became invisible
to the person who could act and inert for the person who could see it. That
would still happen today. See `docs/12-Decision-Log.md`.

Version 1 was deliberately not preserved. It was not a Desicon process — it had
a GL journal this platform does not own and no Director of Finance — so nothing
should ever run down it again. Version 2 is the floor.

### Version 3, and the first real use of this mechanism

**Published 9 August 2026.** Both modules moved to version 3 when
`FinanceOfficer` was split into `CostControlOfficer` and `TreasuryOfficer`.
Version 2 is retained in `modules/expense-reimbursement.v2.workflow.json` and
`modules/cash-advance.v2.workflow.json`.

This is what pinning was built for, and until now it had never been exercised
against an actual difference: version 2 was the only version anything ran
under, so "resolve by the stamped version" and "resolve by the latest version"
had identical behaviour and no test could tell them apart.

They no longer do. A version-2 request names a role version 3 does not define.
Without pinning, publishing version 3 would have made every open request
unactionable by anybody — no error, no failed test, just an empty action list
on a request that stays open. `RoleSeparationTests` now asserts both halves:
that a version-2 request still resolves `FinanceOfficer`, and that version 3's
roles do **not** work on it.

Both versions' roles must appear in `notifications_role_mailboxes`. Dropping
`FinanceOfficer` the day version 3 shipped would have silenced every
notification on every request still open under it.

---

## 3b. Model/migration drift

**Done 9 August 2026** — CI runs
`dotnet ef migrations has-pending-model-changes` between Build and Test.

On 9 August the EF model and the migrations disagreed for three commits and 54
green integration tests did not notice. The migration added
`DefinitionVersion` with a database default of 2; the model, corrected shortly
after, declared none. Nothing fired, because by then every creation path
stamped a version explicitly, so the stale default was never reached.

It would have surfaced later as an unexplained `AlterColumn` inside an
unrelated migration, and whoever hit it would have had no way to know why.

`dotnet ef migrations script` does not compare the snapshot to the model, so
that step is the only thing in the pipeline that can see this. Non-zero exit
means somebody changed an entity or its configuration without adding a
migration.

Nothing to do at go-live. Recorded here because the class of failure it catches
— a disagreement that passing tests demonstrably do not notice — is worth
remembering when adding future checks.

---

## 3c. Read the Terraform plan before applying

A plan on 9 August, intended only to add two app settings, contained four
changes. Two were the intended ones. The other two were drift, and one of them
was a security control being switched off:

- `express_vulnerability_assessment_enabled` on the SQL server, `true` in Azure
  and unset in the configuration. The provider defaults it to `false`, so the
  apply would have disabled SQL vulnerability assessment as a side effect. Now
  set explicitly to `true`.
- Key Vault network ACL IP rules, which `scripts/dev-db-connect.ps1` adds and
  removes outside Terraform. Its own docstring warns about this. Applying drops
  whichever developer IP is currently allowed; re-run the script afterwards.

The lesson is not "check that one setting". It is that an unset attribute
adopts the provider's default, and a provider default is not the same as the
value the resource has today. Anything Azure has enabled and the configuration
does not mention is one apply away from being turned off, quietly, inside a
plan whose headline is something else entirely.

**Before any apply, read every line of the plan and account for each change.**
Two intended edits producing four planned changes is the signal.

---

## 4. Confirm Business Central enforces maker-checker

Version 1 of the workflow enforced that whoever entered a GL journal could not
authorise it — mirroring the two signature boxes on DEL-AC-FRM-002 and
DEL-AC-FRM-003.

Version 2 moved posting to Business Central, and that control went with it.
This platform no longer sees journals and cannot enforce it.

Somebody thought the separation mattered enough to design into the paper form.
Confirm BC provides it before assuming it survived the move. If BC does not,
that is a gap created by this project and it needs an answer.

**Version 3 narrows this but does not close it.** Splitting `FinanceOfficer`
means the person who verifies the costing is no longer the person who posts, so
one separation is back — enforced here, on the approval trail. What this
platform still cannot see is what happens *inside* BC once Treasury opens it:
whether entering and authorising a journal there are two acts by two people.
That question is unchanged, and it is the one to put to whoever administers BC.

---

## 5. Employee data has no source

`Employees` rows are hand-inserted by `scripts/seed-dev-org-chart.sql`. There is
no HR feed and no directory sync.

Every authority decision that is not a role claim — line manager, department
head — reads from this table. Hand-maintained, it drifts from the payroll it is
meant to mirror, and the drift is invisible until someone approves something
they should not have, or a request routes to a person who left.

A step 10 concern, but it is an authority source, not reference data.

---

## 6. Repository and pipeline

- [ ] Move the repository from the personal GitHub account to a Desicon
      organisation
- [ ] `EX-2026-002` in `security-exceptions.yml` expires 2026-09-04 and will
      fail CI on that date — resolve or renew deliberately, not by extending the
      date under time pressure
- [ ] `npm run lint` has never been runnable: there is no ESLint config in the
      web project and no CI step invoking it. The script exists in
      `package.json` and always has
- [ ] `scripts/create-app-user.sql` is still applied by hand. Its Always
      Encrypted grants have not reached uat or prd, and a deployment there will
      fail on the first write to `Beneficiary.BankAccountNumber`
- [ ] Reduce `Microsoft.EntityFrameworkCore.Database.Command` logging to Warning
      in the API — a single readiness probe produced 27,000 log lines
- [ ] Finish the `ILogger` → Application Insights provider wiring; exceptions
      are still not arriving

---

## A note on how this list was built

Every item is something that was found by running the system rather than by
reading it. That is not a coincidence: each one is a control that existed on
paper — in a config file, a docstring, a package script, a documented role
table — and had never been executed.

The pattern is worth carrying forward. When adding to this list, prefer "has
anyone actually watched this work" over "is this implemented".
