import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { getMyApprovals } from "../api/requests";
import { ACTION_LABELS, MODULE_LABELS, type MyApprovals as MyApprovalsData } from "../api/types";
import { Money } from "../components/Money";

/**
 * What you have already decided.
 *
 * Reported by Cost Control on 24 August 2026: "The System does not show the
 * history or trail of request that have been approved by cost control."
 *
 * The gap was total. My Inbox shows what is waiting on you and empties the
 * instant you act; My Requests and My Advances show what you raised. Nothing
 * showed what you had decided. A desk verifying several advances a day could
 * not answer "did I already pass that one?" without asking somebody.
 *
 * Odd company for that gap to keep: every action has been written to a
 * hash-chained, append-only audit trail since the first release. The record was
 * complete and had no reader — kept for an auditor who might come one day, and
 * withheld from the person who produced it.
 */
export function MyApprovals() {
  const [data, setData] = useState<MyApprovalsData | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    getMyApprovals()
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e: Error) => {
        if (!cancelled) setError(e.message);
      });

    return () => {
      cancelled = true;
    };
  }, []);

  if (error) {
    return (
      <p role="alert" className="rounded bg-red-50 p-4 text-red-800">
        {error}
      </p>
    );
  }

  if (!data) {
    return <p className="p-4 text-sm text-gray-500">Loading…</p>;
  }

  return (
    <div className="space-y-6">
      <header className="rounded border border-gray-200 bg-white p-4">
        <h1 className="text-lg font-semibold text-gray-900">My Approvals</h1>
        <p className="mt-1 text-sm text-gray-600">
          Everything you have acted on, most recent first.{" "}
          {data.totals.count === 0
            ? "Nothing yet."
            : `${data.totals.count} request${data.totals.count === 1 ? "" : "s"}, ${
                data.totals.stillOpen
              } still open.`}
        </p>
      </header>

      {data.approvals.length > 0 && (
        <section className="rounded border border-gray-200 bg-white p-4">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                <th className="py-2 pr-2 font-medium">Request</th>
                <th className="py-2 pr-2 font-medium">Raised by</th>
                <th className="py-2 pr-2 font-medium">What I did</th>
                <th className="py-2 pr-2 font-medium">When</th>
                <th className="py-2 pr-2 font-medium">Where it is now</th>
                <th className="py-2 pl-2 text-right font-medium">Amount</th>
              </tr>
            </thead>
            <tbody>
              {data.approvals.map((row) => (
                <tr key={row.requestId} className="border-b border-gray-100">
                  <td className="py-2 pr-2">
                    <Link
                      to={`/requests/${row.requestId}`}
                      className="text-blue-700 hover:underline"
                    >
                      {row.requestNumber}
                    </Link>
                    <span className="ml-2 text-xs text-gray-500">
                      {MODULE_LABELS[row.moduleKey] ?? row.moduleKey}
                    </span>
                  </td>
                  <td className="py-2 pr-2 text-gray-700">{row.requester ?? "—"}</td>
                  <td className="py-2 pr-2 font-medium text-gray-900">
                    {ACTION_LABELS[row.myAction] ?? row.myAction}
                  </td>
                  <td className="py-2 pr-2 text-gray-600">
                    {new Date(row.myActionAt).toLocaleDateString("en-NG", {
                      day: "numeric",
                      month: "short",
                      year: "numeric",
                    })}
                  </td>
                  <td className="py-2 pr-2 text-gray-600">
                    {row.currentState.replaceAll("_", " ").toLowerCase()}
                    {row.isClosed ? (
                      <span className="ml-2 text-xs text-gray-400">closed</span>
                    ) : null}
                  </td>
                  <td className="py-2 pl-2 text-right tabular-nums">
                    <Money amountNgn={row.totalAmountNgn} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}
