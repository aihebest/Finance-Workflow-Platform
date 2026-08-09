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
| `olanrewaju.atanda@desicongroup.com` | `FinanceOfficer` | Confirm whether this is the real holder |
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
`FinanceManager` and `DirectorOfFinance` do not share a person. The maker-checker
guards compare `Employee.Id`, so one human with two accounts would satisfy them
while providing no separation at all.

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

## 4. Confirm Business Central enforces maker-checker

Version 1 of the workflow enforced that whoever entered a GL journal could not
authorise it — mirroring the two signature boxes on DEL-AC-FRM-002 and
DEL-AC-FRM-003.

Version 2 moved posting to Business Central, and that control went with it.
This platform no longer sees journals and cannot enforce it.

Somebody thought the separation mattered enough to design into the paper form.
Confirm BC provides it before assuming it survived the move. If BC does not,
that is a gap created by this project and it needs an answer.

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
