using System.Threading.RateLimiting;
using Cashlane.Api.Features.Accounts;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Features.Budgets;
using Cashlane.Api.Features.Categories;
using Cashlane.Api.Features.Dashboard;
using Cashlane.Api.Features.Forecast;
using Cashlane.Api.Features.Goals;
using Cashlane.Api.Features.Insights;
using Cashlane.Api.Features.Recurring;
using Cashlane.Api.Features.Reports;
using Cashlane.Api.Features.Rules;
using Cashlane.Api.Features.Settings;
using Cashlane.Api.Features.Transactions;
using Cashlane.Api.Configuration;
using Cashlane.Api.Infrastructure.Authentication;
using Cashlane.Api.Infrastructure.Email;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace Cashlane.Api.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCashlaneRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("auth-login", limiterOptions =>
            {
                limiterOptions.PermitLimit = 8;
                limiterOptions.Window = TimeSpan.FromMinutes(5);
                limiterOptions.QueueLimit = 0;
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });

        return services;
    }

    public static IServiceCollection AddCashlaneCors(this IServiceCollection services, CorsOptions corsOptions)
    {
        if (corsOptions.AllowedOrigins.Length == 0)
        {
            throw new InvalidOperationException("At least one frontend origin must be configured for CORS.");
        }

        services.AddCors(options =>
        {
            options.AddPolicy("frontend", policy =>
            {
                policy
                    .WithOrigins(corsOptions.AllowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        return services;
    }

    public static IServiceCollection AddCashlaneFeatureServices(this IServiceCollection services)
    {
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddScoped<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRegistrationVerificationTokenService, RegistrationVerificationTokenService>();
        services.AddScoped<IEmailService, SmtpEmailService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<ITelemetryService, TelemetryService>();
        services.AddScoped<IAccountAccessService, AccountAccessService>();
        services.AddScoped<IAccountBalanceSnapshotService, AccountBalanceSnapshotService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ITransactionService, TransactionService>();
        services.AddScoped<IBudgetService, BudgetService>();
        services.AddScoped<IGoalService, GoalService>();
        services.AddScoped<IRecurringService, RecurringService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IForecastService, ForecastService>();
        services.AddScoped<IInsightsService, InsightsService>();
        services.AddScoped<IRuleService, RuleService>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IDemoDataSeeder, DemoDataSeeder>();

        return services;
    }
}
