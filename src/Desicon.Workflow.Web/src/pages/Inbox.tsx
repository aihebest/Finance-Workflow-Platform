import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { getInbox } from "../api/requests";
import { MODULE_LABELS, type RequestSummary } from "../api/types";
import { Money } from "../components/Money";
import { Sla } from "../components/Sla";

/**
 * Items awaiting my action.
 *
 * The API already returns these ordered by SlaDueAt ascending, nulls last, and
 * that order is deliberately not re-sorted here. Two sorts of the same list in
 * two places is how they drift, and the server's is the one the dashboards and
 * the escalation sweep also reason about.
 *
 * Mobile-first: a department head approving from a site is the normal case,
 * not the exception. Cards stack on small screens and become a table only when
 * there is room, rather than a table that scrolls sideways on a phone.
 */
export function Inbox() {
  const [items, setItems] = useState<RequestSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getInbox()
      .then(setItems)
      .catch((e: Error) => setError(e.message));
  }, []);

  if (error) {
    return <p className="rounded bg-red-50 p-4 text-red-800">{error}</p>;
  }

  if (!items) {
    return <p className="p-4 text-gray-500">Loading…</p>;
  }

  if (items.length === 0) {
    return (
      <div className="rounded border border-gray-200 bg-white p-8 text-center">
        <p className="text-gray-700">Nothing is waiting for you.</p>
      </div>
    );
  }

  return (
    <ul className="space-y-3">
      {items.map((item) => (
        <li key={item.requestId}>
          <Link
            to={`/requests/${item.requestId}`}
            className="block rounded border border-gray-200 bg-white p-4 hover:border-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-600"
          >
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <span className="font-semibold text-gray-900">{item.requestNumber}</span>
              <Sla slaDueAt={item.slaDueAt} />
            </div>

            <div className="mt-2 flex flex-wrap items-baseline justify-between gap-2 text-sm text-gray-600">
              <span>{MODULE_LABELS[item.moduleKey] ?? item.moduleKey}</span>
              <Money amountNgn={item.totalAmountNgn} />
            </div>

            <p className="mt-1 text-sm text-gray-500">
              Waiting at {item.currentState.replaceAll("_", " ").toLowerCase()}
            </p>
          </Link>
        </li>
      ))}
    </ul>
  );
}
