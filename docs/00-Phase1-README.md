# Desicon Enterprise Finance Workflow Platform — Phase 1 Design Package

**Client:** Desicon Engineering Limited
**Phase:** 1 (Expense Reimbursement · Cash Advance · Procurement Requisition)
**Package date:** 31 July 2026
**Source forms analysed:** `DEL-AC-FRM-002 Rev 05` (Expense Form), `DEL-AC-FRM-003 Rev 05` (Cash Advance Form)

---

## Read in this order

| # | Document | What it settles |
|---|---|---|
| 00 | This README | Stack, assumptions, open questions to close before build |
| 01 | `01-Form-Analysis-and-Field-Mapping.md` | Every field on the paper forms → system field, plus the 14 business rules the forms encode that the original brief missed |
| 02 | `02-Solution-Architecture.md` | Azure topology, the generic workflow engine, SLA/escalation design |
| 03 | `03-Data-Model-ERD.md` | ERD, table definitions, audit and immutability strategy |
| 04 | `04-Security-and-DevSecOps.md` | Entra ID, RBAC, network isolation, the full PR pipeline |
| 05 | `05-API-Frontend-and-Operations.md` | API surface, form-faithful UI, monitoring, testing, deployment, readiness checklist |
| — | `workflows/expense-reimbursement.workflow.json` | Working example of a workflow definition consumed by the engine |
| — | `terraform/modules/app-service/main.tf` | Reference Terraform module in house style |

---

## Technology stack

**I could not retrieve the stack from your previous Enterprise Workflow Platform project** — there is no record of it in this project's history. Rather than block, I inferred it from your own DevSecOps requirements, which call for **both `npm audit` and `dotnet audit`**. That is only coherent with a .NET backend and a Node-built SPA frontend. The assumed stack:

| Layer | Choice | Basis |
|---|---|---|
| Backend | ASP.NET Core 8 Web API (C#) | `dotnet audit` in your pipeline spec |
| Frontend | React 18 + TypeScript + Vite | `npm audit` in your pipeline spec |
| ORM | EF Core 8 | Standard pairing; migration-based schema control |
| Database | Azure SQL Database (Business Critical) | Named in your brief |
| Background jobs | Azure Functions (Timer + Service Bus triggers) | Reminders, escalations, SLA sweeps |
| Container | Linux containers on App Service | `docker build` + Trivy image scan in spec |
| IaC | Terraform ≥ 1.7, AzureRM ≥ 3.100 | Named in your brief |
| CI/CD | GitHub Actions | Named in your brief |

**If your previous project used a different stack — say Blazor, Angular, or Node/NestJS — tell me and I will re-cut documents 02, 04 and 05.** Documents 01 and 03 (forms, rules, data model) are stack-independent and stand either way.

---

## Standing assumptions

These are decisions I made to keep the design moving. Each one is cheap to reverse now and expensive to reverse after build starts.

1. **Base/reporting currency is NGN.** Foreign currency amounts are captured at line level with an FX rate and rate date, and converted to NGN for all reporting and totals.
2. **Regional deployment is Azure `southafricanorth` primary** with `southafricawest` as paired region for geo-redundant backup. Nigeria has no Azure region; South Africa North is the lowest-latency option with full paired-region support. Confirm against any data-residency position Desicon holds.
3. **Employee master data comes from Entra ID**, extended with a local `Employees` table holding grade, department, cost centre default, line manager, and bank details. HR system integration is out of Phase 1 scope.
4. **Approval hierarchy is derived from the `Employees.LineManagerId` chain**, with a delegation table for leave cover. No org-chart integration in Phase 1.
5. **The platform does not post to the general ledger in Phase 1.** It *produces* a validated, balanced JV payload (DR/CR lines, account codes, JV number) and exports it. GL integration is a Phase 2 connector.
6. **NRS e-Invoicing is out of Phase 1 scope** but the document model is designed to carry the fields it will need, so the Phase 2 connector is additive rather than a schema change.
7. **Retention:** request records and audit events retained 7 years (Nigerian Companies and Allied Matters Act / FIRS practice). Attachments in cool storage after 12 months.

---

## Open questions — please answer before build kick-off

Ordered by how much rework the wrong guess causes.

1. **The Procurement Requisition form.** You sent `DEL-AC-FRM-002` and `-003` but not a procurement form. Does `DEL-AC-FRM-004` (or a `DEL-PR-FRM-*`) exist? If it does, send it — I would rather map a real form than invent one, given how much the two real forms changed the design. If it doesn't exist, I will design one in the same house style and you can put it through document control.
2. **The 24h / 72h retirement clock.** The Cash Advance form requires retirement within 24 hours in-station and 72 hours out-of-station. Are those **working hours, calendar hours, or working days**? And does the clock start at approval, at cash release, or at recipient acknowledgement? My design starts it at recipient acknowledgement, calendar hours — but this drives every overdue-advance number on the dashboard, so it needs to be right.
3. **Consequence of an overdue advance.** The form says the advance "will be the liability of recipient, till it will be justified." Is there an existing sanction — payroll deduction, block on new advances, escalation to MD? I have designed a hard block on new advance requests where an overdue advance exists, as a configurable policy. Confirm or correct.
4. **The NGN 30,000 cash/bank threshold.** Still current at Rev 05? I have made it a configurable policy value rather than a constant, but I need the live figure for the default.
5. **Who assigns TREAS. No. and JV No.?** Both appear on the paper forms as manual, post-approval entries. Are they sequences owned by Treasury and Accounts respectively, and do they have a format I should reproduce (prefix, year segment, reset cadence)?
6. **Maker–checker on the ACF block.** The forms carry separate *Inputer's sign* and *Authoriser's sign*. I have enforced that these must be two different people, with no override. Is there any legitimate scenario where one person does both — a small office, an emergency? If so it needs an exception path with its own audit event rather than a silent bypass.
7. **Multi-entity scope.** Is this Desicon Engineering Limited only, or do sister companies / other offices share the forms? Multi-tenancy is far cheaper to build in now than to retrofit.
8. **Existing backlog.** The 18-month-old unpaid expense sheets referenced in your correspondence — do those get migrated in as historical records, or does the platform start clean on a cut-over date with the backlog worked on paper? This affects go-live sequencing more than anything else on this list.
