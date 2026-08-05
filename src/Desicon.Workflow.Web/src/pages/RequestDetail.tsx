import { useCallback, useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { executeAction, getHistory, getRequest } from "../api/requests";
import { ACTION_LABELS, ApiError, type AuditEntry } from "../api/types";
import { Money } from "../components/Money";

type Detail = Record<string, unknown>;

/**
 * Actions requiring a written reason.
 *
 * The workflow definitions mark RETURN and REJECT as requiring a comment, and
 * the API refuses them without one. Asking here rather than discovering the
 * refusal keeps the round trip out of the way -- and a rejection with no
 * stated reason is exactly what the paper process avoided by having a person
 * write on the form.
 */
const REQUIRES_COMMENT = new Set(["RETURN", "REJECT"]);

export function RequestDetail() {
  const { id = "" } = useParams();

  const [detail, setDetail] = useState<Detail | null>(null);
  const [history, setHistory] = useState<AuditEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [comment, setComment] = useState("");

  const load = useCallback(async () => {
    try {
      const [d, h] = await Promise.all([getRequest(id), getHistory(id)]);
      setDetail(d);
      setHistory(h);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, [id]);

  useEffect(() => {
    void load();
  }, [load]);

  async function act(action: string) {
    if (REQUIRES_COMMENT.has(action) && comment.trim().length === 0) {
      setError(`${ACTION_LABELS[action] ?? action} needs a reason.`);
      return;
    }

    setBusy(true);
    try {
      await executeAction(id, action, comment.trim() || undefined);
      setComment("");
      await load();
    } catch (e) {
      // A guard rejection arrives as ProblemDetails with a human-readable
      // detail. Show it verbatim: it names the condition that failed, which
      // is more use than anything this screen could invent.
      setError(e instanceof ApiError ? e.message : (e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (!detail) {
    return error ? (
      <p className="rounded bg-red-50 p-4 text-red-800">{error}</p>
    ) : (
      <p className="p-4 text-gray-500">Loading…</p>
    );
  }

  const availableActions = (detail.availableActions as string[] | undefined) ?? [];
  const requestNumber = String(detail.requestNumber ?? "");
  const currentState = String(detail.currentState ?? "");
  const total = Number(detail.totalAmountNgn ?? 0);

  return (
    <div className="space-y-6">
      <header className="rounded border border-gray-200 bg-white p-4">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h1 className="text-xl font-semibold text-gray-900">{requestNumber}</h1>
          <Money amountNgn={total} />
        </div>
        <p className="mt-1 text-sm text-gray-600">
          {currentState.replaceAll("_", " ")}
          {detail.formCode ? ` · ${String(detail.formCode)} ${String(detail.formRevision ?? "")}` : ""}
        </p>
      </header>

      {error && (
        <p role="alert" className="rounded bg-red-50 p-4 text-red-800">
          {error}
        </p>
      )}

      {availableActions.length > 0 && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Available actions
          </h2>

          <label className="mt-3 block text-sm text-gray-700" htmlFor="comment">
            Comment
          </label>
          <textarea
            id="comment"
            value={comment}
            onChange={(e) => setComment(e.target.value)}
            rows={2}
            className="mt-1 w-full rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
            placeholder="Required when returning or rejecting"
          />

          {/* Buttons read Verify, Approve and Endorse -- the words printed on
              DEL-AC-FRM-002. A clerk should not have to learn a new vocabulary
              to do the job they already do. Large tap targets because the
              approval path is used from a phone. */}
          <div className="mt-3 flex flex-wrap gap-2">
            {availableActions.map((action) => (
              <button
                key={action}
                type="button"
                disabled={busy}
                onClick={() => void act(action)}
                className={
                  action === "REJECT"
                    ? "min-h-11 rounded bg-red-700 px-4 py-2 font-medium text-white hover:bg-red-800 disabled:opacity-50"
                    : action === "RETURN"
                      ? "min-h-11 rounded border border-gray-400 px-4 py-2 font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
                      : "min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
                }
              >
                {ACTION_LABELS[action] ?? action}
              </button>
            ))}
          </div>
        </section>
      )}

      <section className="rounded border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">History</h2>
        <ol className="mt-3 space-y-3">
          {history.map((entry) => (
            <li key={entry.auditEventId} className="border-l-2 border-gray-200 pl-3">
              <p className="text-sm font-medium text-gray-900">
                {ACTION_LABELS[entry.eventType] ?? entry.eventType}
                {entry.fromState && entry.toState ? (
                  <span className="font-normal text-gray-500">
                    {" "}
                    · {entry.fromState} → {entry.toState}
                  </span>
                ) : null}
              </p>
              {entry.reason && <p className="text-sm text-gray-700">{entry.reason}</p>}
              <p className="text-xs text-gray-500">
                {new Date(entry.occurredAtUtc).toLocaleString("en-NG")}
                {/* On behalf of is where delegation shows, and where
                    EscalationSweep records who failed to act. Worth surfacing:
                    "who was this waiting on" is the question the screen exists
                    to answer. */}
                {entry.onBehalfOfUserId ? " · on behalf of another user" : ""}
              </p>
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}
