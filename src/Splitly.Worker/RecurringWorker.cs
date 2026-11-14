using Splitly.Application;

namespace Splitly.Worker;

/// <summary>Once a minute, creates the expenses for any recurring schedule that has come due (including missed days).</summary>
public sealed class RecurringWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<RecurringWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Recurring:IntervalSeconds", 60))));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RecurringProcessor>().ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Recurring expense pass failed; will retry on the next pass"); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
