using System.Net.Http.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Splitly.Contracts;
using Splitly.Domain.Splitting;

namespace Splitly.Api.Tests;

/// <summary>
/// End-to-end version of the core invariant: whatever mix of expenses, currencies, split types and payments
/// is thrown at a real group in real SQL Server, the members' balances sum to exactly zero.
/// </summary>
[Collection(SqlServerCollection.Name)]
public class BalanceInvariantTests(SqlServerFixture sql)
{
    private static readonly string[] Currencies = ["USD", "EUR", "JPY"];

    public sealed record Op(int Payer, int Cents, string Currency, int SplitKind, bool[] In, bool IsPayment);

    private static Gen<Op> OpGen(int members) =>
        from payer in Gen.Choose(0, members - 1)
        from cents in Gen.Choose(1, 2_000_000)
        from cur in Gen.Elements(Currencies)
        from kind in Gen.Choose(0, 2)
        from inc in Gen.ArrayOf(Gen.Elements(true, false), members)
        from pay in Gen.Frequency(new[] { (4, Gen.Constant(false)), (1, Gen.Constant(true)) })
        select new Op(payer, cents, cur, kind, inc, pay);

    [Property(MaxTest = 12, QuietOnSuccess = true)]
    public Property Group_balances_always_sum_to_zero() =>
        Prop.ForAll(Gen.ArrayOf(OpGen(4)).Where(a => a.Length is > 0 and <= 15).ToArbitrary(), ops =>
        {
            RunAsync(ops).GetAwaiter().GetResult();
            return true;
        });

    private async Task RunAsync(Op[] ops)
    {
        await using var factory = new ApiFactory(sql.ConnectionString);
        var users = new List<TestUser>();
        for (var i = 0; i < 4; i++) users.Add(await TestUser.CreateAsync(factory, $"u{i}"));
        var group = await users[0].CreateGroupAsync("USD");
        foreach (var u in users.Skip(1)) await users[0].AddMemberAsync(group.Id, u);

        foreach (var op in ops)
        {
            var who = users[op.Payer];
            if (op.IsPayment)
            {
                var to = users[(op.Payer + 1) % 4];
                var pay = await who.Http.PostAsJsonAsync($"/groups/{group.Id}/payments", new PaymentRequest(who.Id, to.Id, op.Cents / 100m));
                pay.EnsureSuccessStatusCode();
                continue;
            }

            var participants = users.Where((_, i) => op.In[i]).Select(u => u.Id).DefaultIfEmpty(who.Id).ToList();
            var amount = op.Currency == "JPY" ? op.Cents : op.Cents / 100m;
            var split = (SplitType)op.SplitKind;
            decimal?[]? values = split switch
            {
                SplitType.Percentage => EvenPercentages(participants.Count),
                SplitType.Exact => null,
                _ => null,
            };
            if (split == SplitType.Exact) split = SplitType.Equal; // exact amounts are covered in the domain property test
            (await who.AddExpenseAsync(group.Id, amount, who.Id, participants, op.Currency, split, values)).EnsureSuccessStatusCode();
        }

        var balances = await users[0].BalancesAsync(group.Id);
        var sum = balances.Balances.Sum(b => b.Amount);
        if (sum != 0m) throw new Exception($"Balances summed to {sum}, expected 0.");
    }

    private static decimal?[] EvenPercentages(int n)
    {
        var basis = Splitly.Domain.Money.Allocator.AllocateEvenly(10_000, n);
        return basis.Select(b => (decimal?)(b / 100m)).ToArray();
    }
}
