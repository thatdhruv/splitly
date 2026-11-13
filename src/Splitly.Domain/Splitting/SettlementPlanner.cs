using Splitly.Domain.Money;

namespace Splitly.Domain.Splitting;

public readonly record struct Transfer(Guid From, Guid To, decimal Amount);

public static class SettlementPlanner
{
    /// <summary>
    /// Greedy net-balance settlement: repeatedly pay the largest debtor's balance to the largest creditor.
    /// Each step clears at least one person, so at most (n - 1) transfers are produced. Ties break on user id.
    /// Positive balance = is owed money; negative = owes money.
    /// </summary>
    public static IReadOnlyList<Transfer> Plan(IReadOnlyDictionary<Guid, decimal> balances, string currency)
    {
        var net = balances.ToDictionary(b => b.Key, b => Currencies.ToMinor(b.Value, currency));
        if (net.Values.Sum() != 0)
            throw new DomainValidationException("Balances do not sum to zero; refusing to compute a settlement.");

        var transfers = new List<Transfer>();
        while (true)
        {
            var debtors = net.Where(n => n.Value < 0).OrderBy(n => n.Value).ThenBy(n => n.Key).ToArray();
            var creditors = net.Where(n => n.Value > 0).OrderByDescending(n => n.Value).ThenBy(n => n.Key).ToArray();
            if (debtors.Length == 0 || creditors.Length == 0) break;

            var (debtor, creditor) = (debtors[0], creditors[0]);
            var amount = Math.Min(-debtor.Value, creditor.Value);
            transfers.Add(new Transfer(debtor.Key, creditor.Key, Currencies.FromMinor(amount, currency)));
            net[debtor.Key] += amount;
            net[creditor.Key] -= amount;
        }
        return transfers;
    }
}
