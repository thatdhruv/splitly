using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Splitly.Application;
using Splitly.Contracts;
using Splitly.Domain.Splitting;

namespace Splitly.Api.Tests;

[Collection(SqlServerCollection.Name)]
public class RecurringTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly ApiFactory _factory = new(sql.ConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private DateOnly Today => DateOnly.FromDateTime(_factory.Clock.GetUtcNow().UtcDateTime);

    private async Task<(TestUser alice, TestUser bob, GroupSummaryDto group)> Setup()
    {
        var alice = await TestUser.CreateAsync(_factory, "alice");
        var bob = await TestUser.CreateAsync(_factory, "bob");
        var group = await alice.CreateGroupAsync();
        await alice.AddMemberAsync(group.Id, bob);
        return (alice, bob, group);
    }

    private async Task<int> RunProcessorAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RecurringProcessor>().ProcessDueAsync(default);
    }

    private static RecurringRequest Rent(Guid payer, Guid other, DateOnly start, Recurrence r = Recurrence.Monthly,
        DateOnly? end = null, string currency = "USD") =>
        new("Rent", 1000m, currency, payer, SplitType.Equal, [new(payer), new(other)], r, start, end);

    private static async Task<RecurringDto> Create(TestUser who, Guid groupId, RecurringRequest req)
    {
        var res = await who.Http.PostAsJsonAsync($"/groups/{groupId}/recurring", req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<RecurringDto>())!;
    }

    private static async Task<List<ExpenseDto>> Expenses(TestUser who, Guid groupId) =>
        (await who.Http.GetFromJsonAsync<List<ExpenseDto>>($"/groups/{groupId}/expenses?take=200"))!;

    [Fact]
    public async Task Missed_occurrences_are_caught_up_and_dated_when_they_were_due()
    {
        var (alice, bob, group) = await Setup();
        var start = Today.AddMonths(-3);
        var rec = await Create(alice, group.Id, Rent(alice.Id, bob.Id, start));

        Assert.Equal(4, await RunProcessorAsync()); // -3, -2, -1 months and today

        var expenses = await Expenses(alice, group.Id);
        Assert.Equal(
            Enumerable.Range(0, 4).Select(n => Recurrences.OccurrenceDate(start, Recurrence.Monthly, n)).Order(),
            expenses.Select(e => e.Date).Order());
        Assert.All(expenses, e => Assert.Equal(1000m, e.Amount));

        var after = (await alice.Http.GetFromJsonAsync<List<RecurringDto>>($"/groups/{group.Id}/recurring"))!.Single(r => r.Id == rec.Id);
        Assert.True(after.IsActive);
        Assert.Equal(Recurrences.OccurrenceDate(start, Recurrence.Monthly, 4), after.NextRunDate);

        Assert.Equal(0m, (await alice.BalancesAsync(group.Id)).Balances.Sum(b => b.Amount));
    }

    [Fact]
    public async Task Running_again_creates_nothing_new()
    {
        var (alice, bob, group) = await Setup();
        await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today.AddDays(-14), Recurrence.Weekly));

        Assert.Equal(3, await RunProcessorAsync());
        Assert.Equal(0, await RunProcessorAsync());
        Assert.Equal(3, (await Expenses(alice, group.Id)).Count);
    }

    [Fact]
    public async Task Racing_workers_never_create_an_occurrence_twice()
    {
        var (alice, bob, group) = await Setup();
        await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today.AddDays(-20), Recurrence.Daily));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunProcessorAsync()));
        await RunProcessorAsync(); // settle anything a loser skipped

        var expenses = await Expenses(alice, group.Id);
        Assert.Equal(21, expenses.Count);                         // -20 .. today
        Assert.Equal(21, expenses.Select(e => e.Date).Distinct().Count());
    }

    [Fact]
    public async Task End_date_stops_the_schedule()
    {
        var (alice, bob, group) = await Setup();
        var rec = await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today.AddDays(-21), Recurrence.Weekly, end: Today.AddDays(-8)));

        Assert.Equal(2, await RunProcessorAsync()); // -21 and -14; -7 is after the end date
        Assert.False((await alice.Http.GetFromJsonAsync<List<RecurringDto>>($"/groups/{group.Id}/recurring"))!.Single(r => r.Id == rec.Id).IsActive);
        Assert.Equal(0, await RunProcessorAsync());
    }

    [Fact]
    public async Task Future_schedules_wait_and_stopped_schedules_do_nothing()
    {
        var (alice, bob, group) = await Setup();
        await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today.AddDays(10)));
        Assert.Equal(0, await RunProcessorAsync());

        var stopped = await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today.AddDays(-1), Recurrence.Daily));
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.DeleteAsync($"/groups/{group.Id}/recurring/{stopped.Id}")).StatusCode);
        Assert.Equal(0, await RunProcessorAsync());
        Assert.Empty(await Expenses(alice, group.Id));
    }

    [Fact]
    public async Task Foreign_currency_occurrences_lock_the_rate_like_any_expense()
    {
        var (alice, bob, group) = await Setup();
        await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today, currency: "EUR"));
        Assert.Equal(1, await RunProcessorAsync());

        var e = (await Expenses(alice, group.Id)).Single();
        Assert.Equal("EUR", e.Currency);
        Assert.Equal(1083.70m, e.BaseAmount);
        Assert.Equal(e.BaseAmount, e.Shares.Sum(s => s.BaseAmount));
    }

    [Fact]
    public async Task Invalid_templates_are_rejected_when_created()
    {
        var (alice, bob, group) = await Setup();
        var badSplit = new RecurringRequest("Gym", 50m, "USD", alice.Id, SplitType.Percentage, [new(alice.Id, 60m), new(bob.Id, 30m)],
            Recurrence.Monthly, Today);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/recurring", badSplit)).StatusCode);

        var backwards = Rent(alice.Id, bob.Id, Today, end: Today.AddDays(-1));
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/recurring", backwards)).StatusCode);

        var outsider = await TestUser.CreateAsync(_factory, "outsider");
        var withOutsider = new RecurringRequest("Gym", 50m, "USD", alice.Id, SplitType.Equal, [new(alice.Id), new(outsider.Id)], Recurrence.Monthly, Today);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PostAsJsonAsync($"/groups/{group.Id}/recurring", withOutsider)).StatusCode);
    }

    [Fact]
    public async Task Non_members_get_403_on_recurring_routes()
    {
        var (alice, bob, group) = await Setup();
        var rec = await Create(alice, group.Id, Rent(alice.Id, bob.Id, Today));
        var mallory = await TestUser.CreateAsync(_factory, "mallory");
        var g = $"/groups/{group.Id}/recurring";

        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.Http.GetAsync(g)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.Http.PostAsJsonAsync(g, Rent(mallory.Id, mallory.Id, Today))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mallory.Http.DeleteAsync($"{g}/{rec.Id}")).StatusCode);
        Assert.True((await alice.Http.GetFromJsonAsync<List<RecurringDto>>(g))!.Single().IsActive);
    }
}
