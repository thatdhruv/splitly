using Anthropic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Splitly.Receipts;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Blob storage comes from <c>ConnectionStrings:receipts</c> (Azure Blob Storage/Azurite); without it, files go to
    /// <c>Receipts:LocalPath</c>. The API only needs storage; the worker also needs a parser.
    /// </summary>
    public static IServiceCollection AddReceiptStorage(this IServiceCollection services, IConfiguration config)
    {
        var blobs = config.GetConnectionString("receipts");
        if (!string.IsNullOrEmpty(blobs))
            return services.AddSingleton<IReceiptStorage>(new AzureBlobReceiptStorage(blobs));

        var root = config["Receipts:LocalPath"] ?? Path.Combine(Path.GetTempPath(), "splitly-receipts");
        return services.AddSingleton<IReceiptStorage>(new LocalFileReceiptStorage(root));
    }

    /// <summary><c>Receipts:Parser</c> = "claude" (default, needs ANTHROPIC_API_KEY) or "fake" (explicit opt-in for dev/tests).</summary>
    public static IServiceCollection AddReceiptProcessing(this IServiceCollection services, IConfiguration config)
    {
        services.AddReceiptStorage(config);
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

        return services.AddScoped<ReceiptProcessor>();
    }
}
