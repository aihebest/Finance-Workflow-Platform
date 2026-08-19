# Roles and the route a request takes

Everyone who touches a request, what they can do, and where their authority
comes from. Written to be checked against how Desicon actually works — if a
line here is wrong, the process is wrong, not just the document.

As at 10 August 2026.

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

| Role | Position at Desicon | Held by (dev) | Notified at |
|---|---|---|---|
| `CostControlOfficer` | Cost Control | Olanrewaju Atanda | `costcontrol@desicongroup.com` |
| `FinanceManager` | Accounts Manager | Chima Onyealilachi | `chima.onyealilachi@desicongroup.com` |
| `DirectorOfFinance` | DMD | Tomy John | `tomy.john@desicongroup.com` |
| `TreasuryOfficer` | Accounts Officer / Treasury | Treasury (shared account) | `treasury@desicongroup.com` |

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

| Position | Who, in dev | Source |
|---|---|---|
| Requester | anyone with an employee record | raises their own |
| Line manager | Best Aihebholoria | `Employees.LineManagerId` |
| Department head | ICT Admin | `Departments.DepartmentHeadId` |
| Beneficiary | whoever is named on the claim | chosen at capture |

---

## 4. Expense claim — DEL-AC-FRM-002

Money already spent: a reimbursement, or the retirement of an advance.

| # | State | Who acts | What they do |
|---|---|---|---|
| 1 | `DRAFT` | Requester | Complete the form, attach receipts, submit |
| 2 | `LINE_MANAGER` | Line manager | Verify. Cannot verify their own claim |
| 3 | `DEPT_HEAD` | Department head | Approve. Last step on the requesting side |
| 4 | `COST_CONTROL_VERIFY` | **Cost Control** | Capture Treasury number, confirm costing. Blocked without at least one receipt attached |
| 5 | `FINANCE_APPROVE` | **Accounts Manager** | Approve. Cannot approve their own claim |
| 6 | `DMD_APPROVAL` | **Director of Finance** | Authorise the payment |
| 7 | `AWAITING_POSTING` | **Treasury** | Post in Business Central, record the BC document number |
| 8 | `AWAITING_PAYMENT` | **Treasury** | Execute payment, record reference and date |
| 9 | `AWAITING_ACK` | Beneficiary | Confirm receipt → **Closed** |

**Branches at step 5**, decided by the arithmetic and not by anyone's choice:

| Net payable | Meaning | Route |
|---|---|---|
| Positive | The company owes the claimant | → DMD → Treasury posts → Treasury pays |
| Zero | Advance matched spend exactly | → Treasury posts → **Closed**. No DMD: no money moves |
| Negative | The claimant owes a refund | → Accounts Manager confirms the refund → Treasury posts |

Step 9 has no equivalent on paper. It is why a claim cannot sit marked-paid and
unreceived.

---

## 5. Cash advance — DEL-AC-FRM-003

Money before it is spent.

| # | State | Who acts | What they do |
|---|---|---|---|
| 1 | `DRAFT` | Requester | Complete the form, submit. Blocked if they have an overdue advance |
| 2 | `LINE_MANAGER` | Line manager | Verify |
| 3 | `DEPT_HEAD` | Department head | Approve |
| 4 | `COST_CONTROL_VERIFY` | **Cost Control** | Capture Treasury number, confirm costing |
| 5 | `FINANCE_APPROVE` | **Accounts Manager** | Approve |
| 6 | `DMD_APPROVAL` | **Director of Finance** | Authorise. Unconditional — an advance always pays somebody |
| 7 | `AWAITING_POSTING` | **Treasury** | Post in Business Central |
| 8 | `CASH_RELEASE` | **Treasury** | Release the cash. Starts the retirement clock |
| 9 | `AWAITING_ACK` | Requester | Confirm receipt of the cash |
| 10 | `OUTSTANDING` | Requester | Retire it — see below |

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
| Nobody verifies or approves their own request | Guard on every approval step |
| Cost Control cannot post what it verified | Separate roles since version 3 |
| Treasury cannot verify the costing it will post | Separate roles since version 3 |
| The DMD cannot approve payment of his own claim | Guard on `DMD_APPROVAL` |
| Whoever set a beneficiary's bank details cannot pay against them | Guard on `EXECUTE_PAYMENT` |
| Only the beneficiary can close a paid claim | `AWAITING_ACK` |

**Not enforced, and known:** what Treasury actually enters in Business Central
is not checked against what was approved. BC does not provide maker-checker and
this platform cannot see the ledger. See `docs/15` §4.

---

## 8. Questions to put to Desicon

Each of these is a place where dev necessarily differs from the real
organisation, and where the answer changes configuration rather than code.

1. **One department exists** — ICT, headed by ICT Admin. Every department that
   will raise requests needs a head recorded, or its claims stop at step 3.
2. **Line-manager chains are seeded by hand.** There is no HR feed. Every
   approver's reporting line has to be entered and kept current, and it decides
   who can approve what.
3. **Treasury is a shared account.** Actions are attributed to the desk, not to
   a person. Accepted deliberately — see `docs/15` §1c.
4. **Is Cost Control one person or a team?** The role is a queue; anyone
   holding it can act on anything in it.
5. **Should the DMD approve every payment regardless of size?** There is no
   threshold today. Every payment reaches him.
6. **Who covers each role on leave?** Delegation exists in the platform but no
   delegations are configured.
7. **Is the Treasury number unique per request?** Currently unvalidated — two
   claims can carry the same one.
