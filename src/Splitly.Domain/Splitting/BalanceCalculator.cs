namespace Splitly.Domain.Splitting;

/// <summary>An expense already converted to the group's base currency.</summary>
public sealed record LedgerExpense(Guid PaidBy, decimal BaseTotal, IReadOnlyList<Share> BaseShares);

/// <summary>A recorded payment between members, in the group's base currency.</summary>
public sealed record LedgerPayment(Guid From, Guid To, decimal Amount);

public static class BalanceCalculator
{
    /// <summary>Net balance per member (positive = owed). Because every expense's shares sum to its total, the result sums to zero.</summary>
    public static IReadOnlyDictionary<Guid, decimal> Compute(
        IEnumerable<LedgerExpense> expenses, IEnumerable<LedgerPayment> payments)
    {
        var net = new Dictionary<Guid, decimal>();
        void Add(Guid id, decimal v) => net[id] = net.GetValueOrDefault(id) + v;

        foreach (var e in expenses)
        {
            Add(e.PaidBy, e.BaseTotal);
            foreach (var s in e.BaseShares) Add(s.UserId, -s.Amount);
        }
        foreach (var p in payments)
        {
            Add(p.From, p.Amount); // paying someone reduces what you owe
            Add(p.To, -p.Amount);
        }
        return net;
    }
}
