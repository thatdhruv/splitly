using Microsoft.EntityFrameworkCore;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Domain.Money;
using Splitly.Domain.Splitting;
using Splitly.Receipts;

namespace Splitly.Api.Services;

public sealed class ReceiptService(AppDbContext db, IReceiptStorage storage, ExpenseService expenses)
{
    public const long MaxBytes = 5 * 1024 * 1024; // the vision API's per-image limit

    public async Task<ReceiptDto> UploadAsync(Guid groupId, Guid userId, Stream file, string? currency, CancellationToken ct)
    {
        // Don't trust the client's Content-Type: sniff the real format from the magic bytes.
        var head = new byte[12];
        var read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var contentType = Sniff(head.AsSpan(0, read))
                          ?? throw new DomainValidationException("Upload a JPEG, PNG or WebP photo of the receipt.");
        file.Position = 0;

        var group = await db.Groups.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
        var receipt = new Receipt
        {
            GroupId = groupId, UploadedBy = userId, ContentType = contentType, Status = ReceiptStatus.Uploaded,
            Currency = string.IsNullOrWhiteSpace(currency) ? group.BaseCurrency : Currencies.Normalize(currency),
        };
        receipt.BlobName = $"{groupId}/{receipt.Id}";

        // Blob first: a receipt row must never point at a missing image. An orphaned blob is harmless; a dangling row isn't.
        await storage.SaveAsync(receipt.BlobName, file, contentType, ct);
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        return ToDto(receipt);
    }

    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            return "image/webp";
        return null;
    }

    public async Task<Receipt> LoadAsync(Guid groupId, Guid receiptId, CancellationToken ct) =>
        await db.Receipts.Include(r => r.Items).ThenInclude(i => i.Claims)
            .FirstOrDefaultAsync(r => r.Id == receiptId && r.GroupId == groupId, ct)
        ?? throw new NotFoundException("Receipt not found.");

    public async Task<ReceiptDto> EditItemAsync(Guid groupId, Guid receiptId, Guid itemId, ItemRequest req, CancellationToken ct)
    {
        var receipt = await LoadEditableAsync(groupId, receiptId, ct);
        var item = receipt.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("Item not found.");
        ValidateItem(req, receipt.Currency);
        item.Description = req.Description.Trim();
        item.Price = req.Price;
        item.NeedsReview = false; // a human looked at it
        item.Confidence = 1;
        return await SaveReviewedAsync(receipt, ct);
    }

    public async Task<ReceiptDto> AddItemAsync(Guid groupId, Guid receiptId, ItemRequest req, CancellationToken ct)
    {
        var receipt = await LoadEditableAsync(groupId, receiptId, ct);
        ValidateItem(req, receipt.Currency);
        receipt.Items.Add(new ReceiptItem { ReceiptId = receipt.Id, Description = req.Description.Trim(), Price = req.Price });
        return await SaveReviewedAsync(receipt, ct);
    }

    public async Task<ReceiptDto> DeleteItemAsync(Guid groupId, Guid receiptId, Guid itemId, CancellationToken ct)
    {
        var receipt = await LoadEditableAsync(groupId, receiptId, ct);
        var item = receipt.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("Item not found.");
        receipt.Items.Remove(item);
        db.ReceiptItems.Remove(item);
        return await SaveReviewedAsync(receipt, ct);
    }

    public async Task<ReceiptDto> SetTotalsAsync(Guid groupId, Guid receiptId, ReceiptTotalsRequest req, CancellationToken ct)
    {
        var receipt = await LoadEditableAsync(groupId, receiptId, ct);
        if (!string.IsNullOrWhiteSpace(req.Currency)) receipt.Currency = Currencies.Normalize(req.Currency);
        if (req.Tax < 0 || req.Tip < 0) throw new DomainValidationException("Tax and tip cannot be negative.");
        Currencies.ToMinor(req.Tax, receipt.Currency);
        Currencies.ToMinor(req.Tip, receipt.Currency);
        receipt.Tax = req.Tax;
        receipt.Tip = req.Tip;
        return await SaveReviewedAsync(receipt, ct);
    }

    /// <summary>"These numbers are right, even though they don't match the printed total."</summary>
    public async Task<ReceiptDto> AcceptAsync(Guid groupId, Guid receiptId, CancellationToken ct)
    {
        var receipt = await LoadEditableAsync(groupId, receiptId, ct);
        return await SaveReviewedAsync(receipt, ct);
    }

    public async Task<ReceiptDto> RetryAsync(Guid groupId, Guid receiptId, CancellationToken ct)
    {
        var receipt = await LoadAsync(groupId, receiptId, ct);
        if (receipt.Status != ReceiptStatus.Failed) throw new DomainValidationException("Only failed receipts can be retried.");
        receipt.Status = ReceiptStatus.Uploaded;
        receipt.Attempts = 0;
        receipt.FailureReason = null;
        await SaveAsync(ct);
        return ToDto(receipt);
    }

    /// <summary>Members claim items for themselves. Claiming is idempotent in both directions.</summary>
    public async Task<ReceiptDto> ClaimAsync(Guid groupId, Guid receiptId, Guid itemId, Guid userId, bool claimed, CancellationToken ct)
    {
        var receipt = await LoadAsync(groupId, receiptId, ct);
        if (receipt.Status is not (ReceiptStatus.NeedsReview or ReceiptStatus.Ready))
            throw new DomainValidationException("This receipt can't be claimed in its current state.");
        var item = receipt.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("Item not found.");

        var existing = item.Claims.FirstOrDefault(c => c.UserId == userId);
        if (claimed && existing is null) item.Claims.Add(new ItemClaim { ReceiptItemId = item.Id, UserId = userId });
        else if (!claimed && existing is not null) { item.Claims.Remove(existing); db.ItemClaims.Remove(existing); }
        else return ToDto(receipt);

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* same claim inserted concurrently: the end state is what the caller wanted */ db.ChangeTracker.Clear(); receipt = await LoadAsync(groupId, receiptId, ct); }
        return ToDto(receipt);
    }

    public async Task<ExpenseDto> FinalizeAsync(Guid groupId, Guid userId, Guid receiptId, FinalizeReceiptRequest req, CancellationToken ct)
    {
        var receipt = await LoadAsync(groupId, receiptId, ct);
        if (receipt.Status == ReceiptStatus.Finalized) throw new DomainValidationException("This receipt has already been turned into an expense.");
        if (receipt.Status != ReceiptStatus.Ready)
            throw new DomainValidationException(receipt.Status == ReceiptStatus.NeedsReview
                ? "Review the flagged items first (or accept the receipt as it is)."
                : "This receipt isn't ready yet.");

        var memberIds = (await db.GroupMembers.Where(m => m.GroupId == groupId).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        var claimed = receipt.Items.Select(i => new ClaimedItem(i.Id, i.Price, i.Claims.Select(c => c.UserId).Where(memberIds.Contains).ToList())).ToList();
        var split = ReceiptSplitter.Split(receipt.Currency, claimed, receipt.Tax, receipt.Tip);

        // Receipt shares become an exact split, so the expense machinery handles FX, validation and rounding checks once.
        var request = new ExpenseRequest(
            receipt.Merchant is { Length: > 0 } m ? m : "Receipt", split.Total, receipt.Currency, req.Date, req.PaidBy, SplitType.Exact,
            split.Shares.Select(s => new ParticipantInput(s.UserId, s.Amount)).ToList());
        return await expenses.CreateAsync(groupId, userId, request, ct, receipt);
    }

    // ---- helpers ----

    private async Task<Receipt> LoadEditableAsync(Guid groupId, Guid receiptId, CancellationToken ct)
    {
        var receipt = await LoadAsync(groupId, receiptId, ct);
        if (receipt.Status is not (ReceiptStatus.NeedsReview or ReceiptStatus.Ready))
            throw new DomainValidationException("This receipt can't be edited in its current state.");
        return receipt;
    }

    private static void ValidateItem(ItemRequest req, string currency)
    {
        if (string.IsNullOrWhiteSpace(req.Description)) throw new DomainValidationException("Item description is required.");
        if (req.Price < 0) throw new DomainValidationException("Item price can't be negative. Remove discount lines or fold them into the item.");
        Currencies.ToMinor(req.Price, currency);
    }

    /// <summary>After a human edit: the printed-total warning is dealt with, and the receipt is Ready once nothing is flagged.</summary>
    private async Task<ReceiptDto> SaveReviewedAsync(Receipt receipt, CancellationToken ct)
    {
        receipt.Note = null;
        receipt.Status = receipt.Items.Count > 0 && receipt.Items.All(i => !i.NeedsReview) ? ReceiptStatus.Ready : ReceiptStatus.NeedsReview;
        // Claims reference items by id, so removed items take their claims with them (cascade).
        await SaveAsync(ct);
        return ToDto(receipt);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConcurrencyConflictException("This receipt was changed by someone else. Reload and try again."); }
    }

    public static ReceiptDto ToDto(Receipt r) => new(
        r.Id, r.GroupId, r.Status.ToString(), r.Currency, r.Merchant, r.Tax, r.Tip, r.StatedTotal, r.Note, r.FailureReason, r.ExpenseId, r.CreatedAt,
        r.Items.OrderBy(i => i.Id).Select(i => new ReceiptItemDto(i.Id, i.Description, i.Price, i.Confidence, i.NeedsReview,
            i.Claims.Select(c => c.UserId).Order().ToList())).ToList());
}
