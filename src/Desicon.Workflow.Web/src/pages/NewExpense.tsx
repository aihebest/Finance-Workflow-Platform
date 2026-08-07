import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { createExpenseDraft, getBeneficiaries, submitRequest } from "../api/requests";
import { ApiError, type BeneficiarySummary, type ExpenseLineInput } from "../api/types";

/**
 * DEL-AC-FRM-002 Rev 05 — Expense Form.
 *
 * Laid out to match the paper, because that is the requirement rather than a
 * preference: a clerk who has filled this form for eight years should
 * recognise it immediately, and an improved layout is a worse layout here.
 * See docs/13-Form-Layout-Reference.md, extracted from the controlled
 * spreadsheet.
 *
 * Faithful: eleven numbered rows starting at 1.0; Project Code and Cost
 * Center Code as two columns under one "Specific Expense Category" heading;
 * Total / Less Advance Taken / Net Payable stacked at the right; tri-state
 * receipts; Amount in Words; and the three signature blocks shown as
 * read-only, because they are filled by the workflow rather than typed.
 *
 * Desktop-first, unlike the approval path. Capture is an eleven-line table
 * that nobody completes on a phone, and forcing it to reflow would cost the
 * recognisability the whole screen exists for.
 */

const ROW_COUNT = 11;

function emptyLine(): ExpenseLineInput {
  return {
    description: "",
    // Deliberately blank. A pre-filled "today" is the silent
    // wrong answer this column exists to stop.
    expenseDate: "",
    projectCode: "",
    costCentreCode: "",
    currencyCode: "NGN",
    amount: 0,
    // No FX feed exists. NGN lines are 1:1, which is honest for the only
    // currency this can currently express -- see the note under the table.
    fxRate: 1,
    // Not the expense date. NGN lines convert 1:1, and this stamps when that
    // rate applied -- today, because there is no rates feed to ask.
    fxRateDate: new Date().toISOString().slice(0, 10),
  };
}

export function NewExpense() {
  const navigate = useNavigate();

  const [beneficiaries, setBeneficiaries] = useState<BeneficiarySummary[]>([]);
  const [beneficiaryId, setBeneficiaryId] = useState("");
  const [receiptStatus, setReceiptStatus] = useState<"Yes" | "No" | "Incomplete">("Yes");
  const [lines, setLines] = useState<ExpenseLineInput[]>(() =>
    Array.from({ length: ROW_COUNT }, emptyLine),
  );
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    getBeneficiaries()
      .then(setBeneficiaries)
      .catch((e: Error) => setError(e.message));
  }, []);

  const total = lines.reduce((sum, line) => sum + (Number(line.amount) || 0), 0);

  const selected =
    beneficiaryId === "__me__" ? undefined : beneficiaries.find((b) => b.id === beneficiaryId);

  function updateLine(index: number, patch: Partial<ExpenseLineInput>) {
    setLines((current) => current.map((line, i) => (i === index ? { ...line, ...patch } : line)));
  }

  async function save(thenSubmit: boolean) {
    setError(null);

    // Empty rows are dropped, not rejected. The paper form has eleven rows
    // and nobody fills all eleven; a validation error for blank rows would
    // punish using the form as printed.
    const filled = lines.filter((line) => line.description.trim() !== "" && Number(line.amount) > 0);

    // Ordered from the most fundamental problem outward. Checking dates
    // first meant an entirely blank form complained that "row 1 needs a
    // date" — naming a row the person had not filled in, which reads as the
    // form being broken rather than incomplete.
    if (!beneficiaryId) {
      setError("Choose who this claim should be paid to.");
      return;
    }

    if (filled.length === 0) {
      setError("Enter at least one expense line with a description and an amount.");
      return;
    }

    const undated = filled.findIndex((line) => !line.expenseDate);

    if (undated >= 0) {
      setError(
        `Row ${undated + 1} needs the date the expense was incurred — it decides which period the claim posts to.`,
      );
      return;
    }

    setBusy(true);
    try {
      const created = await createExpenseDraft({
        ...(beneficiaryId === "__me__" ? {} : { beneficiaryId }),
        receiptStatus,
        lines: filled,
      });

      if (thenSubmit) {
        await submitRequest(created.requestId);
      }

      navigate(`/requests/${created.requestId}`);
    } catch (e) {
      // A guard rejection explains which condition failed -- show it as sent.
      setError(e instanceof ApiError ? e.message : (e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      {/* Header block: title, form code and revision, treasury number.
          TREAS.No. is Treasury's to assign at the Finance stage, so it is
          shown as a placeholder rather than an input -- a requester typing
          one would be inventing another department's reference. */}
      <header className="border border-gray-400 bg-white p-4">
        <div className="flex items-start justify-between">
          <h1 className="text-lg font-bold uppercase tracking-wide text-gray-900">
            Expense Form — (Cash) (Bank)
          </h1>
          <div className="text-right text-xs text-gray-600">
            <div>DEL-AC-FRM-002</div>
            <div>Rev 05</div>
            <div className="mt-2">TREAS.No. ____________</div>
          </div>
        </div>

        <div className="mt-4 space-y-2 text-sm">
          <div className="flex items-center gap-2">
            <label htmlFor="beneficiary" className="w-56 font-medium text-gray-800">
              Name of the Beneficiary:
            </label>
            <select
              id="beneficiary"
              value={beneficiaryId}
              onChange={(e) => setBeneficiaryId(e.target.value)}
              className="flex-1 border-b border-gray-400 bg-transparent py-1 focus:border-blue-600 focus:outline-none"
            >
              <option value="">— select —</option>
              {/* Not a real id. The API resolves the requester's own
                  beneficiary itself, so this only signals intent -- the paper
                  form's ordinary case ("in favour of company/staff") is a
                  member of staff claiming their own expenses. */}
              <option value="__me__">Myself</option>
              {beneficiaries.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name} ({b.type})
                </option>
              ))}
            </select>
          </div>

          {selected && !selected.hasBankDetails && (
            <p className="text-sm text-amber-800">
              {selected.name} has no bank details on file. Finance cannot pay this claim until
              they are recorded.
            </p>
          )}

          {/* On the paper form this is a free-text amount. It is a link to a
              specific advance here, because the expense form IS the advance
              retirement instrument (docs/01, finding 1) and a typed number
              cannot be reconciled. Retirement is raised from My Advances,
              which routes here with the advance already linked -- so this
              stays a placeholder on a fresh claim rather than a field that
              looks editable and is not. */}
          <div className="flex items-center gap-2 text-gray-600">
            <span className="w-56 font-medium text-gray-800">
              Cash Advance Taken (₦,$,£,€,¥):
            </span>
            <span className="flex-1 border-b border-gray-300 py-1">
              Not applicable — retire an advance from My Advances
            </span>
          </div>
        </div>
      </header>

      {error && (
        <p role="alert" className="border border-red-300 bg-red-50 p-3 text-sm text-red-800">
          {error}
        </p>
      )}

      {/* Details of Expense. Project Code and Cost Center Code are two
          columns under one heading, exactly as printed -- they are mutually
          exclusive per line (docs/01, finding 7) and the layout is what
          expresses that. */}
      <section className="border border-gray-400 bg-white">
        <h2 className="border-b border-gray-400 px-4 py-2 text-sm font-bold text-gray-900">
          Details of Expense
        </h2>

        <div className="overflow-x-auto">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="bg-gray-50 text-gray-800">
                <th className="w-12 border border-gray-300 p-2 font-medium">S/n</th>
                {/* Not on the printed form. docs/01 records ExpenseDate as a
                    deliberate addition -- "needed to stop a June expense
                    landing in the July ledger" -- and says it should go into
                    the next paper revision so the two stay in step. Defaulting
                    it silently to today did precisely what the field exists to
                    prevent, so it is asked for rather than assumed. */}
                <th className="w-36 border border-gray-300 p-2 font-medium">Date</th>
                <th className="border border-gray-300 p-2 text-left font-medium">Description</th>
                <th className="border border-gray-300 p-2 font-medium" colSpan={2}>
                  Specific Expense Category
                  <div className="text-xs font-normal text-gray-600">
                    (Pls indicate Project/Cost center code as appropriate)
                  </div>
                </th>
                <th className="w-32 border border-gray-300 p-2 font-medium">
                  Foreign Currency Amount
                  <div className="text-xs font-normal text-gray-600">$/£/€/¥</div>
                </th>
                <th className="w-36 border border-gray-300 p-2 font-medium">
                  Local Currency Amount
                  <div className="text-xs font-normal text-gray-600">NGN</div>
                </th>
              </tr>
              <tr className="bg-gray-50 text-xs text-gray-700">
                <th className="border border-gray-300 p-1"></th>
                <th className="border border-gray-300 p-1"></th>
                <th className="border border-gray-300 p-1"></th>
                <th className="border border-gray-300 p-1 font-medium">Project Code</th>
                <th className="border border-gray-300 p-1 font-medium">Cost Center Code</th>
                <th className="border border-gray-300 p-1"></th>
                <th className="border border-gray-300 p-1"></th>
              </tr>
            </thead>

            <tbody>
              {lines.map((line, index) => (
                <tr key={index}>
                  <td className="border border-gray-300 p-1 text-center text-gray-600">
                    {index + 1}.0
                  </td>
                  <td className="border border-gray-300 p-0">
                    <input
                      aria-label={`Date, row ${index + 1}`}
                      type="date"
                      value={line.expenseDate}
                      onChange={(e) => updateLine(index, { expenseDate: e.target.value })}
                      className="w-full p-2 focus:bg-blue-50 focus:outline-none"
                    />
                  </td>
                  <td className="border border-gray-300 p-0">
                    <input
                      aria-label={`Description, row ${index + 1}`}
                      value={line.description}
                      onChange={(e) => updateLine(index, { description: e.target.value })}
                      className="w-full p-2 focus:bg-blue-50 focus:outline-none"
                    />
                  </td>
                  <td className="border border-gray-300 p-0">
                    <input
                      aria-label={`Project code, row ${index + 1}`}
                      value={line.projectCode}
                      onChange={(e) =>
                        updateLine(index, { projectCode: e.target.value, costCentreCode: "" })
                      }
                      className="w-full p-2 focus:bg-blue-50 focus:outline-none"
                    />
                  </td>
                  <td className="border border-gray-300 p-0">
                    <input
                      aria-label={`Cost centre code, row ${index + 1}`}
                      value={line.costCentreCode}
                      onChange={(e) =>
                        updateLine(index, { costCentreCode: e.target.value, projectCode: "" })
                      }
                      className="w-full p-2 focus:bg-blue-50 focus:outline-none"
                    />
                  </td>
                  <td className="border border-gray-300 bg-gray-50 p-2"></td>
                  <td className="border border-gray-300 p-0">
                    <input
                      aria-label={`Amount in naira, row ${index + 1}`}
                      type="number"
                      inputMode="decimal"
                      step="0.01"
                      min="0"
                      value={line.amount || ""}
                      onChange={(e) => updateLine(index, { amount: Number(e.target.value) })}
                      className="w-full p-2 text-right tabular-nums focus:bg-blue-50 focus:outline-none"
                    />
                  </td>
                </tr>
              ))}
            </tbody>

            {/* Totals stacked at the right, as printed. Less Advance Taken
                and Net Payable are derived by the API from the linked
                advance, so they are shown rather than entered. */}
            <tfoot className="text-gray-900">
              <tr>
                <td colSpan={5} className="border border-gray-300 p-2 text-right font-medium">
                  Total
                </td>
                <td className="border border-gray-300"></td>
                <td className="border border-gray-300 p-2 text-right font-semibold tabular-nums">
                  {total.toFixed(2)}
                </td>
              </tr>
              <tr>
                <td colSpan={5} className="border border-gray-300 p-2 text-right font-medium">
                  Less Advance Taken
                </td>
                <td className="border border-gray-300"></td>
                <td className="border border-gray-300 p-2 text-right tabular-nums text-gray-500">
                  0.00
                </td>
              </tr>
              <tr>
                <td colSpan={5} className="border border-gray-300 p-2 text-right font-medium">
                  Net Payable
                </td>
                <td className="border border-gray-300"></td>
                <td className="border border-gray-300 p-2 text-right font-semibold tabular-nums">
                  {total.toFixed(2)}
                </td>
              </tr>
            </tfoot>
          </table>
        </div>

        {/* Stated on screen, not only in the decision log. Foreign currency,
            expense categories and code validation all require reference data
            that does not exist yet -- project and cost centre codes are
            free text until the Business Central sync lands. Someone filling
            this in deserves to know that before Finance tells them. */}
        <p className="border-t border-gray-300 bg-amber-50 px-4 py-2 text-xs text-amber-900">
          Amounts are in naira only, and project and cost centre codes are not yet validated
          against Business Central. Enter them exactly as they appear on your approved budget.
        </p>
      </section>

      <p className="text-xs italic text-gray-600">
        (If cash advance amount collected is equal to amount spent, please fill &ldquo;non
        applicable&rdquo;)
      </p>

      <section className="space-y-3 border border-gray-400 bg-white p-4 text-sm">
        <p className="font-medium text-gray-800">Please issue payment in favour of company/staff</p>

        {/* Amount in Words is generated on the PDF (docs/01) -- shown here so
            the form reads correctly, but not typed: a hand-typed total that
            disagrees with the figure is the anti-tamper control failing
            silently. */}
        <div className="flex gap-2">
          <span className="font-medium text-gray-800">Amount in Words:</span>
          <span className="flex-1 border-b border-gray-300 text-gray-500">
            generated when the form is printed
          </span>
        </div>

        <fieldset>
          <legend className="font-medium text-gray-800">Attached receipts:</legend>
          <div className="mt-1 flex gap-6">
            {(["Yes", "No", "Incomplete"] as const).map((option) => (
              <label key={option} className="flex items-center gap-2">
                <input
                  type="radio"
                  name="receiptStatus"
                  value={option}
                  checked={receiptStatus === option}
                  onChange={() => setReceiptStatus(option)}
                />
                {option}
              </label>
            ))}
          </div>
        </fieldset>
      </section>

      {/* The three signature blocks.

          Shown, because the form is unrecognisable without them, but not as
          blank ruled lines: underscores are a paper convention that on screen
          reads as "type here", and the first person to see this tried to.
          Nobody types these — each is recorded when that person acts, and the
          audit chain is the signature.

          Greyed and captioned so the panel says what will happen rather than
          inviting input that would go nowhere. */}
      <section
        aria-label="Signatures"
        className="border border-gray-400 bg-gray-50 p-4 text-sm"
      >
        <p className="mb-3 text-xs text-gray-600">
          Signatures are recorded automatically as each approver acts. Nothing here is typed.
        </p>

        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          {[
            { label: "Requested by", state: "you, on submit" },
            { label: "Verified by", state: "awaiting line manager" },
            { label: "Approved by", state: "awaiting approval" },
          ].map(({ label, state }) => (
            <div key={label} className="rounded border border-gray-200 bg-white p-3">
              <p className="font-medium text-gray-700">{label}</p>
              <p className="mt-1 text-gray-500">{state}</p>
            </div>
          ))}
        </div>
      </section>

      <div className="flex flex-wrap gap-3">
        <button
          type="button"
          disabled={busy}
          onClick={() => void save(false)}
          className="min-h-11 rounded border border-gray-400 px-4 py-2 font-medium text-gray-800 hover:bg-gray-50 disabled:opacity-50"
        >
          Save draft
        </button>
        <button
          type="button"
          disabled={busy}
          onClick={() => void save(true)}
          className="min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800 disabled:opacity-50"
        >
          Save and submit
        </button>
      </div>
    </div>
  );
}
