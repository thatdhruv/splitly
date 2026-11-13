namespace Splitly.Domain.Money;

/// <summary>
/// Currency metadata and conversion between <see cref="decimal"/> amounts and integer minor units.
/// All allocation math runs on minor units (long) so totals are exact by construction.
/// </summary>
public static class Currencies
{
    // ISO 4217 minor units for the currencies that differ from the default of 2.
    private static readonly Dictionary<string, int> NonDefault = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JPY"] = 0, ["KRW"] = 0, ["VND"] = 0, ["CLP"] = 0, ["ISK"] = 0, ["UGX"] = 0,
        ["BHD"] = 3, ["KWD"] = 3, ["OMR"] = 3, ["JOD"] = 3, ["TND"] = 3,
    };

    public static string Normalize(string code)
    {
        if (code is null || code.Length != 3 || !code.All(char.IsAsciiLetter))
            throw new DomainValidationException($"'{code}' is not a valid ISO 4217 currency code.");
        return code.ToUpperInvariant();
    }

    public static int MinorUnits(string code) =>
        NonDefault.TryGetValue(Normalize(code), out var digits) ? digits : 2;

    /// <summary>Converts to minor units, rejecting amounts finer than the currency allows (no silent rounding).</summary>
    public static long ToMinor(decimal amount, string currency)
    {
        var digits = MinorUnits(currency);
        var scaled = amount * Pow10(digits);
        if (scaled != decimal.Truncate(scaled))
            throw new DomainValidationException($"{amount} has more than {digits} decimal places, which {currency.ToUpperInvariant()} does not allow.");
        return checked((long)scaled);
    }

    /// <summary>Rounds an arbitrary-precision amount (e.g. after FX multiplication) to minor units, half away from zero.</summary>
    public static long RoundToMinor(decimal amount, string currency) =>
        checked((long)Math.Round(amount * Pow10(MinorUnits(currency)), 0, MidpointRounding.AwayFromZero));

    public static decimal FromMinor(long minor, string currency) => minor / Pow10(MinorUnits(currency));

    private static decimal Pow10(int n) => n switch { 0 => 1m, 1 => 10m, 2 => 100m, 3 => 1000m, _ => throw new ArgumentOutOfRangeException(nameof(n)) };
}
