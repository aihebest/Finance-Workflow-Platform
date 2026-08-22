import { useEffect, useState } from "react";
import { api } from "../api/client";

/**
 * The four roles allowed to see figures spanning departments.
 *
 * Kept in step with ReportEndpoints.ReportingRoles. The API is still the
 * authority -- this only decides whether a link is drawn.
 */
export const FINANCE_ROLES = [
  "CostControlOfficer",
  "TreasuryOfficer",
  "FinanceManager",
  "DirectorOfFinance",
] as const;

export interface Me {
  roles: string[];
  hasEmployeeRecord: boolean;
  employee: {
    id: string;
    staffNumber: string;
    fullName: string;
    email: string;
    departmentId: number;
  } | null;
}

/**
 * Who the API thinks you are.
 *
 * This used to read `roles` off the MSAL account's idTokenClaims, which is a
 * different token from the one the API is sent. For the Cost Control desk that
 * claim was absent, so the Reports tab never appeared for somebody the API
 * would have admitted without hesitation — the browser and the server
 * disagreeing about the same person.
 *
 * Asking removes the disagreement rather than papering over it. One round trip
 * on load, and the two can no longer drift.
 */
export function useMe(): Me | null {
  const [me, setMe] = useState<Me | null>(null);

  useEffect(() => {
    let cancelled = false;

    api
      .get<Me>("/api/v1/me")
      .then((result) => {
        if (!cancelled) setMe(result);
      })
      .catch(() => {
        // Unreachable or refused. Treated as "no roles" rather than surfaced:
        // this only decides whether a nav link renders, and every page behind
        // it reports its own failure properly.
        if (!cancelled) setMe({ roles: [], hasEmployeeRecord: false, employee: null });
      });

    return () => {
      cancelled = true;
    };
  }, []);

  return me;
}

/**
 * Whether to draw the Reports link.
 *
 * Presentation, not security. Hiding a link hides nothing from anyone willing
 * to type the URL; the refusal that counts is the 403 in ReportEndpoints,
 * asserted against a requester and a Head of Department.
 */
export function useIsFinance(): boolean {
  const me = useMe();
  return me !== null && FINANCE_ROLES.some((role) => me.roles.includes(role));
}
