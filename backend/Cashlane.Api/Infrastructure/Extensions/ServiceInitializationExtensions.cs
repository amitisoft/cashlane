using Cashlane.Api.Data;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Infrastructure.Extensions;

public static class ServiceInitializationExtensions
{
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitialization");
        var seeder = scope.ServiceProvider.GetRequiredService<IDemoDataSeeder>();

        try
        {
            var maxAttempts = 10;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await dbContext.Database.MigrateAsync();
                    break;
                }
                catch when (attempt < maxAttempts)
                {
                    logger.LogWarning("Database not ready yet. Retrying initialization attempt {Attempt} of {MaxAttempts}.", attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }

            if (!await dbContext.Database.CanConnectAsync())
            {
                await dbContext.Database.EnsureCreatedAsync();
            }

            await seeder.SeedAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Migration-based initialization failed. Falling back to EnsureCreated.");
            await dbContext.Database.EnsureCreatedAsync();
            await seeder.SeedAsync();
        }
    }
}
