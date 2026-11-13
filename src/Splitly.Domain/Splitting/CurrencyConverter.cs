using Splitly.Domain.Money;

namespace Splitly.Domain.Splitting;

public static class CurrencyConverter
{
    /// <summary>
    /// Converts an expense into the group's base currency using the rate locked at the expense date.
    /// The converted total is rounded once, then re-allocated across shares in proportion to the original
    /// shares so the converted shares still sum exactly to the converted total.
    /// </summary>
    public static (decimal BaseTotal, IReadOnlyList<Share> BaseShares) ToBase(
        decimal total, string currency, string baseCurrency, decimal rate, IReadOnlyList<Share> shares)
    {
        if (rate <= 0) throw new DomainValidationException("Exchange rate must be positive.");
        if (string.Equals(currency, baseCurrency, StringComparison.OrdinalIgnoreCase))
            return (total, shares);

        var baseTotalMinor = Currencies.RoundToMinor(total * rate, baseCurrency);
        var weights = shares.Select(s => (decimal)Currencies.ToMinor(s.Amount, currency)).ToArray();
        var allocated = Allocator.Allocate(baseTotalMinor, weights);
        return (Currencies.FromMinor(baseTotalMinor, baseCurrency),
            shares.Select((s, i) => new Share(s.UserId, Currencies.FromMinor(allocated[i], baseCurrency))).ToArray());
    }
}
