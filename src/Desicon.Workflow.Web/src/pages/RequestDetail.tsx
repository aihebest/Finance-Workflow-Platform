import { useCallback, useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import {
  captureGlLines,
  confirmRefund,
  executePayment,
  executeAction,
  getHistory,
  getRequest,
} from "../api/requests";
import {
  ACTION_LABELS,
  ApiError,
  type AuditEntry,
  type AvailableAction,
  type GlLineInput,
} from "../api/types";
import { Money } from "../components/Money";

type Detail = Record<string, unknown>;

type ExpenseLine = {
  lineId?: string;
  lineNumber?: number;
  description?: string;
  expenseDate?: string;
  projectCode?: string | null;
  costCentreCode?: string | null;
  amountNgn?: number;
};

type GlLineRow = GlLineInput & { key: number };

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

/**
 * Actions that carry captured data and therefore have their own endpoint and
 * their own panel below. They are excluded from the generic button row: a bare
 * button for POST would send the action with no journal attached, the guard
 * would refuse it for having no GL lines, and the message would be true but
 * useless -- there was nowhere to enter them.
 */
const CAPTURE_ACTIONS = new Set(["POST", "EXECUTE_PAYMENT", "CONFIRM_REFUND"]);

const blankGlLine = (key: number): GlLineRow => ({
  key,
  side: "Debit",
  accountNumber: "",
  narration: "",
  amountNgn: 0,
});

const money = (value: number) =>
  new Intl.NumberFormat("en-NG", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
    value,
  );

export function RequestDetail() {
  const { id = "" } = useParams();

  const [detail, setDetail] = useState<Detail | null>(null);
  const [history, setHistory] = useState<AuditEntry[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [comment, setComment] = useState("");

  // Capture state. Kept separate from `detail` because these are things the
  // user is typing, not things the server has said.
  const [jvNumber, setJvNumber] = useState("");
  const [glLines, setGlLines] = useState<GlLineRow[]>([blankGlLine(1), blankGlLine(2)]);
  const [paymentReference, setPaymentReference] = useState("");
  const [paymentDate, setPaymentDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [refundAmount, setRefundAmount] = useState("");
  const [treasuryNumber, setTreasuryNumber] = useState("");

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

  /** Shared wrapper: every capture panel and the generic buttons run through this. */
  async function run(work: () => Promise<unknown>) {
    setBusy(true);
    try {
      await work();
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

  async function act(action: string) {
    if (REQUIRES_COMMENT.has(action) && comment.trim().length === 0) {
      setError(`${ACTION_LABELS[action] ?? action} needs a reason.`);
      return;
    }

    // TreasuryNumber is the one capture the generic /actions endpoint stages
    // onto the entity itself, so VERIFY at FINANCE_VERIFY can send it inline
    // rather than needing an endpoint of its own.
    const payload =
      action === "VERIFY" && treasuryNumber.trim().length > 0
        ? { TreasuryNumber: treasuryNumber.trim() }
        : undefined;

    await run(() => executeAction(id, action, comment.trim() || undefined, payload));
  }

  if (!detail) {
    return error ? (
      <p className="rounded bg-red-50 p-4 text-red-800">{error}</p>
    ) : (
      <p className="p-4 text-gray-500">Loading…</p>
    );
  }

  const availableActions = (detail.availableActions as AvailableAction[] | undefined) ?? [];
  const requestNumber = String(detail.requestNumber ?? "");
  const currentState = String(detail.currentState ?? "");
  const total = Number(detail.totalAmountNgn ?? 0);
  const netPayable = Number(detail.netPayableNgn ?? total);
  const lines = (detail.lines as ExpenseLine[] | undefined) ?? [];
  const postedGlLines = (detail.glPostingLines as GlLineInput[] | undefined) ?? [];

  const genericActions = availableActions.filter((a) => !CAPTURE_ACTIONS.has(a.action));

  /**
   * Authorised, whether or not the guard passes yet.
   *
   * This is what the capture panels key on, and the distinction is the whole
   * point: POST's guard requires balanced GL lines, so gating the journal grid
   * on the guard passing means the grid never appears and the lines can never
   * be entered. Same for the Treasury number and the refund amount.
   */
  const can = (action: string) => availableActions.some((a) => a.action === action);

  const blockedReason = (action: string) =>
    availableActions.find((a) => a.action === action && !a.isEnabled)?.blockedReason ?? null;

  const debitTotal = glLines.reduce((sum, l) => sum + (l.side === "Debit" ? l.amountNgn : 0), 0);
  const creditTotal = glLines.reduce((sum, l) => sum + (l.side === "Credit" ? l.amountNgn : 0), 0);
  const balanced = debitTotal === creditTotal && debitTotal > 0;

  function updateGlLine(key: number, patch: Partial<GlLineInput>) {
    setGlLines((rows) => rows.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  }

  /**
   * Whether something typed on this screen resolves a block the server
   * reported.
   *
   * `isEnabled` answers "could you take this action against what is stored".
   * For an action that carries its own captured data, the answer is no right
   * up until the moment it is taken — the Treasury number travels *with*
   * VERIFY, so it cannot be stored before the click that stores it. Gating the
   * button on the server's answer alone means typing the number changes
   * nothing and the button never comes alive.
   *
   * This is the same deadlock as the one that hid the field, one layer up:
   * fixing where the input renders was not enough while the button still
   * asked the impossible question.
   *
   * Optimistic, and safe to be. The guard runs again inside the transaction;
   * if this screen is wrong the transition is refused and the guardMessage
   * comes back verbatim. The cost of being wrong is a round trip. The cost of
   * being conservative is a state nobody can leave.
   */
  function locallyUnblocked(action: string) {
    return (
      action === "VERIFY" &&
      currentState === "FINANCE_VERIFY" &&
      treasuryNumber.trim().length > 0
    );
  }

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

        {/* Net payable differs from the total only when an advance was taken
            against this claim, and the difference is the whole point of the
            REFUND_DUE branch -- so it is shown only when it says something the
            total does not. */}
        {netPayable !== total && (
          <p className="mt-1 text-sm text-gray-700">
            Net payable <Money amountNgn={netPayable} />
            {netPayable < 0 ? " · refund due from employee" : ""}
          </p>
        )}

        {detail.paymentReference ? (
          <p className="mt-1 text-sm text-gray-700">
            Paid · reference {String(detail.paymentReference)}
          </p>
        ) : null}
      </header>

      {error && (
        <p role="alert" className="rounded bg-red-50 p-4 text-red-800">
          {error}
        </p>
      )}

      {/* What is actually being approved. An approver looking at a single
          total is approving a number, not a claim -- the paper form put the
          itemised table directly above the signature block for that reason. */}
      {lines.length > 0 && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Details of expense
          </h2>
          <div className="mt-3 overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                  <th className="py-2 pr-2 font-medium">#</th>
                  <th className="py-2 pr-2 font-medium">Date</th>
                  <th className="py-2 pr-2 font-medium">Description</th>
                  <th className="py-2 pr-2 font-medium">Project / Cost centre</th>
                  <th className="py-2 pl-2 text-right font-medium">Amount</th>
                </tr>
              </thead>
              <tbody>
                {lines.map((line, index) => (
                  <tr key={line.lineId ?? index} className="border-b border-gray-100">
                    <td className="py-2 pr-2 text-gray-500">{line.lineNumber ?? index + 1}.0</td>
                    <td className="py-2 pr-2 tabular-nums">
                      {line.expenseDate ? String(line.expenseDate).slice(0, 10) : "—"}
                    </td>
                    <td className="py-2 pr-2">{line.description}</td>
                    <td className="py-2 pr-2 text-gray-600">
                      {line.projectCode ?? line.costCentreCode ?? "—"}
                    </td>
                    <td className="py-2 pl-2 text-right tabular-nums">
                      {money(Number(line.amountNgn ?? 0))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {/* --- GL posting (POSTING → AUTHORISATION) --- */}
      {can("POST") && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            GL posting
          </h2>
          <p className="mt-1 text-sm text-gray-600">
            {blockedReason("POST") ??
              "Debits must equal credits and at least two lines are required. Whoever posts here cannot be the person who authorises it."}
          </p>

          <label className="mt-3 block text-sm text-gray-700" htmlFor="jv">
            Journal voucher number
          </label>
          <input
            id="jv"
            value={jvNumber}
            onChange={(e) => setJvNumber(e.target.value)}
            className="mt-1 w-full max-w-xs rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
          />

          <div className="mt-4 overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                  <th className="py-2 pr-2 font-medium">Side</th>
                  <th className="py-2 pr-2 font-medium">Account</th>
                  <th className="py-2 pr-2 font-medium">Narration</th>
                  <th className="py-2 pl-2 text-right font-medium">Amount</th>
                </tr>
              </thead>
              <tbody>
                {glLines.map((line) => (
                  <tr key={line.key} className="border-b border-gray-100">
                    <td className="py-1 pr-2">
                      <select
                        aria-label={`Side, line ${line.key}`}
                        value={line.side}
                        onChange={(e) =>
                          updateGlLine(line.key, { side: e.target.value as "Debit" | "Credit" })
                        }
                        className="min-h-11 rounded border border-gray-300 p-2 text-sm"
                      >
                        <option value="Debit">Debit</option>
                        <option value="Credit">Credit</option>
                      </select>
                    </td>
                    <td className="py-1 pr-2">
                      <input
                        aria-label={`Account number, line ${line.key}`}
                        value={line.accountNumber}
                        onChange={(e) => updateGlLine(line.key, { accountNumber: e.target.value })}
                        className="min-h-11 w-32 rounded border border-gray-300 p-2 text-sm"
                      />
                    </td>
                    <td className="py-1 pr-2">
                      <input
                        aria-label={`Narration, line ${line.key}`}
                        value={line.narration}
                        onChange={(e) => updateGlLine(line.key, { narration: e.target.value })}
                        className="min-h-11 w-full rounded border border-gray-300 p-2 text-sm"
                      />
                    </td>
                    <td className="py-1 pl-2">
                      <input
                        aria-label={`Amount, line ${line.key}`}
                        type="number"
                        step="0.01"
                        min="0"
                        value={line.amountNgn || ""}
                        onChange={(e) =>
                          updateGlLine(line.key, { amountNgn: Number(e.target.value) || 0 })
                        }
                        className="min-h-11 w-32 rounded border border-gray-300 p-2 text-right text-sm tabular-nums"
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <td colSpan={3} className="pt-3 text-right text-sm text-gray-600">
                    Debits
                  </td>
                  <td className="pt-3 pl-2 text-right text-sm tabular-nums">{money(debitTotal)}</td>
                </tr>
                <tr>
                  <td colSpan={3} className="text-right text-sm text-gray-600">
                    Credits
                  </td>
                  <td className="pl-2 text-right text-sm tabular-nums">{money(creditTotal)}</td>
                </tr>
                <tr>
                  <td colSpan={3} className="pt-1 text-right text-sm font-medium text-gray-700">
                    Difference
                  </td>
                  <td
                    className={
                      balanced
                        ? "pt-1 pl-2 text-right text-sm font-medium tabular-nums text-green-700"
                        : "pt-1 pl-2 text-right text-sm font-medium tabular-nums text-red-700"
                    }
                  >
                    {money(debitTotal - creditTotal)}
                  </td>
                </tr>
              </tfoot>
            </table>
          </div>

          <div className="mt-3 flex flex-wrap gap-2">
            <button
              type="button"
              onClick={() => setGlLines((rows) => [...rows, blankGlLine(rows.length + 1)])}
              className="min-h-11 rounded border border-gray-400 px-4 py-2 text-sm font-medium text-gray-800 hover:bg-gray-50"
            >
              Add line
            </button>
            <button
              type="button"
              disabled={busy || !balanced || jvNumber.trim().length === 0}
              onClick={() =>
                void run(() =>
                  captureGlLines(
                    id,
                    jvNumber.trim(),
                    glLines.map(({ key: _key, ...line }) => line),
                    comment.trim() || undefined,
                  ),
                )
              }
              className="min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
            >
              Post journal
            </button>
          </div>
        </section>
      )}

      {/* Posted lines, once they exist -- what the checker is authorising. */}
      {postedGlLines.length > 0 && !can("POST") && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Posted journal
            {detail.journalVoucherNumber ? ` · JV ${String(detail.journalVoucherNumber)}` : ""}
          </h2>
          <table className="mt-3 w-full text-sm">
            <tbody>
              {postedGlLines.map((line, index) => (
                <tr key={index} className="border-b border-gray-100">
                  <td className="py-2 pr-2 text-gray-600">{line.side}</td>
                  <td className="py-2 pr-2 tabular-nums">{line.accountNumber}</td>
                  <td className="py-2 pr-2">{line.narration}</td>
                  <td className="py-2 pl-2 text-right tabular-nums">
                    {money(Number(line.amountNgn ?? 0))}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      {/* --- Payment (AWAITING_PAYMENT → AWAITING_ACK) --- */}
      {can("EXECUTE_PAYMENT") && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Release payment
          </h2>
          <p className="mt-1 text-sm text-gray-600">
            Payment method is {String(detail.paymentMethod ?? "")}, derived from the amount against
            the policy threshold — it is not chosen here. The reference is what the beneficiary
            will be asked to confirm against.
          </p>

          <div className="mt-3 flex flex-wrap gap-4">
            <div>
              <label className="block text-sm text-gray-700" htmlFor="payment-reference">
                Payment reference
              </label>
              <input
                id="payment-reference"
                value={paymentReference}
                onChange={(e) => setPaymentReference(e.target.value)}
                placeholder="Bank transfer or cash voucher reference"
                className="mt-1 min-h-11 w-72 rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
              />
            </div>
            <div>
              <label className="block text-sm text-gray-700" htmlFor="payment-date">
                Payment date
              </label>
              <input
                id="payment-date"
                type="date"
                value={paymentDate}
                onChange={(e) => setPaymentDate(e.target.value)}
                className="mt-1 min-h-11 rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
              />
            </div>
          </div>

          <button
            type="button"
            disabled={busy || paymentReference.trim().length === 0}
            onClick={() =>
              void run(() =>
                executePayment(
                  id,
                  paymentReference.trim(),
                  paymentDate ? `${paymentDate}T00:00:00Z` : undefined,
                  comment.trim() || undefined,
                ),
              )
            }
            className="mt-3 min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
          >
            Record payment
          </button>
        </section>
      )}

      {/* --- Refund received (REFUND_DUE → POSTING) --- */}
      {can("CONFIRM_REFUND") && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Refund received
          </h2>
          <p className="mt-1 text-sm text-gray-600">
            The employee spent less than the advance taken. This must equal{" "}
            <Money amountNgn={Math.abs(netPayable)} /> exactly — the guard compares to the naira.
          </p>
          <input
            aria-label="Refund amount received"
            type="number"
            step="0.01"
            value={refundAmount}
            onChange={(e) => setRefundAmount(e.target.value)}
            className="mt-3 min-h-11 w-48 rounded border border-gray-300 p-2 text-right text-sm tabular-nums"
          />
          <button
            type="button"
            disabled={busy || Number(refundAmount) <= 0}
            onClick={() =>
              void run(() => confirmRefund(id, Number(refundAmount), comment.trim() || undefined))
            }
            className="ml-2 min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
          >
            Confirm refund
          </button>
        </section>
      )}

      {availableActions.length > 0 && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Available actions
          </h2>

          {/* Treasury number is a guard field on VERIFY at Finance
              Verification -- without it the transition is refused, so the
              field appears with the action that needs it rather than on a
              separate screen. */}
          {currentState === "FINANCE_VERIFY" && can("VERIFY") && (
            <>
              <label className="mt-3 block text-sm text-gray-700" htmlFor="treasury">
                Treasury number
              </label>
              <input
                id="treasury"
                value={treasuryNumber}
                onChange={(e) => setTreasuryNumber(e.target.value)}
                className="mt-1 min-h-11 w-64 rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
              />
            </>
          )}

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
            {genericActions.map(({ action, isEnabled }) => (
              <button
                key={action}
                type="button"
                disabled={busy || !(isEnabled || locallyUnblocked(action))}
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

          {/* Why a disabled button is disabled, in the definition's own words.
              Without this the screen shows a greyed-out Verify and no
              explanation, which is barely better than showing nothing --
              the guardMessage names the field to fill in. */}
          {/* Reasons disappear as they are resolved — a message still sitting
              under a button that has just come alive reads as a contradiction. */}
          {genericActions.some((a) => !a.isEnabled && !locallyUnblocked(a.action)) && (
            <ul className="mt-3 space-y-1">
              {genericActions
                .filter((a) => !a.isEnabled && !locallyUnblocked(a.action) && a.blockedReason)
                .map((a) => (
                  <li key={a.action} className="text-sm text-amber-800">
                    <span className="font-medium">{ACTION_LABELS[a.action] ?? a.action}:</span>{" "}
                    {a.blockedReason}
                  </li>
                ))}
            </ul>
          )}
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
