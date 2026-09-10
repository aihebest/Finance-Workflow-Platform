import { api } from "./client";
import type {
  MyApprovals,
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
/**
 * Treasury records the posting: the BC document number and the Treasury
 * number together.
 *
 * The Treasury number moved here from COST_CONTROL_VERIFY in workflow
 * version 6. Cost Control had been required to supply a number belonging to
 * the desk they were split from in version 3, and nothing read it.
 */
/**
 * The route depends on the module, and it always did.
 *
 * This function posted to /api/v1/expenses/{id}/mark-posted for everything
 * until 7 September 2026. An expense claim posted; a cash advance came back
 * with "Expense request '...' does not exist", because the expense endpoint
 * looked for an ExpenseRequest row and a cash advance is not one. Treasury hit
 * it on ADV-2026-000008, the first advance ever to reach AWAITING_POSTING with
 * a real person behind it.
 *
 * Both endpoints existed. Both were tested -- WorkflowSteps has
 * MarkPostedExpenseAsync and MarkPostedAdvanceAsync, and the integration suite
 * drives each. What had no test was the only caller that ships, and it called
 * one of them for both modules.
 */
export const markPosted = (
  id: string,
  moduleKey: string,
  bcDocumentNumber: string,
  treasuryNumber: string,
  comment?: string,
) =>
  api.post<{ toState: string; outcome: string }>(
    `/api/v1/${moduleKey === "CASH_ADVANCE" ? "advances" : "expenses"}/${id}/mark-posted`,
    {
      bcDocumentNumber,
      treasuryNumber,
      comment: comment ?? null,
    },
  );

/**
 * CASH_RELEASE -> AWAITING_ACK. Cash advances only.
 *
 * `CashReleasedAt` is a mandatory captured field: it starts the retirement
 * clock, which is what makes an advance overdue and therefore what the whole
 * retirement rule rests on. Until 7 September nothing in the SPA called this,
 * and RELEASE_CASH was not in CAPTURE_ACTIONS either -- so it rendered as a
 * bare button that would have sent the action with no date and been refused
 * for the field it never offered anywhere to type.
 */
export const releaseCash = (id: string, cashReleasedAt: string, comment?: string) =>
  api.post<{ toState: string; outcome: string }>(`/api/v1/advances/${id}/release`, {
    cashReleasedAt,
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

/**
 * What this person has already decided.
 *
 * Cost Control, 24 Aug 2026: "The System does not show the history or trail of
 * request that have been approved by cost control." My Inbox empties the
 * moment you act, and My Requests shows what you raised, not what you decided.
 */
export const getMyApprovals = () => api.get<MyApprovals>("/api/v1/my/approvals");

/**
 * Cost Control setting or correcting the coding while a request sits in their
 * queue.
 *
 * Cost Control holds the organisation's cost centres and the requester
 * generally does not, so a code that arrives wrong or blank is ordinary. This
 * is the alternative to returning every such request to somebody who would
 * have to come and ask them for the answer.
 *
 * Exactly one of projectCode / costCentreCode, and a reason: the previous
 * coding is kept in the audit trail alongside the new one.
 */
/**
 * The requester answering whether the receipts are attached.
 *
 * Narrow on purpose. `PUT /api/v1/requests/{id}` can also set this field, but
 * it replaces the whole claim — it clears the lines and rebuilds them from the
 * payload — so changing one radio button that way would mean sending every line
 * back through the browser. On a retirement claim those lines are the server's
 * account of what the advance was spent on, and they should not make that trip
 * to change something else.
 */
/**
 * Names a payee who is not on the list yet.
 *
 * Creates a name and nothing else — no bank details, so no money can reach it
 * until Treasury records an account against the claim. That split is the
 * point: the requester says who they paid, a different desk says where the
 * money goes.
 *
 * An exact name match is refused rather than reused. Two people in this system
 * share a display name, and a claim was once raised against the wrong one, so
 * quietly resolving a typed name to whichever row matched first would rebuild
 * that fault inside a convenience.
 */
/**
 * Recording where a claim's money actually goes.
 *
 * Scoped to one claim rather than to the payee directory, because that is the
 * only context Finance ever touches an account number in. Refused once the
 * claim has been authorised for payment — changing the account after the
 * maker-checker has run is the swap-before-payment risk that check exists to
 * stop.
 *
 * The endpoint has existed since the first release and had no caller until 10
 * September 2026. Nothing in the browser could record a bank account, so a
 * payee without one was a dead end with no way out of it.
 */
export const setBankDetails = (
  requestId: string,
  bankName: string,
  bankAccountNumber: string,
) =>
  api.put<unknown>(`/api/v1/requests/${requestId}/beneficiary/bank-details`, {
    bankName,
    bankAccountNumber,
  });

export const createBeneficiary = (name: string) =>
  api.post<BeneficiarySummary>("/api/v1/beneficiaries", { name });

export const setReceiptStatus = (
  requestId: string,
  receiptStatus: "Yes" | "No" | "Incomplete",
) => api.patch<{ requestId: string; requestNumber: string; receiptStatus: string }>(
  `/api/v1/requests/${requestId}/receipt-status`,
  { receiptStatus },
);

export const setAllocation = (
  requestId: string,
  body: { projectCode?: string; costCentreCode?: string; reason: string },
) => api.patch<{ requestId: string; requestNumber: string; allocation: string }>(
  `/api/v1/requests/${requestId}/allocation`,
  body,
);
