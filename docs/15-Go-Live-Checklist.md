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

## 3. Pin definition versions

**Outstanding work, not configuration.**

Workflow definitions resolve by `moduleKey` alone —
`IWorkflowDefinitionProvider.GetAsync` takes no version, and `Request` has no
`DefinitionVersion` column. Every request is evaluated against whatever
definition is deployed *now*, including requests raised weeks earlier.

A request sitting in a state that a new version removes has no way out:
`TransitionsFrom` returns an empty list, no error is raised, and the request
disappears from everyone's list of things to do while still being open.

This already happened twice in dev on 8 August: `EXP-2026-000005` stranded in
`AUTHORISATION` by the version 2 rewrite, and `EXP-2026-000004` stranded by an
org-chart edit that moved its resolved actor. Two causes, one root — the
platform assumes definitions and reporting lines hold still, and neither does.

Desicon will change these definitions; that is the point of a definition-driven
engine. Options are in `docs/12-Decision-Log.md`. **This should not reach UAT
unresolved**, because UAT is where requests first live long enough for a
definition to change underneath them.

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
