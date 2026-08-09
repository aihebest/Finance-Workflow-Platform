import { useCallback, useEffect, useRef, useState } from "react";
import { downloadAttachment, getAttachments, uploadAttachment } from "../api/requests";
import { ApiError, type AttachmentSummary } from "../api/types";

interface AttachmentsProps {
  requestId: string;
  /** False once the request is closed: evidence cannot be added afterwards. */
  canUpload: boolean;
  /** Called after a successful upload, so a guard blocked on evidence re-evaluates. */
  onChanged?: () => void;
}

const size = (bytes: number) =>
  bytes < 1024 * 1024
    ? `${Math.max(1, Math.round(bytes / 1024))} KB`
    : `${(bytes / 1024 / 1024).toFixed(1)} MB`;

/**
 * Receipts and supporting documents.
 *
 * The evidence of what was purchased. Cost Control cannot pass a claim without
 * at least one, and the Accounts Officer posting it in Business Central opens
 * these to see what the money bought — until they existed she had a tick box
 * and nothing behind it.
 */
export function Attachments({ requestId, canUpload, onChanged }: AttachmentsProps) {
  const [files, setFiles] = useState<AttachmentSummary[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const input = useRef<HTMLInputElement>(null);

  const load = useCallback(async () => {
    try {
      setFiles(await getAttachments(requestId));
    } catch (e) {
      setError((e as Error).message);
    }
  }, [requestId]);

  useEffect(() => {
    void load();
  }, [load]);

  async function upload(chosen: FileList | null) {
    if (!chosen || chosen.length === 0) return;

    setBusy(true);
    setError(null);

    try {
      // One at a time rather than in parallel: the API validates each on its
      // own, and a failure part-way through a parallel batch leaves an
      // unclear picture of what actually landed.
      for (const file of Array.from(chosen)) {
        await uploadAttachment(requestId, file);
      }
      await load();
      onChanged?.();
    } catch (e) {
      // The API names the reason — wrong type, too large, request closed —
      // and each is something the person can act on.
      setError(e instanceof ApiError ? e.message : (e as Error).message);
    } finally {
      setBusy(false);
      if (input.current) input.current.value = "";
    }
  }

  return (
    <section className="rounded border border-gray-200 bg-white p-4">
      <h2 className="text-sm font-semibold uppercase tracking-wide text-gray-500">
        Receipts and supporting documents
      </h2>

      {error && (
        <p role="alert" className="mt-2 rounded bg-red-50 p-3 text-sm text-red-800">
          {error}
        </p>
      )}

      {files.length === 0 ? (
        <p className="mt-2 text-sm text-gray-600">
          Nothing attached yet.
          {canUpload ? " Attach the receipts for what was purchased." : ""}
        </p>
      ) : (
        <ul className="mt-3 divide-y divide-gray-100">
          {files.map((file) => (
            <li key={file.attachmentId} className="flex flex-wrap items-baseline gap-2 py-2">
              <button
                type="button"
                onClick={() =>
                  void downloadAttachment(requestId, file.attachmentId, file.fileName)
                }
                className="text-sm font-medium text-blue-700 hover:underline"
              >
                {file.fileName}
              </button>
              <span className="text-xs text-gray-500">
                {size(file.sizeBytes)} · {new Date(file.uploadedAt).toLocaleDateString("en-NG")}
              </span>
              {/* Shown truncated so a file produced in an audit can be checked
                  against what was uploaded, without cluttering the row. */}
              <span className="ml-auto font-mono text-xs text-gray-400" title={file.sha256}>
                {file.sha256.slice(0, 12)}
              </span>
            </li>
          ))}
        </ul>
      )}

      {canUpload && (
        <div className="mt-3">
          <input
            ref={input}
            type="file"
            multiple
            accept="application/pdf,image/jpeg,image/png,image/heic,image/tiff"
            disabled={busy}
            onChange={(e) => void upload(e.target.files)}
            className="block w-full text-sm text-gray-700 file:mr-3 file:min-h-11 file:rounded file:border-0 file:bg-blue-700 file:px-4 file:py-2 file:font-medium file:text-white hover:file:bg-blue-800 disabled:opacity-50"
          />
          <p className="mt-1 text-xs text-gray-500">
            PDF or photograph, up to 10 MB each.
          </p>
        </div>
      )}
    </section>
  );
}
