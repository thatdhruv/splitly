using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Splitly.Api.Services;
using Splitly.Receipts;
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

/// <summary>Scriptable parser: tests set <see cref="Result"/> or <see cref="Throw"/> and count calls.</summary>
public sealed class TestParser : IReceiptParser
{
    public ParsedReceipt Result { get; set; } = FakeReceiptParser.Sample;
    public Func<Exception>? Throw { get; set; }
    private int _calls;
    public int Calls => _calls;

    public async Task<ParsedReceipt> ParseAsync(Stream image, string contentType, string? currencyHint, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        await Task.Delay(50, ct); // widen the race window for the concurrency test
        if (Throw is { } t) throw t();
        return Result;
    }
}

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public TestParser Parser { get; } = new();
    private readonly string _blobDir = Path.Combine(Path.GetTempPath(), "splitly-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>Runs the receipt worker's unit of work in-process until the queue is empty.</summary>
    public async Task<int> ProcessReceiptsAsync()
    {
        var n = 0;
        while (true)
        {
            await using var scope = Services.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<ReceiptProcessor>().ProcessNextAsync(default)) return n;
            n++;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:splitly", connectionString);
        builder.UseSetting("Receipts:LocalPath", _blobDir);
        builder.ConfigureServices(s =>
        {
            s.AddSingleton<IReceiptParser>(Parser);
            s.AddSingleton(new ReceiptProcessingOptions());
            s.AddScoped<ReceiptProcessor>();
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

    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public Task<HttpResponseMessage> UploadReceiptAsync(Guid groupId, byte[]? bytes = null, string fileName = "r.png", string mediaType = "image/png")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes ?? TinyPng);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Add(file, "file", fileName);
        return Http.PostAsync($"/groups/{groupId}/receipts", content);
    }

    public async Task<ReceiptDto> GetReceiptAsync(Guid groupId, Guid receiptId) =>
        (await Http.GetFromJsonAsync<ReceiptDto>($"/groups/{groupId}/receipts/{receiptId}"))!;

    public Task<HttpResponseMessage> ClaimAsync(Guid groupId, Guid receiptId, Guid itemId, bool claimed = true) =>
        Http.PutAsJsonAsync($"/groups/{groupId}/receipts/{receiptId}/items/{itemId}/claim", new ClaimRequest(claimed));
}
