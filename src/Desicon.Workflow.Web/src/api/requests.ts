import { api } from "./client";
import type {
  AdvanceRetirementDraft,
  AttachmentSummary,
  AuditEntry,
  BeneficiarySummary,
  CashAdvanceDraftInput,
  ExpenseDraftInput,
  OutstandingAdvance,
  RequestSummary,
} from "./types";

export const getInbox = () => api.get<RequestSummary[]>("/api/v1/my/inbox");

export const getMyRequests = () => api.get<RequestSummary[]>("/api/v1/my/requests");

export const getRequest = (id: string) =>
  api.get<Record<string, unknown>>(`/api/v1/requests/${id}`);

export const getHistory = (id: string) =>
  api.get<AuditEntry[]>(`/api/v1/requests/${id}/history`);

/**
 * Executes a workflow action. `comment` is optional for most transitions and
 * required by the definition for RETURN and REJECT -- the API enforces that,
 * so the UI asks for it rather than guessing and being refused.
 */
export const executeAction = (
  id: string,
  action: string,
  comment?: string,
  payload?: Record<string, unknown>,
) =>
  api.post<{ toState: string; outcome: string }>(`/api/v1/requests/${id}/actions`, {
    action,
    comment: comment ?? null,
    payload: payload ?? null,
  });

export const getBeneficiaries = (search?: string) =>
  api.get<BeneficiarySummary[]>(
    search ? `/api/v1/beneficiaries?search=${encodeURIComponent(search)}` : "/api/v1/beneficiaries",
  );

/**
 * Creates an EXPENSE draft. The API parses `payload` against
 * ExpenseDraftPayload by module key, so the shape here must match that
 * record exactly -- there is no shared schema between the two, which is a
 * seam worth generating from OpenAPI later.
 */
export const createExpenseDraft = (input: ExpenseDraftInput) =>
  api.post<{ requestId: string; requestNumber: string }>("/api/v1/requests", {
    moduleKey: "EXPENSE",
    payload: input,
  });

export const submitRequest = (id: string) =>
  api.post<{ toState: string }>(`/api/v1/requests/${id}/submit`, {});

/**
 * Transitions that capture data have their own endpoints rather than going
 * through /actions.
 *
 * The generic endpoint only stages TreasuryNumber onto the entity; everything
 * else in a `captures` list reaches the audit event's PayloadJson and stops
 * there. For a guard field (JV number, GL totals, refund amount) that means the
 * guard evaluates against stale data and refuses; for a plain column
 * (PaymentReference) it means the transition succeeds and the column stays
 * null. Both failures are quiet, so the capture endpoints below are the only
 * correct way to take these actions.
 */

/**
 * AWAITING_POSTING → AWAITING_PAYMENT (or → CLOSED when nothing is payable).
 *
 * The Accounts Officer posts in Business Central, then records here that she
 * has. No journal lines travel with this: BC owns the ledger, and this
 * platform owns the approval trail plus the reference joining the two.
 */
export const markPosted = (id: string, bcDocumentNumber: string, comment?: string) =>
  api.post<{ toState: string; outcome: string }>(`/api/v1/expenses/${id}/mark-posted`, {
    bcDocumentNumber,
    comment: comment ?? null,
  });

/** AWAITING_PAYMENT → AWAITING_ACK. */
export const executePayment = (
  id: string,
  paymentReference: string,
  paymentDate?: string,
  comment?: string,
) =>
  api.post<{ toState: string; outcome: string }>(`/api/v1/expenses/${id}/execute-payment`, {
    paymentReference,
    paymentDate: paymentDate ?? null,
    comment: comment ?? null,
  });

/** REFUND_DUE → AWAITING_POSTING. Must equal the amount over-drawn, to the naira. */
export const confirmRefund = (id: string, refundReceivedAmountNgn: number, comment?: string) =>
  api.post<{ toState: string; outcome: string }>(`/api/v1/expenses/${id}/refund-received`, {
    refundReceivedAmountNgn,
    comment: comment ?? null,
  });

/**
 * Creates a CASH_ADVANCE draft. Same generic endpoint as an expense claim —
 * the API parses `payload` against CashAdvanceDraftPayload by module key.
 */
export const createCashAdvanceDraft = (input: CashAdvanceDraftInput) =>
  api.post<{ requestId: string; requestNumber: string }>("/api/v1/requests", {
    moduleKey: "CASH_ADVANCE",
    payload: input,
  });

/** Advances still carrying a balance, with how overdue each one is. */
export const getOutstandingAdvances = () =>
  api.get<OutstandingAdvance[]>("/api/v1/advances/outstanding");

/**
 * Starts a retirement.
 *
 * An advance is retired *by* an expense claim, not by an action on the advance
 * itself: the server creates a linked draft carrying the outstanding balance
 * as its Cash Advance Taken, and that claim then runs the ordinary approval
 * chain. Netting happens when it is posted.
 */
export const retireAdvance = (id: string) =>
  api.post<AdvanceRetirementDraft>(`/api/v1/advances/${id}/retire`, {});

/** Receipts and supporting documents on a request. */
export const getAttachments = (id: string) =>
  api.get<AttachmentSummary[]>(`/api/v1/requests/${id}/attachments`);

/**
 * Uploads one file.
 *
 * Multipart through the API rather than direct to storage: read access to an
 * attachment is then decided by the same rules that decide read access to the
 * request, rather than by a second mechanism that has to agree with the first.
 */
export const uploadAttachment = (id: string, file: File) => {
  const form = new FormData();
  form.append("file", file);
  return api.postForm<AttachmentSummary>(`/api/v1/requests/${id}/attachments`, form);
};

/**
 * Downloads an attachment and saves it under its original name.
 *
 * Goes through fetch rather than an href because the API needs the bearer
 * token, and an anchor cannot send one. The object URL is revoked immediately
 * -- it holds the whole file in memory until it is.
 */
export const downloadAttachment = async (id: string, attachmentId: string, fileName: string) => {
  const blob = await api.getBlob(`/api/v1/requests/${id}/attachments/${attachmentId}`);
  const url = URL.createObjectURL(blob);

  try {
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
};
