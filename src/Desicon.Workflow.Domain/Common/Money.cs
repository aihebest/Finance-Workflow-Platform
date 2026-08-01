using System.Globalization;

namespace Desicon.Workflow.Domain.Common;

/// <summary>
/// A monetary amount in a named currency, with the NGN equivalent fixed at
/// capture time.
///
/// The FX rate is stored, not re-derived. A claim approved at NGN 1,540 to the
/// dollar must still report at NGN 1,540 in 2031, whatever the rate is then.
/// Re-deriving historical rates at read time is the commonest way a finance
/// system quietly stops reconciling.
/// </summary>
public readonly record struct Money
{
    public const string BaseCurrency = "NGN";

    private Money(string currencyCode, decimal amount, decimal fxRate, DateOnly fxRateDate)
    {
        CurrencyCode = currencyCode;
        Amount = amount;
        FxRate = fxRate;
        FxRateDate = fxRateDate;
    }

    public string CurrencyCode { get; }

    public decimal Amount { get; }

    public decimal FxRate { get; }

    public DateOnly FxRateDate { get; }

    /// <summary>Rounded once, half away from zero, so a header total that sums
    /// the rounded lines foots exactly against the printed form.</summary>
    public decimal AmountNgn => Math.Round(Amount * FxRate, 2, MidpointRounding.AwayFromZero);

    public static Money Naira(decimal amount, DateOnly? asAt = null) =>
        new(BaseCurrency, amount, 1.0m, asAt ?? DateOnly.FromDateTime(DateTime.UtcNow));

    public static Money Foreign(
        string currencyCode, decimal amount, decimal fxRate, DateOnly fxRateDate)
    {
        if (string.IsNullOrWhiteSpace(currencyCode) || currencyCode.Length != 3)
        {
            throw new ArgumentException(
                "Currency code must be a three-letter ISO 4217 code.", nameof(currencyCode));
        }

        var normalised = currencyCode.ToUpperInvariant();

        if (normalised == BaseCurrency && fxRate != 1.0m)
        {
            throw new ArgumentException(
                "An NGN amount must carry an FX rate of exactly 1.0.", nameof(fxRate));
        }

        if (fxRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fxRate), "FX rate must be positive.");
        }

        return new Money(normalised, amount, fxRate, fxRateDate);
    }

    public override string ToString() =>
        CurrencyCode == BaseCurrency
            ? Amount.ToString("N2", CultureInfo.InvariantCulture) + " NGN"
            : $"{Amount.ToString("N2", CultureInfo.InvariantCulture)} {CurrencyCode} " +
              $"({AmountNgn.ToString("N2", CultureInfo.InvariantCulture)} NGN @ {FxRate})";
}
