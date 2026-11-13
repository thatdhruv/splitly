using Splitly.Domain;
using Splitly.Domain.Money;
using Splitly.Domain.Splitting;

namespace Splitly.Domain.Tests;

public class SplittingTests
{
    private static readonly Guid A = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B = new("00000000-0000-0000-0000-000000000002");
    private static readonly Guid C = new("00000000-0000-0000-0000-000000000003");

    private static SplitInput[] Inputs(params (Guid id, decimal? v)[] xs) => xs.Select(x => new SplitInput(x.id, x.v)).ToArray();

    [Fact]
    public void Equal_split_of_100_among_three_gives_extra_cent_to_lowest_id()
    {
        var shares = SplitCalculator.Calculate(SplitType.Equal, 100m, "USD", Inputs((C, null), (A, null), (B, null)));
        Assert.Equal([33.34m, 33.33m, 33.33m], shares.Select(s => s.Amount));
        Assert.Equal([A, B, C], shares.Select(s => s.UserId));
    }

    [Fact]
    public void Equal_split_respects_zero_decimal_currencies()
    {
        var shares = SplitCalculator.Calculate(SplitType.Equal, 1000m, "JPY", Inputs((A, null), (B, null), (C, null)));
        Assert.Equal([334m, 333m, 333m], shares.Select(s => s.Amount));
    }

    [Fact]
    public void Percentage_split_sums_exactly()
    {
        var shares = SplitCalculator.Calculate(SplitType.Percentage, 10.01m, "USD",
            Inputs((A, 33.333m), (B, 33.333m), (C, 33.334m)));
        Assert.Equal(10.01m, shares.Sum(s => s.Amount));
    }

    [Fact]
    public void Exact_split_must_match_total()
    {
        var ex = Assert.Throws<DomainValidationException>(() =>
            SplitCalculator.Calculate(SplitType.Exact, 10m, "USD", Inputs((A, 4m), (B, 5m))));
        Assert.Contains("add up to 9", ex.Message);
    }

    [Fact]
    public void Percentages_must_total_100()
    {
        Assert.Throws<DomainValidationException>(() =>
            SplitCalculator.Calculate(SplitType.Percentage, 10m, "USD", Inputs((A, 50m), (B, 40m))));
    }

    [Fact]
    public void Amounts_finer_than_the_currency_allows_are_rejected()
    {
        Assert.Throws<DomainValidationException>(() =>
            SplitCalculator.Calculate(SplitType.Equal, 10.005m, "USD", Inputs((A, null))));
        Assert.Throws<DomainValidationException>(() =>
            SplitCalculator.Calculate(SplitType.Equal, 10.5m, "JPY", Inputs((A, null))));
    }

    [Fact]
    public void Duplicate_participants_are_rejected()
    {
        Assert.Throws<DomainValidationException>(() =>
            SplitCalculator.Calculate(SplitType.Equal, 10m, "USD", Inputs((A, null), (A, null))));
    }

    [Fact]
    public void Receipt_tax_and_tip_are_proportional_to_item_subtotals()
    {
        // A: 30.00 alone; B: 10.00 alone; shared item 20.00 between A and B => A=40, B=20.
        var items = new[]
        {
            new ClaimedItem(Guid.NewGuid(), 30m, [A]),
            new ClaimedItem(Guid.NewGuid(), 10m, [B]),
            new ClaimedItem(Guid.NewGuid(), 20m, [A, B]),
        };
        var result = ReceiptSplitter.Split("USD", items, tax: 6m, tip: 12m);
        Assert.Equal(78m, result.Total);
        // A: 40 + 4 tax + 8 tip = 52 ; B: 20 + 2 + 4 = 26
        Assert.Equal([52m, 26m], result.Shares.Select(s => s.Amount));
    }

    [Fact]
    public void Receipt_with_awkward_tax_still_sums_exactly()
    {
        var items = new[]
        {
            new ClaimedItem(Guid.NewGuid(), 9.99m, [A]),
            new ClaimedItem(Guid.NewGuid(), 4.50m, [B]),
            new ClaimedItem(Guid.NewGuid(), 7.77m, [C]),
        };
        var result = ReceiptSplitter.Split("USD", items, tax: 1.83m, tip: 3.17m);
        Assert.Equal(result.Total, result.Shares.Sum(s => s.Amount));
        Assert.Equal(27.26m, result.Total);
    }

    [Fact]
    public void Receipt_rejects_unclaimed_items()
    {
        var items = new[] { new ClaimedItem(Guid.NewGuid(), 5m, []) };
        Assert.Throws<DomainValidationException>(() => ReceiptSplitter.Split("USD", items, 0, 0));
    }

    [Fact]
    public void Currency_conversion_keeps_shares_summing_to_converted_total()
    {
        var shares = SplitCalculator.Calculate(SplitType.Equal, 100m, "EUR", Inputs((A, null), (B, null), (C, null)));
        var (baseTotal, baseShares) = CurrencyConverter.ToBase(100m, "EUR", "USD", 1.0837m, shares);
        Assert.Equal(108.37m, baseTotal);
        Assert.Equal(baseTotal, baseShares.Sum(s => s.Amount));
    }

    [Fact]
    public void Settlement_example_needs_two_payments()
    {
        // A paid for everyone: A +20, B -10, C -10
        var plan = SettlementPlanner.Plan(new Dictionary<Guid, decimal> { [A] = 20m, [B] = -10m, [C] = -10m }, "USD");
        Assert.Equal(2, plan.Count);
        Assert.All(plan, t => Assert.Equal(A, t.To));
    }

    [Fact]
    public void Settlement_rejects_unbalanced_input()
    {
        Assert.Throws<DomainValidationException>(() =>
            SettlementPlanner.Plan(new Dictionary<Guid, decimal> { [A] = 1m, [B] = -2m }, "USD"));
    }
}
