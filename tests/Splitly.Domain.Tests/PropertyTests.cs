using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Splitly.Domain.Money;
using Splitly.Domain.Splitting;

namespace Splitly.Domain.Tests;

/// <summary>Randomised invariants: whatever the inputs, the money always adds up.</summary>
public class PropertyTests
{
    private static readonly string[] Currs = ["USD", "EUR", "JPY", "KWD", "GBP"];
    private static readonly Guid[] People = Enumerable.Range(1, 8).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray();

    public sealed record Scenario(string Base, ExpenseSpec[] Expenses, (int From, int To, int Cents)[] Payments);
    public sealed record ExpenseSpec(string Currency, int Payer, int Cents, int[] Participants, int SplitKind, int RateMilli);

    private static Gen<Scenario> ScenarioGen()
    {
        var expense =
            from cur in Gen.Elements(Currs)
            from payer in Gen.Choose(0, People.Length - 1)
            from cents in Gen.Choose(1, 5_000_000)
            from n in Gen.Choose(1, People.Length)
            from parts in Gen.Shuffle(Enumerable.Range(0, People.Length).ToArray()).Select(p => p.Take(n).ToArray())
            from kind in Gen.Choose(0, 2)
            from rate in Gen.Choose(1, 5_000_000)
            select new ExpenseSpec(cur, payer, cents, parts, kind, rate);
        var payment =
            from f in Gen.Choose(0, People.Length - 1)
            from t in Gen.Choose(0, People.Length - 1)
            from c in Gen.Choose(1, 100_000)
            select (f, t, c);
        return
            from b in Gen.Elements(Currs)
            from exps in Gen.ArrayOf(expense).Select(a => a.Take(40).ToArray())
            from pays in Gen.ArrayOf(payment).Select(a => a.Take(20).ToArray())
            select new Scenario(b, exps, pays);
    }

    private static (IReadOnlyList<LedgerExpense>, IReadOnlyList<LedgerPayment>) Build(Scenario s)
    {
        var ledger = new List<LedgerExpense>();
        foreach (var e in s.Expenses)
        {
            var total = Currencies.FromMinor(e.Cents, e.Currency);
            var ids = e.Participants.Select(i => People[i]).ToArray();
            var type = (SplitType)e.SplitKind;
            // Build a valid input for each split type.
            IReadOnlyList<SplitInput> inputs = type switch
            {
                SplitType.Equal => ids.Select(id => new SplitInput(id)).ToArray(),
                SplitType.Exact => ExactInputs(ids, e.Cents, e.Currency),
                _ => PercentInputs(ids),
            };
            var shares = SplitCalculator.Calculate(type, total, e.Currency, inputs);
            var rate = e.Currency == s.Base ? 1m : e.RateMilli / 1000m;
            var (baseTotal, baseShares) = CurrencyConverter.ToBase(total, e.Currency, s.Base, rate, shares);
            ledger.Add(new LedgerExpense(People[e.Payer], baseTotal, baseShares));
        }
        var payments = s.Payments.Where(p => p.From != p.To)
            .Select(p => new LedgerPayment(People[p.From], People[p.To], Currencies.FromMinor(p.Cents, s.Base))).ToArray();
        return (ledger, payments);
    }

    private static SplitInput[] ExactInputs(Guid[] ids, int cents, string cur)
    {
        var parts = Allocator.Allocate(cents, ids.Select((_, i) => (decimal)(i + 1)).ToArray());
        return ids.Select((id, i) => new SplitInput(id, Currencies.FromMinor(parts[i], cur))).ToArray();
    }

    private static SplitInput[] PercentInputs(Guid[] ids)
    {
        var pct = Allocator.Allocate(10_000, ids.Select((_, i) => (decimal)(i + 1)).ToArray()); // basis points
        return ids.Select((id, i) => new SplitInput(id, pct[i] / 100m)).ToArray();
    }

    [Property(MaxTest = 300)]
    public Property Allocation_always_sums_to_total() =>
        Prop.ForAll(
            Gen.Choose(-10_000_000, 10_000_000).ToArbitrary(),
            Gen.ArrayOf(Gen.Choose(0, 1000).Select(i => i / 10m)).Where(a => a.Length > 0 && a.Sum() > 0).ToArbitrary(),
            (total, weights) => Allocator.Allocate(total, weights).Sum() == total);

    [Property(MaxTest = 300)]
    public Property Allocation_is_deterministic_and_never_off_by_more_than_one_unit() =>
        Prop.ForAll(
            Gen.Choose(0, 10_000_000).ToArbitrary(),
            Gen.ArrayOf(Gen.Choose(1, 100)).Where(a => a.Length > 0).ToArbitrary(),
            (total, w) =>
            {
                var weights = w.Select(x => (decimal)x).ToArray();
                var a = Allocator.Allocate(total, weights);
                var b = Allocator.Allocate(total, weights);
                var sum = weights.Sum();
                return a.SequenceEqual(b) &&
                       a.Select((v, i) => Math.Abs(v - total * weights[i] / sum) < 1m).All(x => x);
            });

    [Property(MaxTest = 200)]
    public Property Every_groups_balances_sum_to_zero() =>
        Prop.ForAll(ScenarioGen().ToArbitrary(), s =>
        {
            var (expenses, payments) = Build(s);
            var balances = BalanceCalculator.Compute(expenses, payments);
            return balances.Values.Sum() == 0m;
        });

    [Property(MaxTest = 200)]
    public Property Settlement_clears_every_balance_in_at_most_n_minus_one_transfers() =>
        Prop.ForAll(ScenarioGen().ToArbitrary(), s =>
        {
            var (expenses, payments) = Build(s);
            var balances = BalanceCalculator.Compute(expenses, payments);
            var plan = SettlementPlanner.Plan(balances, s.Base);

            var after = balances.ToDictionary(b => b.Key, b => b.Value);
            foreach (var t in plan)
            {
                after[t.From] += t.Amount;
                after[t.To] -= t.Amount;
            }
            var nonZero = balances.Count(b => b.Value != 0m);
            return after.Values.All(v => v == 0m) && plan.Count <= Math.Max(0, nonZero - 1);
        });

    [Property(MaxTest = 200)]
    public Property Receipt_shares_sum_to_receipt_total() =>
        Prop.ForAll(
            Gen.ArrayOf(Gen.Choose(1, 20_000)).Where(a => a.Length > 0).ToArbitrary(),
            Gen.Choose(0, 5_000).ToArbitrary(),
            (from tip in Gen.Choose(0, 5_000) from seed in Gen.Choose(0, 1 << 20) select (tip, seed)).ToArbitrary(),
            (prices, taxCents, extra) =>
            {
                var (tipCents, seed) = extra;
                var rng = new Random(seed);
                var items = prices.Select(p =>
                {
                    var claimants = People.Where(_ => rng.Next(3) == 0).ToArray();
                    if (claimants.Length == 0) claimants = [People[rng.Next(People.Length)]];
                    return new ClaimedItem(Guid.NewGuid(), p / 100m, claimants);
                }).ToArray();
                var r = ReceiptSplitter.Split("USD", items, taxCents / 100m, tipCents / 100m);
                return r.Shares.Sum(s => s.Amount) == r.Total
                       && r.Total == items.Sum(i => i.Price) + taxCents / 100m + tipCents / 100m;
            });
}
