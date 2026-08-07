import { api } from "./client";
import type {
  AuditEntry,
  BeneficiarySummary,
  ExpenseDraftInput,
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
