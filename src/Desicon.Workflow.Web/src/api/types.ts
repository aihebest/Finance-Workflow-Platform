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
  MARK_POSTED: "Mark posted in BC",
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

  /**
   * Staff number and email, so a name is never the only thing distinguishing
   * one payee from another. Null for vendors and one-off payees.
   *
   * This is the field that decides who gets paid, and two people sharing a
   * name is ordinary. See BeneficiaryLookupEndpoints.
   */
  staffNumber: string | null;
  email: string | null;
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
 * An action the caller is authorised to take, and whether they can take it yet.
 *
 * The distinction is load-bearing, not decorative. Several guards exist to
 * require data the action itself captures — FINANCE_VERIFY's VERIFY needs a
 * Treasury number, POST needs balanced GL lines. If the screen only renders
 * actions whose guard already passes, the field that supplies the missing
 * value appears only once the value is present, and the step is unreachable.
 * Three of them were.
 *
 * So `isEnabled: false` means "yours to do, but something is missing", and
 * `blockedReason` is the definition's own guardMessage naming what. Render the
 * capture panel; disable the button.
 *
 * Advisory only. Every entry is re-checked inside the transaction when the
 * action is executed, so a stale list can produce a refusal but never an
 * unauthorised transition.
 */
export interface AvailableAction {
  action: string;
  isEnabled: boolean;
  blockedReason: string | null;
}

/**
 * One row of DEL-AC-FRM-003's table.
 *
 * The paper form has a naira box and a separate kobo box rather than a
 * decimal, so the capture screen presents two inputs and combines them here.
 * `decimal(18,2)` storage is right; the input shape is what makes the form
 * recognisable.
 */
export interface AdvanceLineInput {
  description: string;
  currencyCode: string;
  amount: number;
  fxRate: number;
  fxRateDate: string;
}

export interface CashAdvanceDraftInput {
  purpose: string;
  /** Which tick box: "Projects Specific" or "Non Projects Specific". */
  allocationType: "Project" | "CostCentre";
  projectCode?: string;
  costCentreCode?: string;
  /** Decides the retirement window: 24 working hours in station, 72 out. */
  stationScope: "InStation" | "OutOfStation";
  hasSupportingDocuments: boolean;
  lines: AdvanceLineInput[];
}

/** An entry in GET /api/v1/advances/outstanding. */
export interface OutstandingAdvance {
  requestId: string;
  requestNumber: string;
  totalAmountNgn: number;
  retiredAmountNgn: number;
  retirementBalanceNgn: number;
  retirementDueDate: string | null;
  retirementStatus: string;
  /** Days past the due date. Null when no due date has been set yet. */
  ageingDays: number | null;
}

/** What POST /api/v1/advances/{id}/retire returns: a new, linked expense claim. */
export interface AdvanceRetirementDraft {
  expenseRequestId: string;
  requestNumber: string;
  currentState: string;
  advanceAmountNgn: number;
  lineCount: number;
}

/** An entry in GET /api/v1/requests/{id}/attachments. */
export interface AttachmentSummary {
  attachmentId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedByUserId: string;
  uploadedAt: string;
  /** SHA-256 of the bytes, so a file produced later can be shown to be this one. */
  sha256: string;
}

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
