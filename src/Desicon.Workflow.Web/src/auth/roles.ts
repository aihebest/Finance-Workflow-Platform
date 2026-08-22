import { useMsal } from "@azure/msal-react";

/**
 * The four roles allowed to see figures spanning departments.
 *
 * Must match ReportEndpoints.ReportingRoles. Two lists that have to agree is
 * not ideal, but the alternative — the browser asking the API what it is
 * allowed to see — is a round trip to decide whether to draw a link.
 */
export const FINANCE_ROLES = [
  "CostControlOfficer",
  "TreasuryOfficer",
  "FinanceManager",
  "DirectorOfFinance",
] as const;

/**
 * Roles from the signed-in account's token.
 *
 * Entra puts app role assignments in the `roles` claim. An unassigned user has
 * no claim at all rather than an empty array, hence the fallback.
 */
export function useRoles(): string[] {
  const { instance } = useMsal();
  const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0];

  const claims = account?.idTokenClaims as { roles?: string[] } | undefined;
  return claims?.roles ?? [];
}

/**
 * Whether to draw the Reports link.
 *
 * This is presentation, not security, and the distinction matters: hiding a
 * link hides nothing from anyone willing to type the URL. The refusal that
 * counts is the 403 in ReportEndpoints, which is asserted by a test against a
 * requester and a Head of Department. This only keeps a tab off the screen of
 * people it would do nothing for.
 */
export function useIsFinance(): boolean {
  const roles = useRoles();
  return FINANCE_ROLES.some((role) => roles.includes(role));
}
