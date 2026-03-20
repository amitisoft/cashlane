using Cashlane.Api.Features.Recurring;

namespace Cashlane.Api.Infrastructure.Background;

public sealed class RecurringTransactionWorker(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<RecurringTransactionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var recurringService = scope.ServiceProvider.GetRequiredService<IRecurringService>();
                await recurringService.ProcessDueRecurringTransactionsAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Recurring transaction worker failed.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
