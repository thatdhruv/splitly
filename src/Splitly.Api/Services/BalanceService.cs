using Microsoft.EntityFrameworkCore;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain.Splitting;

namespace Splitly.Api.Services;

public sealed class BalanceService(AppDbContext db)
{
    /// <summary>Net balance per current member in the group's base currency (positive = is owed).</summary>
    public async Task<BalancesDto> GetBalancesAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await db.Groups.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);

        var paid = await db.Expenses.Where(e => e.GroupId == groupId)
            .GroupBy(e => e.PaidBy).Select(g => new { Id = g.Key, Sum = g.Sum(e => e.BaseAmount) }).ToListAsync(ct);
        var owed = await db.ExpenseShares.Where(s => db.Expenses.Any(e => e.Id == s.ExpenseId && e.GroupId == groupId))
            .GroupBy(s => s.UserId).Select(g => new { Id = g.Key, Sum = g.Sum(s => s.BaseAmount) }).ToListAsync(ct);
        var sent = await db.Payments.Where(p => p.GroupId == groupId)
            .GroupBy(p => p.FromUserId).Select(g => new { Id = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);
        var received = await db.Payments.Where(p => p.GroupId == groupId)
            .GroupBy(p => p.ToUserId).Select(g => new { Id = g.Key, Sum = g.Sum(p => p.Amount) }).ToListAsync(ct);

        var net = new Dictionary<Guid, decimal>();
        void Add(Guid id, decimal v) => net[id] = net.GetValueOrDefault(id) + v;
        foreach (var x in paid) Add(x.Id, x.Sum);
        foreach (var x in owed) Add(x.Id, -x.Sum);
        foreach (var x in sent) Add(x.Id, x.Sum);
        foreach (var x in received) Add(x.Id, -x.Sum);

        var names = await db.GroupMembers.Where(m => m.GroupId == groupId)
            .Select(m => new { m.UserId, m.User!.DisplayName }).ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);

        var rows = net.Keys.Union(names.Keys).Order()
            .Select(id => new BalanceDto(id, names.GetValueOrDefault(id, "(former member)"), net.GetValueOrDefault(id)))
            .ToList();
        return new BalancesDto(groupId, group.BaseCurrency, rows);
    }

    public async Task<SettleUpDto> GetSettleUpAsync(Guid groupId, CancellationToken ct = default)
    {
        var balances = await GetBalancesAsync(groupId, ct);
        var plan = SettlementPlanner.Plan(balances.Balances.ToDictionary(b => b.UserId, b => b.Amount), balances.Currency);
        return new SettleUpDto(balances.Currency, plan.Select(t => new TransferDto(t.From, t.To, t.Amount)).ToList());
    }
}
