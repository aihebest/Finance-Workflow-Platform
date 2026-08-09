import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { getOutstandingAdvances, retireAdvance } from "../api/requests";
import { ApiError, type OutstandingAdvance } from "../api/types";
import { Money } from "../components/Money";

/**
 * Advances still carrying a balance, and the one screen that makes an overdue
 * one impossible to ignore.
 *
 * The form says the advance is the recipient's personal liability until it is
 * justified. That sentence only means anything if the person can see what they
 * owe and by when, which is what this is for.
 */
export function MyAdvances() {
  const navigate = useNavigate();

  const [advances, setAdvances] = useState<OutstandingAdvance[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setAdvances(await getOutstandingAdvances());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  /**
   * Starts a retirement and goes straight to the claim it created.
   *
   * An advance is retired *by* an expense claim, so the server builds a linked
   * draft carrying the outstanding balance as its Cash Advance Taken. Landing
   * on it rather than returning here matters: the draft is not the end of the
   * job, and a screen that said "retirement started" and stayed put would
   * leave people believing it was.
   */
  async function retire(id: string) {
    setBusyId(id);
    try {
      const draft = await retireAdvance(id);
      navigate(`/requests/${draft.expenseRequestId}`);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : (e as Error).message);
    } finally {
      setBusyId(null);
    }
  }

  if (!advances) {
    return error ? (
      <p className="rounded bg-red-50 p-4 text-red-800">{error}</p>
    ) : (
      <p className="p-4 text-gray-500">Loading…</p>
    );
  }

  return (
    <div className="space-y-4">
      {error && (
        <p role="alert" className="rounded bg-red-50 p-4 text-red-800">
          {error}
        </p>
      )}

      {advances.length === 0 ? (
        <p className="rounded border border-gray-200 bg-white p-4 text-sm text-gray-600">
          You have no advances outstanding.
        </p>
      ) : (
        advances.map((advance) => {
          const overdue = advance.retirementStatus === "Overdue";
          const due = advance.retirementDueDate
            ? new Date(advance.retirementDueDate).toLocaleString("en-NG")
            : null;

          return (
            <article
              key={advance.requestId}
              className={
                overdue
                  ? "rounded border border-red-300 bg-red-50 p-4"
                  : "rounded border border-gray-200 bg-white p-4"
              }
            >
              <div className="flex flex-wrap items-baseline justify-between gap-2">
                <button
                  type="button"
                  onClick={() => navigate(`/requests/${advance.requestId}`)}
                  className="text-lg font-semibold text-blue-700 hover:underline"
                >
                  {advance.requestNumber}
                </button>
                <Money amountNgn={advance.retirementBalanceNgn} />
              </div>

              {/* Taken and retired shown alongside the balance: after a partial
                  retirement the balance alone does not explain itself. */}
              <p className="mt-1 text-sm text-gray-600">
                Taken <Money amountNgn={advance.totalAmountNgn} />
                {advance.retiredAmountNgn > 0 ? (
                  <>
                    {" · retired "}
                    <Money amountNgn={advance.retiredAmountNgn} />
                  </>
                ) : null}
              </p>

              {due && (
                <p className={overdue ? "mt-1 text-sm font-medium text-red-800" : "mt-1 text-sm text-gray-700"}>
                  {overdue
                    ? `Overdue since ${due}${advance.ageingDays ? ` · ${advance.ageingDays} day${advance.ageingDays === 1 ? "" : "s"}` : ""}`
                    : `Retire by ${due}`}
                </p>
              )}

              {overdue && (
                <p className="mt-2 text-sm text-red-800">
                  This advance is your liability until it is justified, and you cannot request
                  another while it is outstanding.
                </p>
              )}

              <button
                type="button"
                disabled={busyId === advance.requestId}
                onClick={() => void retire(advance.requestId)}
                className="mt-3 min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
              >
                Retire this advance
              </button>
            </article>
          );
        })
      )}
    </div>
  );
}
