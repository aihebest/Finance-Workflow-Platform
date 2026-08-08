/**
 * Types mirroring the API's DTOs.
 *
 * Hand-written rather than generated, and that is a debt worth naming: these
 * can drift from RequestEndpoints.ToSummaryDto / ToDetailDto with nothing to
 * catch it, which is precisely the seam this project keeps finding elsewhere
 * (guard fields, connection-string keys, the audit hash). Generating them from
 * the OpenAPI document at build time would close it. Until then, changing a
 * DTO means changing this file, and a CI check comparing the two would be the
 * right next step.
 */

/** Item as returned by /api/v1/my/inbox and /api/v1/my/requests. */
export interface RequestSummary {
  requestId: string;
  requestNumber: string;
  moduleKey: string;
  currentState: string;
  currentActorId: string | null;
  requesterId: string;
  departmentId: number;
  totalAmountNgn: number;
  stateEnteredAt: string;
  slaDueAt: string | null;
  submittedAt: string | null;
  closedAt: string | null;
}

/** One entry in /api/v1/requests/{id}/history. */
export interface AuditEntry {
  auditEventId: number;
  eventType: string;
  fromState: string | null;
  toState: string | null;
  actorId: string;
  actorRole: string;
  onBehalfOfUserId: string | null;
  reason: string | null;
  occurredAtUtc: string;
}

/**
 * RFC 7807 problem details. The API returns these for guard rejections and
 * policy violations, and the `detail` is written for a person — a guard that
 * refuses a transition explains which condition failed. Surfacing it verbatim
 * is better than replacing it with "Something went wrong".
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  fromState?: string;
  toState?: string;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: ProblemDetails | null,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/**
 * Actions, in the paper form's vocabulary.
 *
 * "Verify", "Approve" and "Endorse" are the words on DEL-AC-FRM-002, and the
 * build plan is explicit that buttons must read that way. A clerk should not
 * have to learn that "Submit for review" means what "Verify" meant on paper.
 */
export const ACTION_LABELS: Record<string, string> = {
  SUBMIT: "Submit",
  VERIFY: "Verify",
  APPROVE: "Approve",
  ENDORSE: "Endorse",
  RETURN: "Return for correction",
  REJECT: "Reject",
  POST: "Post",
  AUTHORISE: "Authorise",
  EXECUTE_PAYMENT: "Execute payment",
  ACKNOWLEDGE: "Acknowledge receipt",
  CONFIRM_REFUND: "Confirm refund",
  RESUBMIT: "Resubmit",
};

export const MODULE_LABELS: Record<string, string> = {
  CASH_ADVANCE: "Cash Advance",
  EXPENSE: "Expense",
  LEAVE_REQUEST: "Leave Request",
};

/** An entry in GET /api/v1/beneficiaries. Bank details are deliberately absent. */
export interface BeneficiarySummary {
  id: string;
  type: "Employee" | "Vendor" | "Other";
  name: string;
  hasBankDetails: boolean;
}

/**
 * One row of DEL-AC-FRM-002's "Details of Expense" table.
 *
 * `expenseCategoryId` and the FX fields are on the API contract but have no
 * source: there is no category table and no rates feed. Left optional here
 * and unfilled by the form until those exist, rather than inventing values
 * that would look authoritative in a ledger.
 */
export interface ExpenseLineInput {
  description: string;
  expenseDate: string;
  projectCode: string;
  costCentreCode: string;
  currencyCode: string;
  amount: number;
  fxRate: number;
  fxRateDate: string;
}

/**
 * One side of a journal entry. `side` matches the PostingSide enum the API
 * parses case-insensitively; the string form is what travels on the wire.
 */
export interface GlLineInput {
  side: "Debit" | "Credit";
  accountNumber: string;
  narration: string;
  amountNgn: number;
}

/**
 * Actions the caller may take right now, as computed by the API from the
 * workflow definition's actor resolvers and guards.
 *
 * Advisory only. Every entry is re-checked inside the transaction when the
 * action is actually executed, so a stale list can produce a refusal but never
 * an unauthorised transition.
 */
export type AvailableAction = string;

export interface ExpenseDraftInput {
  /**
   * Omitted means "pay me". The API resolves the requester's own beneficiary
   * server-side through the audited path -- the browser never asserts which
   * beneficiary it is paying, because creating one requires bank details and
   * writes a SecurityEvent.
   */
  beneficiaryId?: string;
  receiptStatus: "Yes" | "No" | "Incomplete";
  lines: ExpenseLineInput[];
}
