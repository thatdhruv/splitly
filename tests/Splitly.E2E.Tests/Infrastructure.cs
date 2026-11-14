using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Testcontainers.MsSql;

namespace Splitly.E2E.Tests;

/// <summary>
/// Boots the whole stack for browser tests: SQL Server (Testcontainers), the real API process and the real
/// Blazor process, plus a headless Chromium. Requires the solution to be built first (`dotnet build`).
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly List<Process> _procs = [];
    private string _blobDir = "";

    public string WebUrl { get; private set; } = "";
    public IPlaywright Playwright { get; private set; } = default!;
    public IBrowser Browser { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _sql.StartAsync();
        var root = FindRepoRoot();
        var apiPort = FreePort();
        var webPort = FreePort();
        var apiUrl = $"http://127.0.0.1:{apiPort}";
        WebUrl = $"http://localhost:{webPort}"; // WebAuthn needs a real host name or localhost, not an IP

        // The API (stores uploads) and the worker (reads them) must share a blob location; "fake" parser = no API key needed.
        _blobDir = Path.Combine(Path.GetTempPath(), "splitly-e2e-" + Guid.NewGuid().ToString("N"));
        Start(root, "src/Splitly.Api", apiUrl, new()
        {
            ["ConnectionStrings__splitly"] = _sql.GetConnectionString(),
            ["Database__MigrateOnStartup"] = "true",
            ["Receipts__LocalPath"] = _blobDir,
            ["Passkeys__RelyingPartyId"] = "localhost",
            ["Passkeys__AllowedOrigins__0"] = WebUrl,
        });
        Start(root, "src/Splitly.Web", WebUrl, new() { ["Api__BaseUrl"] = apiUrl });
        await WaitForAsync($"{apiUrl}/health"); // the API applies migrations on start, so the worker waits for it
        Start(root, "src/Splitly.Worker", null, new()
        {
            ["ConnectionStrings__splitly"] = _sql.GetConnectionString(),
            ["Receipts__LocalPath"] = _blobDir,
            ["Receipts__Parser"] = "fake",
            ["Recurring__IntervalSeconds"] = "2",
        });
        await WaitForAsync($"{WebUrl}/health");

        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        Playwright?.Dispose();
        foreach (var p in _procs) { try { p.Kill(entireProcessTree: true); } catch { } }
        await _sql.DisposeAsync();
    }

    private void Start(string root, string project, string? url, Dictionary<string, string> env)
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        var psi = new ProcessStartInfo("dotnet", $"run --no-build -c {config} --project {project}" + (url is null ? "" : $" --urls {url}"))
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        foreach (var (k, v) in env) psi.Environment[k] = v;
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { }; p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine(); p.BeginErrorReadLine(); // drain so the child never blocks on a full pipe
        _procs.Add(p);
    }

    private static async Task WaitForAsync(string url)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            try { if ((await http.GetAsync(url)).IsSuccessStatusCode) return; } catch { }
            await Task.Delay(500);
        }
        throw new TimeoutException($"{url} did not become healthy in time.");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Splitly.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate Splitly.slnx.");
    }
}

[CollectionDefinition(Name)]
public sealed class StackCollection : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}
