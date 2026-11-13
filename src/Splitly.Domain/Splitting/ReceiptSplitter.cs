using Splitly.Domain.Money;

namespace Splitly.Domain.Splitting;

public sealed record ClaimedItem(Guid ItemId, decimal Price, IReadOnlyList<Guid> ClaimedBy);

public sealed record ReceiptSplitResult(decimal Total, IReadOnlyList<Share> Shares);

/// <summary>
/// Splits a receipt: each item is divided equally among its claimants; tax and tip are then
/// distributed in proportion to each member's item subtotal (largest-remainder, so totals are exact).
/// </summary>
public static class ReceiptSplitter
{
    public static ReceiptSplitResult Split(string currency, IReadOnlyList<ClaimedItem> items, decimal tax, decimal tip)
    {
        if (items.Count == 0) throw new DomainValidationException("A receipt needs at least one item.");
        if (tax < 0 || tip < 0) throw new DomainValidationException("Tax and tip cannot be negative.");

        var unclaimed = items.Count(i => i.ClaimedBy.Count == 0);
        if (unclaimed > 0)
            throw new DomainValidationException($"{unclaimed} item(s) have not been claimed by anyone.");

        var subtotals = new SortedDictionary<Guid, long>();
        long itemsTotal = 0;
        foreach (var item in items)
        {
            if (item.ClaimedBy.Distinct().Count() != item.ClaimedBy.Count)
                throw new DomainValidationException($"Item {item.ItemId} lists a claimant twice.");
            var priceMinor = Currencies.ToMinor(item.Price, currency);
            if (priceMinor < 0) throw new DomainValidationException("Item prices cannot be negative.");
            itemsTotal += priceMinor;

            var claimants = item.ClaimedBy.Order().ToArray();
            var parts = Allocator.AllocateEvenly(priceMinor, claimants.Length);
            for (var i = 0; i < claimants.Length; i++)
                subtotals[claimants[i]] = subtotals.GetValueOrDefault(claimants[i]) + parts[i];
        }

        var users = subtotals.Keys.ToArray();
        var weights = users.Select(u => (decimal)subtotals[u]).ToArray();
        var taxMinor = Currencies.ToMinor(tax, currency);
        var tipMinor = Currencies.ToMinor(tip, currency);

        long[] taxShares, tipShares;
        if (itemsTotal == 0)
        {
            if (taxMinor + tipMinor != 0)
                throw new DomainValidationException("Cannot apportion tax/tip on a receipt whose items total zero.");
            taxShares = tipShares = new long[users.Length];
        }
        else
        {
            taxShares = Allocator.Allocate(taxMinor, weights);
            tipShares = Allocator.Allocate(tipMinor, weights);
        }

        var shares = users.Select((u, i) =>
            new Share(u, Currencies.FromMinor(subtotals[u] + taxShares[i] + tipShares[i], currency))).ToArray();
        return new ReceiptSplitResult(Currencies.FromMinor(itemsTotal + taxMinor + tipMinor, currency), shares);
    }
}
