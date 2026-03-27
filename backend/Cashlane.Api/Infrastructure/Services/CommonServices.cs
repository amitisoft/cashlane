using System.Net;
using System.Text.Json;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;

namespace Cashlane.Api.Infrastructure.Services;

public interface IAuditLogService
{
    Task WriteAsync(string action, string entityName, Guid? entityId, object metadata, CancellationToken cancellationToken = default, Guid? accountId = null);
}

public interface ITelemetryService
{
    Task TrackAsync(string eventName, Guid? userId = null, object? payload = null, CancellationToken cancellationToken = default);
}

public interface IDemoDataSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

public abstract class UserScopedService(ICurrentUserService currentUserService)
{
    protected Guid GetRequiredUserId()
    {
        return currentUserService.UserId ?? throw new AppException(HttpStatusCode.Unauthorized, "Unauthorized", "A valid user session is required.");
    }
}

public sealed class AuditLogService(AppDbContext dbContext, ICurrentUserService currentUserService) : IAuditLogService
{
    public async Task WriteAsync(string action, string entityName, Guid? entityId, object metadata, CancellationToken cancellationToken = default, Guid? accountId = null)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            UserId = currentUserService.UserId,
            AccountId = accountId,
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            MetadataJson = JsonSerializer.Serialize(metadata)
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public sealed class TelemetryService(ILogger<TelemetryService> logger) : ITelemetryService
{
    public Task TrackAsync(string eventName, Guid? userId = null, object? payload = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Telemetry event {EventName} for user {UserId} with payload {@Payload}", eventName, userId, payload);
        return Task.CompletedTask;
    }
}
