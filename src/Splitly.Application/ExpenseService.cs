using Microsoft.EntityFrameworkCore;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Domain.Money;
using Splitly.Domain.Splitting;

namespace Splitly.Application;

public sealed class ConcurrencyConflictException(string message) : Exception(message);
public sealed class NotFoundException(string message) : Exception(message);

public sealed class ExpenseService(AppDbContext db, IExchangeRateProvider rates)
{
    /// <param name="receipt">When the expense comes from a receipt, it is marked finalized in the same SaveChanges,
    /// so the expense and the receipt state change together (or a concurrent finalize gets a 409).</param>
    public async Task<ExpenseDto> CreateAsync(Guid groupId, Guid userId, ExpenseRequest req, CancellationToken ct, Receipt? receipt = null)
    {
        var expense = new Expense { GroupId = groupId, CreatedBy = userId };
        await ApplyAsync(expense, req, keepRate: false, ct);
        if (receipt is not null)
        {
            expense.ReceiptId = receipt.Id;
            receipt.Status = ReceiptStatus.Finalized;
            receipt.ExpenseId = expense.Id;
        }
        db.Expenses.Add(expense);
        await SaveOrConflictAsync(ct);
        return ToDto(expense);
    }

    public async Task<ExpenseDto> UpdateAsync(Guid groupId, Guid expenseId, ExpenseRequest req, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.RowVersion))
            throw new DomainValidationException("rowVersion is required when updating an expense.");

        var expense = await db.Expenses.Include(e => e.Shares)
                          .FirstOrDefaultAsync(e => e.Id == expenseId && e.GroupId == groupId, ct)
                      ?? throw new NotFoundException("Expense not found.");

        // Optimistic concurrency: the UPDATE is conditioned on the version the client last saw.
        db.Entry(expense).Property(e => e.RowVersion).OriginalValue = ParseVersion(req.RowVersion);

        var keepRate = expense.Currency == Currencies.Normalize(req.Currency) && expense.Date == req.Date && req.ExchangeRate is null;
        await ApplyAsync(expense, req, keepRate, ct);
        await SaveOrConflictAsync(ct);
        return ToDto(expense);
    }

    public async Task DeleteAsync(Guid groupId, Guid expenseId, string? rowVersion, CancellationToken ct)
    {
        var expense = await db.Expenses.FirstOrDefaultAsync(e => e.Id == expenseId && e.GroupId == groupId, ct)
                      ?? throw new NotFoundException("Expense not found.");
        if (!string.IsNullOrEmpty(rowVersion))
            db.Entry(expense).Property(e => e.RowVersion).OriginalValue = ParseVersion(rowVersion);
        expense.IsDeleted = true;
        expense.ModifiedAt = DateTimeOffset.UtcNow;
        await SaveOrConflictAsync(ct);
    }

    private async Task ApplyAsync(Expense expense, ExpenseRequest req, bool keepRate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Description)) throw new DomainValidationException("Description is required.");

        var group = await db.Groups.AsNoTracking().FirstAsync(g => g.Id == expense.GroupId, ct);
        var memberIds = (await db.GroupMembers.Where(m => m.GroupId == expense.GroupId).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();

        if (!memberIds.Contains(req.PaidBy)) throw new DomainValidationException("The payer must be a member of the group.");
        if (req.Participants.Any(p => !memberIds.Contains(p.UserId)))
            throw new DomainValidationException("Every participant must be a member of the group.");

        var currency = Currencies.Normalize(req.Currency);
        var rate = req.ExchangeRate
                   ?? (keepRate ? expense.ExchangeRate : await rates.GetRateAsync(currency, group.BaseCurrency, req.Date, ct));

        var shares = SplitCalculator.Calculate(req.SplitType, req.Amount, currency,
            req.Participants.Select(p => new SplitInput(p.UserId, p.Value)).ToList());
        var (baseTotal, baseShares) = CurrencyConverter.ToBase(req.Amount, currency, group.BaseCurrency, rate, shares);

        expense.Description = req.Description.Trim();
        expense.Currency = currency;
        expense.Amount = req.Amount;
        expense.ExchangeRate = rate;
        expense.BaseAmount = baseTotal;
        expense.Date = req.Date;
        expense.PaidBy = req.PaidBy;
        expense.SplitType = req.SplitType;
        expense.ModifiedAt = DateTimeOffset.UtcNow;

        db.ExpenseShares.RemoveRange(expense.Shares);
        expense.Shares = shares.Select((s, i) => new ExpenseShare
        {
            ExpenseId = expense.Id, UserId = s.UserId, Amount = s.Amount, BaseAmount = baseShares[i].Amount,
        }).ToList();
    }

    private async Task SaveOrConflictAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException("This expense was changed by someone else. Reload it and try again.");
        }
    }

    private static byte[] ParseVersion(string v)
    {
        try { return Convert.FromBase64String(v); }
        catch (FormatException) { throw new DomainValidationException("rowVersion is not valid."); }
    }

    public static ExpenseDto ToDto(Expense e) => new(
        e.Id, e.GroupId, e.Description, e.Amount, e.Currency, e.ExchangeRate, e.BaseAmount, e.Date, e.PaidBy, e.SplitType,
        e.Shares.OrderBy(s => s.UserId).Select(s => new ShareDto(s.UserId, s.Amount, s.BaseAmount)).ToList(),
        Convert.ToBase64String(e.RowVersion));
}
