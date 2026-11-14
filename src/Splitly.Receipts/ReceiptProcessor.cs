using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Domain.Money;

namespace Splitly.Receipts;

public sealed class ReceiptProcessingOptions
{
    /// <summary>Items the parser is less sure of than this are flagged for human review.</summary>
    public double ConfidenceThreshold { get; set; } = 0.8;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>A receipt stuck in Processing longer than this (worker crashed) is picked up again.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Takes one uploaded receipt at a time: claims it atomically (so several workers can run safely), reads the image
/// from blob storage, extracts line items, and flags anything a human should double-check.
/// </summary>
public sealed class ReceiptProcessor(
    AppDbContext db, IReceiptStorage storage, IReceiptParser parser, TimeProvider clock,
    ReceiptProcessingOptions options, ILogger<ReceiptProcessor> log)
{
    /// <summary>Returns false when there was nothing to do.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var receiptId = await ClaimNextAsync(ct);
        if (receiptId is null) return false;
        await ProcessAsync(receiptId.Value, ct);
        return true;
    }

    private async Task<Guid?> ClaimNextAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var staleBefore = now - options.StaleAfter;

        var candidates = await db.Receipts.AsNoTracking()
            .Where(r => r.Status == ReceiptStatus.Uploaded
                        || (r.Status == ReceiptStatus.Processing && r.ProcessingStartedAt < staleBefore))
            .OrderBy(r => r.CreatedAt).Select(r => new { r.Id, r.Status, r.Attempts }).Take(5).ToListAsync(ct);

        foreach (var c in candidates)
        {
            if (c.Attempts >= options.MaxAttempts)
            {
                await db.Receipts.Where(r => r.Id == c.Id && r.Status == c.Status).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, ReceiptStatus.Failed)
                    .SetProperty(r => r.FailureReason, "Gave up after repeated failures. You can retry."), ct);
                continue;
            }

            // Compare-and-set on the status we saw: only one worker wins this row.
            var won = await db.Receipts.Where(r => r.Id == c.Id && r.Status == c.Status).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ReceiptStatus.Processing)
                .SetProperty(r => r.Attempts, r => r.Attempts + 1)
                .SetProperty(r => r.ProcessingStartedAt, now), ct);
            if (won == 1) return c.Id;
        }
        return null;
    }

    private async Task ProcessAsync(Guid receiptId, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("receipt.process");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        db.ChangeTracker.Clear();
        var receipt = await db.Receipts.Include(r => r.Items).FirstAsync(r => r.Id == receiptId, ct);
        string outcome;
        try
        {
            await using var image = await storage.OpenAsync(receipt.BlobName, ct);
            var parsed = await parser.ParseAsync(image, receipt.ContentType, receipt.Currency, ct);
            Apply(receipt, parsed);
            await db.SaveChangesAsync(ct);
            outcome = receipt.Status == ReceiptStatus.Ready ? "ready" : "needs_review";
            log.LogInformation("Receipt {Id} parsed: {Items} items, status {Status}", receipt.Id, receipt.Items.Count, receipt.Status);
        }
        catch (ReceiptParseException ex)
        {
            outcome = "unreadable";
            await FailAsync(receipt, ex.Message, retry: false, ct); // retrying the same image won't help
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Receipt {Id} attempt {Attempt} failed", receipt.Id, receipt.Attempts);
            outcome = receipt.Attempts < options.MaxAttempts ? "retry" : "failed";
            await FailAsync(receipt, "Couldn't read this receipt right now.", retry: receipt.Attempts < options.MaxAttempts, ct);
        }
        Telemetry.ReceiptsProcessed.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        Telemetry.ReceiptParseSeconds.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        activity?.SetTag("splitly.receipt.outcome", outcome);
    }

    private async Task FailAsync(Receipt receipt, string reason, bool retry, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await db.Receipts.Where(r => r.Id == receipt.Id).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, retry ? ReceiptStatus.Uploaded : ReceiptStatus.Failed)
            .SetProperty(r => r.FailureReason, reason), ct);
    }

    private void Apply(Receipt receipt, ParsedReceipt parsed)
    {
        // Prefer the group's/uploader's currency if the parser couldn't tell; a bad code from the model must not poison the receipt.
        var currency = TryNormalize(parsed.Currency) ?? receipt.Currency;
        receipt.Currency = currency;
        receipt.Merchant = Truncate(parsed.Merchant, 200);
        receipt.StatedTotal = parsed.Total;

        long ItemsMinor() => receipt.Items.Sum(i => Currencies.RoundToMinor(i.Price, currency));

        receipt.Items.Clear();
        foreach (var p in parsed.Items.Take(200))
        {
            var rounded = Currencies.FromMinor(Currencies.RoundToMinor(p.Price, currency), currency);
            receipt.Items.Add(new ReceiptItem
            {
                ReceiptId = receipt.Id,
                Description = Truncate(string.IsNullOrWhiteSpace(p.Description) ? "(unreadable)" : p.Description, 200)!,
                Price = rounded,
                Confidence = p.Confidence,
                // Flag low confidence, unreadable names, and negative lines (discounts must be sorted out before splitting).
                NeedsReview = p.Confidence < options.ConfidenceThreshold || string.IsNullOrWhiteSpace(p.Description) || rounded < 0,
            });
        }

        receipt.Tax = Currencies.FromMinor(Currencies.RoundToMinor(parsed.Tax, currency), currency);
        receipt.Tip = Currencies.FromMinor(Currencies.RoundToMinor(parsed.Tip, currency), currency);

        string? note = null;
        if (parsed.Total is { } printed)
        {
            var computed = ItemsMinor() + Currencies.RoundToMinor(receipt.Tax + receipt.Tip, currency);
            var diff = computed - Currencies.RoundToMinor(printed, currency);
            if (diff != 0)
                note = $"Items, tax and tip add up to {Currencies.FromMinor(computed, currency)} but the receipt says {printed}. Check for a missed or misread line.";
        }
        receipt.Note = note;
        receipt.FailureReason = null;
        receipt.Status = receipt.Items.Count == 0
            ? ReceiptStatus.NeedsReview
            : receipt.Items.Any(i => i.NeedsReview) || note is not null ? ReceiptStatus.NeedsReview : ReceiptStatus.Ready;
        if (receipt.Items.Count == 0) receipt.Note = "No line items were found. Add them manually.";
    }

    private static string? TryNormalize(string? code)
    {
        try { return code is null ? null : Currencies.Normalize(code); }
        catch (DomainValidationException) { return null; }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
