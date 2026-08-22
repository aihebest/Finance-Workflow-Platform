import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api } from "../api/client";
import { Money } from "../components/Money";

/**
 * The two questions Finance could not answer without asking somebody.
 *
 * Everything before this was personal: My Inbox is what is waiting for *you*,
 * My Advances what *you* owe. Nobody could see the whole picture, so "how much
 * cash are we holding out there" and "why has that claim not been paid" were
 * answered by phoning Accounts.
 *
 * Deliberately two plain tables rather than charts. The questions are "who,
 * how much, how late" — every one of them is a row, and a row can be read
 * aloud on a phone call, which is what these figures are actually used for.
 */

type OutstandingAdvance = {
  requestId: string;
  requestNumber: string;
  requester: string;
  department: string;
  purpose: string;
  balanceNgn: number;
  totalAmountNgn: number;
  daysOverdue: number;
  isOverdue: boolean;
  dueAt: string | null;
};

type PipelineRequest = {
  requestId: string;
  requestNumber: string;
  moduleKey: string;
  currentState: string;
  totalAmountNgn: number;
  daysWaiting: number;
  holder: string | null;
  holderIsRole: boolean;
  slaBreached: boolean;
};

type ByState = {
  moduleKey: string;
  state: string;
  holder: string | null;
  holderIsRole: boolean;
  count: number;
  valueNgn: number;
  oldestDays: number;
  breachedCount: number;
};

const readable = (state: string) => state.replaceAll("_", " ").toLowerCase();

function Days({ days, late }: { days: number; late: boolean }) {
  return (
    <span className={late ? "font-medium text-red-700" : "text-gray-700"}>
      {days === 0 ? "today" : `${days} day${days === 1 ? "" : "s"}`}
    </span>
  );
}

export function Reports() {
  const [tab, setTab] = useState<"advances" | "pipeline">("advances");
  const [advances, setAdvances] = useState<{
    totals: { count: number; outstandingNgn: number; overdueCount: number; overdueNgn: number };
    byDepartment: { department: string; departmentName: string; count: number; outstandingNgn: number; overdueCount: number }[];
    advances: OutstandingAdvance[];
  } | null>(null);
  const [pipeline, setPipeline] = useState<{
    totals: { count: number; valueNgn: number; breachedCount: number };
    byState: ByState[];
    requests: PipelineRequest[];
  } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setError(null);

    const load =
      tab === "advances"
        ? api.get<NonNullable<typeof advances>>("/api/v1/reports/outstanding-advances").then(setAdvances)
        : api.get<NonNullable<typeof pipeline>>("/api/v1/reports/pipeline").then(setPipeline);

    load.catch((e: Error) => setError(e.message));
  }, [tab]);

  return (
    <div className="space-y-4">
      <div className="flex gap-2">
        {(["advances", "pipeline"] as const).map((t) => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            className={
              tab === t
                ? "rounded bg-blue-700 px-3 py-2 text-sm font-medium text-white"
                : "rounded border border-gray-300 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50"
            }
          >
            {t === "advances" ? "Outstanding advances" : "What's waiting on whom"}
          </button>
        ))}
      </div>

      {error && (
        <p role="alert" className="rounded bg-red-50 p-4 text-sm text-red-800">
          {error}
        </p>
      )}

      {tab === "advances" && advances && (
        <>
          {/* Overdue is called out separately from the total. A large balance
              retired on time is not a problem; the overdue figure is the one
              somebody has to do something about. */}
          <section className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <Figure label="Advances outstanding" value={advances.totals.count.toString()} />
            <Figure label="Total held" value={<Money amountNgn={advances.totals.outstandingNgn} />} />
            <Figure label="Overdue" value={advances.totals.overdueCount.toString()} alert={advances.totals.overdueCount > 0} />
            <Figure label="Overdue value" value={<Money amountNgn={advances.totals.overdueNgn} />} alert={advances.totals.overdueNgn > 0} />
          </section>

          <section className="rounded border border-gray-200 bg-white p-4">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">By department</h2>
            <table className="mt-3 w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                  <th className="py-2 pr-2 font-medium">Department</th>
                  <th className="py-2 pr-2 text-right font-medium">Advances</th>
                  <th className="py-2 pr-2 text-right font-medium">Overdue</th>
                  <th className="py-2 pl-2 text-right font-medium">Outstanding</th>
                </tr>
              </thead>
              <tbody>
                {advances.byDepartment.map((d) => (
                  <tr key={d.department} className="border-b border-gray-100">
                    <td className="py-2 pr-2">{d.departmentName}</td>
                    <td className="py-2 pr-2 text-right tabular-nums">{d.count}</td>
                    <td className={`py-2 pr-2 text-right tabular-nums ${d.overdueCount > 0 ? "font-medium text-red-700" : ""}`}>
                      {d.overdueCount}
                    </td>
                    <td className="py-2 pl-2 text-right tabular-nums">
                      <Money amountNgn={d.outstandingNgn} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>

          <section className="rounded border border-gray-200 bg-white p-4">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
              Every advance not yet retired
            </h2>
            {advances.advances.length === 0 ? (
              <p className="mt-3 text-sm text-gray-600">
                Nothing outstanding. Every advance released has been accounted for.
              </p>
            ) : (
              <div className="mt-3 overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                      <th className="py-2 pr-2 font-medium">Request</th>
                      <th className="py-2 pr-2 font-medium">Held by</th>
                      <th className="py-2 pr-2 font-medium">Dept</th>
                      <th className="py-2 pr-2 font-medium">Purpose</th>
                      <th className="py-2 pr-2 font-medium">Overdue by</th>
                      <th className="py-2 pl-2 text-right font-medium">Balance</th>
                    </tr>
                  </thead>
                  <tbody>
                    {advances.advances.map((a) => (
                      <tr key={a.requestId} className="border-b border-gray-100">
                        <td className="py-2 pr-2">
                          <Link to={`/requests/${a.requestId}`} className="text-blue-700 hover:underline">
                            {a.requestNumber}
                          </Link>
                        </td>
                        <td className="py-2 pr-2">{a.requester}</td>
                        <td className="py-2 pr-2 text-gray-600">{a.department}</td>
                        <td className="py-2 pr-2 text-gray-600">{a.purpose}</td>
                        <td className="py-2 pr-2">
                          {a.isOverdue ? <Days days={a.daysOverdue} late /> : <span className="text-gray-500">not yet due</span>}
                        </td>
                        <td className="py-2 pl-2 text-right tabular-nums">
                          <Money amountNgn={a.balanceNgn} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}

      {tab === "pipeline" && pipeline && (
        <>
          <section className="grid grid-cols-2 gap-3 sm:grid-cols-3">
            <Figure label="Open requests" value={pipeline.totals.count.toString()} />
            <Figure label="Total value" value={<Money amountNgn={pipeline.totals.valueNgn} />} />
            <Figure label="Past their deadline" value={pipeline.totals.breachedCount.toString()} alert={pipeline.totals.breachedCount > 0} />
          </section>

          <section className="rounded border border-gray-200 bg-white p-4">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">Where the queue sits</h2>
            <table className="mt-3 w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                  <th className="py-2 pr-2 font-medium">Waiting at</th>
                  <th className="py-2 pr-2 font-medium">On</th>
                  <th className="py-2 pr-2 text-right font-medium">Count</th>
                  <th className="py-2 pr-2 text-right font-medium">Oldest</th>
                  <th className="py-2 pl-2 text-right font-medium">Value</th>
                </tr>
              </thead>
              <tbody>
                {pipeline.byState.map((s) => (
                  <tr key={`${s.moduleKey}-${s.state}-${s.holder ?? ""}`} className="border-b border-gray-100">
                    <td className="py-2 pr-2">{readable(s.state)}</td>
                    <td className="py-2 pr-2">
                      {s.holder ?? <span className="text-red-700">nobody</span>}
                      {/* A desk, not a person. Saying so avoids the reader
                          concluding a request is unassigned when a role
                          legitimately holds it. */}
                      {s.holderIsRole && <span className="ml-1 text-xs text-gray-500">(desk)</span>}
                    </td>
                    <td className="py-2 pr-2 text-right tabular-nums">{s.count}</td>
                    <td className="py-2 pr-2 text-right">
                      <Days days={s.oldestDays} late={s.breachedCount > 0} />
                    </td>
                    <td className="py-2 pl-2 text-right tabular-nums">
                      <Money amountNgn={s.valueNgn} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>

          <section className="rounded border border-gray-200 bg-white p-4">
            <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
              Every open request, oldest first
            </h2>
            {pipeline.requests.length === 0 ? (
              <p className="mt-3 text-sm text-gray-600">Nothing is open.</p>
            ) : (
              <div className="mt-3 overflow-x-auto">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                      <th className="py-2 pr-2 font-medium">Request</th>
                      <th className="py-2 pr-2 font-medium">Waiting at</th>
                      <th className="py-2 pr-2 font-medium">On</th>
                      <th className="py-2 pr-2 font-medium">Waiting</th>
                      <th className="py-2 pl-2 text-right font-medium">Amount</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pipeline.requests.map((r) => (
                      <tr key={r.requestId} className="border-b border-gray-100">
                        <td className="py-2 pr-2">
                          <Link to={`/requests/${r.requestId}`} className="text-blue-700 hover:underline">
                            {r.requestNumber}
                          </Link>
                        </td>
                        <td className="py-2 pr-2">{readable(r.currentState)}</td>
                        <td className="py-2 pr-2">
                          {r.holder ?? <span className="text-red-700">nobody</span>}
                          {r.holderIsRole && <span className="ml-1 text-xs text-gray-500">(desk)</span>}
                        </td>
                        <td className="py-2 pr-2">
                          <Days days={r.daysWaiting} late={r.slaBreached} />
                        </td>
                        <td className="py-2 pl-2 text-right tabular-nums">
                          <Money amountNgn={r.totalAmountNgn} />
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}

function Figure({
  label,
  value,
  alert = false,
}: {
  label: string;
  value: React.ReactNode;
  alert?: boolean;
}) {
  return (
    <div className="rounded border border-gray-200 bg-white p-3">
      <div className="text-xs uppercase tracking-wide text-gray-500">{label}</div>
      <div className={`mt-1 text-lg font-semibold ${alert ? "text-red-700" : "text-gray-900"}`}>{value}</div>
    </div>
  );
}
