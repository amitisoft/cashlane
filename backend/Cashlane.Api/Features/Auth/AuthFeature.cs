using System.Net;
using System.Text.RegularExpressions;
using Cashlane.Api.Configuration;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Authentication;
using Cashlane.Api.Infrastructure.Email;
using Cashlane.Api.Infrastructure.Middleware;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cashlane.Api.Features.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Token, string Password);
public sealed record VerifyRegistrationRequest(string Token);
public sealed record LogoutRequest(string RefreshToken);

public sealed record AuthUserDto(Guid Id, string Email, string DisplayName, bool NeedsOnboarding);
public sealed record AuthResponse(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, AuthUserDto User);
public sealed record SimpleMessageResponse(string Message);

public interface IAuthService
{
    Task RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> VerifyRegistrationAsync(VerifyRegistrationRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);
    Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);
    Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
}

public sealed partial class AuthService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IRegistrationVerificationTokenService registrationVerificationTokenService,
    IEmailService emailService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService,
    IOptions<AppUrlOptions> appUrlOptions,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly AppUrlOptions _appUrls = appUrlOptions.Value;

    public async Task RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        request = NormalizeRegistrationRequest(request);

        if (await dbContext.Users.AnyAsync(x => x.Email == request.Email, cancellationToken))
        {
            throw new AppException(HttpStatusCode.Conflict, "Email already used", "An account with this email already exists.");
        }

        var verificationToken = registrationVerificationTokenService.CreateToken(
            new PendingRegistration(request.Email, request.DisplayName, passwordHasher.Hash(request.Password)));
        var verificationUrl =
            $"{_appUrls.FrontendBaseUrl.TrimEnd('/')}?registrationToken={Uri.EscapeDataString(verificationToken)}&email={Uri.EscapeDataString(request.Email)}";

        try
        {
            await emailService.SendRegistrationVerificationAsync(request.Email, request.DisplayName, verificationUrl, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to send registration verification email. Verification URL for {Email}: {VerificationUrl}",
                request.Email,
                verificationUrl);
        }
    }

    public async Task<AuthResponse> VerifyRegistrationAsync(VerifyRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        var pendingRegistration = registrationVerificationTokenService.ReadToken(request.Token)
            ?? throw new AppException(HttpStatusCode.BadRequest, "Invalid token", "Verification link is invalid or expired.");

        if (await dbContext.Users.AnyAsync(x => x.Email == pendingRegistration.Email, cancellationToken))
        {
            throw new AppException(HttpStatusCode.Conflict, "Email already used", "An account with this email already exists.");
        }

        var user = new User
        {
            Email = pendingRegistration.Email,
            DisplayName = pendingRegistration.DisplayName,
            PasswordHash = pendingRegistration.PasswordHash
        };

        dbContext.Users.Add(user);
        dbContext.Categories.AddRange(DefaultCategoryFactory.Create(user.Id));
        await dbContext.SaveChangesAsync(cancellationToken);

        await telemetryService.TrackAsync("signup_completed", user.Id, new { user.Email }, cancellationToken);
        await auditLogService.WriteAsync("user.registered", nameof(User), user.Id, new { user.Email }, cancellationToken);

        return await CreateAuthResponseAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken)
            ?? throw new AppException(HttpStatusCode.Unauthorized, "Login failed", "Invalid email or password.");

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            throw new AppException(HttpStatusCode.Unauthorized, "Login failed", "Invalid email or password.");
        }

        return await CreateAuthResponseAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        var tokenHash = tokenService.HashOpaqueToken(request.RefreshToken);
        var refreshToken = await dbContext.RefreshTokens
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            ?? throw new AppException(HttpStatusCode.Unauthorized, "Refresh failed", "Refresh token is invalid.");

        if (!refreshToken.IsActive)
        {
            throw new AppException(HttpStatusCode.Unauthorized, "Refresh failed", "Refresh token is expired or revoked.");
        }

        refreshToken.RevokedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        return await CreateAuthResponseAsync(refreshToken.User, cancellationToken);
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
    {
        var tokenHash = tokenService.HashOpaqueToken(request.RefreshToken);
        var refreshToken = await dbContext.RefreshTokens.FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);
        if (refreshToken is null)
        {
            return;
        }

        refreshToken.RevokedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (user is null)
        {
            return;
        }

        var rawToken = tokenService.CreateRandomToken();
        dbContext.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = tokenService.HashOpaqueToken(rawToken),
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var resetUrl =
            $"{_appUrls.FrontendBaseUrl.TrimEnd('/')}{_appUrls.ResetPasswordPath}?token={Uri.EscapeDataString(rawToken)}&email={Uri.EscapeDataString(user.Email)}";

        try
        {
            await emailService.SendPasswordResetAsync(user.Email, user.DisplayName, resetUrl, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to send password reset email. Reset URL for {Email}: {ResetUrl}", email, resetUrl);
        }
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        ValidatePassword(request.Password);

        var tokenHash = tokenService.HashOpaqueToken(request.Token);
        var resetToken = await dbContext.PasswordResetTokens
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            ?? throw new AppException(HttpStatusCode.BadRequest, "Invalid token", "Reset token is invalid.");

        if (!resetToken.IsActive)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid token", "Reset token is expired or already used.");
        }

        resetToken.UsedAtUtc = DateTime.UtcNow;
        resetToken.User.PasswordHash = passwordHasher.Hash(request.Password);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("user.password-reset", nameof(User), resetToken.UserId, new { resetToken.User.Email }, cancellationToken);
    }

    private async Task<AuthResponse> CreateAuthResponseAsync(User user, CancellationToken cancellationToken)
    {
        var accessToken = tokenService.CreateAccessToken(user);
        var refreshToken = tokenService.CreateRefreshToken();

        dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshToken.tokenHash,
            ExpiresAtUtc = refreshToken.expiresAtUtc
        });
        await dbContext.SaveChangesAsync(cancellationToken);

        var needsOnboarding = !await dbContext.Accounts.AnyAsync(x => x.UserId == user.Id, cancellationToken);

        return new AuthResponse(
            accessToken,
            refreshToken.rawToken,
            DateTime.UtcNow.AddMinutes(30),
            new AuthUserDto(user.Id, user.Email, user.DisplayName, needsOnboarding));
    }

    private static RegisterRequest NormalizeRegistrationRequest(RegisterRequest request)
    {
        request = request with
        {
            Email = request.Email.Trim().ToLowerInvariant(),
            DisplayName = request.DisplayName.Trim()
        };

        ValidatePassword(request.Password);

        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid display name", "Display name is required.");
        }

        return request;
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length < 8)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Weak password", "Password must be at least 8 characters.");
        }

        if (!PasswordRuleRegex().IsMatch(password))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Weak password", "Password must include uppercase, lowercase, and a number.");
        }
    }

    [GeneratedRegex(@"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d).+$")]
    private static partial Regex PasswordRuleRegex();
}

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [EnableRateLimiting("auth-login")]
    [HttpPost("register")]
    public async Task<ActionResult<SimpleMessageResponse>> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        await authService.RegisterAsync(request, cancellationToken);
        return Ok(new SimpleMessageResponse("Check your email to verify your account."));
    }

    [EnableRateLimiting("auth-login")]
    [HttpPost("verify-registration")]
    public Task<AuthResponse> VerifyRegistration([FromBody] VerifyRegistrationRequest request, CancellationToken cancellationToken)
        => authService.VerifyRegistrationAsync(request, cancellationToken);

    [EnableRateLimiting("auth-login")]
    [HttpPost("login")]
    public Task<AuthResponse> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
        => authService.LoginAsync(request, cancellationToken);

    [HttpPost("logout")]
    public async Task<ActionResult<SimpleMessageResponse>> Logout([FromBody] LogoutRequest request, CancellationToken cancellationToken)
    {
        await authService.LogoutAsync(request, cancellationToken);
        return Ok(new SimpleMessageResponse("Logged out."));
    }

    [HttpPost("refresh")]
    public Task<AuthResponse> Refresh([FromBody] RefreshRequest request, CancellationToken cancellationToken)
        => authService.RefreshAsync(request, cancellationToken);

    [EnableRateLimiting("auth-login")]
    [HttpPost("forgot-password")]
    public async Task<ActionResult<SimpleMessageResponse>> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ForgotPasswordAsync(request, cancellationToken);
        return Ok(new SimpleMessageResponse("If the account exists, a reset email has been sent."));
    }

    [HttpPost("reset-password")]
    public async Task<ActionResult<SimpleMessageResponse>> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request, cancellationToken);
        return Ok(new SimpleMessageResponse("Password updated."));
    }
}

public static class DefaultCategoryFactory
{
    public static IReadOnlyList<Category> Create(Guid userId)
    {
        var expenseCategories = new[]
        {
            ("Food", "#1F9D74", "utensils"),
            ("Rent", "#2D6FA3", "home"),
            ("Utilities", "#C49A3A", "bolt"),
            ("Transport", "#D48A1F", "car"),
            ("Entertainment", "#7B6EF6", "ticket"),
            ("Shopping", "#C44E47", "bag"),
            ("Health", "#1F7A8C", "heart"),
            ("Education", "#4C7A5A", "book"),
            ("Travel", "#8E5D52", "plane"),
            ("Subscriptions", "#6F6A5E", "repeat"),
            ("Miscellaneous", "#5F6A70", "circle")
        };

        var incomeCategories = new[]
        {
            ("Salary", "#1F9D74", "briefcase"),
            ("Freelance", "#2D6FA3", "spark"),
            ("Bonus", "#C49A3A", "star"),
            ("Investment", "#4C7A5A", "chart"),
            ("Gift", "#C44E47", "gift"),
            ("Refund", "#1F7A8C", "rotate"),
            ("Other", "#5F6A70", "dots")
        };

        return expenseCategories
            .Select(item => new Category
            {
                UserId = userId,
                Name = item.Item1,
                Color = item.Item2,
                Icon = item.Item3,
                Type = CategoryType.Expense
            })
            .Concat(incomeCategories.Select(item => new Category
            {
                UserId = userId,
                Name = item.Item1,
                Color = item.Item2,
                Icon = item.Item3,
                Type = CategoryType.Income
            }))
            .ToList();
    }
}
