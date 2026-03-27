using Cashlane.Api.Infrastructure.Services;

namespace Cashlane.Api.Infrastructure.Background;

public sealed class AccountBalanceSnapshotWorker(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<AccountBalanceSnapshotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var snapshotService = scope.ServiceProvider.GetRequiredService<IAccountBalanceSnapshotService>();
                await snapshotService.EnsureDailySnapshotsAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Account balance snapshot worker failed.");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
