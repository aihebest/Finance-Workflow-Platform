interface MoneyProps {
  amountNgn: number;
}

/**
 * Naira, grouped, two decimals, no currency conversion.
 *
 * en-NG rather than the browser locale: the figure must read identically to
 * every approver regardless of their machine's regional settings, because two
 * people comparing a number over the phone is a normal part of an approval.
 */
const formatter = new Intl.NumberFormat("en-NG", {
  style: "currency",
  currency: "NGN",
  minimumFractionDigits: 2,
});

export function Money({ amountNgn }: MoneyProps) {
  return <span className="tabular-nums">{formatter.format(amountNgn)}</span>;
}
