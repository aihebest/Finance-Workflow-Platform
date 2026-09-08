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

### 1c. Shared and functional accounts break attribution

`treasury@desicongroup.com` is a real sign-in account with the display name
"Treasury", not only a mailbox — confirmed 9 August 2026 when it was assigned
`TreasuryOfficer` and signed in. The directory also holds `ictadmin@`, `hr@`,
`genservices@` and `logistics@` in the same shape.

Everything this platform records about who did what keys off `Employee.Id`:
`PostedByUserId`, `AuthorisedByUserId`, `AcknowledgedByUserId`,
`BankDetailsSetByUserId`, and every row of the hash-chained `AuditEvent`
table. When several people share one login, all of that names an *account*
rather than a person. The chain is still tamper-evident; it is just evidence
about `treasury@`.

The controls most affected are the ones that cost the most to build:

- Maker-checker guards compare `ActorId` to another `Employee.Id`. Two people
  sharing one account are one actor as far as every guard is concerned, so a
  guard that should refuse will pass.
- The Director of Finance gate is a second pair of eyes. It is only that if
  the eyes belong to a specific person.
- `EXP-2026-000004` was traced by asking who acted and when. That question has
  no answer for a shared account.

This is not a defect in the platform and there is nothing to fix in the
repository. It is a decision for management: either named accounts for anyone
who acts on a request, with the shared address kept as a notification mailbox
only, or an explicit acceptance that Accounts actions are attributed to a desk
rather than a person.

**Decided 9 August 2026: shared desk accounts remain.** `treasury@` and the
other functional accounts keep their sign-in, and `TreasuryOfficer` stays
assigned to `treasury@` rather than to a named person.

This is a deliberate acceptance, not an oversight, and the consequence should
be stated plainly to anyone who later reads an audit trail: **Accounts actions
are attributed to a desk, not to a person.** `PostedByUserId` naming
`treasury@` means "somebody at the Treasury desk", and the platform cannot say
who. If that ever needs to change, it is one role reassignment and no code.

### 1d. There was no way to sign out

Asked on 6 September 2026, by Aihe, looking at a live request: *"do anyone
login need where to logout or you just close the browser page?"*

Closing the page was the only way out, and it is not one.

The token cache is `sessionStorage`, chosen deliberately (see `auth/msal.ts`)
so that closing the tab clears this application's copy. It does. But the Entra
sign-in cookie lives in the **browser**, not the tab, and survives. The next
person to open `finance.desiconapp.com` on that machine is signed straight back
in as the last one, silently and without a prompt, because that is exactly what
single sign-on is for.

Which lands on the same machines §1c is about. A shared site PC was the reason
the cache is `sessionStorage`; the half of the problem that lives outside the
tab was never addressed.

`postLogoutRedirectUri` has been configured since the first release. The
configuration for signing out was there the whole time. The button was not —
the same shape as the guard message that promised receipts nobody checked, and
the `PUT` endpoint nothing ever called.

Alongside it, a second gap that made the first invisible: **the header never
said who was signed in.** Nothing on screen could have let anyone notice they
were acting as somebody else.

**Both fixed 6 September 2026.** The account's display name *and* its username
now sit in the header — both, because §3e was written after a claim was paid to
the wrong one of two employees sharing a display name — beside a Sign out that
calls MSAL's `logoutRedirect` scoped to that account. Not a cache clear: that
would leave the Entra session intact and the next Sign in would walk straight
back in, which is the behaviour being removed.

- [ ] Tell staff that Sign out also signs that account out of other Microsoft
      sessions in the same browser. That is the intended trade on a shared
      machine and a surprise on a personal one
- [ ] Consider whether a session timeout is wanted as well. Sign out only helps
      the person who remembers to press it, and the case this is really about —
      someone walking away from a site PC — is the case where nobody does

---

## 2. Turn notifications on — DONE 10 AUGUST 2026

**This section was stale and was being read as current on 25 August**, two
weeks after the work was finished. It is kept, ticked, because the steps are
the record of what was done and what must be redone for uat and prd.

- [x] Provision the shared sender mailbox and set `notifications_sender_mailbox`
      — `financeworkflow@desicongroup.com`
- [x] Grant `Mail.Send` application permission to the Function App's managed
      identity, with admin consent
- [x] **Scope it with an Exchange application access policy** to that one
      mailbox. Without this, `Mail.Send` as an application permission allows
      sending as *any* mailbox in the tenant. Nothing in this repository can
      enforce or detect that, which is why it is called out separately rather
      than left as part of the step above. Tested rather than assumed —
      `Test-ApplicationAccessPolicy` returns Granted for the sender and Denied
      for the Director of Finance's own mailbox
- [x] Set `notifications_use_graph = true`
- [x] Confirm `notifications_role_mailboxes` names the real people

### 2b. The links in those emails pointed at the wrong host until 25 Aug 2026

Found while answering "why are notifications still off" — they were not, and
checking turned up something else.

`notifications_application_base_url` was built from the Front Door *endpoint*
hostname, so every approval email sent people to
`https://fde-desicon-fw-dev-e5deetfbdxfvfsfq.z01.azurefd.net/requests/{id}`.

The custom domain was bought for exactly this. The commit that added it says
so: *"that generated hostname is about to appear in every approval notification
this platform sends — to Heads of Department, the Accounts Manager and the DMD,
most of them reading it on a phone."* The domain went live and the notifications
carried on sending the old host, because the links still worked. Nothing was
broken enough to notice.

A change made for one reason, with the single place that reason applied left
pointing at the old value. The same shape as the Treasury number that stayed at
Cost Control after the role was split.

Now `coalesce(custom_domain_host_name, endpoint_hostname)`, so an environment
without a custom domain still sends links that work rather than links to a host
that does not exist.

- [ ] `terraform apply` in dev — this is a Function App setting, so it takes
      effect on apply, not on the next code deploy
- [ ] Raise one request afterwards and read the link in the email before
      trusting it

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

## 3d. Test through the front door, not around it

**Found and fixed 9 August 2026.** Receipt upload had never worked in dev.
Every `POST /api/v1/requests/{id}/attachments` was blocked by the Front Door
WAF and never reached the API.

Nothing reported it as a security event:

- Front Door answers a block with an HTML error page, not ProblemDetails, so
  the UI could only say `Request failed (403)` — no rule, no reason.
- The API never saw the request, so its logs were silent and correct.
- App Insights request telemetry is not flowing at all (§6), so the first
  query run against it returned nothing, which looked like confirmation and
  was actually no evidence either way.
- 61 integration tests were green. None goes through Front Door, and each
  seeds attachment rows directly into the table.

The rules were `200002` (failed to parse request body) and `200003` (multipart
failed strict validation), scoring against `949110` until it blocked. Both are
now disabled with the reasoning recorded in `modules/frontdoor/main.tf`.

### And immediately behind it, a second one

With the WAF fixed the upload reached the API and threw a 500. The storage
account holding receipts had `default_action = Deny` and **zero** virtual
network rules, so the App Service — whose traffic leaves through the
integrated subnet with a private source address — could never reach it. The
single `ip_rules` entry admits a developer laptop, which is why the account
looked reachable to whoever checked it by hand.

The `sql` module documents this precise trap in a twenty-line comment. The
functions storage account already carried the rule. The attachments account
was the one that did not, and an empty allow-list is indistinguishable from a
correct one until something knocks.

Two failures, stacked, on one endpoint — and neither could surface until a
byte was actually sent. The container had existed, correctly configured with
immutability, versioning and a customer-managed key, since the infrastructure
work.

**The lesson is the gap between where the tests stop and where the users
start.** Everything between the test host and the browser — Front Door, the
WAF, CORS, the SPA's own fetch layer — is unexercised by the suite, and that
is precisely where this lived. It was found by one person clicking one button.

- [ ] Extend the deploy smoke test to upload a small file through the Front
      Door hostname and assert 201, so this class cannot return silently
- [ ] Re-run the WAF query below after any managed-ruleset version bump; a new
      version reinstates default rule behaviour

```powershell
$ws = az monitor log-analytics workspace list -g rg-desicon-fw-dev --query "[0].customerId" -o tsv
az monitor log-analytics query --workspace $ws --analytics-query "AzureDiagnostics | where TimeGenerated > ago(2h) | where Category == 'FrontDoorWebApplicationFirewallLog' | where action_s == 'Block' | order by TimeGenerated desc | take 20" --query "[].{time:TimeGenerated, rule:ruleName_s, uri:requestUri_s}" -o table
```

A blocked request is invisible to the application by construction. This query
is the only place it is visible at all, which is worth remembering the next
time something works locally and not in dev.

---

## 3e. A name is not an identifier

**Found and fixed 9 August 2026.** A claim was raised against the wrong one of
two employees who share a display name, ran the entire approval chain, and was
paid.

Nobody was careless. There was nothing on screen to be careful about:

- The beneficiary picker showed `Name (Type)` and nothing else.
- The request detail API returned `beneficiaryId` — a bare guid — and the
  approval screen rendered no payee at all. A line manager, a department head,
  Cost Control, the Director of Finance and Treasury each approved a payment
  without the recipient appearing anywhere on the page.

The Director of Finance gate exists to put a second pair of eyes on money
leaving the company. Those eyes were not being shown the destination.

Both halves now carry staff number and email, and
`A_payee_is_identifiable_when_chosen_and_when_approved` asserts it at both
points.

**Decided 9 August 2026: no payee confirmation step at the DMD.** He approves
the release of money, not the mechanics of who is named on a claim, and
retirements do not reach him at all. Adding a confirmation there would put the
check in the wrong place and slow the one approval that must not become
routine.

The consequence, stated so it is not discovered later: **the payee is chosen
once, at capture, and nothing downstream re-affirms it.** It is now visible on
every approval screen, but visible is not the same as checked. The control
against a wrong payee is the requester getting it right, and the identifiers
now shown to them.

- [ ] Check whether any two active employees share a `FullName` before go-live:

```sql
SELECT FullName, COUNT(*) AS Rows FROM Employees WHERE IsActive = 1
GROUP BY FullName HAVING COUNT(*) > 1;
```

---

## 4. Confirm Business Central enforces maker-checker

Version 1 of the workflow enforced that whoever entered a GL journal could not
authorise it — mirroring the two signature boxes on DEL-AC-FRM-002 and
DEL-AC-FRM-003.

Version 2 moved posting to Business Central, and that control went with it.
This platform no longer sees journals and cannot enforce it.

**Confirmed 9 August 2026: Business Central does not enforce it.** The control
that was on the paper form as two signature boxes, and in version 1 of this
workflow as a maker-checker guard, no longer exists anywhere. This project
removed it and nothing replaced it.

### What is and is not covered

Five people approve before Treasury posts — line manager, department head,
Cost Control, the Accounts Manager, the Director of Finance. **What was
approved is thoroughly checked.**

What nothing checks is **what Treasury actually entered in Business Central**.
This platform captures a document number and never compares the posted amount,
account or payee to the approved claim. A posting that differs from what was
authorised is invisible here, and now also invisible in BC.

Note what this does *not* mean. The payment decision is still dual-controlled —
the DMD authorises before anything is posted, and he is a different person from
whoever posts. The gap is narrower than "anyone can pay anyone": it is that the
ledger entry is not verified against the approval it came from.

### Chosen remedy: periodic reconciliation

**Decided 9 August 2026.** A report listing approved amounts against BC
document numbers for a period, reconciled in batch by someone other than
Treasury.

Two things to be honest about:

- It is **detective, not preventive**. A discrepancy is found after the money
  has moved, not before. That is a real reduction against what the paper form
  provided, and it is the accepted position rather than an equivalent one.
- **It does not exist yet.** This platform has no BC data feed, so it cannot
  compare anything today. Until that feed exists, posting is single-controlled
  with no detection at all.

- [ ] Establish a BC data feed, or an export Finance can reconcile against
- [ ] Define the cadence and who performs it — explicitly not Treasury
- [ ] Until then, record in the risk register that posting is unverified

The last item matters most. A remedy that is decided but not yet built is
indistinguishable, in practice, from no remedy — and that is the exact failure
mode this whole document exists to catch.

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

## 5b. Requests submitted before 22 Aug 2026 are invisible in the pipeline report

`Request.SubmittedAt` existed from the first migration — entity, EF
configuration, guard field, three DTOs and a composite index — and nothing ever
wrote to it. `RequestActionService` was fixed on 22 Aug 2026, so everything
submitted from that date forward carries the value.

Everything submitted *before* it still has `NULL`, and the pipeline report
filters on exactly that:

```csharp
.Where(r => r.ClosedAt == null && r.SubmittedAt != null)
```

So an open request raised before 22 August does not appear in "What's waiting on
whom" at all. Not late, not queued — absent. The report reads zero and looks
calm while work sits in somebody's queue.

That is the same failure this report was built to end, wearing the report's own
uniform. A zero because nothing is there is fine. A zero because the rows were
filtered out is worse than no number, because it is believed.

- [ ] Run `scripts/backfill-submitted-at.sql` before trusting the pipeline
      report:

      ```powershell
      . .\scripts\dev-db-connect.ps1

      Invoke-Sqlcmd -ServerInstance "sql-desicon-fw-dev.database.windows.net" `
        -Database "DesiconFinanceWorkflow" -AccessToken $token `
        -InputFile "scripts/backfill-submitted-at.sql" `
        -Variable @("Apply=0") -Verbose
      ```

It reports by default and writes nothing until `Apply=1`. The value is
reconstructed from `AuditEvents` — the earliest departure from `DRAFT`, which is
append-only and hash-chained, so it is recovered from evidence rather than
estimated. `RESUBMIT` is deliberately not matched, so a returned-and-resubmitted
claim keeps its original submission date, which is what an SLA should measure
from.

**This is why an empty report is not yet evidence of an empty queue.** Confirm
the count is zero because the count is zero: raise one request and watch it
appear.

---

## 5c. A request raised by mistake cannot be withdrawn

Found 23 August 2026, by raising one by mistake.

`ADV-2026-000001` (₦330,000) was raised in error. There is no `CANCEL`,
`WITHDRAW`, `ABANDON` or `VOID` action anywhere in either module. Checked, not
assumed. The only exits from `DEPT_HEAD` are `VERIFY`, `RETURN` and `REJECT`,
all performed by the department head — never by the person who raised it.

So a requester who mistypes an amount, picks the wrong beneficiary, or submits
twice cannot undo it. They must ask an approver to reject their own mistake,
which means the error becomes a conversation and lands in the audit trail as a
rejection by somebody else. For an ordinary member of staff that is a call to
their Head of Department to say "please throw that away".

In this case it was recoverable only because the requester happened to *be* the
department head: `REJECT` carries no `ActorId != RequesterId` guard, while
`VERIFY` does. That asymmetry is correct and deliberate — you may not approve
your own request, but declining it harms nobody. It just does not help anyone
who is not their own approver.

There is a second consequence that is easy to miss: a mistaken request does not
sit still. `DEPT_HEAD` has a 24-hour SLA with `escalateTo: COST_CONTROL_VERIFY`,
so an abandoned error escalates itself into the next desk's queue rather than
ageing quietly.

- [x] ~~Decide whether requesters should be able to withdraw a request while it
      is still at the first approval step~~ — **decided 23 Aug 2026: yes.**
      Built as workflow **version 5** on branch `feat/withdraw-own-request`,
      **not yet merged.**

      `WITHDRAW` from `DEPT_HEAD` and from `RETURNED`, actor `Requester`,
      guarded `ActorId == RequesterId`, comment required, landing in a new
      terminal state `WITHDRAWN`.

      `WITHDRAWN` rather than reusing `REJECTED`, deliberately: a rejection is
      an approver's decision and belongs to that approver. Recording a
      withdrawal as one would put a refusal in a tamper-evident trail against
      somebody who never made it — the same attribution problem as §1c, from
      the other direction.

      Not available past `DEPT_HEAD`. Once Cost Control holds it, someone else
      has spent time on it, and the way out is a rejection somebody signs.

      Held back from `main` until after the Monday walkthrough. Version pinning
      means requests raised under v4 — including `ADV-2026-000002` — keep the
      process they were raised under, so merging changes nothing already in
      flight.

Worth noting this is not a deviation from DEL-AC-FRM-003. The paper form has no
concept of withdrawal either — but on paper you simply do not hand the form in,
and the platform has no equivalent of not handing it in.

---

## 5d. The buttons offered and the actions permitted are computed differently

Found 23 August 2026, trying to reject `ADV-2026-000001`.

The request detail page offered **Verify** (disabled, with a reason), **Return
for correction** and **Reject**. Return and Reject looked ordinary. Both were
refused, silently as far as the click was concerned, and the request stayed at
`DEPT_HEAD`.

The reason was already on the screen, at the top, phrased as a page-level
notice:

> Self-approval blocked: the acting user is this request's own requester.

That is `RequestActionService.EvaluatePolicyViolation` — a defence-in-depth
layer that re-checks self-approval and maker-checker independently of the
definition's own guards, so that a misconfigured or forged definition cannot
authorise what policy forbids. It is a good control and it worked.

**But `GetAvailableActionsAsync` does not run it.** Availability is computed
from the definition's guards alone. `REJECT` and `RETURN` carry no guard — only
`VERIFY` does — so the API reports them as available, and the engine then
refuses them at execution.

Two layers, the same question, different answers. The user sees a button, a
banner that appears unrelated to it, and no response to their click.

### Why it deadlocked

`REJECT`'s actor is `DepartmentHeadOf(DepartmentId)`. The requester *was* the
Head of Department of their own department, so nobody else resolves as an
eligible actor either. The request could not be cleared by anyone:

- the requester is blocked by the self-approval policy
- no other person satisfies the actor spec

It unsticks itself after 24 hours — `DEPT_HEAD` escalates to
`COST_CONTROL_VERIFY`, where a different desk can reject it. That the SLA
escalation is what rescues this is not a coincidence; it is the only mechanism
in the system for a queue nobody can act on.

`WITHDRAW` (version 5, branch `feat/withdraw-own-request`) also resolves it,
and now looks less like a convenience: `Requester` is in
`SelfServiceResolvers`, so the self-approval policy permits it by design. The
feature built last night for tidiness turns out to be the only self-service
route out of a genuine deadlock.

- [ ] Make `GetAvailableActionsAsync` run `EvaluatePolicyViolation` and return
      policy-blocked actions as *disabled with a reason*, the way guard-blocked
      ones already are. The page already knows how to render that — `Verify`
      does it correctly on the same screen
- [ ] Nothing should render as an enabled button that the engine will refuse.
      A control that stops the wrong thing is worth having; one that stops it
      without saying so teaches people the system is unreliable

Not a Monday blocker: it only bites when the acting user is also the requester,
which is not the walkthrough. It bites the moment a Head of Department raises
anything for themselves.
## 5e. Cost Control cannot correct a miscoded request at its own step

Raised by Cost Control on 24 August 2026, alongside three defects that have
been fixed (the amount box, the department not showing, and the code inputs
being unclickable — see the branch `fix/advance-amount-and-request-header`).

This one is not a defect. It is a question only Finance can answer.

**Resolved in workflow version 6 (25 Aug 2026).** `COST_CONTROL_VERIFY` no
longer captures a Treasury number — that moved to `MARK_POSTED`, where Treasury
acts. Cost Control now guards on the coding instead, and can set it through the
Coding panel. The text below is kept because the reasoning is still the record
of why.

`COST_CONTROL_VERIFY` captured a Treasury number and nothing else. The desk
whose entire job is checking that spend is costed to the right centre cannot
set or amend the project code or cost centre. Their only lever is **Return for
correction**, which sends the whole request back to the requester.

Two defensible answers:

- **Return it.** The requester owns their own coding, the correction is made by
  the person who knows what the spend was for, and the trail shows a request
  that was miscoded and fixed. Slower, and for a wrong cost centre on an
  otherwise perfect request it is a lot of ceremony.
- **Let Cost Control amend it.** Faster, and matches what the desk is for. But
  it means the figures a department head approved can change after they
  approved them, which is a different thing from correcting a typo and should
  be visible in the trail if it happens.

Whichever is chosen, it is a workflow version: a `captures` entry on
`COST_CONTROL_VERIFY`, and — if amendment is allowed — an audit entry that
records the old and new coding rather than silently overwriting it.

**Decided 24 August 2026: both.** Cost Control returns a request with comments
when the *request* is wrong, and sets the coding themselves when only the coding
is. Built as `PATCH /api/v1/requests/{id}/allocation`, restricted to
`CostControlOfficer` while the request is at `COST_CONTROL_VERIFY`.

The reason it is Cost Control rather than the requester: **they hold the
organisation's cost centres and the requester does not.** A code that arrives
wrong or blank is ordinary, not careless, and returning every one of them makes
the desk that knows the answer ask the person who does not.

It is not a transition and not a `captures` field. `captures` are all mandatory
in `WorkflowEngine`, and project code and cost centre are alternatives, so
requiring both would refuse every verification. Nothing moves either — a fact
about the request is corrected while it sits in the same queue — so modelling it
as a state change would put a step in the trail that did not happen.

The previous coding is recorded alongside the new one, with a required reason,
in a hash-chained `ALLOCATION_SET` event. A silent correction is
indistinguishable from the original entry a week later, and this is the figure
that decides which budget carries the spend.

- [ ] **The fix at source, not yet built:** Cost Control holds the cost centre
      list; this platform does not. Codes are free text on both forms, so a
      requester can type anything and Cost Control corrects it afterwards. A
      cost centre reference table and a picker would stop most miscoding before
      it happens, and would need nothing from us but the list itself

---

## 5h. A retired advance could not be submitted, and never could be

Found 6 September 2026, while updating a test comment that turned out to be
half wrong in the half that mattered.

**Retiring an advance dead-ends.** The sequence:

1. Requester opens My Advances and presses **Retire this advance**
2. `AdvanceRetirementEndpoints.RetireAsync` inserts a linked `ExpenseRequest`
   directly — a plain DB insert, not a guarded transition — and the SPA
   navigates them straight to it
3. That insert never sets `ReceiptStatus`, so it takes the property default,
   which is `ReceiptStatus.No`
4. EXPENSE's SUBMIT guard requires `ReceiptStatus != 'No'`
5. The claim cannot be submitted

And there is no way out of it from the browser. `PUT /api/v1/requests/{id}`
exists and routes to `UpdateDraftAsync`, which calls the same
`ApplyExpenseFields` that draft creation does — so the API can set the field.
**Nothing calls it.** The SPA has no update-draft function in `api/requests.ts`
and no edit-draft route in `App.tsx`. A requester who retires an advance lands
on a claim they can neither submit nor edit.

This was written down. `CashAdvanceWorkflowTests` carried a note saying a
retirement-linked claim "is therefore stuck at DRAFT via the API alone", and
the test worked around it by poking the field through the `DbContext`. The
note was read as a test-harness inconvenience for eleven weeks. It was a
description of a user-facing dead end, sitting in the codebase, in a comment,
in the past tense.

**Why this one is worse than the others in this document.** The Director of
Finance's stated objection to cash advances is that around 98 percent of them
were never retired. Retirement is the single behaviour this platform most
needs to make easy. It is the one that does not work.

The paper process at least had somewhere for the claim to go. The digital one
accepts the request, creates the claim, shows it to the requester, and then
refuses it with a guard message about a radio button they cannot reach.

**Fixed 6 September 2026.** `PATCH /api/v1/requests/{id}/receipt-status`, with
the radio row on `RequestDetail` sitting directly under the attachments panel —
attach the receipts, then answer the question about them, in that order and in
one place.

- [x] ~~Decide the fix~~ — a narrow endpoint rather than the existing PUT.
      `ApplyExpenseFields` is a full replace: it clears the lines and rebuilds
      them from the payload, so changing one radio button through it would mean
      the browser round-tripping every line the server generated from the
      advance. On a retirement claim those lines *are* the account of what the
      money was spent on. They should not make that trip to change something
      else
- [x] ~~Do not fix it by having `RetireAsync` set `ReceiptStatus = Yes`~~ — not
      done, and worth keeping the reason: that would assert on the requester's
      behalf that a receipt exists, which is the exact confusion between
      *asserted* and *provided* that §5g and this section are both about
- [x] ~~Whatever the fix, it needs a test that drives it over HTTP as a
      requester~~ — `RetirementCanBeCompletedTests` retires an advance and
      submits the claim end to end without writing to the database, and asserts
      the dead end first so the thing being fixed is visible in the test rather
      than only in this document. `CashAdvanceWorkflowTests` no longer reaches
      around the gap either
- [ ] The panel is gated on `can("SUBMIT")`, which is the API's own answer to
      "may this person submit this" — the requester, in DRAFT or RETURNED. It
      deliberately ignores whether the guard passes, because SUBMIT is blocked
      precisely when the field says No. Worth remembering if anyone ever makes
      `availableActions` omit blocked actions: that would hide the control that
      unblocks them, and re-create this exact deadlock
- [ ] Check how many retirement claims are sitting in DRAFT in production
      right now, unable to move:

      ```sql
      SELECT COUNT(*) FROM Requests r
      JOIN ExpenseRequests e ON e.RequestId = r.RequestId
      WHERE r.CurrentState = 'DRAFT' AND e.RetiresAdvanceId IS NOT NULL;
      ```

Every existing test reached past this by writing to the database, which is
precisely why nobody found it.

---

## 5i. Treasury's half of the cash advance had no screen at all

Found 7 September 2026 by Treasury, on `ADV-2026-000008` — the first cash
advance ever to reach `AWAITING_POSTING` with a real person behind it. They
entered the BC document number and the Treasury number, pressed **Mark posted
in BC**, and got:

```
Expense request '1264671a-7639-496f-8d38-e59073e6beca' does not exist.
```

Which was true. The SPA's `markPosted` posted to
`/api/v1/expenses/{id}/mark-posted` for every request regardless of module, and
that endpoint looks for an `ExpenseRequest` row. A cash advance is not one.

**Both endpoints existed. Both were tested.** `WorkflowSteps` has
`MarkPostedExpenseAsync` and `MarkPostedAdvanceAsync`, and the integration
suite drives each of them. `WorkflowCompletenessTests` asserts that every
transition in both definitions is exercised, and it passes. What had no test
was the only caller that ships to a person, and it called one endpoint for both
modules.

That is the shape of it worth keeping: **209 green tests, complete transition
coverage, and the button did not work.** The tests cover the API. Nothing
covers the SPA — there is no test framework in `src/Desicon.Workflow.Web` at
all — so every defect in the layer people actually touch is found by a person.

### And two more directly behind it

Fixing the route alone would have moved Treasury one step and stopped them
again:

- `RELEASE_CASH` captures `CashReleasedAt`, and was missing from
  `CAPTURE_ACTIONS` in `RequestDetail`. It would have rendered as a bare button
  that sent the action with no date, been refused for the missing field, and
  offered nowhere to type one — the exact deadlock the comment on that constant
  already describes, in the one branch nobody had walked.
- Nothing in `api/requests.ts` called `/api/v1/advances/{id}/release` at all.
  The SPA used two of the six advance endpoints.

`CashReleasedAt` is not a formality. It starts the retirement clock, so it is
what makes an advance overdue, what the SUBMIT guard blocks a new advance on,
and what the Director of Finance's entire objection rests on. It had no input
anywhere in the product.

**All three fixed 7 September 2026.** Module-aware routing on `markPosted`, a
Cash release panel with the released-at datetime, and `RELEASE_CASH` added to
`CAPTURE_ACTIONS`.

- [ ] The remaining advance steps have still never been walked by a person:
      `RELEASE_CASH → AWAITING_ACK → ACKNOWLEDGE → OUTSTANDING`, then retire.
      `ACKNOWLEDGE` should work through the generic action button — the
      dedicated endpoint does nothing the generic one does not — but "should"
      is what this section is about
- [ ] Decide whether the SPA gets tests. Every defect found in the last three
      days that reached a person lived in the browser: the missing upload, the
      receipt-status dead end, the absent sign-out, and now this. The API suite
      has never once been the thing that failed
- [ ] Until it does, the release check is a person walking each branch. Both
      modules, end to end, after any change to `RequestDetail` or
      `api/requests.ts`

---

## 5j. A button retired an advance that nobody had accounted for

7 September 2026, on `ADV-2026-000008` — ₦360,000, the first cash advance ever
walked end to end by real people. It reached `OUTSTANDING`, the request page
drew a button marked **RETIRE**, the requester pressed it, saw nothing change,
and pressed it again. The trail now reads:

```
RETIRE · OUTSTANDING → PARTIALLY_RETIRED       22:32:13
RETIRE · PARTIALLY_RETIRED → PARTIALLY_RETIRED 22:32:57
```

Nothing was retired. No claim, no receipts, no figure. The advance declared
itself partly accounted for, twice, and the hash-chained audit trail — the
thing this whole platform exists to produce — now carries two entries saying a
retirement happened on a day when none did. Those entries cannot be removed;
they can only be explained.

**Everything about the button was correct except its existence.**
`GetAvailableActionsAsync` reported RETIRE as available because its actor is
the Requester and its guard only asks that a balance remains. Both true.
Neither expressed the thing that matters: RETIRE is a *consequence*, not a
choice. An advance is retired by the expense claim that accounts for it, and
`AdvanceRetirementHandler` fires the transition as a cascade once that claim
carries a figure. Nobody presses it.

### The test made it look legitimate

`WorkflowCompletenessTests` had been firing RETIRE straight at the actions
endpoint as the requester — five times — and reporting the transition covered.
It was covered. What it covered was a route that should not have existed, and
its being covered is part of why nobody questioned the button.

That is a sharper version of §5i's lesson. There, 209 green tests missed a
defect because nothing tested the browser. Here a test *asserted the defective
behaviour was correct*, in a file whose entire purpose is proving completeness.

**Fixed the same evening.** `WorkflowTransition.SystemOnly`, set on all four
RETIRE branches:

- `ExecuteAsync` refuses a system-only transition and logs the denial;
  `ExecuteCascadedAsync` still allows it, so the real path is untouched
- `GetAvailableActionsAsync` stops returning it, so no page can draw it
- The request page now offers **Start retirement claim** instead, which raises
  the linked claim — the same thing My Advances does, in the place the
  requester was already looking
- `RetireIsNotAButtonTests` asserts the refusal, the absence from
  `availableActions`, and that the cascade still works
- The completeness test excludes system-only transitions from its expected set,
  with the reason written where the old calls were

### Also fixed: the retirement half sat in nobody's inbox

Same evening, found while chasing the same walkthrough. Cash advance **version
8**. `AWAITING_ACK`, `OUTSTANDING` and `PARTIALLY_RETIRED` each have one exit
belonging to the requester and one belonging to a role — Treasury's `RETURN`,
Finance's `WRITE_OFF`. `ResolveNextActorAsync` returns null the moment it meets
a queue-owning exit whose actor is a role, because a role does not resolve to
somebody to put in an inbox. So the advance was waiting on the requester and
the platform recorded it as waiting on no one.

`ADV-2026-000008` reached `AWAITING_ACK` and disappeared from the requester's
inbox entirely. The only way to acknowledge it was to already know the URL.

Those three are escape hatches, not queues — the same distinction version 5
drew for WITHDRAW — so each is now `ownsQueue: false`. EXPENSE has no state of
this shape, which is why it never appeared there.

- [ ] `ADV-2026-000008` is on version 7 and stays there. It is currently
      `PARTIALLY_RETIRED` with nothing retired; the balance is intact and the
      proper retirement still works from My Advances. Finish it and note in the
      claim's comment why two RETIRE entries sit above it
- [ ] Decide whether the two false entries need anything said to the auditor
      beyond this section. They are honest — somebody did press a button — but
      a reader a year from now will not know what the button was

### And the wall directly behind it: bank details have no screen

Pressing **Retire this advance** on `ADV-2026-000008` returns:

> Employee 'ICT Admin' (DEV-0003) has no bank details on file. Record bank
> details for this employee before an advance can be retired to them.

Correct, and deliberate: `AdvanceRetirementEndpoints` creates the requester's
own Employee-type Beneficiary and sources bank details from the Employee
record rather than leaving them blank, because a beneficiary with none would
pass a check it should fail.

**There is nowhere to record them.** `PUT /api/v1/requests/{id}/beneficiary/
bank-details` exists and is tested. Nothing in the SPA calls it — the frontend
reads `hasBankDetails` in `NewExpense` to show a warning and offers no way to
resolve the thing it warns about. Same shape as the receipt-status dead end in
§5h, one layer along.

Worth questioning as well as fixing: a retirement where the claim matches the
advance pays nobody anything. Requiring bank details before the claim can even
be *raised* blocks a net-zero retirement on a detail that will never be used.

- [ ] Add a way to record an employee's bank details, and decide who may. It is
      the field that decides where money goes, so it is not an ordinary edit
- [ ] Decide whether retirement should require them at all, or only when the
      finished claim leaves Desicon owing the employee

---

## 5f. FluentAssertions 8 is not free for Desicon

Found 24 August 2026 while looking at why several Dependabot pull requests were
failing CI. The failing build was the smaller problem.

**The licence changed at 8.0.0.** Ownership moved to Xceed Software Inc., and
the package description says it plainly: *"free for open-source projects and
non-commercial use, but commercial use requires a paid license."*
`requireLicenseAcceptance` is set on the package. Desicon is a commercial
company and this is internal commercial software.

This project is on **6.12.1**. The **7.x line remains Apache-2.0**, is still
maintained by the original author, and is current — 7.2.2 shipped 16 March
2026, the same day as 8.9.0. Staying on 7.x is a supported position, not a
stale one.

The trap is the shape of it: an 8.x bump arrives as an ordinary Dependabot pull
request, in a group called `test`, alongside xunit and Respawn. Nothing in the
title, the diff or a green CI run would mention a licence. That is how a
commercial obligation gets acquired on a Monday morning by clicking Merge.

**Blocked in `.github/dependabot.yml`** — major updates to `FluentAssertions`
are now ignored, with the reason written next to the rule. Patches and minors
within 7.x still arrive, which is where any fixes are.

- [ ] Move 6.12.1 → 7.2.2 deliberately, with the suite running. Not done here
      because it is a major bump and this session could not execute the .NET
      tests
- [ ] Check whether anything else in the tree has changed licence since it was
      chosen. This one was caught by accident, which is not a control

---

## 5g. An advance could be approved with no evidence behind it

Raised by the Director of Finance on 5 September 2026. He authorises every
payment Desicon makes, and his objection was not to the workflow — it was that
he was being asked to release money against a purpose line and a figure, with
nothing to check either against. His own estimate is that roughly **98 percent
of cash advances raised on paper were never retired**.

An expense claim has always needed a receipt, because it is money already spent
and the evidence exists. An advance is money *not yet* spent, so its evidence is
a quotation, a pro-forma or a written request — and until now nothing asked for
one.

**`HasSupportingDocuments` was there the whole time.** It is DEL-AC-FRM-003's
tick box, captured on every advance since the first release, stored, indexed,
and read by no guard ever. The platform faithfully recorded that somebody said
there was a document. It never once asked for the document.

Fixed in **cash advance version 7**: `SUBMIT` now also requires
`AttachmentCount > 0`, counted from the Attachments table the same way the
expense claim's receipt check works — what was provided, not what was asserted.
The form uploads the file between creating the draft and submitting it, so the
requester still does it in one action.

Deliberately *not* done, per Aihe on 5 September: the block on raising a new
advance stays on the overdue rule alone. Requiring a document is a check on this
advance; blocking on someone's retirement history is a different decision and
belongs to whoever sets that policy.

- [ ] **Drafts created before v7 deploys are still on v6 and will submit
      without a document.** `DefinitionVersion` is pinned at draft creation and
      never changes — that is the whole point of pinning, and it is right. But
      it means the rule starts with advances raised *after* the deploy, not
      with the ones already sitting in DRAFT. Count them before deploying:

      ```sql
      SELECT COUNT(*) FROM Requests
      WHERE ModuleKey = 'CASH_ADVANCE' AND CurrentState = 'DRAFT'
        AND DefinitionVersion < 7;
      ```

      If it is a handful, ask those requesters to discard and re-raise. If it is
      not, they will reach the DMD with nothing attached and he will have been
      told the problem was fixed
- [ ] `BLOCK_NEW_ADVANCE_WHEN_OVERDUE` is still a policy value nothing reads —
      the overdue check is hard-coded in the SUBMIT guard instead. A policy row
      that looks like a switch and is not one will eventually be turned off by
      somebody expecting it to do something
- [ ] The DMD asked to *see* the evidence, not only for it to exist. He can open
      the attachment from the request page, but nothing puts it in front of him
      at DMD_APPROVAL. Worth watching whether he actually opens them

### The frontend lint has never run

Noticed 5 September 2026 while checking this change. `src/Desicon.Workflow.Web`
has an `npm run lint` script, four ESLint packages installed, and **no ESLint
configuration file anywhere in the repository**. The script fails immediately
with "couldn't find a configuration file" — and CI never calls it, only
`npm run build`, so nothing has ever reported this.

- [ ] Either add a config and put `npm run lint` in CI, or remove the script and
      the four dependencies. What is there now is the appearance of a control

---

## 6. Repository and pipeline

- [ ] Move the repository from the personal GitHub account to a Desicon
      organisation
- [x] ~~`EX-2026-002` in `security-exceptions.yml` expires 2026-09-04 and will
      fail CI on that date — resolve or renew deliberately, not by extending the
      date under time pressure~~ — **retired 22 Aug 2026, and not for the
      reason expected.**

      The advisory was re-published as two ranges after the exception was
      written. The 7.x half is `>=7.12.0 <7.18.2`, and this project already
      held 7.18.2 — the patched version. The finding had stopped being
      reported some time ago; `npm audit` on `main` today does not raise it.
      The exception was suppressing nothing.

      An expiry date makes you look again. It does not make you look at the
      right thing: what was needed was re-reading the advisory at source, not
      re-reading our own justification of it.

- [ ] **Nothing in CI runs `npm audit`.** `ci.yml` runs
      `scripts/check-exceptions.mjs`, which enforces that entries in
      `security-exceptions.yml` have not expired — but no step produces the
      npm-audit findings those entries are exceptions *to*. The register has
      a `tool: npm-audit` field, an owner, an expiry and a checker, and the
      scan itself has never run in the pipeline.

      This is the same shape as every other item on this list: a control
      built, documented, provisioned, and never once executed.

      Cheap to close right now, and this is the moment: the regenerated
      lockfile audits at **zero vulnerabilities, all severities**, so adding
      a gate would pass today rather than arriving pre-broken. Deferred only
      because turning on a blocking gate is a policy decision, not a
      refactor — once added, the next published advisory stops the pipeline,
      which is the point and should be a choice made deliberately.
- [ ] `npm run lint` has never been runnable: there is no ESLint config in the
      web project and no CI step invoking it. The script exists in
      `package.json` and always has
- [ ] `scripts/create-app-user.sql` is still applied by hand. Its Always
      Encrypted grants have not reached uat or prd, and a deployment there will
      fail on the first write to `Beneficiary.BankAccountNumber`
- [ ] Reduce `Microsoft.EntityFrameworkCore.Database.Command` logging to Warning
      in the API — a single readiness probe produced 27,000 log lines
- [x] ~~Finish the `ILogger` → Application Insights provider wiring; exceptions
      are still not arriving~~ — **this entry was wrong.** Corrected
      22 Aug 2026: telemetry has been arriving since the API was first
      deployed. In a six-hour window: 7,178 `AppRequests`, 29,322
      `AppDependencies`, plus traces and exceptions.

      The entry existed because every query run against it returned nothing.
      Application Insights here is **workspace-based** (`workspace_id` is set
      on the resource), so the data lands in the Log Analytics workspace as
      `AppRequests`, `AppTraces`, `AppExceptions`, `AppDependencies` — not the
      classic `requests` / `traces` names that were being asked for.

      Empty result, wrong question. It cost real time twice: a WAF block and a
      500 were both diagnosed the slow way because the instrument was believed
      to be dark. **Query the App\* tables on the workspace, not the legacy
      names:**

      ```powershell
      $ws = az monitor log-analytics workspace list -g rg-desicon-fw-dev --query "[0].customerId" -o tsv
      az monitor log-analytics query --workspace $ws --analytics-query "AppExceptions | where TimeGenerated > ago(1h) | project TimeGenerated, ProblemId, OuterMessage, Method" -o table
      ```

      Worth keeping as a written finding rather than a silent correction: a
      monitoring gap that does not exist is as expensive as one that does. It
      sends people to build an instrument they already have, and — worse —
      teaches them to distrust the one reading they should have believed.

---

## 7. "dev" is production now, and several things still assume it is not

**Decided 22 August 2026.** There is no separate production environment. The
environment named `dev` — resource group `rg-desicon-fw-dev`, server
`sql-desicon-fw-dev` — is the one Desicon will run on, served at
`finance.desiconapp.com`.

That is a reasonable decision at this scale. It is recorded here because it
silently changed the meaning of things written when it was not true, and none
of them will announce it.

### The one that could have destroyed data

`scripts/reset-dev-requests.sql` deletes every request, every expense line,
every retirement link. Its guard was:

```sql
IF @@SERVERNAME NOT LIKE '%-dev%'
```

The server is `sql-desicon-fw-dev`. **The guard passes.** It would have deleted
every claim and advance Desicon holds and printed a line saying it was doing so
safely.

Nothing in that file changed. A control that was correct was made wrong by a
decision taken somewhere else — which is this project's recurring finding
arriving through the one door nobody was watching: not a control that was never
executed, but one whose premise expired.

Rewritten. It no longer tries to infer whether an environment is precious,
because it cannot and only appeared to. It now requires the operator to name the
server explicitly:

```
-v ConfirmServer="SQL-DESICON-FW-DEV"
```

There is no environment where that runs by accident, and no future rename that
can quietly re-arm it.

### Still carrying dev assumptions

These were deliberate choices for a throwaway environment. Each is now a
production setting and needs a decision rather than a default:

- [ ] `use_private_endpoints = false` — Key Vault, SQL and Storage are reached
      over the public internet through the `deployer_ip_addresses` allow-list.
      Fine for an environment holding test data; it is now the data plane for
      real bank details
- [ ] `deployer_ip_addresses` contains a personal ISP address that rotates. A
      production data plane should not have a home connection in its allow-list
- [ ] `notifications_use_graph = false` — nothing has ever been emailed to a
      real person (§2). This is now the blocker it always was, on the
      environment that matters
- [ ] Temporary role assignments (§1) are no longer "revoke before go-live".
      They are live production authority, held by accounts assigned because
      somebody was unavailable in August
- [ ] SKUs were chosen as "scaled down relative to uat/prd" — `P1v3`,
      `GP_Gen5_2`, `EP1`. Re-examine against real load rather than inheriting a
      dev sizing decision
- [ ] Naming: the resources keep `-dev` throughout, and every future reader
      will assume this is a test environment. See below for why renaming is not
      the remedy, and what is

### There is no rename. There is only a rebuild wearing the word.

Checked 5 September 2026, because "just rename them" is the obvious idea and it
is worth having the answer written down before somebody acts on it.

Azure has no rename operation for anything in `rg-desicon-fw-dev`:

| Resource | Renameable in place? |
|---|---|
| The resource group itself | **No.** Azure has no rename for resource groups |
| `sql-desicon-fw-dev` | No — the logical server name *is* the FQDN |
| `app-desicon-fw-api-dev`, `-web-dev`, `func-desicon-fw-dev` | No — the name *is* the `azurewebsites.net` hostname |
| `afd-desicon-fw-dev` | No |
| Key Vault | No — and soft-delete reserves the old name for the retention window (90 days), so it cannot even be reused promptly |
| Log Analytics workspace, App Insights | No |
| The user-assigned managed identity | No — and recreating it mints a **new principal ID**, silently invalidating every role assignment and Key Vault policy that names the old one |
| `DesiconFinanceWorkflow` (the database) | Yes — and it is the one thing already named correctly |

So changing any of those names in Terraform yields `-/+ destroy and then create
replacement`. On the SQL server that destroys the database holding every
approved request and every real amount Desicon has put through this platform.
The cosmetic fix costs the data.

This is §3c with the stakes made explicit: the plan does say so, in the form
Terraform always says it, and the whole finding of this document is that a
correct warning nobody reads is not a control.

**Remedy — a `CanNotDelete` lock.** Not as a reminder: the lock makes that
`terraform apply` *fail*. It is enforcement rather than documentation, which is
the distinction this checklist keeps turning on.

The scope is the whole question, and the first attempt got it wrong. See "The
lock was the wrong scope" below for what happened and for the command that
should actually be run.

- [x] ~~Apply the same lock to `rg-ddw-dev`. It is a separate system, but it
      serves `alerts.desiconapp.com` from an app called `app-ddw-dev-x6zi99`
      and carries the identical hazard~~ — **applied 5 Sep 2026 and then
      removed.** The recommendation was made from resource *names* alone. The
      tags, read a few minutes later, say `application: Desicon Digital
      Workplace`, `owner: ddw-platform-team`, `managed_by: terraform`. Locking
      another team's Terraform-managed estate without telling them turns their
      next replacement apply into a 409 they have no reason to connect to
      anything Finance did. If that group should be locked, it is their
      decision and their runbook
- [x] ~~Tag both groups `environment=production` so the portal contradicts the
      name at the point somebody reads it~~ — **applied 5 Sep 2026, and it had
      already been half-true.** `rg-desicon-fw-dev` was already carrying
      `cost_centre 1103`, `owner ICT` and `data_classification Confidential`.
      What it was carrying for `environment` was `dev`, because
      `locals.tags` in `environments/dev/main.tf` set `environment =
      var.environment`. So the one tag that mattered agreed with the misleading
      name rather than with the truth
- [x] ~~Add the tags to the Terraform so the next apply does not strip them~~ —
      **and this was not optional.** The azurerm provider writes `tags` as the
      complete set: the next `terraform apply` would have reset `environment`
      to `dev` and deleted `criticality` outright, with no warning and nothing
      in the plan that reads as a loss. A tag applied at the console is a tag
      with an expiry date nobody is told about — which is this document's
      recurring finding arriving one more time, in the remedy for it.
      `environment` is now hardcoded to `production` in the dev environment's
      locals, with the reason beside it
- [x] ~~Note that a lock also blocks *intended* destructive applies. That is the
      point, but it means the next legitimate teardown needs the lock removed
      first and put back after — write that into the deploy runbook rather than
      discovering it at the worst moment~~ — **discovered at the worst moment,
      about forty minutes after writing that line.** See below

### The lock was the wrong scope, and it took under an hour to prove it

5 September 2026. The resource-group lock went on. The very next
`terraform apply` — the one applying these tags — failed at the last step:

```
Error: deleting Firewall Rule ... sql-desicon-fw-dev
  409 ScopeLocked: ... cannot perform delete operation because following
  scope(s) are locked: '/subscriptions/.../rg-desicon-fw-dev'
```

Terraform was deleting `deployer-102-90-125-12`, the stale SQL firewall rule
for the previous deployer address, exactly as it should have been. Seventeen
resources took their tags; the eighteenth step could not clean up after itself.

**The scope was wrong, and wrong in the way that matters.** The deployer IP
rotates on this ISP (§7), so a stale firewall rule is deleted on *every* apply.
A resource-group lock therefore has to be removed and restored on every single
deployment. A control with that much friction is removed once and never put
back, which leaves the estate less protected than if it had never been applied
— the failure mode being *protected on paper only* is the one this entire
document exists to catch, arriving this time inside a remedy written on the
same afternoon.

The thing that must never be deleted is not the resource group. It is the
database. Locking that instead protects every approved request and every real
amount, and leaves tags, firewall rules, app settings and deployments to churn
freely. Deleting the server or the group still fails, because either delete
must remove the locked child to succeed.

```powershell
az lock create --name db-do-not-delete --lock-type CanNotDelete `
  --resource-group rg-desicon-fw-dev `
  --namespace Microsoft.Sql `
  --parent servers/sql-desicon-fw-dev `
  --resource-type databases `
  --resource-name DesiconFinanceWorkflow
```

`az lock create` takes `--resource`, not `--resource-id`, and a database is a
child resource, so it needs `--namespace` / `--parent` / `--resource-type`
spelled out. Noted because the first version of this line was written with
`--resource-id` and failed with `unrecognized arguments` — a runbook command
that has never been run is not a runbook.

- [ ] **Nothing is currently locked.** The group lock was removed to let the
      5 Sep apply finish and has not been replaced. Apply the database-scoped
      lock above
- [ ] Consider the same for `stdesiconfwdev`, once confirmed that nothing
      routinely churns its containers. That account holds the receipts and the
      quotations — which, since cash advance version 7 (§5g), are the evidence
      the Director of Finance authorises against. Losing it loses the proof,
      not just the file
- [ ] Whatever lock ends up in place, the deploy runbook must name it. A lock
      nobody documented is a deployment that fails at 6pm for a reason nobody
      on call can explain

---

## 6b. Twenty-three open pull requests, and what was in them

Triaged 6 September 2026. Three findings, in order of how much they matter.

### An `ignore` rule does not close a pull request that is already open

`FluentAssertions` major was ignored in `.github/dependabot.yml` on 24 August,
with §5f written beside it explaining that 8.x is not free for a commercial
company. **Two pull requests carrying `FluentAssertions 8.10.0` were still open
and still mergeable on 6 September** — `test-2525e3642d` and `test-6aab582b53`.

The rule governs what Dependabot raises next. It does nothing about what it has
already raised. So the control was written, was correct, was committed — and
the exact thing it existed to prevent sat one green Merge button away for three
weeks.

The same is true of `Microsoft.EntityFrameworkCore 9.0.18` and `dotnet-ef
10.0.10`, both ignored at major on 24 August, both still open.

- [ ] Close every pull request an ignore rule was written for. Adding the rule
      and stopping there is where this went wrong
- [ ] Whenever an ignore is added in future, close the matching PR in the same
      sitting. Noted at the top of `dependabot.yml` so the next person reads it
      before the mistake rather than after

### A SHA pin can carry a comment that lies about it

`scripts/check-action-pinning.mjs` enforces that every action is pinned to a
full 40-character SHA, and warns when a pin has no version comment — a bare SHA
being unreadable in review. It never checks that the comment is *true*, which
it cannot without asking GitHub.

Two of the open pull requests move `actions/checkout` and `azure/login` to new
SHAs while leaving the comments reading `# v4` and `# v2`. Merged as they
stand, the workflows would run one version while telling every future reviewer
they run another — and the pinning check would pass, because a comment is
present.

The pin is still doing its job: what runs is fixed and auditable. It is the
human-readable half that goes quietly wrong, which is the half people actually
read.

- [ ] Before merging either, confirm the SHA matches the tag the comment claims
      and correct the comment if not
- [ ] Consider having the check resolve the comment against the GitHub API. It
      needs a token and a network call in CI, so it is a real decision, not an
      obvious one

### Version drift inside the solution

`Azure.Identity` is at **1.11.4** in the API and **1.12.0** in Functions. The
Functions csproj carries a comment explaining its pin; the API's does not
explain why it is behind. Both are reached through the managed identity that
holds every credential this platform uses.

- [ ] Bring the two to the same version deliberately. `Azure.Identity-1.21.0`
      is open and does exactly this for the API side

---

## A note on how this list was built

Every item is something that was found by running the system rather than by
reading it. That is not a coincidence: each one is a control that existed on
paper — in a config file, a docstring, a package script, a documented role
table — and had never been executed.

The pattern is worth carrying forward. When adding to this list, prefer "has
anyone actually watched this work" over "is this implemented".
