# Roles and the route a request takes

Everyone who touches a request, what they can do, and where their authority
comes from. Written to be checked against how Desicon actually works — if a
line here is wrong, the process is wrong, not just the document.

As at 19 August 2026. Both modules are at workflow **version 4**.

---

## 1. Two kinds of authority

This matters more than it looks, because the two are maintained in different
places by different people.

**Role claims — held in Entra ID.** Four of them. Granted by an administrator,
carried in the sign-in token, and checked by the platform on every action.
Changing who holds one is an IT action.

**Org-chart authority — held in the platform's own employee table.** Line
manager, department head, requester, beneficiary. Nobody holds a "LineManager"
claim; the platform reads who reports to whom. Changing it is a data action.

A person needs **both** an employee record and, where relevant, a role claim.
A role claim with no employee record produces *"No active Employee record is
linked to Entra object id …"* and the person cannot use the system at all.

---

## 2. The four roles

| Role | Position at Desicon | Held by | Notified at |
|---|---|---|---|
| `CostControlOfficer` | Cost Control | Desicon Costcontrol — **desk account** | `costcontrol@desicongroup.com` |
| `FinanceManager` | Accounts Manager | Chima Onyealilachi | `chima.onyealilachi@desicongroup.com` |
| `DirectorOfFinance` | DMD | Tomy John | `tomy.john@desicongroup.com` |
| `TreasuryOfficer` | Accounts Officer / Treasury | Desicon Treasury — **desk account** | `treasury@desicongroup.com` |

**Two of the four are desks, not people.** Cost Control and Treasury are shared
sign-in accounts; the Accounts Manager and the Director of Finance are named
individuals. That is a deliberate decision, recorded in `docs/15` §1c, and its
consequence is worth restating here because this is the table people will read:

Every attribution column — `PostedByUserId`, `AuthorisedByUserId`, and every
row of the hash-chained audit log — records the **account** that acted. For
Chima and Tomy that names a person. For Cost Control and Treasury it names a
desk, and the platform cannot say which of the people sharing that login did
the work.

The maker-checker guards compare account identity, so the separations in §7
still hold between the four roles. What they cannot do is separate two people
who share one of them.

**What each may do**

- **Cost Control** — verify that a claim or advance is costed to the right cost
  centre or project, check receipts, capture the Treasury number. Cannot post
  to Business Central, cannot release cash, cannot pay.
- **Accounts Manager** — approve on behalf of Accounts, confirm refunds, write
  off an unretired advance. Cannot release money.
- **Director of Finance** — final approval before any payment or cash release.
  Cannot approve his own claim.
- **Treasury** — post the approved request in Business Central and record the
  document number, execute payment, release cash. Cannot verify the costing
  they will go on to post.

`FinanceOfficer` existed until workflow version 3 and covered Cost Control and
Treasury as one role. It is now held by nobody and can be deleted.

---

## 3. Org-chart positions

| Position | Source | Used for |
|---|---|---|
| Requester | raises their own request | — |
| **Head of Department** | `Departments.DepartmentHeadId` | **the one approval before Cost Control** |
| Line manager | `Employees.LineManagerId` | no longer an approval step. Still used to decide who may *see* a request, and who is copied when a cash advance goes overdue |
| Beneficiary | chosen at capture | receives the money, confirms receipt |

**Version 4 removed the line-manager approval.** Desicon has one approval on
the requesting side, so modelling two meant the same person approving the same
request twice — and a step people treat as ceremony is not a control.

---

## 4. Expense claim — DEL-AC-FRM-002

Money already spent: a reimbursement, or the retirement of an advance.

| # | State | Who acts | What they do |
|---|---|---|---|
| 1 | `DRAFT` | Requester | Complete the form, attach receipts, submit |
| 2 | `DEPT_HEAD` | **Head of Department** | Approve. Cannot approve their own claim. Last step on the requesting side |
| 3 | `COST_CONTROL_VERIFY` | **Cost Control** | Capture Treasury number, confirm costing. Blocked without at least one receipt attached |
| 4 | `FINANCE_APPROVE` | **Accounts Manager** | Approve. Cannot approve their own claim |
| 5 | `DMD_APPROVAL` | **Director of Finance** | Authorise the payment |
| 6 | `AWAITING_POSTING` | **Treasury** | Post in Business Central, record the BC document number |
| 7 | `AWAITING_PAYMENT` | **Treasury** | Execute payment, record reference and date |
| 8 | `AWAITING_ACK` | Beneficiary | Confirm receipt → **Closed** |

**Branches at the Accounts Manager**, decided by the arithmetic and not by
anyone's choice:

| Net payable | Meaning | Route |
|---|---|---|
| Positive | The company owes the claimant | → DMD → Treasury posts → Treasury pays |
| Zero | Advance matched spend exactly | → Treasury posts → **Closed**. No DMD: no money moves |
| Negative | The claimant owes a refund | → Accounts Manager confirms the refund → Treasury posts |

The acknowledgement step has no equivalent on paper. It is why a claim cannot sit marked-paid and
unreceived.

---

## 5. Cash advance — DEL-AC-FRM-003

Money before it is spent.

| # | State | Who acts | What they do |
|---|---|---|---|
| 1 | `DRAFT` | Requester | Complete the form, submit. Blocked if they have an overdue advance |
| 2 | `DEPT_HEAD` | **Head of Department** | Approve. Cannot approve their own request |
| 3 | `COST_CONTROL_VERIFY` | **Cost Control** | Capture Treasury number, confirm costing |
| 4 | `FINANCE_APPROVE` | **Accounts Manager** | Approve |
| 5 | `DMD_APPROVAL` | **Director of Finance** | Authorise. Unconditional — an advance always pays somebody |
| 6 | `AWAITING_POSTING` | **Treasury** | Post in Business Central |
| 7 | `CASH_RELEASE` | **Treasury** | Release the cash. Starts the retirement clock |
| 8 | `AWAITING_ACK` | Requester | Confirm receipt of the cash |
| 9 | `OUTSTANDING` | Requester | Retire it — see below |

Retirement clock: **24 working hours** in-station, **72 working hours**
out-of-station, from cash release.

An unretired advance can be written off by the **Accounts Manager**, which is
terminal and reported.

---

## 6. Retirement

Retirement is not a separate form. From **My Advances → Retire**, the platform
opens an expense claim already linked to the advance, with *Less Advance Taken*
filled in. The requester states what was purchased and attaches the receipts.

It then follows the expense route in section 4, and where it ends depends on
the arithmetic:

- **Spent more than the advance** → DMD authorises the difference, Treasury pays
- **Spent exactly the advance** → Treasury posts in BC, closed. The DMD is not
  involved: nothing moves
- **Spent less** → Accounts Manager confirms the refund, Treasury posts

**The DMD has nothing to do with retirement** unless money is going out.

---

## 7. Separation of duties, as enforced

| Rule | Enforced where |
|---|---|
| Nobody approves their own request | Guard on every approval step |
| A returned request goes back to the approver who returned it | `RESUBMIT` targets `DEPT_HEAD` |
| Cost Control cannot post what it verified | Separate roles since version 3 |
| Treasury cannot verify the costing it will post | Separate roles since version 3 |
| The DMD cannot approve payment of his own claim | Guard on `DMD_APPROVAL` |
| Whoever set a beneficiary's bank details cannot pay against them | Guard on `EXECUTE_PAYMENT` |
| Only the beneficiary can close a paid claim | `AWAITING_ACK` |

**Not enforced, and known:** what Treasury actually enters in Business Central
is not checked against what was approved. BC does not provide maker-checker and
this platform cannot see the ledger. See `docs/15` §4.

---

## 8. Decisions taken, and what still needs one

### Answered

**Heads of Department do not raise requests.** Confirmed 19 August 2026.

This is an assumption the system depends on rather than one it enforces, so it
is worth writing down what happens if it turns out not to hold. An HOD who
raises a request has it routed to `DEPT_HEAD`, where the approver resolves to
themselves. The self-approval guard refuses, correctly — so the request sits
open with the approve button disabled and the reason shown, and **nobody else
can move it either**. It is recoverable only by changing that department's head
or reassigning the request.

Not silent, then, but not survivable in normal use. If an HOD ever needs to
claim an expense, the answer is to record a different approver for them —
a peer head or someone above — before they raise it, not after.

**Seven requesting accounts and six Heads of Department are loaded** from
`org-chart.csv` via `scripts/import-org-chart.ps1`. Four of the seven
requesters are desk accounts (`ictadmin@`, `logistics@`, `genservices@`,
`hr@`), accepted deliberately — claims are attributed to the desk, not the
person who raised them.

**Wilson Obarueroro heads two departments** (Project and PMC). Supported, and
worth knowing because both departments queue on one person.

### Still open

Each of these changes configuration rather than code.

1. **Every department that will raise requests needs a head recorded**, or its
   claims stop at the first approval. Seven are covered; a department added
   later without a head fails this way.
2. **Department heads are seeded by hand.** There is no HR feed. `department`
   is populated in Entra but `manager` is not, so the head of each department
   has to be recorded here and kept current. It decides who can approve what.

2b. **When a Head of Department misses their SLA, the request escalates into
   Cost Control's queue** — not to another named person, because there is no
   tier above the HOD in the model. Anyone holding the Cost Control role can
   then take it. If Desicon wants it to reach a specific person instead, that
   is a definition change.
3. **Cost Control and Treasury are shared desk accounts.** Actions are
   attributed to the desk, not to a person. Accepted deliberately — see
   `docs/15` §1c. Both roles are queues: anyone holding one can act on
   anything in it.
5. **Should the DMD approve every payment regardless of size?** There is no
   threshold today. Every payment reaches him.
6. **Who covers each role on leave?** Delegation exists in the platform but no
   delegations are configured.
7. **Is the Treasury number unique per request?** Currently unvalidated — two
   claims can carry the same one.
