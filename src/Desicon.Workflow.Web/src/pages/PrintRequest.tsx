import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router";
import { getAttachmentBlob, getAttachments, getHistory, getRequest } from "../api/requests";
import { ACTION_LABELS, type AttachmentSummary, type AuditEntry } from "../api/types";

/**
 * The request as a document, for filing.
 *
 * Asked for by the Accounts Manager on 10 September 2026: at the point Treasury
 * pays, they should be able to take the approved request away as a PDF and keep
 * it on the shared drive. Aihe's steer was that printing should be Treasury's
 * choice rather than something the system does for them, and that the receipts
 * should come with it.
 *
 * WHY THIS IS NOT A GENERATED PDF
 * -------------------------------
 * Because it does not need to be. The browser's own "Save as PDF" produces the
 * file, which means no PDF library in the build and therefore no licence to
 * check -- and licences are not hypothetical here: FluentAssertions 8 nearly
 * arrived as a routine dependency bump in August and would have put a paid
 * commercial obligation into a green pull request (docs/15 §5f).
 *
 * The trade is that nothing is archived automatically. If Treasury does not
 * save the file, there is no file. That is acceptable only because this
 * document is a convenience copy: the record of what happened is the
 * hash-chained audit trail, which cannot be printed away or lost.
 *
 * WHAT THE MACHINERY ALREADY EXPECTED
 * -----------------------------------
 * Every request carries a FormRevision, pinned at submission and never changed,
 * with a comment saying it exists so "a request reprints in the layout it was
 * raised under". WorkflowDefinition pins its own for the same stated reason.
 * ExpenseRequest says outright that "the printed PDF is what gets filed and
 * auditors expect it". The Amount in Words row on both forms says "generated
 * when the form is printed".
 *
 * All of that was designed, built and stamped on every row since the first
 * release, for a printer nobody had written. This is the printer. Nothing
 * needed backfilling, because every request already knew which layout it should
 * print in.
 */

type Detail = Record<string, unknown>;

type Line = {
  lineNumber?: number;
  description?: string;
  expenseDate?: string;
  projectCode?: string | null;
  costCentreCode?: string | null;
  amountNgn?: number;
};

const money = (value: number) =>
  new Intl.NumberFormat("en-NG", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(
    value,
  );

const ONES = [
  "",
  "One",
  "Two",
  "Three",
  "Four",
  "Five",
  "Six",
  "Seven",
  "Eight",
  "Nine",
  "Ten",
  "Eleven",
  "Twelve",
  "Thirteen",
  "Fourteen",
  "Fifteen",
  "Sixteen",
  "Seventeen",
  "Eighteen",
  "Nineteen",
];

const TENS = [
  "",
  "",
  "Twenty",
  "Thirty",
  "Forty",
  "Fifty",
  "Sixty",
  "Seventy",
  "Eighty",
  "Ninety",
];

// `?? ""` on every lookup because noUncheckedIndexedAccess is on: indexing an
// array yields `string | undefined` whatever the caller knows about the range.
// The compiler is right to insist -- the alternative is a `!` that says "trust
// me" about arithmetic, and arithmetic on a money figure is the last place to
// start trusting.
const one = (n: number) => ONES[n] ?? "";
const ten = (n: number) => TENS[n] ?? "";

function underThousand(n: number): string {
  if (n === 0) return "";
  if (n < 20) return one(n);
  if (n < 100) {
    const rest = n % 10;
    return ten(Math.floor(n / 10)) + (rest ? `-${one(rest)}` : "");
  }
  const rest = n % 100;
  return `${one(Math.floor(n / 100))} Hundred${rest ? ` and ${underThousand(rest)}` : ""}`;
}

/**
 * The figure spelled out, the way the paper form has it.
 *
 * On DEL-AC-FRM-002 this row reads "generated when the form is printed", and
 * until now nothing printed, so nothing generated it. The stored AmountInWords
 * column has been empty on every request ever raised.
 *
 * Derived here rather than read from that column, which is the right way round:
 * the words are a restatement of the figure, and two places holding the same
 * fact is how they come to disagree. A stored string could be edited; this
 * cannot say anything the total does not.
 */
function amountInWords(amount: number): string {
  const naira = Math.floor(Math.abs(amount));
  const kobo = Math.round((Math.abs(amount) - naira) * 100);

  const chunks: string[] = [];
  const scales = [
    { value: 1_000_000_000, name: "Billion" },
    { value: 1_000_000, name: "Million" },
    { value: 1_000, name: "Thousand" },
  ];

  let remaining = naira;

  for (const scale of scales) {
    const count = Math.floor(remaining / scale.value);
    if (count > 0) {
      chunks.push(`${underThousand(count)} ${scale.name}`);
      remaining %= scale.value;
    }
  }

  if (remaining > 0) chunks.push(underThousand(remaining));

  const words = chunks.length > 0 ? chunks.join(", ") : "Zero";
  const sign = amount < 0 ? "Minus " : "";

  return kobo > 0
    ? `${sign}${words} Naira and ${underThousand(kobo)} Kobo only`
    : `${sign}${words} Naira only`;
}

/**
 * The states whose entry is worth printing as an approval.
 *
 * A printed form has three signature blocks. This has the audit trail, which is
 * stronger: it cannot be forged, it carries the exact time, and it includes the
 * steps a paper form never recorded. Returns, rejections and withdrawals are
 * included deliberately -- a document that showed only the approvals would be a
 * tidier and less honest account of what happened.
 */
const SIGNATURE_EVENTS = new Set([
  "SUBMIT",
  "VERIFY",
  "APPROVE",
  "RETURN",
  "REJECT",
  "WITHDRAW",
  "RESUBMIT",
  "MARK_POSTED",
  "EXECUTE_PAYMENT",
  "RELEASE_CASH",
  "ACKNOWLEDGE",
  "CONFIRM_REFUND",
]);

export function PrintRequest() {
  const { id = "" } = useParams();

  const [detail, setDetail] = useState<Detail | null>(null);
  const [history, setHistory] = useState<AuditEntry[]>([]);
  const [images, setImages] = useState<{ file: AttachmentSummary; url: string }[]>([]);
  const [others, setOthers] = useState<AttachmentSummary[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [d, h, files] = await Promise.all([
        getRequest(id),
        getHistory(id),
        getAttachments(id),
      ]);

      setDetail(d);
      setHistory(h);

      // Images can be laid out as pages. PDFs cannot -- a browser will not
      // inline one document inside another for printing, and pretending
      // otherwise would produce a file that silently omitted the receipt it
      // claimed to include. They are listed instead, with the hash, so the
      // separate file can be shown to be the one this request carried.
      const pictures = files.filter((f) => f.contentType.startsWith("image/"));
      setOthers(files.filter((f) => !f.contentType.startsWith("image/")));

      const loaded = await Promise.all(
        pictures.map(async (file) => ({
          file,
          url: URL.createObjectURL(await getAttachmentBlob(id, file.attachmentId)),
        })),
      );

      setImages(loaded);
    } catch (e) {
      setError((e as Error).message);
    }
  }, [id]);

  useEffect(() => {
    void load();
  }, [load]);

  // Object URLs live as long as the page does, not as long as one render.
  useEffect(
    () => () => {
      images.forEach((i) => URL.revokeObjectURL(i.url));
    },
    [images],
  );

  if (error) {
    return <p className="p-4 text-red-800">{error}</p>;
  }

  if (!detail) {
    return <p className="p-4 text-gray-500">Loading…</p>;
  }

  const isAdvance = String(detail.moduleKey ?? "") === "CASH_ADVANCE";
  const lines = (detail.lines as Line[] | undefined) ?? [];
  const total = Number(detail.totalAmountNgn ?? 0);

  const payee = detail.beneficiary as { name: string } | null | undefined;
  const raisedBy = detail.requester as
    | { name: string; staffNumber: string | null; department: string | null }
    | null
    | undefined;

  const signatures = history.filter((h) => SIGNATURE_EVENTS.has(h.eventType));

  const when = (iso: string) =>
    new Date(iso).toLocaleString("en-NG", { dateStyle: "medium", timeStyle: "short" });

  return (
    <div className="mx-auto max-w-3xl bg-white p-6 text-sm text-gray-900">
      <style>{`
        @media print {
          .no-print { display: none !important; }
          .page-break { break-before: page; }
          body { background: #fff; }
        }
        @page { margin: 14mm; }
      `}</style>

      <div className="no-print mb-4 flex gap-3">
        <button
          type="button"
          onClick={() => window.print()}
          className="min-h-11 rounded bg-blue-700 px-4 py-2 font-medium text-white hover:bg-blue-800"
        >
          Print / Save as PDF
        </button>
        <Link
          to={`/requests/${id}`}
          className="min-h-11 rounded border border-gray-400 px-4 py-2 font-medium text-gray-800"
        >
          Back to the request
        </Link>
      </div>

      <header className="border border-gray-400 p-4">
        <div className="flex items-start justify-between">
          <div>
            <h1 className="text-base font-bold uppercase tracking-wide">
              {isAdvance ? "Cash Advance — To Be Justified" : "Expense Form — (Cash) (Bank)"}
            </h1>
            <p className="mt-1 text-lg font-semibold">{String(detail.requestNumber ?? "")}</p>
          </div>
          <div className="text-right text-xs">
            <div>{String(detail.formCode ?? "")}</div>
            <div>Rev {String(detail.formRevision ?? "")}</div>
            <div className="mt-2">TREAS.No. {String(detail.treasuryNumber ?? "____________")}</div>
          </div>
        </div>

        <dl className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1 text-xs">
          <div>
            <dt className="inline text-gray-600">Raised by: </dt>
            <dd className="inline font-medium">
              {raisedBy?.name ?? "—"}
              {raisedBy?.staffNumber ? ` (${raisedBy.staffNumber})` : ""}
            </dd>
          </div>
          <div>
            <dt className="inline text-gray-600">Department: </dt>
            <dd className="inline font-medium">{raisedBy?.department ?? "—"}</dd>
          </div>
          <div>
            <dt className="inline text-gray-600">
              {isAdvance ? "To be collected by: " : "Name of the Beneficiary: "}
            </dt>
            <dd className="inline font-medium">
              {isAdvance
                ? String(detail.beneficiaryName ?? "the requester")
                : (payee?.name ?? "—")}
            </dd>
          </div>
          <div>
            <dt className="inline text-gray-600">Status: </dt>
            <dd className="inline font-medium">{String(detail.currentState ?? "")}</dd>
          </div>
        </dl>
      </header>

      <section className="mt-4 border border-gray-400 p-4">
        <h2 className="mb-2 text-xs font-bold uppercase tracking-wide text-gray-700">
          {isAdvance ? "Details of the advance" : "Details of expense"}
        </h2>

        <table className="w-full border-collapse text-xs">
          <thead>
            <tr className="border-b border-gray-400 text-left">
              <th className="py-1 pr-2">S/n</th>
              {!isAdvance && <th className="py-1 pr-2">Date</th>}
              <th className="py-1 pr-2">Description</th>
              {!isAdvance && <th className="py-1 pr-2">Project / Cost centre</th>}
              <th className="py-1 pl-2 text-right">Amount (₦)</th>
            </tr>
          </thead>
          <tbody>
            {lines.map((line, i) => (
              <tr key={line.lineNumber ?? i} className="border-b border-gray-200">
                <td className="py-1 pr-2">{line.lineNumber ?? i + 1}.0</td>
                {!isAdvance && <td className="py-1 pr-2">{line.expenseDate ?? ""}</td>}
                <td className="py-1 pr-2">{line.description}</td>
                {!isAdvance && (
                  <td className="py-1 pr-2">
                    {line.projectCode ?? line.costCentreCode ?? "—"}
                  </td>
                )}
                <td className="py-1 pl-2 text-right tabular-nums">
                  {money(Number(line.amountNgn ?? 0))}
                </td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr className="font-semibold">
              <td className="py-1 pr-2" colSpan={isAdvance ? 2 : 4}>
                Total
              </td>
              <td className="py-1 pl-2 text-right tabular-nums">{money(total)}</td>
            </tr>
          </tfoot>
        </table>

        <p className="mt-3 text-xs">
          <span className="font-medium">Amount in words: </span>
          {amountInWords(total)}
        </p>
      </section>

      {/* The signature blocks, filled in by what actually happened.

          A paper form has three ruled lines and whatever was written on them.
          This is the audit trail, which records every step rather than three,
          carries the minute it happened, and cannot be signed by somebody who
          was not there. */}
      <section className="mt-4 border border-gray-400 p-4">
        <h2 className="mb-2 text-xs font-bold uppercase tracking-wide text-gray-700">
          Approvals
        </h2>

        {signatures.length === 0 ? (
          <p className="text-xs text-gray-600">Nothing has been actioned on this request yet.</p>
        ) : (
          <table className="w-full border-collapse text-xs">
            <thead>
              <tr className="border-b border-gray-400 text-left">
                <th className="py-1 pr-2">Action</th>
                <th className="py-1 pr-2">Role</th>
                <th className="py-1 pr-2">When</th>
                <th className="py-1 pr-2">Comment</th>
              </tr>
            </thead>
            <tbody>
              {signatures.map((s) => (
                <tr key={s.auditEventId} className="border-b border-gray-200">
                  <td className="py-1 pr-2 font-medium">
                    {ACTION_LABELS[s.eventType] ?? s.eventType}
                  </td>
                  <td className="py-1 pr-2">{s.actorRole}</td>
                  <td className="py-1 pr-2">{when(s.occurredAtUtc)}</td>
                  <td className="py-1 pr-2">{s.reason ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {others.length > 0 && (
        <section className="mt-4 border border-gray-400 p-4">
          <h2 className="mb-2 text-xs font-bold uppercase tracking-wide text-gray-700">
            Attached documents filed separately
          </h2>
          <p className="mb-2 text-xs text-gray-600">
            These are PDFs. A browser cannot fold one document into another when printing, so
            they are listed here rather than reproduced — download them from the request and
            file them beside this page. The hash identifies the exact file.
          </p>
          <ul className="text-xs">
            {others.map((f) => (
              <li key={f.attachmentId} className="border-b border-gray-200 py-1">
                <span className="font-medium">{f.fileName}</span>{" "}
                <span className="text-gray-600">
                  · {Math.max(1, Math.round(f.sizeBytes / 1024))} KB · SHA-256{" "}
                  {f.sha256.slice(0, 16)}…
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {images.map((image) => (
        <section key={image.file.attachmentId} className="page-break mt-4">
          <h2 className="mb-2 text-xs font-bold uppercase tracking-wide text-gray-700">
            {image.file.fileName}
          </h2>
          <p className="mb-2 text-xs text-gray-600">
            Attached to {String(detail.requestNumber ?? "")} · SHA-256{" "}
            {image.file.sha256.slice(0, 16)}…
          </p>
          <img
            src={image.url}
            alt={image.file.fileName}
            className="max-h-[240mm] w-auto max-w-full border border-gray-300"
          />
        </section>
      ))}

      <footer className="mt-6 text-center text-[10px] text-gray-500">
        Printed from the Desicon Finance Workflow Platform. The authoritative record is the
        hash-chained audit trail held against {String(detail.requestNumber ?? "")}.
      </footer>
    </div>
  );
}
