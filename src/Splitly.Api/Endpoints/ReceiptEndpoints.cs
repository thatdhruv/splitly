using Microsoft.EntityFrameworkCore;
using Splitly.Api.Auth;
using Splitly.Api.Hubs;
using Splitly.Api.Services;
using Splitly.Contracts;
using Splitly.Data;
using Splitly.Domain;
using Splitly.Receipts;

namespace Splitly.Api.Endpoints;

public static class ReceiptEndpoints
{
    /// <param name="group">The /groups/{groupId} route group, which already enforces the membership policy.</param>
    public static void MapReceipts(this RouteGroupBuilder group)
    {
        var r = group.MapGroup("/receipts");

        r.MapPost("/", async (Guid groupId, HttpRequest http, ReceiptService svc, GroupNotifier notifier, CancellationToken ct) =>
        {
            if (!http.HasFormContentType) throw new DomainValidationException("Send the photo as multipart form data.");
            var form = await http.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw new DomainValidationException("Attach the photo as a 'file' field.");
            if (file.Length is 0 or > ReceiptService.MaxBytes)
                throw new DomainValidationException($"The photo must be between 1 byte and {ReceiptService.MaxBytes / 1024 / 1024} MB.");

            await using var stream = file.OpenReadStream();
            var dto = await svc.UploadAsync(groupId, http.HttpContext.User.GetUserId(), stream, form["currency"], ct);
            await notifier.ReceiptChangedAsync(groupId, dto.Id, ct);
            return Results.Created($"/groups/{groupId}/receipts/{dto.Id}", dto);
        }).DisableAntiforgery(); // bearer-token API, not cookie-authenticated: no CSRF surface

        r.MapGet("/", async (Guid groupId, AppDbContext db, CancellationToken ct) =>
            await db.Receipts.AsNoTracking().Where(x => x.GroupId == groupId).OrderByDescending(x => x.CreatedAt).Take(100)
                .Select(x => new ReceiptSummaryDto(x.Id, x.Status.ToString(), x.Merchant, x.Currency, x.Items.Count, x.CreatedAt)).ToListAsync(ct));

        r.MapGet("/{receiptId:guid}", async (Guid groupId, Guid receiptId, ReceiptService svc, CancellationToken ct) =>
            ReceiptService.ToDto(await svc.LoadAsync(groupId, receiptId, ct)));

        r.MapGet("/{receiptId:guid}/image", async (Guid groupId, Guid receiptId, ReceiptService svc, IReceiptStorage storage, CancellationToken ct) =>
        {
            var receipt = await svc.LoadAsync(groupId, receiptId, ct);
            return Results.Stream(await storage.OpenAsync(receipt.BlobName, ct), receipt.ContentType);
        });

        // Review: any member can correct what the parser got wrong. Every change is pushed live to the group.
        r.MapPut("/{receiptId:guid}", async (Guid groupId, Guid receiptId, ReceiptTotalsRequest req, ReceiptService svc, GroupNotifier n, CancellationToken ct) =>
            await Push(n, groupId, await svc.SetTotalsAsync(groupId, receiptId, req, ct), ct));
        r.MapPost("/{receiptId:guid}/items", async (Guid groupId, Guid receiptId, ItemRequest req, ReceiptService svc, GroupNotifier n, CancellationToken ct) =>
            await Push(n, groupId, await svc.AddItemAsync(groupId, receiptId, req, ct), ct));
        r.MapPut("/{receiptId:guid}/items/{itemId:guid}", async (Guid groupId, Guid receiptId, Guid itemId, ItemRequest req, ReceiptService svc, GroupNotifier n, CancellationToken ct) =>
            await Push(n, groupId, await svc.EditItemAsync(groupId, receiptId, itemId, req, ct), ct));
        r.MapDelete("/{receiptId:guid}/items/{itemId:guid}", async (Guid groupId, Guid receiptId, Guid itemId, ReceiptService svc, GroupNotifier n, CancellationToken ct) =>
            await Push(n, groupId, await svc.DeleteItemAsync(groupId, receiptId, itemId, ct), ct));
        r.MapPost("/{receiptId:guid}/accept", async (Guid groupId, Guid receiptId, ReceiptService svc, GroupNotifier n, CancellationToken ct) =>
            await Push(n, groupId, await svc.AcceptAsync(groupId, receiptId, ct), ct));
        r.MapPost("/{receiptId:guid}/retry", async (Guid groupId, Guid receiptId, ReceiptService svc, GroupNotifier n, CancellationToken ct) =>
            await Push(n, groupId, await svc.RetryAsync(groupId, receiptId, ct), ct));

        // Claiming is always for yourself: you can't claim (or un-claim) on someone else's behalf.
        r.MapPut("/{receiptId:guid}/items/{itemId:guid}/claim", async (Guid groupId, Guid receiptId, Guid itemId, ClaimRequest req,
            HttpContext http, ReceiptService svc, GroupNotifier notifier, CancellationToken ct) =>
        {
            var dto = await svc.ClaimAsync(groupId, receiptId, itemId, http.User.GetUserId(), req.Claimed, ct);
            await notifier.ReceiptChangedAsync(groupId, receiptId, ct);
            return dto;
        });

        r.MapPost("/{receiptId:guid}/finalize", async (Guid groupId, Guid receiptId, FinalizeReceiptRequest req,
            HttpContext http, ReceiptService svc, GroupNotifier notifier, CancellationToken ct) =>
        {
            var expense = await svc.FinalizeAsync(groupId, http.User.GetUserId(), receiptId, req, ct);
            await notifier.ReceiptChangedAsync(groupId, receiptId, ct);
            await notifier.BalancesChangedAsync(groupId, ct);
            return Results.Created($"/groups/{groupId}/expenses/{expense.Id}", expense);
        });
    }

    private static async Task<ReceiptDto> Push(GroupNotifier notifier, Guid groupId, ReceiptDto dto, CancellationToken ct)
    {
        await notifier.ReceiptChangedAsync(groupId, dto.Id, ct);
        return dto;
    }
}
