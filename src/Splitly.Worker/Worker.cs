using Splitly.Receipts;

namespace Splitly.Worker;

/// <summary>Polls for uploaded receipts and parses them. Sleeps briefly when idle and backs off after infrastructure errors.</summary>
public sealed class ReceiptWorker(IServiceScopeFactory scopes, ILogger<ReceiptWorker> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Receipt worker started");
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                worked = await scope.ServiceProvider.GetRequiredService<ReceiptProcessor>().ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Receipt worker iteration failed; backing off");
                await Delay(TimeSpan.FromSeconds(10), stoppingToken);
                continue;
            }
            if (!worked) await Delay(Idle, stoppingToken);
        }
    }

    private static async Task Delay(TimeSpan span, CancellationToken ct)
    {
        try { await Task.Delay(span, ct); } catch (OperationCanceledException) { }
    }
}
