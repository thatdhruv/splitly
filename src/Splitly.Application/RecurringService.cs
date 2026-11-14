using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Domain.Money;
using Splitly.Domain.Splitting;

namespace Splitly.Application;

public sealed class RecurringService(AppDbContext db)
{
    public async Task<RecurringDto> CreateAsync(Guid groupId, Guid userId, RecurringRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Description)) throw new DomainValidationException("Description is required.");
        if (req.EndDate is { } end && end < req.StartDate) throw new DomainValidationException("The end date can't be before the start date.");
        var currency = Currencies.Normalize(req.Currency);

        var members = (await db.GroupMembers.Where(m => m.GroupId == groupId).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        if (!members.Contains(req.PaidBy)) throw new DomainValidationException("The payer must be a member of the group.");
        if (req.Participants.Any(p => !members.Contains(p.UserId))) throw new DomainValidationException("Every participant must be a member of the group.");

        // Dry-run the split now so an invalid template is rejected at creation, not silently on the first run.
        SplitCalculator.Calculate(req.SplitType, req.Amount, currency, req.Participants.Select(p => new SplitInput(p.UserId, p.Value)).ToList());

        var rec = new RecurringExpense
        {
            GroupId = groupId, Description = req.Description.Trim(), Currency = currency, Amount = req.Amount, PaidBy = req.PaidBy,
            SplitType = req.SplitType, CreatedBy = userId, Recurrence = req.Recurrence, StartDate = req.StartDate,
            NextRunDate = req.StartDate, EndDate = req.EndDate,
            Shares = req.Participants.Select(p => new RecurringShare { UserId = p.UserId, Value = p.Value }).ToList(),
        };
        db.RecurringExpenses.Add(rec);
        await db.SaveChangesAsync(ct);
        return ToDto(rec);
    }

    public async Task<List<RecurringDto>> ListAsync(Guid groupId, CancellationToken ct) =>
        (await db.RecurringExpenses.AsNoTracking().Include(r => r.Shares).Where(r => r.GroupId == groupId)
            .OrderBy(r => r.NextRunDate).ToListAsync(ct)).Select(ToDto).ToList();

    /// <summary>Stops future occurrences. Expenses already generated are left alone.</summary>
    public async Task StopAsync(Guid groupId, Guid id, CancellationToken ct)
    {
        var rec = await db.RecurringExpenses.FirstOrDefaultAsync(r => r.Id == id && r.GroupId == groupId, ct)
                  ?? throw new NotFoundException("Recurring expense not found.");
        rec.IsActive = false;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConcurrencyConflictException("This schedule just changed. Reload and try again."); }
    }

    public static RecurringDto ToDto(RecurringExpense r) => new(
        r.Id, r.GroupId, r.Description, r.Amount, r.Currency, r.PaidBy, r.SplitType,
        r.Shares.OrderBy(s => s.UserId).Select(s => new ParticipantInput(s.UserId, s.Value)).ToList(),
        r.Recurrence, r.StartDate, r.NextRunDate, r.EndDate, r.IsActive);
}

/// <summary>Generates the expenses that have fallen due. Safe to run from several workers at once.</summary>
public sealed class RecurringProcessor(AppDbContext db, ExpenseService expenses, TimeProvider clock, ILogger<RecurringProcessor> log)
{
    /// <summary>Upper bound on catch-up occurrences per schedule per pass, so a schedule starting years ago can't hog the worker.</summary>
    private const int MaxPerSchedulePerPass = 400;

    /// <returns>How many expenses were created.</returns>
    public async Task<int> ProcessDueAsync(CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("recurring.process");
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var due = await db.RecurringExpenses.AsNoTracking().Where(r => r.IsActive && r.NextRunDate <= today)
            .OrderBy(r => r.NextRunDate).Select(r => r.Id).Take(100).ToListAsync(ct);

        var created = 0;
        foreach (var id in due) created += await ProcessOneAsync(id, today, ct);
        if (created > 0) Telemetry.RecurringGenerated.Add(created);
        activity?.SetTag("splitly.recurring.created", created);
        return created;
    }

    private async Task<int> ProcessOneAsync(Guid id, DateOnly today, CancellationToken ct)
    {
        var created = 0;
        while (created < MaxPerSchedulePerPass)
        {
            db.ChangeTracker.Clear();
            var rec = await db.RecurringExpenses.Include(r => r.Shares).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (rec is null || !rec.IsActive || rec.NextRunDate > today) break;

            var date = rec.NextRunDate; // each occurrence is dated when it was due, including missed ones (catch-up)
            var request = new ExpenseRequest(rec.Description, rec.Amount, rec.Currency, date, rec.PaidBy, rec.SplitType,
                rec.Shares.Select(s => new ParticipantInput(s.UserId, s.Value)).ToList());
            try
            {
                await expenses.CreateAsync(rec.GroupId, rec.CreatedBy, request, ct, recurring: rec);
                created++;
            }
            catch (Exception ex) when (ex is ConcurrencyConflictException or DbUpdateException)
            {
                break; // another worker produced this occurrence first; nothing to do
            }
            catch (Exception ex) when (ex is DomainValidationException or HttpRequestException)
            {
                // e.g. no exchange rate available yet, or the template became invalid. The schedule doesn't advance, so it is retried next pass.
                log.LogWarning(ex, "Recurring expense {Id} could not be generated for {Date}", id, date);
                break;
            }
        }
        if (created > 0) log.LogInformation("Recurring expense {Id}: generated {Count} expense(s)", id, created);
        return created;
    }
}
