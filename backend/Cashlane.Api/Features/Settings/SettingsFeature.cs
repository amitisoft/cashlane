using System.Net;
using Cashlane.Api.Data;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Infrastructure.Authentication;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Settings;

public sealed record SettingsProfileDto(Guid Id, string Email, string DisplayName);
public sealed record UpdateProfileRequest(string DisplayName);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record SessionDto(Guid Id, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime? RevokedAtUtc, string? DeviceLabel);

public interface ISettingsService
{
    Task<SettingsProfileDto> GetProfileAsync(CancellationToken cancellationToken = default);
    Task<SettingsProfileDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default);
    Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IPasswordHasher passwordHasher,
    IAuditLogService auditLogService) : UserScopedService(currentUserService), ISettingsService
{
    public async Task<SettingsProfileDto> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        return await dbContext.Users
            .Where(x => x.Id == userId)
            .Select(x => new SettingsProfileDto(x.Id, x.Email, x.DisplayName))
            .FirstAsync(cancellationToken);
    }

    public async Task<SettingsProfileDto> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var user = await dbContext.Users.FirstAsync(x => x.Id == userId, cancellationToken);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid profile", "Display name is required.");
        }

        user.DisplayName = request.DisplayName.Trim();
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("settings.profile-updated", "User", user.Id, new { user.DisplayName }, cancellationToken);

        return new SettingsProfileDto(user.Id, user.Email, user.DisplayName);
    }

    public async Task ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var user = await dbContext.Users.Include(x => x.RefreshTokens).FirstAsync(x => x.Id == userId, cancellationToken);

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid password", "Current password is incorrect.");
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Weak password", "New password must be at least 8 characters.");
        }

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        foreach (var refreshToken in user.RefreshTokens.Where(x => x.RevokedAtUtc is null))
        {
            refreshToken.RevokedAtUtc = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("settings.password-changed", "User", user.Id, new { user.Email }, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        return await dbContext.RefreshTokens
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new SessionDto(x.Id, x.CreatedAtUtc, x.ExpiresAtUtc, x.RevokedAtUtc, x.DeviceLabel))
            .ToListAsync(cancellationToken);
    }
}

[ApiController]
[Authorize]
[Route("api/settings")]
public sealed class SettingsController(ISettingsService settingsService) : ControllerBase
{
    [HttpGet("profile")]
    public Task<SettingsProfileDto> GetProfile(CancellationToken cancellationToken)
        => settingsService.GetProfileAsync(cancellationToken);

    [HttpPut("profile")]
    public Task<SettingsProfileDto> UpdateProfile([FromBody] UpdateProfileRequest request, CancellationToken cancellationToken)
        => settingsService.UpdateProfileAsync(request, cancellationToken);

    [HttpPost("change-password")]
    public async Task<ActionResult<SimpleMessageResponse>> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await settingsService.ChangePasswordAsync(request, cancellationToken);
        return Ok(new SimpleMessageResponse("Password changed."));
    }

    [HttpGet("sessions")]
    public Task<IReadOnlyList<SessionDto>> GetSessions(CancellationToken cancellationToken)
        => settingsService.GetSessionsAsync(cancellationToken);
}
