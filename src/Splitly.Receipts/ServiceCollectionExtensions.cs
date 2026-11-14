using Azure.Storage.Blobs;
using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Splitly.Receipts;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Blob storage comes from <c>ConnectionStrings:receipts</c> (Azure Blob Storage, or Azurite under Aspire), wired through the
    /// Aspire client so it also gets health checks and tracing. Without it, files go to <c>Receipts:LocalPath</c>.
    /// The API only needs storage; the worker also needs a parser.
    /// </summary>
    public static IHostApplicationBuilder AddReceiptStorage(this IHostApplicationBuilder builder)
    {
        if (!string.IsNullOrEmpty(builder.Configuration.GetConnectionString("receipts")))
        {
            builder.AddAzureBlobServiceClient("receipts");
            builder.Services.AddSingleton<IReceiptStorage>(sp => new AzureBlobReceiptStorage(sp.GetRequiredService<BlobServiceClient>()));
        }
        else
        {
            var root = builder.Configuration["Receipts:LocalPath"] ?? Path.Combine(Path.GetTempPath(), "splitly-receipts");
            builder.Services.AddSingleton<IReceiptStorage>(new LocalFileReceiptStorage(root));
        }
        return builder;
    }

    /// <summary><c>Receipts:Parser</c> = "claude" (default, needs ANTHROPIC_API_KEY) or "fake" (explicit opt-in for dev/tests).</summary>
    public static IHostApplicationBuilder AddReceiptProcessing(this IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var services = builder.Services;
        builder.AddReceiptStorage();
        services.AddSingleton(config.GetSection("Receipts:Processing").Get<ReceiptProcessingOptions>() ?? new());
        services.AddSingleton(TimeProvider.System);

        var parser = config["Receipts:Parser"] ?? "claude";
        switch (parser.ToLowerInvariant())
        {
            case "fake":
                services.AddSingleton<IReceiptParser>(new FakeReceiptParser());
                break;
            case "claude":
                if (string.IsNullOrEmpty(config["ANTHROPIC_API_KEY"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
                    throw new InvalidOperationException(
                        "Receipts:Parser is 'claude' but ANTHROPIC_API_KEY is not set. Set the key, or set Receipts:Parser=fake for local development.");
                services.AddSingleton<IReceiptParser>(_ => new ClaudeReceiptParser(new AnthropicClient()));
                break;
            default:
                throw new InvalidOperationException($"Unknown Receipts:Parser '{parser}'. Use 'claude' or 'fake'.");
        }

        services.AddScoped<ReceiptProcessor>();
        return builder;
    }
}
