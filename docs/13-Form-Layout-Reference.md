# 13 — Form layout reference

Extracted from the controlled spreadsheets (`DEL-AC-FRM-002 Rev 05`,
`DEL-AC-FRM-003 Rev 05`) so the capture screens can be built and reviewed
without the source files to hand.

`docs/01` maps fields to the data model. This records **layout**: section
order, groupings, column headings and the exact wording on the page. The
build plan's acceptance test is that a clerk who has filled the form for
eight years recognises it in one second, and that is a claim about
arrangement, not about field names.

---

## DEL-AC-FRM-002 Rev 05 — Expense Form

```
                EXPENSE FORM -  (CASH) (BANK)              DEL-AC-FRM-002
                                                                   Rev 05
                                                        TREAS.No. ________

Name of the Beneficiary: ______________________________
Cash Advance Taken (₦,$,£,€,¥): ________________________

Details of Expense
┌─────┬──────────────┬───────────────────────────┬────────────┬────────────┐
│ S/n │ Description  │ Specific Expense Category │  Foreign   │   Local    │
│     │              │ (Pls indicate Project/    │  Currency  │  Currency  │
│     │              │  Cost center code as      │  Amount    │  Amount    │
│     │              │  appropriate)             │  $/£/€/¥   │    NGN     │
│     │              ├─────────────┬─────────────┤            │            │
│     │              │ Project Code│ Cost Center │            │            │
│     │              │             │    Code     │            │            │
├─────┼──────────────┼─────────────┼─────────────┼────────────┼────────────┤
│ 1.0 │              │             │             │            │            │
│ ... │              │             │             │            │            │
│11.0 │              │             │             │            │            │
└─────┴──────────────┴─────────────┴─────────────┴────────────┴────────────┘
                                          Total              │
                                          Less Advance Taken  │
                                          Net Payable         │

(If cash advance amount collected is equal to amount spent, please fill
"non applicable")                                        Total NGN ______

Please issue payment in favour of company/staff

Amount in Words: _______________________________________________

Attached receipts:   [ ] Yes   [ ] No   [ ] Incomplete

Requested by:        Verified by:        Approved by:
Name:                Name:               Name:
Dept:                Dept:               Dept:
Sign/Date:           Sign/Date:          Sign/Date:

┌───────────────────────── FOR ACF DEPT USE ONLY ─────────────────────────┐
│ Endorsed by:____________________                    JV No. ____________ │
│                                                                         │
│  DR/CR            Account No.                    Amount                 │
│  ______           ___________                    ______                 │
│  (repeating rows)                                                       │
│                                                                         │
│         Inputers Signs      Authorisers Sign      Date                  │
└─────────────────────────────────────────────────────────────────────────┘

Amount above NGN 30,000 net payable will be transferred to employee bank
account

                     RECIPIENT'S ACKNOWLEDGEMENT
                     Cash received by me:
                     Name:_______________________________
                     Signature:__________________________
                     Date:_______________________________
```

**Notes for implementation**

- **Eleven numbered rows**, labelled `1.0` to `11.0`, not `1`–`11`. Digitally
  unbounded, but the default view should open with eleven so the page reads
  the same.
- Project Code and Cost Center Code are **two columns under one heading**, not
  a single field with a type selector. They are mutually exclusive per line
  (`docs/01`, finding 7), so exactly one is filled — the layout expresses that
  by giving each its own column.
- `Attached receipts` is **tri-state**: Yes / No / Incomplete.
- The NGN 30,000 threshold is **printed on the form**. It is a policy value
  with effective dating in the system (`docs/12`), so the rendered text should
  come from the policy, not be hardcoded — otherwise the screen and the rule
  can disagree.
- The GL block is a repeating table (DR/CR, Account No., Amount) with
  Inputer and Authoriser signatures beneath — the maker-checker split, already
  present on paper.

---

## DEL-AC-FRM-003 Rev 05 — Cash Advance Form

```
              CASH ADVANCE - TO BE JUSTIFIED
                                                                   Rev 05
                                                       TREAS. No. ________

Please approve a Cash Advance for the underlisted expense(s):   Date: ____

┌─────┬────────────────────────────────────────┬────────────┬────────────┐
│ s/n │ Description                            │ ₦/$/£/€/¥  │  k/¢/p     │
├─────┼────────────────────────────────────────┼────────────┼────────────┤
│  1  │                                        │            │            │
│ ... │                                        │            │            │
│  6  │                                        │            │            │
├─────┴────────────────────────────────────────┼────────────┼────────────┤
│ Total                                        │            │            │
├──────────────────────────────────────────────┴────────────┴────────────┤
│ (amount in words, full width)                                          │
└────────────────────────────────────────────────────────────────────────┘

Please tick as appropriate:
[ ] Projects Specific        Project Code:___________________________
[ ] Non Projects Specific    Cost Center Code:_______________________

Attached documentation:   [ ] Yes   [ ] No

Requested by:        Verified by:        Approved by:
Name:                Name:               Name:
Dept:                Dept:               Dept:
Sign/Date:           Sign/Date:          Sign/Date:

┌───────────────────────── FOR ACF DEPT USE ONLY ─────────────────────────┐
│ Endorsed by:________________________                JV No. ____________ │
│                                                                         │
│ DEBIT_______________________  A/C No.__________  REF__________________  │
│                                                                         │
│ CREDIT BENEFICIARY ________________________________  A/C No___________  │
│                                                                         │
│           Inputers sign        Authorisers sign        Date             │
└─────────────────────────────────────────────────────────────────────────┘

Please endeavour to retire within 24 hours for transactions within local
station state and 72 hours for transactions out of station state. This
transaction will be the liability of receipient, till it will be justified.

                     RECIPIENT'S ACKNOWLEDGEMENT
                     Cash received by me:
                     Name:_______________________________
                     Signature:__________________________
                     Date:_______________________________
```

**Notes for implementation**

- **Six numbered rows**, not eleven. The two forms differ and the difference
  is visible at a glance, so it is part of what makes each recognisable.
- **A separate minor-unit column** (`k/¢/p` — kobo, cents, pence). Amounts on
  this form are entered as major and minor units in two boxes, not as a
  decimal. Storing `decimal(18,2)` is right; the *input* should present two
  boxes or the form stops looking like itself.
- Allocation is **a tick plus a code**: "Projects Specific" or "Non Projects
  Specific" with the corresponding code field. This is form-level, unlike the
  expense form where allocation is per line.
- `Attached documentation` is **binary** here (Yes / No), where the expense
  form is tri-state. Not an inconsistency to tidy up — the advance has nothing
  to be incomplete about yet.
- The GL block differs from the expense form: **DEBIT / A-C No. / REF** on one
  line and **CREDIT BENEFICIARY / A-C No.** on another, rather than a
  repeating DR/CR table.
- The retirement wording is printed on the form, including its typos
  ("receipient", "till it will be justified"). Reproduce it verbatim. The
  24/72 figures are the policy this platform enforces in working hours
  (`docs/12`), so the note and the clock must be stated from the same source —
  a screen that says 24 hours while the system counts 24 *working* hours is
  how the disagreement recorded in the decision log reaches a user.

---

## What the forms confirm

Both carry `FOR ACF DEPT USE ONLY`, three signature blocks
(Requested / Verified / Approved), a separate `Endorsed by`, the
Inputer/Authoriser split, and a `RECIPIENT'S ACKNOWLEDGEMENT` block at the
foot — which is the step that actually closes a request, not Finance marking
it paid (`docs/01`, finding 11).

Buttons therefore read **Verify**, **Approve** and **Endorse**. Those are the
words on the page.
