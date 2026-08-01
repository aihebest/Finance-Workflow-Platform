# Security Policy

This platform holds employee personal data, bank account details, and the
company's financial approval record. Treat every finding as material until
shown otherwise.

## Reporting a vulnerability

Report privately to **security@desicon.example** or via GitHub's private
vulnerability reporting on this repository. Do not open a public issue.

Include: affected component, reproduction steps, and impact assessment.

| Stage | Target |
|---|---|
| Acknowledgement | 1 working day |
| Initial assessment and severity | 3 working days |
| Fix for Critical | 7 calendar days |
| Fix for High | 30 calendar days |
| Fix for Medium | 90 calendar days |
| Public disclosure | After fix is deployed, coordinated with the reporter |

## Supported versions

The deployed production release and the immediately preceding release.

## Remediation SLAs for pipeline findings

These are enforced, not aspirational. The security gate fails the build when a
finding exceeds its window without an approved exception.

| Severity | Build behaviour | Remediation window |
|---|---|---|
| Critical | Blocks merge | Immediate |
| High | Blocks merge | 7 days with an approved exception |
| Medium | Warns | 30 days |
| Low | Warns | 90 days |

## Exceptions

No finding is suppressed silently. An exception requires an entry in
`security-exceptions.yml` carrying an owner, a justification, a linked issue and
an **expiry date**. `scripts/check-exceptions.mjs` runs on every PR and fails the
build once an exception expires — which is the whole point. A suppression with no
expiry is not an exception, it is a decision to accept the risk forever, and
those tend to be made by whoever was in a hurry rather than whoever owns the risk.

## Controls that are not negotiable

Changing any of these requires review from `@desicon/security` (enforced by
`CODEOWNERS`) and a documented risk acceptance:

- Maker–checker separation between GL posting and authorisation
- Append-only, hash-chained audit trail
- Managed Identity for all Azure resource access — no stored credentials
- Private endpoints on SQL, Key Vault, Storage and Service Bus
- The administrator role's inability to act on requests
- Guard expression allowlist

## Secure development requirements

- Every state change goes through `WorkflowEngine.ExecuteAsync`. Adding a second
  path bypasses audit emission.
- No raw SQL. `FromSqlRaw` is blocked by an analyzer rule at error severity.
- No `dangerouslySetInnerHTML`. Blocked by ESLint.
- All GitHub Actions pinned to a full commit SHA, verified by the pipeline.
- Container images are signed and carry an SBOM and build provenance.
