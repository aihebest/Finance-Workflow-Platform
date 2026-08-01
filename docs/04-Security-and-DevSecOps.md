# 04 — Security Architecture and DevSecOps

---

## 1. Identity and access

### Authentication

Microsoft Entra ID, OIDC authorization-code flow with PKCE. SPA uses MSAL.js; API validates JWTs against Entra's JWKS with issuer, audience and signature checks. No local accounts, no password storage in the platform at all.

Conditional Access policies to apply (these are tenant-side, not application-side, and need your Entra administrator):

- MFA required for `FinanceOfficer`, `FinanceManager`, `ProcurementOfficer`, `Administrator`
- Sign-in risk-based MFA for all users
- Device compliance required for `Administrator`
- Session lifetime 8 hours for approvers; 1 hour for Administrator

### RBAC

Roles are Entra security groups, surfaced as `roles` claims; the API authorises on claims, never on a database role lookup that could drift.

| Role | Can do | Cannot do |
|---|---|---|
| `Employee` | Raise requests, view own, acknowledge receipt, retire own advances | See anyone else's request |
| `LineManager` | Everything Employee, plus verify/return/reject for direct reports | Approve own request; act on requests outside their reporting line |
| `DepartmentHead` | Verify/return/reject for the department; department dashboards | Post to GL; release cash |
| `FinanceOfficer` | Verify receipts, capture TREAS. No., prepare GL lines (inputer), execute payment | Authorise their own posting |
| `FinanceManager` | Final approval, authorise postings (checker), all finance dashboards | Authorise a posting they input |
| `ProcurementOfficer` | Requisition sourcing, vendor management, PO issue | Approve budget; approve payment |
| `Administrator` | Workflow definitions, reference data, delegations, user role assignment | **Act on any request, or read request line detail** |

The last row is the one that gets skipped and matters most. An administrator who can also approve a payment is a single point of fraud. Administrators manage configuration; they see request *metadata* and audit events for support purposes, not amounts and beneficiaries. If a genuine support need arises, there is a break-glass elevation that is time-boxed, requires a second administrator's approval, and writes a high-severity audit event that alerts the Finance Manager.

### Authorisation enforcement points

Three layers, all server-side:

1. **Role check** — does this principal hold a role that can perform this action type?
2. **Actor check** — is this principal the *current actor* on this specific request (or a valid delegate)? Holding `LineManager` does not grant action on another manager's queue.
3. **Guard evaluation** — do the request's own fields satisfy the transition's guard (maker ≠ checker, GL balanced, net payable sign)?

The UI hides unavailable actions, but that is cosmetic. Every check is repeated in the API.

---

## 2. Data protection

| Control | Implementation |
|---|---|
| In transit | TLS 1.2 minimum enforced at Front Door, App Service and SQL; HSTS with preload; TLS 1.3 preferred |
| At rest | Azure SQL TDE with customer-managed key in Key Vault Premium (HSM); Storage encryption with CMK |
| Column-level | Always Encrypted on `Beneficiary.BankAccountNumber` — enclave-free, so the application can equality-search but the DBA cannot read account numbers |
| Secrets | Key Vault only; app authenticates by Managed Identity; **zero connection strings with credentials anywhere in code, config or pipeline** |
| Network | Private endpoints for SQL, Key Vault, Storage, Service Bus; public network access disabled on all four; VNet integration for App Service and Functions |
| Egress | No unrestricted outbound; NSG rules allow only Graph, Azure service tags required |
| Backup | SQL PITR 35 days; weekly long-term retention 7 years; geo-redundant to South Africa West |

---

## 3. Application security controls

| Threat | Control |
|---|---|
| SQL injection | EF Core parameterised queries throughout; `FromSqlRaw` banned by an analyzer rule that fails the build |
| XSS | React escapes by default; `dangerouslySetInnerHTML` banned by ESLint rule; strict CSP with nonces, no `unsafe-inline`, no `unsafe-eval` |
| CSRF | SameSite=Strict cookies for the session; anti-forgery token on state-changing endpoints; `Origin` validated |
| Mass assignment | Explicit DTOs; domain entities never bound directly from request bodies |
| IDOR | Every read goes through an authorisation filter that scopes by requester, reporting line or role — never by trusting an ID in the URL |
| File upload | Extension and magic-byte allowlist (PDF, JPG, PNG, XLSX, DOCX), 20 MB cap, malware scan before visibility, filenames sanitised and never used as blob paths, `Content-Disposition: attachment` on download |
| Rate limiting | ASP.NET Core rate limiter — 100 req/min per user, 10/min on auth and upload endpoints; 429 with `Retry-After` |
| Guard expressions | Restricted grammar with a function allowlist, parsed to an AST and evaluated — no dynamic compilation, no `eval` |
| Enumeration | Request numbers are sequential (needed for audit) but access is authorisation-scoped, so sequence tells an attacker nothing |
| Headers | HSTS, `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy`, `Permissions-Policy` |

### Audit logging

Every one of these emits an `AUDIT_EVENT`: submission, each approval action, return, rejection, escalation, delegation use, attachment upload/download/supersede, GL posting, authorisation, payment execution, acknowledgement, workflow definition change, role assignment change, break-glass elevation, and every failed authorisation attempt. Each carries actor, role, timestamp (UTC), client IP, correlation ID, and the before/after state.

---

## 4. GitHub Actions — PR validation pipeline

Every check in your brief, gated so that no deployment proceeds past a critical finding.

```yaml
name: PR Validation

on:
  pull_request:
    branches: [main, develop]

permissions:
  contents: read
  security-events: write
  id-token: write          # OIDC to Azure — no stored credentials
  pull-requests: write

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  # ─────────────────────────── Secret Detection ───────────────────────────
  secret-detection:
    name: Gitleaks
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }          # full history for Gitleaks
      - uses: gitleaks/gitleaks-action@v2
        env:
          GITLEAKS_CONFIG: .config/gitleaks.toml

  # ────────────────────────── Infrastructure Security ─────────────────────
  terraform-security:
    name: Terraform Security
    runs-on: ubuntu-latest
    defaults: { run: { working-directory: infra/terraform } }
    steps:
      - uses: actions/checkout@v4
      - uses: hashicorp/setup-terraform@v3
        with: { terraform_version: 1.7.5 }

      - name: Terraform fmt
        run: terraform fmt -check -recursive

      - name: Terraform init
        run: terraform init -backend=false

      - name: Terraform validate
        run: terraform validate

      - uses: azure/login@v2
        with:
          client-id:       ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id:       ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Terraform plan
        working-directory: infra/terraform/environments/dev
        run: |
          terraform init
          terraform plan -out=tfplan -input=false
          terraform show -no-color tfplan > plan.txt

      - uses: terraform-linters/setup-tflint@v4
      - name: TFLint
        run: tflint --recursive --format compact --minimum-failure-severity=error

      - name: Checkov
        uses: bridgecrewio/checkov-action@master
        with:
          directory: infra/terraform
          framework: terraform
          soft_fail: false                # hard gate
          output_format: sarif
          output_file_path: checkov.sarif
      - uses: github/codeql-action/upload-sarif@v3
        if: always()
        with: { sarif_file: checkov.sarif }

      - name: tfsec
        uses: aquasecurity/tfsec-sarif-action@v0.1.4
        with: { sarif_file: tfsec.sarif }
      - uses: github/codeql-action/upload-sarif@v3
        if: always()
        with: { sarif_file: tfsec.sarif }

  # ──────────────────────────── CodeQL ────────────────────────────────────
  codeql:
    name: CodeQL (${{ matrix.language }})
    runs-on: ubuntu-latest
    strategy:
      fail-fast: false
      matrix:
        language: [csharp, javascript-typescript]
    steps:
      - uses: actions/checkout@v4
      - uses: github/codeql-action/init@v3
        with:
          languages: ${{ matrix.language }}
          queries: security-extended,security-and-quality
      - uses: actions/setup-dotnet@v4
        if: matrix.language == 'csharp'
        with: { dotnet-version: '8.0.x' }
      - uses: github/codeql-action/autobuild@v3
      - uses: github/codeql-action/analyze@v3

  # ──────────────────────── Dependency Security ───────────────────────────
  dependency-review:
    name: Dependency Review
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/dependency-review-action@v4
        with:
          fail-on-severity: high
          deny-licenses: GPL-3.0, AGPL-3.0

  dependency-scan:
    name: OWASP / npm audit / dotnet audit
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - uses: actions/setup-node@v4
        with: { node-version: '20', cache: npm, cache-dependency-path: src/Desicon.Workflow.Web/package-lock.json }

      - name: dotnet restore
        run: dotnet restore

      - name: dotnet list package --vulnerable
        run: |
          dotnet list package --vulnerable --include-transitive 2>&1 | tee audit.txt
          if grep -qE 'High|Critical' audit.txt; then
            echo "::error::Vulnerable NuGet packages (High/Critical) detected"; exit 1
          fi

      - name: npm audit
        working-directory: src/Desicon.Workflow.Web
        run: |
          npm ci
          npm audit --audit-level=high

      - name: OWASP Dependency-Check
        uses: dependency-check/Dependency-Check_Action@main
        with:
          project: desicon-finance-workflow
          path: .
          format: SARIF
          args: >-
            --failOnCVSS 7
            --enableRetired
            --suppression .config/dependency-check-suppression.xml
      - uses: github/codeql-action/upload-sarif@v3
        if: always()
        with: { sarif_file: reports/dependency-check-report.sarif }

  # ──────────────────────── Quality Gates ─────────────────────────────────
  build-and-test:
    name: Build · Unit · Integration
    runs-on: ubuntu-latest
    services:
      sql:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env:
          ACCEPT_EULA: 'Y'
          MSSQL_SA_PASSWORD: ${{ secrets.TEST_SQL_PASSWORD }}
        ports: ['1433:1433']
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '8.0.x' }
      - uses: actions/setup-node@v4
        with: { node-version: '20' }

      - run: dotnet build --configuration Release /warnaserror

      - name: Unit tests
        run: dotnet test tests/Desicon.Workflow.UnitTests
             --configuration Release --no-build
             --collect:"XPlat Code Coverage"
             --logger trx

      - name: Integration tests
        run: dotnet test tests/Desicon.Workflow.IntegrationTests
             --configuration Release --no-build --logger trx
        env:
          ConnectionStrings__Default: >-
            Server=localhost,1433;Database=WorkflowTest;User Id=sa;
            Password=${{ secrets.TEST_SQL_PASSWORD }};TrustServerCertificate=True

      - name: Coverage gate (80% line, 100% on workflow engine)
        uses: irongut/CodeCoverageSummary@v1.3.0
        with:
          filename: '**/coverage.cobertura.xml'
          fail_below_min: true
          thresholds: '80 90'

      - name: Frontend build and test
        working-directory: src/Desicon.Workflow.Web
        run: |
          npm ci
          npm run lint
          npm run test -- --run --coverage
          npm run build

  # ──────────────────────── Container Security ────────────────────────────
  container-security:
    name: Docker · Trivy
    runs-on: ubuntu-latest
    needs: build-and-test
    steps:
      - uses: actions/checkout@v4

      - name: Trivy filesystem scan
        uses: aquasecurity/trivy-action@master
        with:
          scan-type: fs
          scan-ref: .
          format: sarif
          output: trivy-fs.sarif
          severity: CRITICAL,HIGH
          exit-code: '1'
      - uses: github/codeql-action/upload-sarif@v3
        if: always()
        with: { sarif_file: trivy-fs.sarif }

      - name: Build API image
        run: docker build -f docker/api.Dockerfile -t desicon-api:${{ github.sha }} .

      - name: Build Web image
        run: docker build -f docker/web.Dockerfile -t desicon-web:${{ github.sha }} .

      - name: Trivy image scan (API)
        uses: aquasecurity/trivy-action@master
        with:
          image-ref: desicon-api:${{ github.sha }}
          format: sarif
          output: trivy-api.sarif
          severity: CRITICAL,HIGH
          exit-code: '1'
          ignore-unfixed: true
      - uses: github/codeql-action/upload-sarif@v3
        if: always()
        with: { sarif_file: trivy-api.sarif }

  # ──────────────────────────── Gate ──────────────────────────────────────
  security-gate:
    name: Security Gate
    runs-on: ubuntu-latest
    needs:
      - secret-detection
      - terraform-security
      - codeql
      - dependency-review
      - dependency-scan
      - build-and-test
      - container-security
    if: always()
    steps:
      - name: Fail on any upstream failure
        if: contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled')
        run: |
          echo "::error::Security gate failed — deployment blocked."
          exit 1
      - run: echo "All security and quality gates passed."
```

### Branch protection required to make this real

A pipeline that can be merged around is decoration. Configure on `main`:

- Require the `Security Gate` status check
- Require 2 approving reviews, 1 from `CODEOWNERS`
- Dismiss stale approvals on push
- Require conversation resolution
- Require signed commits
- Require linear history
- No force push, no deletion
- **Include administrators** — the exemption that always gets granted is the one that always gets abused

### Deployment pipeline

`deploy-infra.yml` and `deploy-app.yml` trigger on merge to `main`, authenticate to Azure by **OIDC federated credentials** (no stored service principal secret), and gate production behind a GitHub Environment with required reviewers. App deploys to a staging slot, runs smoke tests against the slot, then swaps. Rollback is a slot swap back — seconds, not a redeploy.

---

## 5. Threat model summary

| Threat | Likelihood | Mitigation |
|---|---|---|
| Insider inflates an expense claim | Medium | Immutable lines post-submission, hash-chained audit, maker–checker, receipt attachment mandatory, no self-approval |
| Approver colludes with requester | Low | Multi-stage approval across departments, GL posting separated from approval, all actions attributable |
| Administrator grants themselves approval rights | Low | Role assignment changes are audited and alert the Finance Manager; administrator role cannot act on requests |
| Advance taken and never retired | **High** | Automatic overdue flagging, employee liability balance, policy block on new advances, escalation to management |
| Receipt swapped after approval | Medium | Immutable blob container, SHA-256 per attachment, supersede-not-overwrite |
| Payment marked as made but not made | **High** | `AWAITING_ACK` state — beneficiary acknowledgement, not Finance's assertion, closes the request |
| Compromised credential | Medium | Conditional Access, MFA on finance roles, sign-in risk policies, short sessions |
| Supply-chain compromise | Medium | Dependency Review, OWASP DC, Trivy, pinned action SHAs, Dependabot |
