# 01 — Form Analysis and Field Mapping

Derived from the actual contents of `DEL-AC-FRM-002 Rev 05` and `DEL-AC-FRM-003 Rev 05`.

---

## Part A — What the forms revealed

Fourteen findings. Items 1, 2 and 11 are structural: they change the workflow and data model, not just the field list.

### 1. The Expense Form *is* the Cash Advance retirement document

The brief treats Expense Reimbursement and Cash Advance as two independent modules. The forms say otherwise. `DEL-AC-FRM-002` contains:

```
Cash Advance Taken (₦,$,£,€,¥): ______________
...
Total
Less Advance Taken
Net Payable
```

An employee who took an advance retires it by filing an Expense Form that nets the advance off. The two modules are one lifecycle. This produces three outcomes the brief has no state for:

| Net Payable | Meaning | Required behaviour |
|---|---|---|
| **> 0** | Employee spent more than the advance | Reimburse the difference; advance fully retired |
| **= 0** | Spend matched advance exactly | No payment moves; advance fully retired; request still must reach Closed |
| **< 0** | Employee spent less than the advance | **Employee owes the company a refund.** Needs a `Refund Due` state, a cash-return receipt, and confirmation by Accounts before the advance can be marked retired |

The `Net Payable < 0` path is entirely absent from the original brief and is the one most likely to be quietly skipped in the paper process. It is where money actually leaks.

**Implication:** `ExpenseRequest` carries a nullable `RetiresAdvanceId` foreign key to `CashAdvanceRequest`. Retirement status is computed from linked expense claims, not entered by hand.

### 2. Retirement is time-bound by station scope, not by a typed-in date

Printed on `DEL-AC-FRM-003`:

> retire within 24 hours for transactions within local station state and 72 hours for transactions out of station state. This transaction will be the liability of recipient, till it will be justified.

The brief asked for a free-text "Required Date". The form encodes a **rule**. So:

- New field `StationScope` — enum `InStation` / `OutOfStation`, mandatory
- `RetirementDueDate` is **computed**, never entered: `AcknowledgedAt + (24h | 72h)`
- The liability clause means an unretired advance is a **receivable against the employee**. The system needs an employee advance-liability balance, and a policy switch blocking new advance requests while an overdue one exists.

### 3. Three different reference numbers, three different owners

| Number | Assigned by | When | On which form |
|---|---|---|---|
| Request Number (new) | System | On submission | Both |
| **TREAS. No.** | Treasury | On receipt into Treasury | Both |
| **JV No.** | Accounts (ACF) | At GL posting | Both |

These are not aliases. A design that auto-generates one reference and calls it done will break reconciliation against Treasury and Accounts records. All three are stored, all three are searchable, and TREAS./JV numbers are captured at their respective workflow stages by the role that owns them.

### 4. Maker–checker is already in the paper process

The ACF block on both forms has **`Inputer's sign`** and **`Authoriser's sign`** as separate signatures. This is deliberate segregation of duties at the posting stage. The digital system must enforce that `PostedByUserId != AuthorisedByUserId`, hard, with no self-approval — and treat any exception as an auditable event rather than a config toggle.

### 5. The forms carry a general ledger posting block

`DEL-AC-FRM-002`: `DR/CR`, `Account No.`, `Amount`, `JV No.`
`DEL-AC-FRM-003`: `DEBIT ___ A/C No. ___ REF ___`, `CREDIT BENEFICIARY ___ A/C No. ___`, `JV No.`

The platform is not only an approval router; it is the origination point of a journal entry. Phase 1 stores and validates these lines (debits must equal credits before the request can be posted) and exports the payload. Phase 2 pushes it to the accounting system.

### 6. Receipt attachment status is tri-state, not boolean

`Attached receipts: [Yes] [No] [Incomplete]`

"Incomplete" is the interesting one — it is the state that generates a *return for correction* rather than a rejection. The workflow therefore needs `ReturnedForCorrection` as a state distinct from `Rejected`: the request goes back to the requester, keeps its number and history, and re-enters the flow at the stage it left. A rejection is terminal.

### 7. Cost allocation is per line item, and mutually exclusive

`DEL-AC-FRM-002` puts `Project Code` and `Cost Center Code` **inside the line-item table**, under "Specific Expense Category". `DEL-AC-FRM-003` makes it a mutually exclusive tick:

```
[ ] Projects Specific        Project Code: ______
[ ] Non Projects Specific    Cost Center Code: ______
```

So: allocation lives on the line, not the header, and exactly one of {ProjectCode, CostCentreCode} must be populated per line. Validation rule, enforced server-side.

### 8. Dual currency at line level, with a minor-unit column

`DEL-AC-FRM-002` line items carry **both** `Foreign Currency Amount ($/£/€/¥)` and `Local Currency Amount (NGN)`.
`DEL-AC-FRM-003` splits amounts into `₦/$/£/€/¥` and **`k/¢/p`** — major and minor units in separate columns.

Design consequence: store a single `decimal(18,2)` per line plus `CurrencyCode`, `FxRate`, `FxRateDate`, and a computed `AmountNgn`. The paper minor-unit column is a presentation artefact of hand-written forms; digitally it is decimal places. Never store money as float, and never re-derive a historical FX rate at read time.

### 9. Amount in Words is an anti-tamper control

Both forms require it. On paper it stops a "10,000" becoming "110,000". Digitally the amount cannot be altered post-submission anyway, but the field must still be **auto-generated and rendered on the PDF** — because the printed PDF is what gets filed, and auditors expect it. Nigerian convention: Naira and Kobo, e.g. *"Fourteen Thousand Naira Only"* (the sample data in your Cash Advance form reads "Fourteen Thousand" against a total of 14,000).

### 10. There is a payment-routing threshold on the form

> Amount above NGN 30,000 net payable will be transferred to employee bank account

This explains the form title: `EXPENSE FORM - (CASH) (BANK)`. Payment method is derived, not chosen:

```
PaymentMethod = NetPayableNgn > PolicyThreshold ? BankTransfer : Cash
```

Held as a policy value in configuration, not a constant in code, with an effective-date history so old requests reprint with the threshold that applied at the time.

### 11. Recipient's Acknowledgement is what actually closes a request

Both forms end with a `RECIPIENT'S ACKNOWLEDGEMENT` block — Name, Signature, Date, "Cash received by me". This is the proof-of-receipt that closes the loop.

This matters more than it looks. In your own correspondence you cite expense sheets unpaid for over 18 months, where nobody could say whether an item was paid, awaiting funds, queried, or overlooked. The fix is precisely this: **`Paid` is not `Closed`.** A request marked Paid by Finance sits in `AwaitingAcknowledgement` until the beneficiary confirms receipt. Anything sitting there past SLA is a red item on the dashboard — it is exactly the "claimed as paid but the employee never saw it" case, and it becomes visible on day one instead of month eighteen.

### 12. The beneficiary is not always the requester

> Please issue payment in favour of company/staff

Payee can be a staff member or a third-party company. `Beneficiary` is therefore its own entity with a type (`Employee` / `Vendor` / `Other`) and its own bank details — not a string on the request, and not assumed to be the submitter.

### 13. The forms are QMS-controlled documents

`DEL-AC-FRM-002 Rev 05` decomposes as **DEL** (Desicon Engineering Limited) · **AC** (Accounts) · **FRM** (Form) · **002** · **Rev 05**. That is ISO 9001 document control. Two consequences:

- The digital form template carries `FormCode` and `Revision`, and **every stored submission records the revision it was captured under**, so a request from 2026 reprints in its 2026 layout even after Rev 06 ships.
- Publishing a new revision is a controlled action with its own approval, not a deployment side-effect.

### 14. Paper has three signature blocks; the brief specifies six stages

The forms show **Requested by → Verified by → Approved by**, plus **Endorsed by** in the ACF block. The brief specifies Employee → Line Manager → Department Head → Finance → Payment → Closed.

These are reconcilable, and the reconciliation matters for adoption: run the engine's longer stage list underneath, but **label the UI and the printed PDF with the paper terms** users already know. An approver sees "Verify" on screen because that is the word on the form they have signed for years.

| Paper block | Engine stage(s) |
|---|---|
| Requested by | `Submitted` (requester identity, auto-filled) |
| Verified by | `LineManagerApproval` → `DepartmentHeadApproval` |
| Approved by | `FinanceApproval` |
| Endorsed by (ACF) | `FinancePosting` (inputer) + `FinanceAuthorisation` (authoriser) |
| Recipient's Acknowledgement | `AwaitingAcknowledgement` → `Closed` |

---

## Part B — Field mapping: `DEL-AC-FRM-002 Rev 05` (Expense Form)

### Header

| Form label | System field | Type | Source | Notes |
|---|---|---|---|---|
| *(none — new)* | `RequestNumber` | string(20) | System | `EXP-2026-000123` |
| `DEL-AC-FRM-002` | `FormCode` | string(20) | Template | Displayed, immutable |
| `Rev 05` | `FormRevision` | string(10) | Template | Stamped on submission |
| `TREAS.No.` | `TreasuryNumber` | string(30) | Treasury | Captured at Finance stage |
| `EXPENSE FORM - (CASH) (BANK)` | `PaymentMethod` | enum | Derived | `Cash` / `BankTransfer`, from threshold |
| `Name of the Beneficiary` | `BeneficiaryId` → `Beneficiary` | FK | User | Employee, Vendor or Other |
| `Cash Advance Taken (₦,$,£,€,¥)` | `RetiresAdvanceId`, `AdvanceAmountNgn` | FK, decimal | User selects advance | Picker of the requester's open advances |

### Line items (rows 1–11 on paper, unbounded digitally)

| Form label | System field | Type | Notes |
|---|---|---|---|
| `S/n` | `LineNumber` | int | Auto-sequenced |
| `Description` | `Description` | string(500) | Required |
| `Project Code` | `ProjectCode` | string(20) | XOR with cost centre |
| `Cost Center Code` | `CostCentreCode` | string(20) | XOR with project |
| `Foreign Currency Amount $/£/€/¥` | `Amount`, `CurrencyCode`, `FxRate`, `FxRateDate` | decimal(18,2), char(3), decimal(18,6), date | |
| `Local Currency Amount NGN` | `AmountNgn` | decimal(18,2) | Computed, persisted |
| *(new)* | `ExpenseCategoryId` | FK | Not on paper; needed for reporting |
| *(new)* | `ExpenseDate` | date | Not on paper; needed for period cut-off |

> Two fields are additions rather than mappings. `ExpenseCategoryId` is required by the brief's reporting goals and cannot be reconstructed from free text. `ExpenseDate` is needed to stop a June expense landing in the July ledger. Both should go into the next paper revision so the two stay in step.

### Totals

| Form label | System field | Derivation |
|---|---|---|
| `Total` | `TotalAmountNgn` | `SUM(lines.AmountNgn)` |
| `Less Advance Taken` | `AdvanceAmountNgn` | From linked advance |
| `Net Payable` | `NetPayableNgn` | `Total − Advance` (signed) |
| `Amount in Words` | `AmountInWords` | Auto-generated on the PDF |

### Controls and approvals

| Form label | System field | Notes |
|---|---|---|
| `Attached receipts: Yes / No / Incomplete` | `ReceiptStatus` | Tri-state; `Incomplete` → return for correction |
| `Requested by` (Name, Dept, Sign/Date) | `RequesterId`, `SubmittedAt` | From Entra ID identity |
| `Verified by` (Name, Dept, Sign/Date) | `ApprovalAction` rows | Electronic approval, timestamped |
| `Approved by` (Name, Dept, Sign/Date) | `ApprovalAction` rows | |
| `Endorsed by` | `EndorsedByUserId` | ACF stage |
| `JV No.` | `JournalVoucherNumber` | Accounts |
| `DR/CR`, `Account No.`, `Amount` | `GlPostingLine[]` | Debits must equal credits |
| `Inputer's Sign` | `PostedByUserId` | ≠ Authoriser |
| `Authoriser's Sign` | `AuthorisedByUserId` | ≠ Inputer |
| `Cash received by me` (Name, Signature, Date) | `AcknowledgedByUserId`, `AcknowledgedAt` | Closes the request |

---

## Part C — Field mapping: `DEL-AC-FRM-003 Rev 05` (Cash Advance Form)

| Form label | System field | Type | Notes |
|---|---|---|---|
| *(new)* | `RequestNumber` | string(20) | `ADV-2026-000456` |
| `DEL-AC-FRM-003` / `Rev 05` | `FormCode`, `FormRevision` | | |
| `TREAS. No.` | `TreasuryNumber` | string(30) | |
| `Date` | `RequestDate` | date | |
| `Please approve a Cash Advance for the underlisted expense(s)` | `Purpose` | string(1000) | |
| `s/n` | `LineNumber` | int | 6 rows on paper, unbounded digitally |
| `Description` | `Description` | string(500) | |
| `₦/$/£/€/¥` + `k/¢/p` | `Amount`, `CurrencyCode` | decimal(18,2), char(3) | Minor unit becomes decimal places |
| `Total` | `TotalAmountNgn` | decimal(18,2) | |
| *(words row)* | `AmountInWords` | | Auto-generated |
| `[ ] Projects Specific` / `[ ] Non Projects Specific` | `AllocationType` | enum | Drives which code is required |
| `Project Code` | `ProjectCode` | string(20) | Required if `AllocationType = Project` |
| `Cost Center Code` | `CostCentreCode` | string(20) | Required if `AllocationType = CostCentre` |
| `Attached documentation: Yes / No` | `HasSupportingDocuments` | bool | |
| *(new — from the 24h/72h clause)* | `StationScope` | enum | `InStation` / `OutOfStation` |
| *(new — computed)* | `RetirementDueDate` | datetime2 | `AcknowledgedAt + 24h or 72h` |
| `Requested by` / `Verified by` / `Approved by` | `ApprovalAction` rows | | |
| `Endorsed by` | `EndorsedByUserId` | | |
| `JV No.` | `JournalVoucherNumber` | | |
| `DEBIT ___ A/C No. ___ REF ___` | `GlPostingLine` (debit) | | |
| `CREDIT BENEFICIARY ___ A/C No. ___` | `GlPostingLine` (credit) | | |
| `Inputers sign` / `Authorisers sign` / `Date` | `PostedByUserId`, `AuthorisedByUserId`, `PostedAt` | | Must differ |
| `Cash received by me` (Name, Signature, Date) | `AcknowledgedByUserId`, `AcknowledgedAt` | | **Starts the retirement clock** |
| *(new — computed)* | `RetirementStatus` | enum | `NotDue` / `Due` / `Overdue` / `PartiallyRetired` / `FullyRetired` |
| *(new — computed)* | `RetirementBalanceNgn` | decimal(18,2) | `Total − SUM(linked expense claims)` |

---

## Part D — Procurement Requisition

No form was supplied. Until `DEL-AC-FRM-004` (or equivalent) arrives, the module is designed from the brief alone, in the house style of the other two: same header block (form code, revision, TREAS. No.), same three-signature layout, same ACF block, same acknowledgement footer — with `Vendor`, `Quantity`, `Unit Price`, `Budget Code`, `Required Date`, and quotation/specification attachments.

**Please send the real form if one exists.** The two forms you did send changed the design substantially — the advance/expense linkage, the 24/72 rule, and the acknowledgement-closes-the-loop finding all came from reading them rather than from the brief. It is reasonable to expect a procurement form would do the same.
