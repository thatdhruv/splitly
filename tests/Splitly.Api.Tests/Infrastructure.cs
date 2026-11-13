using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Splitly.Api.Services;
using Splitly.Contracts;
using Splitly.Domain.Splitting;
using Testcontainers.MsSql;

namespace Splitly.Api.Tests;

/// <summary>One real SQL Server container shared by every integration test in the run.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    public string ConnectionString => _sql.GetConnectionString();

    public Task InitializeAsync() => _sql.StartAsync();
    public Task DisposeAsync() => _sql.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

public sealed class FakeRates : IExchangeRateProvider
{
    public Dictionary<(string From, string To), decimal> Table { get; } = new()
    {
        [("EUR", "USD")] = 1.0837m,
        [("USD", "EUR")] = 0.9227m,
        [("JPY", "USD")] = 0.0067m,
    };

    public Task<decimal> GetRateAsync(string from, string to, DateOnly date, CancellationToken ct) =>
        Task.FromResult(from == to ? 1m : Table[(from, to)]);
}

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:splitly", connectionString);
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IExchangeRateProvider>();
            s.AddSingleton<IExchangeRateProvider, FakeRates>();
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>A signed-in test user with a typed wrapper over the HTTP API.</summary>
public sealed class TestUser
{
    public required HttpClient Http { get; init; }
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    public static async Task<TestUser> CreateAsync(ApiFactory factory, string name)
    {
        var http = factory.CreateClient();
        var email = $"{name}-{Guid.NewGuid():N}@example.com";
        const string password = "Sup3r-secret-pw!";

        (await http.PostAsJsonAsync("/signup", new SignUpRequest(email, password, name))).EnsureSuccessStatusCode();
        var login = await http.PostAsJsonAsync("/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var me = await http.GetFromJsonAsync<ProfileDto>("/me");
        return new TestUser { Http = http, Id = me!.Id, Name = name };
    }

    private sealed record LoginResponse(string AccessToken);

    public async Task<GroupSummaryDto> CreateGroupAsync(string currency = "USD")
    {
        var r = await Http.PostAsJsonAsync("/groups", new CreateGroupRequest($"Trip {Guid.NewGuid():N}", currency));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<GroupSummaryDto>())!;
    }

    /// <summary>Invites <paramref name="other"/> into the group via a real invite link.</summary>
    public async Task AddMemberAsync(Guid groupId, TestUser other)
    {
        var invite = await (await Http.PostAsJsonAsync($"/groups/{groupId}/invites", new CreateInviteRequest())).Content.ReadFromJsonAsync<InviteDto>();
        (await other.Http.PostAsJsonAsync("/invites/accept", new AcceptInviteRequest(invite!.Token))).EnsureSuccessStatusCode();
    }

    public Task<HttpResponseMessage> AddExpenseAsync(Guid groupId, decimal amount, Guid paidBy, IEnumerable<Guid> participants,
        string currency = "USD", SplitType split = SplitType.Equal, IEnumerable<decimal?>? values = null) =>
        Http.PostAsJsonAsync($"/groups/{groupId}/expenses", ExpenseBody(amount, paidBy, participants, currency, split, values));

    public static ExpenseRequest ExpenseBody(decimal amount, Guid paidBy, IEnumerable<Guid> participants,
        string currency = "USD", SplitType split = SplitType.Equal, IEnumerable<decimal?>? values = null, string? rowVersion = null)
    {
        var vs = values?.ToArray();
        var parts = participants.Select((id, i) => new ParticipantInput(id, vs?[i])).ToList();
        return new ExpenseRequest("Dinner", amount, currency, new DateOnly(2026, 3, 14), paidBy, split, parts, RowVersion: rowVersion);
    }

    public async Task<BalancesDto> BalancesAsync(Guid groupId) =>
        (await Http.GetFromJsonAsync<BalancesDto>($"/groups/{groupId}/balances"))!;
}
