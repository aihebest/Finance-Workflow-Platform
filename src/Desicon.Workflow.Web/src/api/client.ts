import { acquireApiToken } from "../auth/msal";
import { ApiError, type ProblemDetails } from "./types";

const baseUrl = (import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");

/**
 * Fetch wrapper that attaches the Entra access token and turns a non-2xx
 * response into an ApiError carrying the ProblemDetails.
 *
 * The API answers a guard rejection with RFC 7807 and a `detail` written for a
 * person -- "cannot submit: RetirementBalanceNgn is outstanding" rather than a
 * status code. Replacing that with a generic message would throw away the only
 * explanation the user gets, and would leave them re-clicking a button that
 * cannot work.
 */
async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = await acquireApiToken();

  if (token === null) {
    // A redirect is in flight. Never resolves, and must never reject: the
    // page is unloading, and a rejection here surfaces as an error toast on
    // the way out.
    return new Promise<T>(() => {});
  }

  const response = await fetch(`${baseUrl}${path}`, {
    ...init,
    headers: {
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...init.headers,
      Authorization: `Bearer ${token}`,
    },
  });

  if (!response.ok) {
    let problem: ProblemDetails | null = null;

    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // Not every failure is ProblemDetails -- a 502 from Front Door is HTML.
    }

    throw new ApiError(
      response.status,
      problem,
      problem?.detail ?? problem?.title ?? `Request failed (${response.status}).`,
    );
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

// The init object is built conditionally rather than passing
// `body: undefined`. With exactOptionalPropertyTypes an explicit undefined is
// not the same as an absent property, and the strictness is worth keeping:
// it is the same class of distinction as a null connection string falling
// back to a default instead of failing.
export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(
      path,
      body === undefined
        ? { method: "POST" }
        : { method: "POST", body: JSON.stringify(body) },
    ),
  put: <T>(path: string, body: unknown) =>
    request<T>(path, { method: "PUT", body: JSON.stringify(body) }),
};
