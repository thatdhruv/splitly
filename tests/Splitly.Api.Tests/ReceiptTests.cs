using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Splitly.Contracts;
using Splitly.Receipts;

namespace Splitly.Api.Tests;

[Collection(SqlServerCollection.Name)]
public class ReceiptTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(sql.ConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<(TestUser alice, TestUser bob, GroupSummaryDto group)> TwoMemberGroup()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        var bob = await TestUser.CreateAsync(_factory, "bob");
        var group = await alice.CreateGroupAsync();
        await alice.AddMemberAsync(group.Id, bob);
        return (alice, bob, group);
    }

    private async Task<ReceiptDto> UploadAndProcess(TestUser who, Guid groupId)
    {
        var res = await who.UploadReceiptAsync(groupId);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<ReceiptDto>())!;
        Assert.Equal("Uploaded", dto.Status);
        await _factory.ProcessReceiptsAsync();
        return await who.GetReceiptAsync(groupId, dto.Id);
    }

    // ---- upload ----

    [Fact]
    public async Task Upload_rejects_non_images_even_if_the_client_claims_image_jpeg()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var res = await alice.UploadReceiptAsync(group.Id, "not an image at all"u8.ToArray(), "evil.jpg", "image/jpeg");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Upload_rejects_oversized_files()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var big = new byte[6 * 1024 * 1024];
        TestUser.TinyPng.CopyTo(big, 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.UploadReceiptAsync(group.Id, big)).StatusCode);
    }

    [Fact]
    public async Task Non_members_get_403_on_every_receipt_route()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        var mallory = await TestUser.CreateAsync(_factory, "mallory");
        var g = $"/groups/{group.Id}/receipts";
        var item = receipt.Items[0].Id;

        var responses = new[]
        {
            await mallory.UploadReceiptAsync(group.Id),
            await mallory.Http.GetAsync(g),
            await mallory.Http.GetAsync($"{g}/{receipt.Id}"),
            await mallory.Http.GetAsync($"{g}/{receipt.Id}/image"),
            await mallory.ClaimAsync(group.Id, receipt.Id, item),
            await mallory.Http.PutAsJsonAsync($"{g}/{receipt.Id}/items/{item}", new ItemRequest("x", 1m)),
            await mallory.Http.PostAsJsonAsync($"{g}/{receipt.Id}/items", new ItemRequest("x", 1m)),
            await mallory.Http.DeleteAsync($"{g}/{receipt.Id}/items/{item}"),
            await mallory.Http.PutAsJsonAsync($"{g}/{receipt.Id}", new ReceiptTotalsRequest(0, 0)),
            await mallory.Http.PostAsync($"{g}/{receipt.Id}/accept", null),
            await mallory.Http.PostAsJsonAsync($"{g}/{receipt.Id}/finalize", new FinalizeReceiptRequest(mallory.Id, new DateOnly(2026, 1, 1))),
        };
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        // Another group's id in the URL can't be used to reach this group's receipt either.
        var mallorysGroup = await mallory.CreateGroupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.GetAsync($"/groups/{mallorysGroup.Id}/receipts/{receipt.Id}")).StatusCode);
    }

    [Fact]
    public async Task Members_can_view_the_stored_image()
    {
        var (alice, bob, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        var res = await bob.Http.GetAsync($"/groups/{group.Id}/receipts/{receipt.Id}/image");
        Assert.Equal("image/png", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TestUser.TinyPng, await res.Content.ReadAsByteArrayAsync());
    }

    // ---- processing & review ----

    [Fact]
    public async Task Low_confidence_items_are_flagged_for_review()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);

        Assert.Equal("NeedsReview", receipt.Status);
        Assert.Equal("Corner Bistro", receipt.Merchant);
        Assert.Equal(3, receipt.Items.Count);
        Assert.Equal(["Smudged line"], receipt.Items.Where(i => i.NeedsReview).Select(i => i.Description));
        Assert.Equal(2.30m, receipt.Tax);
        Assert.Equal(5.00m, receipt.Tip);
        Assert.Null(receipt.Note); // 12.50 + 9.00 + 4.25 + 2.30 + 5.00 = 33.05 = printed total
    }

    [Fact]
    public async Task Fully_confident_receipt_is_ready_immediately()
    {
        _factory.Parser.Result = FakeReceiptParser.Sample with { Items = [new("Pizza", 20m, 0.99), new("Soda", 3m, 0.95)], Tax = 0, Tip = 0, Total = 23m };
        var (alice, _, group) = await TwoMemberGroup();
        Assert.Equal("Ready", (await UploadAndProcess(alice, group.Id)).Status);
    }

    [Fact]
    public async Task Totals_that_dont_add_up_need_review_and_can_be_accepted()
    {
        _factory.Parser.Result = FakeReceiptParser.Sample with { Items = [new("Pizza", 20m, 0.99)], Tax = 0, Tip = 0, Total = 25m };
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);

        Assert.Equal("NeedsReview", receipt.Status);
        Assert.Contains("25", receipt.Note);
        Assert.All(receipt.Items, i => Assert.False(i.NeedsReview));

        var accepted = await (await alice.Http.PostAsync($"/groups/{group.Id}/receipts/{receipt.Id}/accept", null)).Content.ReadFromJsonAsync<ReceiptDto>();
        Assert.Equal("Ready", accepted!.Status);
        Assert.Null(accepted.Note);
    }

    [Fact]
    public async Task Reviewing_the_flagged_item_makes_the_receipt_ready()
    {
        var (alice, bob, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        var flagged = receipt.Items.Single(i => i.NeedsReview);

        var res = await bob.Http.PutAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/items/{flagged.Id}", new ItemRequest("Cheesecake", 4.75m));
        var updated = (await res.Content.ReadFromJsonAsync<ReceiptDto>())!;

        Assert.Equal("Ready", updated.Status);
        Assert.Contains(updated.Items, i => i.Description == "Cheesecake" && i.Price == 4.75m && !i.NeedsReview);
    }

    [Fact]
    public async Task Item_prices_must_fit_the_currency()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        var res = await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/items", new ItemRequest("Tea", 1.005m));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- claim & finalize ----

    [Fact]
    public async Task Claim_and_finalize_splits_tax_and_tip_proportionally_and_updates_balances()
    {
        _factory.Parser.Result = FakeReceiptParser.Sample with
        {
            Items = [new("Burger", 30m, 0.99), new("Salad", 10m, 0.99), new("Fries", 20m, 0.99)], Tax = 6m, Tip = 12m, Total = 78m,
        };
        var (alice, bob, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        Assert.Equal("Ready", receipt.Status);
        var (burger, salad, fries) = (receipt.Items.Single(i => i.Description == "Burger"), receipt.Items.Single(i => i.Description == "Salad"), receipt.Items.Single(i => i.Description == "Fries"));

        // Alice: burger + half the fries (40). Bob: salad + half the fries (20).
        foreach (var id in new[] { burger.Id, fries.Id }) (await alice.ClaimAsync(group.Id, receipt.Id, id)).EnsureSuccessStatusCode();
        foreach (var id in new[] { salad.Id, fries.Id }) (await bob.ClaimAsync(group.Id, receipt.Id, id)).EnsureSuccessStatusCode();

        var res = await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/finalize", new FinalizeReceiptRequest(alice.Id, new DateOnly(2026, 3, 14)));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var expense = (await res.Content.ReadFromJsonAsync<ExpenseDto>())!;

        Assert.Equal(78m, expense.Amount);
        Assert.Equal("Corner Bistro", expense.Description);
        // Alice 40/60 of (6 + 12) = 12 => 40 + 12 = 52 ; Bob 20/60 => 6 => 26.
        Assert.Equal(52m, expense.Shares.Single(s => s.UserId == alice.Id).Amount);
        Assert.Equal(26m, expense.Shares.Single(s => s.UserId == bob.Id).Amount);

        var balances = (await alice.BalancesAsync(group.Id)).Balances.ToDictionary(b => b.UserId, b => b.Amount);
        Assert.Equal(26m, balances[alice.Id]);
        Assert.Equal(-26m, balances[bob.Id]);

        var done = await alice.GetReceiptAsync(group.Id, receipt.Id);
        Assert.Equal("Finalized", done.Status);
        Assert.Equal(expense.Id, done.ExpenseId);
    }

    [Fact]
    public async Task Finalize_requires_every_item_to_be_claimed()
    {
        _factory.Parser.Result = FakeReceiptParser.Sample with { Items = [new("A", 5m, 1), new("B", 5m, 1)], Tax = 0, Tip = 0, Total = 10m };
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        (await alice.ClaimAsync(group.Id, receipt.Id, receipt.Items[0].Id)).EnsureSuccessStatusCode();

        var res = await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/finalize", new FinalizeReceiptRequest(alice.Id, new DateOnly(2026, 3, 14)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("not been claimed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Finalize_is_blocked_while_items_are_flagged()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        var res = await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/finalize", new FinalizeReceiptRequest(alice.Id, new DateOnly(2026, 3, 14)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_receipt_can_only_be_finalized_once_and_is_locked_afterwards()
    {
        _factory.Parser.Result = FakeReceiptParser.Sample with { Items = [new("A", 10m, 1)], Tax = 0, Tip = 0, Total = 10m };
        var (alice, _, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        (await alice.ClaimAsync(group.Id, receipt.Id, receipt.Items[0].Id)).EnsureSuccessStatusCode();
        var body = new FinalizeReceiptRequest(alice.Id, new DateOnly(2026, 3, 14));

        Assert.Equal(HttpStatusCode.Created, (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/finalize", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/receipts/{receipt.Id}/finalize", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.ClaimAsync(group.Id, receipt.Id, receipt.Items[0].Id, claimed: false)).StatusCode);
        Assert.Single((await alice.Http.GetFromJsonAsync<List<ExpenseDto>>($"/groups/{group.Id}/expenses"))!);
    }

    [Fact]
    public async Task Claims_are_idempotent_and_can_be_withdrawn()
    {
        var (alice, bob, group) = await TwoMemberGroup();
        var receipt = await UploadAndProcess(alice, group.Id);
        var item = receipt.Items[0].Id;

        await alice.ClaimAsync(group.Id, receipt.Id, item);
        await alice.ClaimAsync(group.Id, receipt.Id, item);
        await bob.ClaimAsync(group.Id, receipt.Id, item);
        Assert.Equal(2, (await alice.GetReceiptAsync(group.Id, receipt.Id)).Items.Single(i => i.Id == item).ClaimedBy.Count);

        await alice.ClaimAsync(group.Id, receipt.Id, item, claimed: false);
        Assert.Equal([bob.Id], (await alice.GetReceiptAsync(group.Id, receipt.Id)).Items.Single(i => i.Id == item).ClaimedBy);
    }

    // ---- worker behaviour ----

    [Fact]
    public async Task Several_workers_never_parse_the_same_receipt_twice()
    {
        var (alice, _, group) = await TwoMemberGroup();
        (await alice.UploadReceiptAsync(group.Id)).EnsureSuccessStatusCode();
        var before = _factory.Parser.Calls;

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _factory.ProcessReceiptsAsync()));

        Assert.Equal(1, _factory.Parser.Calls - before);
    }

    [Fact]
    public async Task Transient_failures_are_retried_then_give_up_and_can_be_retried_by_a_member()
    {
        _factory.Parser.Throw = () => new HttpRequestException("boom");
        var (alice, _, group) = await TwoMemberGroup();
        var dto = (await (await alice.UploadReceiptAsync(group.Id)).Content.ReadFromJsonAsync<ReceiptDto>())!;

        await _factory.ProcessReceiptsAsync();
        var failed = await alice.GetReceiptAsync(group.Id, dto.Id);
        Assert.Equal("Failed", failed.Status);
        Assert.NotNull(failed.FailureReason);

        _factory.Parser.Throw = null;
        var retried = await alice.Http.PostAsync($"/groups/{group.Id}/receipts/{dto.Id}/retry", null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        await _factory.ProcessReceiptsAsync();
        Assert.Equal("NeedsReview", (await alice.GetReceiptAsync(group.Id, dto.Id)).Status);
    }

    [Fact]
    public async Task Unreadable_receipts_fail_immediately_without_retries()
    {
        _factory.Parser.Throw = () => new ReceiptParseException("The model declined to read this image.");
        var (alice, _, group) = await TwoMemberGroup();
        var dto = (await (await alice.UploadReceiptAsync(group.Id)).Content.ReadFromJsonAsync<ReceiptDto>())!;
        var before = _factory.Parser.Calls;

        await _factory.ProcessReceiptsAsync();

        Assert.Equal(1, _factory.Parser.Calls - before);
        var r = await alice.GetReceiptAsync(group.Id, dto.Id);
        Assert.Equal("Failed", r.Status);
        Assert.Equal("The model declined to read this image.", r.FailureReason);
    }

    [Fact]
    public async Task A_worker_that_crashed_mid_parse_is_recovered_after_the_stale_timeout()
    {
        var (alice, _, group) = await TwoMemberGroup();
        var dto = (await (await alice.UploadReceiptAsync(group.Id)).Content.ReadFromJsonAsync<ReceiptDto>())!;

        // Simulate a worker that claimed the receipt and died.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Splitly.Data.AppDbContext>();
            var r = await db.Receipts.FindAsync(dto.Id);
            r!.Status = Splitly.Data.ReceiptStatus.Processing;
            r.Attempts = 1;
            r.ProcessingStartedAt = _factory.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await _factory.ProcessReceiptsAsync()); // still looks alive

        _factory.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await _factory.ProcessReceiptsAsync());
    }

    // ---- parser output handling (no network) ----

    [Fact]
    public void Claude_reply_with_code_fences_and_prose_is_still_parsed()
    {
        var parsed = ClaudeReceiptParser.ParseJson("""
            Sure! Here is the data:
            ```json
            {"currency":"EUR","merchant":"Café","items":[{"description":"Latte","price":3.5,"confidence":0.9},{"description":"Cake","price":4,"confidence":0.3},{"description":"no price"}],"tax":0.5,"tip":null,"total":8}
            ```
            """);
        Assert.Equal("EUR", parsed.Currency);
        Assert.Equal(2, parsed.Items.Count); // the priceless line can't be split, so it is dropped
        Assert.Equal(0.3, parsed.Items[1].Confidence);
        Assert.Equal(0m, parsed.Tip);
        Assert.Equal(8m, parsed.Total);
    }

    [Theory]
    [InlineData("I can't read this image.")]
    [InlineData("{ not json")]
    [InlineData("{\"currency\":\"USD\"}")]
    public void Claude_replies_that_arent_receipt_data_are_rejected(string reply) =>
        Assert.Throws<ReceiptParseException>(() => ClaudeReceiptParser.ParseJson(reply));
}
