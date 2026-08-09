import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { createCashAdvanceDraft, submitRequest } from "../api/requests";
import { ApiError, type AdvanceLineInput } from "../api/types";

/**
 * DEL-AC-FRM-003 Rev 05 — Cash Advance, To Be Justified.
 *
 * Six rows, not eleven. The expense form has eleven and the difference is
 * visible at a glance, which is part of what makes each form recognisable to
 * someone who has filled them for years. See docs/13.
 */
const ROW_COUNT = 6;

/**
 * A row as typed: naira and kobo in separate boxes, exactly as printed.
 *
 * The paper form's amount column is split ₦ / k, so this presents two inputs
 * and combines them on submit. Storage is decimal(18,2) either way — the split
 * is about the form looking like itself, not about the data.
 */
type Row = {
  description: string;
  naira: string;
  kobo: string;
};

const emptyRow = (): Row => ({ description: "", naira: "", kobo: "" });

const money = (value: number) =>
  new Intl.NumberFormat("en-NG", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
    value,
  );

/** Naira and kobo boxes to one decimal. Blank kobo is zero, not invalid. */
function rowAmount(row: Row): number {
  const naira = Number(row.naira) || 0;
  const kobo = Number(row.kobo) || 0;
  return naira + kobo / 100;
}

export function NewCashAdvance() {
  const navigate = useNavigate();

  const [rows, setRows] = useState<Row[]>(() =>
    Array.from({ length: ROW_COUNT }, emptyRow),
  );
  const [purpose, setPurpose] = useState("");
  const [allocationType, setAllocationType] = useState<"Project" | "CostCentre">("CostCentre");
  const [projectCode, setProjectCode] = useState("");
  const [costCentreCode, setCostCentreCode] = useState("");
  const [stationScope, setStationScope] = useState<"InStation" | "OutOfStation">("InStation");
  const [hasSupportingDocuments, setHasSupportingDocuments] = useState(false);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const total = rows.reduce((sum, row) => sum + rowAmount(row), 0);
  const filled = rows.filter((row) => row.description.trim().length > 0 && rowAmount(row) > 0);

  const allocationCode = allocationType === "Project" ? projectCode : costCentreCode;
  const canSubmit =
    filled.length > 0 &&
    purpose.trim().length > 0 &&
    allocationCode.trim().length > 0 &&
    !busy;

  function updateRow(index: number, patch: Partial<Row>) {
    setRows((current) => current.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  }

  async function save() {
    setBusy(true);
    setError(null);

    try {
      const lines: AdvanceLineInput[] = filled.map((row) => ({
        description: row.description.trim(),
        currencyCode: "NGN",
        amount: rowAmount(row),
        // No FX rates feed exists, so a naira line carries a rate of 1 rather
        // than a number this screen invented. Same reasoning as the expense
        // form's untouched FX column.
        fxRate: 1,
        fxRateDate: new Date().toISOString().slice(0, 10),
      }));

      const created = await createCashAdvanceDraft({
        purpose: purpose.trim(),
        allocationType,
        ...(allocationType === "Project"
          ? { projectCode: projectCode.trim() }
          : { costCentreCode: costCentreCode.trim() }),
        stationScope,
        hasSupportingDocuments,
        lines,
      });

      await submitRequest(created.requestId);
      navigate(`/requests/${created.requestId}`);
    } catch (e) {
      // Guard refusals arrive as ProblemDetails with a sentence written for a
      // person — most likely here "retire any overdue advance before
      // requesting another", which names exactly what to do.
      setError(e instanceof ApiError ? e.message : (e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-6">
      <header className="rounded border border-gray-200 bg-white p-4">
        <h1 className="text-lg font-semibold text-gray-900">Cash Advance — To Be Justified</h1>
        <p className="mt-1 text-sm text-gray-600">DEL-AC-FRM-003 Rev 05</p>
      </header>

      {error && (
        <p role="alert" className="rounded bg-red-50 p-4 text-red-800">
          {error}
        </p>
      )}

      <section className="rounded border border-gray-200 bg-white p-4">
        <label className="block text-sm text-gray-700" htmlFor="purpose">
          Please approve a Cash Advance for the underlisted expense(s)
        </label>
        <input
          id="purpose"
          value={purpose}
          onChange={(e) => setPurpose(e.target.value)}
          placeholder="Purpose of this advance"
          className="mt-1 min-h-11 w-full rounded border border-gray-300 p-2 text-sm focus:border-blue-600 focus:outline-none focus:ring-1 focus:ring-blue-600"
        />

        <div className="mt-4 overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-left text-xs uppercase text-gray-500">
                <th className="py-2 pr-2 font-medium">S/n</th>
                <th className="py-2 pr-2 font-medium">Description</th>
                <th className="py-2 pr-2 text-right font-medium">₦</th>
                <th className="py-2 pl-2 text-right font-medium">k</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row, index) => (
                <tr key={index} className="border-b border-gray-100">
                  <td className="py-1 pr-2 text-gray-500">{index + 1}</td>
                  <td className="py-1 pr-2">
                    <input
                      aria-label={`Description, row ${index + 1}`}
                      value={row.description}
                      onChange={(e) => updateRow(index, { description: e.target.value })}
                      className="min-h-11 w-full rounded border border-gray-300 p-2 text-sm"
                    />
                  </td>
                  <td className="py-1 pr-2">
                    <input
                      aria-label={`Naira, row ${index + 1}`}
                      type="number"
                      min="0"
                      step="1"
                      value={row.naira}
                      onChange={(e) => updateRow(index, { naira: e.target.value })}
                      className="min-h-11 w-32 rounded border border-gray-300 p-2 text-right text-sm tabular-nums"
                    />
                  </td>
                  <td className="py-1 pl-2">
                    {/* Two boxes rather than one decimal, because that is what
                        the printed form has. Blank means zero. */}
                    <input
                      aria-label={`Kobo, row ${index + 1}`}
                      type="number"
                      min="0"
                      max="99"
                      step="1"
                      value={row.kobo}
                      onChange={(e) => updateRow(index, { kobo: e.target.value })}
                      className="min-h-11 w-20 rounded border border-gray-300 p-2 text-right text-sm tabular-nums"
                    />
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <td colSpan={2} className="pt-3 text-right text-sm font-medium text-gray-700">
                  Total
                </td>
                <td colSpan={2} className="pt-3 pl-2 text-right text-sm font-medium tabular-nums">
                  ₦{money(total)}
                </td>
              </tr>
            </tfoot>
          </table>
        </div>
      </section>

      <section className="rounded border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
          Please tick as appropriate
        </h2>

        {/* Allocation is form-level here, unlike the expense form where it is
            per line. Radio rather than checkbox: the paper form's two boxes
            are alternatives, and a form that allowed both would be capturing
            something the process has no meaning for. */}
        <div className="mt-3 space-y-3">
          <label className="flex items-center gap-3 text-sm text-gray-800">
            <input
              type="radio"
              name="allocation"
              checked={allocationType === "Project"}
              onChange={() => setAllocationType("Project")}
              className="h-4 w-4"
            />
            Projects Specific
            <input
              aria-label="Project code"
              value={projectCode}
              onChange={(e) => setProjectCode(e.target.value)}
              disabled={allocationType !== "Project"}
              placeholder="Project code"
              className="min-h-11 w-56 rounded border border-gray-300 p-2 text-sm disabled:bg-gray-50 disabled:text-gray-400"
            />
          </label>

          <label className="flex items-center gap-3 text-sm text-gray-800">
            <input
              type="radio"
              name="allocation"
              checked={allocationType === "CostCentre"}
              onChange={() => setAllocationType("CostCentre")}
              className="h-4 w-4"
            />
            Non Projects Specific
            <input
              aria-label="Cost centre code"
              value={costCentreCode}
              onChange={(e) => setCostCentreCode(e.target.value)}
              disabled={allocationType !== "CostCentre"}
              placeholder="Cost centre code"
              className="min-h-11 w-56 rounded border border-gray-300 p-2 text-sm disabled:bg-gray-50 disabled:text-gray-400"
            />
          </label>
        </div>

        <label className="mt-4 flex items-center gap-3 text-sm text-gray-800">
          <input
            type="checkbox"
            checked={hasSupportingDocuments}
            onChange={(e) => setHasSupportingDocuments(e.target.checked)}
            className="h-4 w-4"
          />
          Attached documentation
        </label>
      </section>

      <section className="rounded border border-gray-200 bg-white p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
          Where is this being spent?
        </h2>

        {/* Not on the paper form as a tick box, but it sets the retirement
            deadline, and the footer states both windows. Asking here is
            honest; deriving it from somewhere else would be a guess about
            somebody's liability. */}
        <p className="mt-1 text-sm text-gray-600">
          This sets your retirement deadline: 24 working hours in station, 72 out of station.
          The advance is your liability until it is retired.
        </p>

        <div className="mt-3 space-y-2">
          {(
            [
              ["InStation", "Within local station state — retire within 24 working hours"],
              ["OutOfStation", "Out of station state — retire within 72 working hours"],
            ] as const
          ).map(([value, label]) => (
            <label key={value} className="flex items-center gap-3 text-sm text-gray-800">
              <input
                type="radio"
                name="station"
                checked={stationScope === value}
                onChange={() => setStationScope(value)}
                className="h-4 w-4"
              />
              {label}
            </label>
          ))}
        </div>
      </section>

      {/* Signatures are not typed here, for the same reason as the expense
          form: each one is recorded when its approver acts, and a typed name
          would be a claim nobody made. */}
      <section className="rounded border border-gray-200 bg-gray-50 p-4">
        <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
          Requested by · Verified by · Approved by
        </h2>
        <p className="mt-2 text-sm text-gray-600">
          Signatures are recorded automatically as each approver acts. Nothing here is typed.
        </p>
      </section>

      <div className="flex flex-wrap items-center gap-3">
        <button
          type="button"
          disabled={!canSubmit}
          onClick={() => void save()}
          className="min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
        >
          Save and submit
        </button>
        <span className="text-sm text-gray-600">
          {filled.length === 0
            ? "Add at least one line with a description and an amount."
            : `${filled.length} line${filled.length === 1 ? "" : "s"} · ₦${money(total)}`}
        </span>
      </div>
    </div>
  );
}
