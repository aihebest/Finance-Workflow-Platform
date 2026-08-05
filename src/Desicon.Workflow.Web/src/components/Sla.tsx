interface SlaProps {
  slaDueAt: string | null;
  now?: Date;
}

/**
 * Time remaining against the SLA, or how far past it.
 *
 * Overdue is red and stated in the same breath as the item, because the inbox
 * is sorted by SLA and the whole point of the sort is that the top of the list
 * is where the delay is. A colour alone would not survive being printed,
 * screenshotted, or read by someone with a colour vision deficiency -- hence
 * the word "overdue" as well as the colour, which is also WCAG 1.4.1.
 */
export function Sla({ slaDueAt, now = new Date() }: SlaProps) {
  if (!slaDueAt) {
    return <span className="text-sm text-gray-400">No SLA</span>;
  }

  const due = new Date(slaDueAt);
  const diffMs = due.getTime() - now.getTime();
  const overdue = diffMs < 0;
  const hours = Math.floor(Math.abs(diffMs) / 3_600_000);
  const days = Math.floor(hours / 24);

  const magnitude =
    days >= 1 ? `${days} day${days === 1 ? "" : "s"}` : `${hours} hour${hours === 1 ? "" : "s"}`;

  if (overdue) {
    return (
      <span className="inline-flex items-center rounded bg-red-100 px-2 py-1 text-sm font-semibold text-red-800">
        Overdue by {magnitude}
      </span>
    );
  }

  // Under a working day left is worth distinguishing without shouting: an
  // approver scanning a list needs to see which items will breach today.
  const soon = hours < 9;

  return (
    <span
      className={
        soon
          ? "inline-flex items-center rounded bg-amber-100 px-2 py-1 text-sm font-medium text-amber-800"
          : "inline-flex items-center rounded bg-gray-100 px-2 py-1 text-sm text-gray-700"
      }
    >
      {magnitude} left
    </span>
  );
}
