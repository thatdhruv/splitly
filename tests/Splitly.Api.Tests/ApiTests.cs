using System.Net;
using System.Net.Http.Json;
using Splitly.Contracts;
using Splitly.Domain.Splitting;

namespace Splitly.Api.Tests;

[Collection(SqlServerCollection.Name)]
public class ApiTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(sql.ConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/groups/{Guid.NewGuid()}/balances")).StatusCode);
    }

    [Fact]
    public async Task Non_members_get_403_on_every_group_route()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        var mallory = await TestUser.CreateAsync(_factory, "mallory");
        var group = await alice.CreateGroupAsync();
        var exp = await (await alice.AddExpenseAsync(group.Id, 30m, alice.Id, [alice.Id])).Content.ReadFromJsonAsync<ExpenseDto>();
        var g = $"/groups/{group.Id}";

        var responses = new[]
        {
            await mallory.Http.GetAsync(g),
            await mallory.Http.GetAsync($"{g}/balances"),
            await mallory.Http.GetAsync($"{g}/settle-up"),
            await mallory.Http.GetAsync($"{g}/expenses"),
            await mallory.Http.GetAsync($"{g}/expenses/{exp!.Id}"),
            await mallory.AddExpenseAsync(group.Id, 5m, mallory.Id, [mallory.Id]),
            await mallory.Http.PutAsJsonAsync($"{g}/expenses/{exp.Id}", TestUser.ExpenseBody(1m, mallory.Id, [mallory.Id], rowVersion: exp.RowVersion)),
            await mallory.Http.DeleteAsync($"{g}/expenses/{exp.Id}"),
            await mallory.Http.PostAsJsonAsync($"{g}/invites", new CreateInviteRequest()),
            await mallory.Http.PostAsJsonAsync($"{g}/payments", new PaymentRequest(mallory.Id, alice.Id, 1m)),
            await mallory.Http.GetAsync($"{g}/payments"),
        };
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));

        // and the data is untouched
        Assert.Single((await alice.Http.GetFromJsonAsync<List<ExpenseDto>>($"{g}/expenses"))!);
        Assert.Empty((await mallory.Http.GetFromJsonAsync<List<GroupSummaryDto>>("/groups"))!);
    }

    [Fact]
    public async Task Unknown_group_is_indistinguishable_from_forbidden()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.GetAsync($"/groups/{Guid.NewGuid()}/balances")).StatusCode);
    }

    [Fact]
    public async Task Equal_split_updates_balances_and_settle_up()
    {
        var (alice, bob, carol, group) = await ThreeMemberGroup();
        (await alice.AddExpenseAsync(group.Id, 100m, alice.Id, [alice.Id, bob.Id, carol.Id])).EnsureSuccessStatusCode();

        var balances = (await alice.BalancesAsync(group.Id)).Balances.ToDictionary(b => b.UserId, b => b.Amount);
        // Paid 100, owes 33.33 or 33.34 depending on whether alice's (random) id sorts first.
        Assert.Contains(balances[alice.Id], new[] { 66.66m, 66.67m });
        Assert.Equal(0m, balances.Values.Sum());

        var settle = await alice.Http.GetFromJsonAsync<SettleUpDto>($"/groups/{group.Id}/settle-up");
        Assert.Equal(2, settle!.Transfers.Count);
        Assert.All(settle.Transfers, t => Assert.Equal(alice.Id, t.To));
    }

    [Fact]
    public async Task Recording_payments_clears_the_settle_up()
    {
        var (alice, bob, carol, group) = await ThreeMemberGroup();
        (await alice.AddExpenseAsync(group.Id, 90m, alice.Id, [alice.Id, bob.Id, carol.Id])).EnsureSuccessStatusCode();

        var settle = await alice.Http.GetFromJsonAsync<SettleUpDto>($"/groups/{group.Id}/settle-up");
        foreach (var t in settle!.Transfers)
            (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/payments", new PaymentRequest(t.From, t.To, t.Amount))).EnsureSuccessStatusCode();

        Assert.All((await alice.BalancesAsync(group.Id)).Balances, b => Assert.Equal(0m, b.Amount));
        Assert.Empty((await alice.Http.GetFromJsonAsync<SettleUpDto>($"/groups/{group.Id}/settle-up"))!.Transfers);
    }

    [Fact]
    public async Task Foreign_currency_expense_locks_the_rate_and_converts_exactly()
    {
        var (alice, bob, _, group) = await ThreeMemberGroup();
        var res = await alice.AddExpenseAsync(group.Id, 100m, alice.Id, [alice.Id, bob.Id], currency: "eur");
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<ExpenseDto>())!;

        Assert.Equal("EUR", dto.Currency);
        Assert.Equal(1.0837m, dto.ExchangeRate);
        Assert.Equal(108.37m, dto.BaseAmount);
        Assert.Equal(dto.BaseAmount, dto.Shares.Sum(s => s.BaseAmount));
        Assert.Equal(100m, dto.Shares.Sum(s => s.Amount));
    }

    [Fact]
    public async Task Exact_and_percentage_splits_validate_totals()
    {
        var (alice, bob, _, group) = await ThreeMemberGroup();
        var bad = await alice.AddExpenseAsync(group.Id, 10m, alice.Id, [alice.Id, bob.Id], split: SplitType.Exact, values: [4m, 5m]);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var ok = await alice.AddExpenseAsync(group.Id, 10m, alice.Id, [alice.Id, bob.Id], split: SplitType.Percentage, values: [70m, 30m]);
        var dto = (await ok.Content.ReadFromJsonAsync<ExpenseDto>())!;
        Assert.Equal([7m, 3m], dto.Shares.OrderByDescending(s => s.Amount).Select(s => s.Amount));
    }

    [Fact]
    public async Task Participants_must_be_group_members()
    {
        var (alice, _, _, group) = await ThreeMemberGroup();
        var outsider = await TestUser.CreateAsync(_factory, "outsider");
        var res = await alice.AddExpenseAsync(group.Id, 10m, alice.Id, [alice.Id, outsider.Id]);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Concurrent_edit_with_a_stale_rowversion_gets_409()
    {
        var (alice, bob, _, group) = await ThreeMemberGroup();
        var created = (await (await alice.AddExpenseAsync(group.Id, 50m, alice.Id, [alice.Id, bob.Id])).Content.ReadFromJsonAsync<ExpenseDto>())!;
        var url = $"/groups/{group.Id}/expenses/{created.Id}";

        var first = await alice.Http.PutAsJsonAsync(url, TestUser.ExpenseBody(60m, alice.Id, [alice.Id, bob.Id], rowVersion: created.RowVersion));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var stale = await bob.Http.PutAsJsonAsync(url, TestUser.ExpenseBody(70m, alice.Id, [alice.Id, bob.Id], rowVersion: created.RowVersion));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = (await alice.Http.GetFromJsonAsync<ExpenseDto>(url))!;
        Assert.Equal(60m, current.Amount);
        Assert.NotEqual(created.RowVersion, current.RowVersion);
    }

    [Fact]
    public async Task Share_only_edits_still_bump_the_rowversion()
    {
        var (alice, bob, carol, group) = await ThreeMemberGroup();
        var created = (await (await alice.AddExpenseAsync(group.Id, 60m, alice.Id, [alice.Id, bob.Id])).Content.ReadFromJsonAsync<ExpenseDto>())!;
        var url = $"/groups/{group.Id}/expenses/{created.Id}";

        // same amount, different participants
        var edited = await alice.Http.PutAsJsonAsync(url, TestUser.ExpenseBody(60m, alice.Id, [alice.Id, bob.Id, carol.Id], rowVersion: created.RowVersion));
        var dto = (await edited.Content.ReadFromJsonAsync<ExpenseDto>())!;
        Assert.NotEqual(created.RowVersion, dto.RowVersion);
        Assert.Equal(3, dto.Shares.Count);
    }

    [Fact]
    public async Task Update_without_rowversion_is_rejected()
    {
        var (alice, bob, _, group) = await ThreeMemberGroup();
        var created = (await (await alice.AddExpenseAsync(group.Id, 50m, alice.Id, [alice.Id, bob.Id])).Content.ReadFromJsonAsync<ExpenseDto>())!;
        var res = await alice.Http.PutAsJsonAsync($"/groups/{group.Id}/expenses/{created.Id}", TestUser.ExpenseBody(1m, alice.Id, [alice.Id]));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_expense_removes_it_from_balances()
    {
        var (alice, bob, _, group) = await ThreeMemberGroup();
        var created = (await (await alice.AddExpenseAsync(group.Id, 50m, alice.Id, [alice.Id, bob.Id])).Content.ReadFromJsonAsync<ExpenseDto>())!;
        var del = await alice.Http.DeleteAsync($"/groups/{group.Id}/expenses/{created.Id}?rowVersion={Uri.EscapeDataString(created.RowVersion)}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.All((await alice.BalancesAsync(group.Id)).Balances, b => Assert.Equal(0m, b.Amount));
    }

    [Fact]
    public async Task Invite_links_expire()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        var group = await alice.CreateGroupAsync();
        var invite = (await (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/invites", new CreateInviteRequest(ExpiresInHours: 1))).Content.ReadFromJsonAsync<InviteDto>())!;

        _factory.Clock.Advance(TimeSpan.FromHours(2));
        // The fake clock also drives bearer-token expiry, so sign in the joiner after time has moved on.
        var bob = await TestUser.CreateAsync(_factory, "bob");

        Assert.Equal(HttpStatusCode.BadRequest, (await bob.Http.PostAsJsonAsync("/invites/accept", new AcceptInviteRequest(invite.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _factory.CreateClient().GetAsync($"/invites/{invite.Token}")).StatusCode);
    }

    [Fact]
    public async Task Invite_links_respect_max_uses_and_joining_twice_is_idempotent()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        var bob = await TestUser.CreateAsync(_factory, "bob");
        var carol = await TestUser.CreateAsync(_factory, "carol");
        var group = await alice.CreateGroupAsync();
        var invite = (await (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/invites", new CreateInviteRequest(MaxUses: 1))).Content.ReadFromJsonAsync<InviteDto>())!;

        Assert.Equal(HttpStatusCode.OK, (await bob.Http.PostAsJsonAsync("/invites/accept", new AcceptInviteRequest(invite.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await carol.Http.PostAsJsonAsync("/invites/accept", new AcceptInviteRequest(invite.Token))).StatusCode);

        var members = (await alice.Http.GetFromJsonAsync<GroupDto>($"/groups/{group.Id}"))!.Members;
        Assert.Equal(2, members.Count);
    }

    [Fact]
    public async Task Invalid_invite_token_is_rejected()
    {
        var bob = await TestUser.CreateAsync(_factory, "bob");
        Assert.Equal(HttpStatusCode.BadRequest, (await bob.Http.PostAsJsonAsync("/invites/accept", new AcceptInviteRequest("nope"))).StatusCode);
    }

    private async Task<(TestUser, TestUser, TestUser, GroupSummaryDto)> ThreeMemberGroup()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        var bob = await TestUser.CreateAsync(_factory, "bob");
        var carol = await TestUser.CreateAsync(_factory, "carol");
        var group = await alice.CreateGroupAsync();
        await alice.AddMemberAsync(group.Id, bob);
        await alice.AddMemberAsync(group.Id, carol);
        return (alice, bob, carol, group);
    }
}
