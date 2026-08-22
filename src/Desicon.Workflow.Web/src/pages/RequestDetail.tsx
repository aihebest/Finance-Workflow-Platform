import { useCallback, useEffect, useState } from "react";
import { useParams } from "react-router";
import {
  confirmRefund,
  executeAction,
  executePayment,
  getHistory,
  getRequest,
  markPosted,
} from "../api/requests";
import { ACTION_LABELS, ApiError, type AuditEntry, type AvailableAction } from "../api/types";
import { Attachments } from "../components/Attachments";
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
 * button for MARK_POSTED would send the action with no BC document number,
 * the guard would refuse it for lacking one, and the message would be true but
 * useless -- there was nowhere to type it.
 */
const CAPTURE_ACTIONS = new Set(["MARK_POSTED", "EXECUTE_PAYMENT", "CONFIRM_REFUND"]);

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
  const [bcDocumentNumber, setBcDocumentNumber] = useState("");
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
    // onto the entity itself, so VERIFY at COST_CONTROL_VERIFY can send it
    // inline rather than needing an endpoint of its own.
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

  // Null for a cash advance, which pays its requester, and on a claim whose
  // beneficiary the API has not resolved yet.
  const payee = detail.beneficiary as
    | { name: string; type: string; staffNumber: string | null; email: string | null }
    | null
    | undefined;
  const genericActions = availableActions.filter((a) => !CAPTURE_ACTIONS.has(a.action));

  /**
   * Authorised, whether or not the guard passes yet.
   *
   * This is what the capture panels key on, and the distinction is the whole
   * point: MARK_POSTED's guard requires a BC document number, so gating the
   * field on the guard passing means the field never appears and the number
   * can never be entered. Same for the Treasury number and the refund amount.
   */
  const can = (action: string) => availableActions.some((a) => a.action === action);

  const blockedReason = (action: string) =>
    availableActions.find((a) => a.action === action && !a.isEnabled)?.blockedReason ?? null;

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
      currentState === "COST_CONTROL_VERIFY" &&
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

        {/* Who gets the money.

            This screen showed no payee at all until 9 August 2026 — the API
            returned a beneficiary id and nothing rendered it. Every approver
            in the chain, up to and including the Director of Finance whose
            entire function is authorising money to leave, was approving a
            payment without the recipient appearing anywhere on the page.

            It surfaced when a claim was raised against the wrong one of two
            employees sharing a display name and was paid. Nobody was careless;
            there was nothing on screen to be careful about. Staff number and
            email are here for that reason — a name is not an identifier. */}
        {payee && (
          <p className="mt-1 text-sm text-gray-800">
            Payable to <span className="font-medium">{payee.name}</span>
            {payee.staffNumber || payee.email ? (
              <span className="text-gray-600">
                {payee.staffNumber ? ` · ${payee.staffNumber}` : ""}
                {payee.email ? ` · ${payee.email}` : ""}
              </span>
            ) : (
              <span className="text-gray-600"> · {payee.type}</span>
            )}
          </p>
        )}

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

      {/* Evidence sits directly above the actions, so an approver sees what
          was purchased in the same glance as the button that approves it.
          Reloading on change matters: Cost Control's guard counts attachments,
          so uploading one can turn a disabled Verify live. */}
      <Attachments
        requestId={id}
        canUpload={!detail.closedAt}
        onChanged={() => void load()}
      />

      {/* --- Business Central posting (AWAITING_POSTING) --- */}
      {can("MARK_POSTED") && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Posting in Business Central
          </h2>
          <p className="mt-1 text-sm text-gray-600">
            Post this in Business Central first, then record the document number here. The journal
            itself lives in BC — this records that it was posted and where to find it.
          </p>

          <label className="mt-3 block text-sm text-gray-700" htmlFor="bc-document">
            BC document number
          </label>
          <input
            id="bc-document"
            value={bcDocumentNumber}
            onChange={(e) => setBcDocumentNumber(e.target.value)}
            placeholder="Business Central posting or document number"
            className="mt-1 min-h-11 w-72 rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
          />

          <p className="mt-2 text-sm text-gray-500">
            {netPayable > 0
              ? "Once posted, this moves to payment."
              : "Nothing is payable on this claim, so it closes once posted."}
          </p>

          <button
            type="button"
            disabled={busy || bcDocumentNumber.trim().length === 0}
            onClick={() =>
              void run(() => markPosted(id, bcDocumentNumber.trim(), comment.trim() || undefined))
            }
            className="mt-3 min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
          >
            Mark posted in BC
          </button>
        </section>
      )}

      {/* Once recorded, the reference is the thread back to the ledger. Shown
          to everyone who can see the request, not just Accounts: "where is this
          in BC" is the question asked when a payment is queried. */}
      {detail.bcDocumentNumber && !can("MARK_POSTED") ? (
        <section className="rounded border border-gray-200 bg-white p-4">
          <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
            Business Central
          </h2>
          <p className="mt-2 text-sm text-gray-700">
            Posted as document{" "}
            <span className="font-medium tabular-nums">{String(detail.bcDocumentNumber)}</span>
          </p>
        </section>
      ) : null}

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

          {/* The guard's own words when payment is blocked — most often that
              the beneficiary has no account number on file, which is fixable
              and which nothing else on this screen would explain. */}
          {blockedReason("EXECUTE_PAYMENT") && (
            <p className="mt-2 text-sm text-amber-800">{blockedReason("EXECUTE_PAYMENT")}</p>
          )}

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
            disabled={
              busy ||
              paymentReference.trim().length === 0 ||
              blockedReason("EXECUTE_PAYMENT") !== null
            }
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
          {currentState === "COST_CONTROL_VERIFY" && can("VERIFY") && (
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
