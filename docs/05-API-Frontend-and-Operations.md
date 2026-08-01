# 05 — API, Frontend and Operations

---

## 1. API design

REST, versioned at `/api/v1`, JSON, problem+json for errors (RFC 7807). Bearer JWT from Entra ID on every endpoint.

### Generic engine endpoints — identical for every module

```
GET    /api/v1/modules                              List available modules
GET    /api/v1/modules/{key}/form-schema            Form definition for rendering

POST   /api/v1/requests                             Create draft { moduleKey, payload }
GET    /api/v1/requests/{id}                        Full request + history
PUT    /api/v1/requests/{id}                        Update draft (DRAFT/RETURNED only)
POST   /api/v1/requests/{id}/submit                 Enter the workflow
POST   /api/v1/requests/{id}/actions                Execute a transition
                                                    { action, comment, payload }
GET    /api/v1/requests/{id}/history                Approval + audit trail
GET    /api/v1/requests/{id}/pdf                    Rendered form, in paper layout

GET    /api/v1/requests?state=&module=&from=&to=    Scoped to caller's authority
GET    /api/v1/my/inbox                             Awaiting my action
GET    /api/v1/my/requests                          Raised by me
GET    /api/v1/my/advances                          My outstanding advances

POST   /api/v1/requests/{id}/attachments            Multipart upload
GET    /api/v1/attachments/{id}/download            Short-lived SAS redirect
DELETE /api/v1/attachments/{id}                     Supersede (never hard delete)

POST   /api/v1/requests/{id}/comments
```

`POST /actions` is the single mutation entry point for the workflow. Every transition — verify, approve, return, reject, post, authorise, pay, acknowledge — goes through it, so authorisation, guard evaluation, audit emission and notification dispatch have exactly one code path. That single choice removes the commonest class of bug in approval systems: the endpoint someone added later that forgot to write an audit row.

### Module-specific endpoints

```
GET    /api/v1/expenses/{id}/advance-netting        Total, advance, net payable
POST   /api/v1/expenses/{id}/acknowledge            Beneficiary confirms receipt
POST   /api/v1/expenses/{id}/gl-lines                Inputer captures DR/CR
POST   /api/v1/expenses/{id}/authorise-posting      Checker authorises (≠ inputer)
POST   /api/v1/expenses/{id}/refund-received        Confirms negative-net-payable refund

GET    /api/v1/advances/outstanding                 Filterable, with ageing
GET    /api/v1/advances/{id}/retirement             Balance, linked claims, due date
POST   /api/v1/advances/{id}/acknowledge            Recipient — starts 24/72h clock
POST   /api/v1/advances/{id}/release                Cash released

POST   /api/v1/requisitions/{id}/purchase-order
POST   /api/v1/requisitions/{id}/goods-received
POST   /api/v1/requisitions/{id}/invoice
```

### Dashboard and reporting

```
GET /api/v1/dashboards/my
GET /api/v1/dashboards/department/{id}
GET /api/v1/dashboards/finance
GET /api/v1/reports/ageing?module=&buckets=30,60,90,180
GET /api/v1/reports/sla-performance?from=&to=&groupBy=approver|department|stage
GET /api/v1/reports/outstanding-advances
GET /api/v1/reports/liability?groupBy=department|project
GET /api/v1/reports/export?format=xlsx|csv|pdf
```

All dashboard and report reads route to the SQL read replica.

### Conventions

Idempotency keys on `POST /actions` and payment execution. `ETag` / `If-Match` on updates, mapped to the `rowversion` — a stale write returns 412, not a silent overwrite. Cursor pagination on all list endpoints. Correlation ID header propagated to Application Insights.

---

## 2. Frontend design

### The adoption principle

Your brief makes the right call and it is worth stating sharply: **the digital form must look like the paper form.** An accounts clerk who has filled `DEL-AC-FRM-002` for eight years should recognise it in the first second. That means:

- Same field order, same section order, same headings — including "FOR ACF DEPT USE ONLY"
- Same words on the buttons: **Verify**, **Approve**, **Endorse** — not "Submit for stage 2"
- Form code and revision printed in the top-right, exactly where they sit on paper
- The three signature blocks preserved as a visual row — Requested by / Verified by / Approved by — but populated with name, role, timestamp and a status chip instead of a signature line
- The printed PDF is a faithful reproduction of the paper form, because it will be filed, photocopied and attached to bank instructions

What changes is what paper could not do: a live status chip in the header, a running total that foots itself, an advance picker that auto-fills "Cash Advance Taken", inline receipt thumbnails, and an approval history panel down the right side.

### Screens

| Screen | Purpose |
|---|---|
| My Inbox | Items awaiting my action, sorted by SLA remaining; overdue first, in red |
| My Requests | What I raised, with live status and current holder — the "where is it?" screen |
| My Advances | Outstanding advances, days to retirement, retire-now button |
| New Request | Module picker → form-faithful capture |
| Request Detail | The form, plus history, attachments, comments, available actions |
| Department Dashboard | Volume, ageing, SLA breaches, bottleneck by approver |
| Finance Dashboard | Ageing of approved-unpaid, outstanding advances, liability by project/department, items awaiting acknowledgement past SLA |
| Admin | Workflow definitions, reference data, delegations, role assignment |

### Accessibility and reach

WCAG 2.1 AA. Keyboard-navigable throughout. **Mobile-first for the approval path specifically** — a department head approving from a site or a car is the normal case, not the exception, and a two-tap approve-from-email flow is what makes SLA targets achievable. Capture forms can assume desktop.

Consider offline-tolerant draft saving. Connectivity across Nigerian sites is not uniform, and losing a half-completed eleven-line expense form to a dropped connection is exactly the kind of friction that sends people back to paper.

---

## 3. Monitoring and alerting

Application Insights with OpenTelemetry; Log Analytics as the sink; Azure Monitor alerts to Action Groups (email + Teams).

### Custom business metrics — not just infrastructure

| Metric | Alert threshold |
|---|---|
| `workflow.sla.breached` | Any breach → notify holder + their manager |
| `workflow.escalations` | > 10/day for one approver → notify Department Head |
| `advances.overdue.count` | Any increase → daily digest to Finance Manager |
| `advances.overdue.value` | > ₦2m aggregate → alert Finance Manager |
| `requests.awaiting_ack.aged` | > 48h → alert Finance Officer (paid but unconfirmed) |
| `approval.turnaround.p90` | Weekly report by stage and approver |
| `gl.posting.unbalanced` | Any → high severity, blocks posting |
| `audit.chain.verification` | Any failure → **critical**, page the Administrator |

### Technical alerts

API 5xx rate > 1% over 5 min · p95 latency > 2s · SQL DTU > 80% for 15 min · failed auth attempts > 20/user/hour · Key Vault access denied · function execution failures · certificate expiry within 30 days.

### Dashboards

An Azure Workbook per audience: Operations (health, latency, errors), Finance (the business metrics above), Security (auth failures, break-glass use, role changes, audit chain status).

---

## 4. Testing strategy

| Layer | Scope | Target |
|---|---|---|
| Unit | Domain rules, guard evaluation, netting arithmetic, FX rounding, amount-in-words, due-date computation | 80% overall, **100% on `Desicon.Workflow.Core`** |
| Integration | API + real SQL in a container, EF migrations, authorisation filters, concurrency | Every endpoint, every transition |
| Workflow | Each definition driven through every path including rejection, return, escalation, and negative net payable | Every state and transition covered |
| Contract | OpenAPI schema regression | On every PR |
| E2E | Playwright: raise → approve → post → pay → acknowledge, on Chrome/Edge/mobile Safari | Critical paths |
| Performance | k6: 200 concurrent users, 1,000 requests/day steady state | p95 < 2s |
| Security | OWASP ZAP baseline in CI; annual external penetration test | No high findings |
| UAT | Finance and ACF staff running real historical forms through the system | Sign-off gate |

The workflow row deserves emphasis. Test the *definitions*, not only the engine — a valid engine executing a definition with an unreachable state or a missing rejection path is a live incident that unit tests will not catch. A definition-validation test that walks every state for reachability and terminal-attainability should run in CI.

---

## 5. Deployment sequence

**Phase 1A — Foundation (weeks 1–3)**
Terraform for all three environments; Entra app registrations and groups; CI/CD with OIDC; base API skeleton deployed to dev with health checks passing through private endpoints.

**Phase 1B — Engine (weeks 4–7)**
Workflow core, request/audit/attachment model, action endpoint, notification pipeline, timer functions. Validated with a throwaway two-state test module.

**Phase 1C — Expense + Cash Advance (weeks 8–13)**
Both modules together, because they are one lifecycle. Includes the netting logic, the refund path, the retirement clock, and the acknowledgement close.

**Phase 1D — Procurement (weeks 14–17)**
Pending the real form.

**Phase 1E — Hardening and UAT (weeks 18–21)**
Penetration test, performance test, UAT with real historical forms, admin and end-user guides, training.

**Phase 1F — Pilot and rollout (weeks 22–26)**
Pilot with one department — ICT is the obvious candidate — running parallel with paper for four weeks. Then department-by-department cut-over, not big bang. Paper stops accepting new submissions per department as it goes live.

### Cut-over guidance

Run parallel, but set an end date for it at the start. Indefinite parallel running means paper wins, because paper is what people already know and there is always one urgent case that justifies an exception. Four weeks per department, then the paper form is no longer accepted for new requests in that department.

---

## 6. Production readiness checklist

**Infrastructure**
☐ Terraform applied to prd with no drift · ☐ state in Azure Storage with locking and versioning · ☐ private endpoints verified, public access confirmed disabled on SQL/KV/Storage/Service Bus · ☐ zone redundancy on · ☐ geo-backup to paired region tested by an actual restore · ☐ autoscale rules set and load-tested · ☐ WAF in prevention mode, not detection · ☐ TLS 1.2 minimum verified externally · ☐ cost alerts and budgets configured

**Security**
☐ Penetration test complete, high and critical findings closed · ☐ Conditional Access policies live · ☐ MFA enforced on all finance roles · ☐ no secrets in code, config or pipeline (Gitleaks clean on full history) · ☐ Managed Identity used for every Azure resource access · ☐ CMK rotation scheduled · ☐ break-glass procedure documented and rehearsed · ☐ maker–checker verified by test in production · ☐ audit hash-chain verification job running and alerting · ☐ role assignments reviewed and signed off by Finance Manager

**Application**
☐ All quality gates green · ☐ coverage thresholds met · ☐ workflow definitions validated for reachability · ☐ every module's rejection and return path tested · ☐ negative-net-payable refund path tested end to end · ☐ retirement clock verified against the 24/72h rule as confirmed by Finance · ☐ NGN 30,000 threshold set to the current policy value · ☐ amount-in-words correct for Naira and Kobo edge cases · ☐ PDF output reviewed and approved by ACF against the paper original

**Operations**
☐ Runbooks for the top ten alerts · ☐ on-call rota agreed · ☐ RTO/RPO agreed and tested (suggest RTO 4h, RPO 15min) · ☐ DR failover rehearsed · ☐ support and escalation path published · ☐ administrator guide complete · ☐ end-user guide and quick-reference card issued per role · ☐ training delivered per department before its cut-over date

**Governance**
☐ Retention policy applied (7 years) · ☐ data classification signed off · ☐ NDPR compliance reviewed — employee personal and bank data is in scope · ☐ management policy issued requiring timely action on electronic approvals, with the same force as physical documents · ☐ SLA targets formally agreed per stage rather than assumed · ☐ digital forms entered into QMS document control with their own revision numbers

That penultimate governance item is the one to push hardest on. The controls in this design make delay visible and attributable, but visibility only converts into speed if management policy attaches a consequence to sitting on an item. That is a decision for the MD, not a feature — and it is best secured before go-live, while the project still has attention, rather than after.
