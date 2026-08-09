# The Desicon process, as described

Recorded 8 August 2026, from Aihe's account of how cash advances, expense
claims and retirements actually move through Desicon today. This is the source
for workflow definition **version 2**, and it corrects version 1 in two
material ways.

Everything here is a claim about the real process, not about the software. If
any of it is wrong, the definitions are wrong, and this file is where to fix it
first.

---

## What changed, and why it matters

Version 1 was built from the paper forms — DEL-AC-FRM-002 and DEL-AC-FRM-003 —
and from `docs/01`. It got the approval chain broadly right and two things
badly wrong.

**GL posting is not done here.** It is done in Microsoft Dynamics Business
Central. Version 1 modelled a journal: a debit/credit grid, a balance check, an
inputer and a separate authoriser. All of that duplicates BC. This platform's
job is to carry the approval trail and *tell the Accounts Officer there is
something waiting for her to post* — that, in Aihe's words, is the conclusion of
the system.

**There was no Director of Finance.** No money moves at Desicon without the
DMD's approval. Version 1 let the Accounts Officer execute payment on the
Accounts Manager's approval alone. That is the single most important control in
the process and it was absent from the state machine entirely.

---

## The chain

### Requesting side

1. **Requester** raises the request and signs.
2. **Line manager** verifies.
3. **Department head** approves. This is the final approval on the requesting
   side; everything after it happens inside Accounts.

### Accounts

4. **Cost Control verification.** The record is taken and checked as costed to
   the right cost centre, and updated in BC accordingly. Cost Control sit with
   Accounts, in the same office, and are the same people who post later — their
   BC work at posting is simply deeper than the costing check here. The state is
   named `COST_CONTROL_VERIFY` for the work, not for a separate team.
5. **Accounts Manager** approves. This releases the request for treatment. It
   does not release money.

### Payment gate

6. **Director of Finance (DMD).** Final approval before any payment. No cash
   moves without it, and no other role substitutes for it — an Accounts Manager
   holding every other Finance role still cannot give this approval.

   **Conditional on money actually leaving.** The DMD has nothing to do with
   retirement, so the gate applies only where a payment follows:

   | Net payable | Path |
   |---|---|
   | Positive — owed to the employee | DMD → post → pay → acknowledge → closed |
   | Zero — retirement balanced exactly | post → closed. No DMD. |
   | Negative — employee owes money back | refund confirmed by Accounts Manager → post → closed. No DMD. |

   The zero case is the *ordinary* outcome of a retirement, not an edge case:
   most people spend roughly what they took. Without this branch, the majority
   of retirements would queue on the DMD's desk asking him to authorise a
   payment of nothing.

### Posting and payment

7. **Accounts Officer posts in Business Central**, then records here that she
   has, and under which BC document number. Required, not optional — that number
   is the only key joining this approval trail to the ledger entry. Without it,
   reconciling the two systems means matching on amounts and dates, which stops
   working the first time two people claim the same sum on the same day.
8. **Payment** is executed and its reference recorded.
9. **Recipient acknowledges** receipt, or disputes it.

Step 9 is not part of the manual process. It is here deliberately: claims
marked paid but never received are how items sat unresolved for eighteen
months. Finance recording a payment is not the same as the money arriving, and
only the beneficiary can close that gap.

### Retirement

A retirement runs the identical chain from the beginning — same approvals,
ending with the Accounts Manager — and then goes back to the Accounts Officer,
who retires it in BC. **The DMD is not involved.** Whether money is owed to the
employee or back to the company, the Accounts Manager's approval is sufficient.

In this platform a retirement *is* an expense claim linked to an advance
(`RetiresAdvanceId`), which is why the conditional DMD branch above does the
work: the same definition serves both, and the net payable decides.

---

## Where the boundary sits

| This platform owns | Business Central owns |
|---|---|
| Who approved what, when, and in what order | The general ledger |
| The hash-chained audit trail | Journal lines, debits and credits |
| SLA clocks, reminders, escalation | Cost centre postings |
| Telling the next person it is their turn | The financial record of record |
| The BC document number as the join between the two | |

Consequence worth stating plainly: **the inputer/authoriser separation moved to
BC along with the posting.** Version 1 enforced that whoever entered a journal
could not authorise it. This platform no longer sees journals, so it can no
longer enforce that. BC has to. Worth confirming BC does, because on the paper
form it was two signature boxes and somebody thought it mattered enough to
design in.

---

## Roles

Three Entra app roles, created by `scripts/bootstrap-app-roles.ps1`:

| Role | Who | Steps |
|---|---|---|
| `FinanceOfficer` | Cost Control / Accounts Officer | 4, 7, 8 |
| `FinanceManager` | Accounts Manager | 5, and refund confirmation |
| `DirectorOfFinance` | DMD | 6 |

Line manager and department head authority is **not** a role claim. It comes
from the org chart — `Employees.LineManagerId` and
`Departments.DepartmentHeadId` — resolved by `EmployeeActorResolver`. See the
implementation-status note in `docs/04`.

Aihe noted that payment is made by "the account personnel in charge of
payment". Currently modelled as `FinanceOfficer`, the same role that posts. If
that is a distinct person, splitting it is a one-line change to each definition.

---

## Notifications

Every responsible party is notified. Because roles reach the API only as a
token claim and nothing records who holds one, role recipients resolve through
`NotificationOptions.RoleMailboxes` — a configured mailbox per role, set in
`infra/terraform/environments/dev/main.tf`.

A role with no configured mailbox resolves to nobody and its message is parked
with the role named in `LastError`. That is deliberate. A finance approval
nobody was told about is the failure this system exists to prevent, so silence
is the one answer it must never give.

`Notifications:UseGraph` is `false` in dev: messages are written to Application
Insights rather than sent. Turning it on is a decision about real inboxes.

Outstanding: a daily digest for the DMD, listing everything awaiting his
approval. He is the single gate on all payments and gets a lot of mail.

---

## Assumptions I made, which may be wrong

Listed because each one shaped a state machine, and a wrong one is cheaper to
find here than in UAT.

1. **Cost Control and the Accounts Officer are the same role.** Aihe: "I think
   it's the same... but their job in BC is deeper than that." Modelled as one
   role, two states.
2. **The DMD approves before posting**, not after. His approval authorises the
   payment decision; the BC entry executes it.
3. **Posting and payment are two steps**, so the platform can show anything
   posted but not yet paid.
4. **A retirement that pays nobody closes at posting** rather than passing
   through a payment queue. A queue containing items that can never be actioned
   stops being read, and then the ones that matter get missed in it.
5. **Acknowledgement is kept** despite not being in the manual process.

---

## Known gap: definition versions are not pinned

Both files carry `"version": 2`. Nothing consumes it.

`IWorkflowDefinitionProvider.GetAsync` resolves by `moduleKey` alone, and
`Request` has no `DefinitionVersion` column, so every request — including ones
raised weeks ago — is evaluated against whatever definition is deployed now. A
request sitting in a state that a new version removes has no transitions out of
it: `TransitionsFrom` returns an empty list, no error is raised, and the request
becomes invisible to everyone who could have acted on it.

This is not hypothetical. `EXP-2026-000005` was stranded in `AUTHORISATION` by
this very change, and `EXP-2026-000004` was stranded earlier by an org-chart
edit that moved its resolved actor. Two requests, two causes, one root: the
platform assumes definitions and reporting lines hold still, and neither does.

Acceptable in dev. **Not acceptable before UAT**, because Desicon will change
these definitions — that is the entire point of a definition-driven engine.
Options are recorded in `docs/12-Decision-Log.md`.
