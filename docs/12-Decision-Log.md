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

## Status after step 5

Dev environment deployed to Azure (rg-desicon-fw-dev, southafricanorth).
UAT topology written, not applied — needs the self-hosted runner first.
Next: run scripts/create-app-user.sql, then step 6 (timer functions).
