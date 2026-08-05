import { api } from "./client";
import type { AuditEntry, RequestSummary } from "./types";

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
