using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Splitly.Contracts;

namespace Splitly.AppHost.Tests;

/// <summary>
/// Boots the real Aspire app model: SQL Server container, Azurite, API, worker and web. Proves the wiring (connection strings,
/// migrations, blob storage, worker, service URLs) works together, which the other test projects deliberately don't cover.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        // Headless runs: don't try to trust the dev certificate (it can block on an OS prompt), and allow plain-HTTP endpoints.
        Environment.SetEnvironmentVariable("ASPIRE_DEVELOPER_CERTIFICATE_DEFAULT_TRUST", "false");
        Environment.SetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT", "true");

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Splitly_AppHost>(
            ["--Splitly:PersistData=false"]);
        App = await builder.BuildAsync();
        await App.StartAsync();

        var timeout = TimeSpan.FromMinutes(3);
        try
        {
            await App.ResourceNotifications.WaitForResourceHealthyAsync("api").WaitAsync(timeout);
            await App.ResourceNotifications.WaitForResourceHealthyAsync("web").WaitAsync(timeout);
            await App.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Running).WaitAsync(timeout);
        }
        catch (Exception ex)
        {
            // A hang here is otherwise silent: surface each resource's console output in the failure.
            throw new InvalidOperationException("The app model didn't become healthy." + Environment.NewLine + await DumpLogsAsync(), ex);
        }
    }

    private async Task<string> DumpLogsAsync()
    {
        var logs = App.Services.GetRequiredService<ResourceLoggerService>();
        var sb = new System.Text.StringBuilder();
        foreach (var name in new[] { "sql", "storage", "api", "worker", "web" })
        {
            sb.AppendLine($"--- {name} ---");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await foreach (var batch in logs.WatchAsync(name).WithCancellation(cts.Token))
                    foreach (var line in batch.TakeLast(40)) sb.AppendLine(line.Content);
            }
            catch (OperationCanceledException) { }
        }
        return sb.ToString();
    }

    public async Task DisposeAsync() => await App.DisposeAsync();
}

public class AppHostTests(AppHostFixture fixture) : IClassFixture<AppHostFixture>
{
    private const string Password = "Sup3r-secret-pw!";

    [Fact]
    public async Task Web_app_is_up_and_serves_the_sign_in_page()
    {
        using var web = fixture.App.CreateHttpClient("web");
        var res = await web.GetAsync("/login");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Splitly", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Receipt_goes_through_azurite_and_the_worker_end_to_end()
    {
        using var api = fixture.App.CreateHttpClient("api");
        var email = $"it-{Guid.NewGuid():N}@example.com";
        (await api.PostAsJsonAsync("/signup", new SignUpRequest(email, Password, "Integration"))).EnsureSuccessStatusCode();
        var login = await api.PostAsJsonAsync("/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<Login>())!.AccessToken;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var group = await (await api.PostAsJsonAsync("/groups", new CreateGroupRequest("Aspire", "USD"))).Content.ReadFromJsonAsync<GroupSummaryDto>();

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        using var form = new MultipartFormDataContent { { new ByteArrayContent(png) { Headers = { ContentType = new("image/png") } }, "file", "r.png" } };
        var upload = await api.PostAsync($"/groups/{group!.Id}/receipts", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var receipt = (await upload.Content.ReadFromJsonAsync<ReceiptDto>())!;

        // The worker (a separate process) reads the image from Azurite and parses it (fake parser: no API key in tests).
        var deadline = DateTime.UtcNow.AddSeconds(60);
        ReceiptDto current;
        do
        {
            await Task.Delay(1000);
            current = (await api.GetFromJsonAsync<ReceiptDto>($"/groups/{group.Id}/receipts/{receipt.Id}"))!;
        }
        while (current.Status is "Uploaded" or "Processing" && DateTime.UtcNow < deadline);

        Assert.Equal("NeedsReview", current.Status);
        Assert.Equal(3, current.Items.Count);

        // The stored blob round-trips through the API.
        var image = await api.GetAsync($"/groups/{group.Id}/receipts/{receipt.Id}/image");
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
    }

    private sealed record Login(string AccessToken);
}
