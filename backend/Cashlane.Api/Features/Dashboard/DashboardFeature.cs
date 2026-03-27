using Cashlane.Api.Data;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Services;
using Cashlane.Api.Features.Transactions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Dashboard;

public sealed record DashboardCardDto(string Label, decimal Amount);
public sealed record CategorySpendDto(Guid CategoryId, string CategoryName, decimal Amount, string Color);
public sealed record TrendPointDto(string Label, decimal Income, decimal Expense);
public sealed record GoalSummaryDto(Guid Id, string Name, decimal CurrentAmount, decimal TargetAmount, int ProgressPercent, DateOnly? TargetDate);
public sealed record RecurringPreviewDto(Guid Id, string Title, decimal Amount, DateOnly NextRunDate);
public sealed record AlertDto(string Kind, string Message);
public sealed record InsightDto(string Title, string Body);

public sealed record DashboardSummaryDto(
    DashboardCardDto Income,
    DashboardCardDto Expense,
    DashboardCardDto NetBalance,
    IReadOnlyList<CategorySpendDto> SpendingByCategory,
    IReadOnlyList<TrendPointDto> Trend,
    IReadOnlyList<TransactionDto> RecentTransactions,
    IReadOnlyList<RecurringPreviewDto> UpcomingRecurring,
    IReadOnlyList<GoalSummaryDto> Goals,
    IReadOnlyList<string> TopSpendingCategories,
    IReadOnlyList<AlertDto> Alerts,
    IReadOnlyList<InsightDto> Insights);

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
}

public sealed class DashboardService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAccountAccessService accountAccessService) : UserScopedService(currentUserService), IDashboardService
{
    public async Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        var monthTransactions = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Category)
            .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.TransactionDate >= monthStart && x.TransactionDate <= monthEnd)
            .OrderByDescending(x => x.TransactionDate)
            .ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var income = monthTransactions.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount);
        var expense = monthTransactions.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount);

        var categorySpend = monthTransactions
            .Where(x => x.Type == TransactionType.Expense && x.Category is not null)
            .GroupBy(x => new { x.CategoryId, x.Category!.Name, x.Category.Color })
            .Select(x => new CategorySpendDto(x.Key.CategoryId!.Value, x.Key.Name, x.Sum(y => y.Amount), x.Key.Color))
            .OrderByDescending(x => x.Amount)
            .Take(6)
            .ToList();

        var goals = await dbContext.Goals
            .Where(x => x.UserId == userId || (x.LinkedAccountId != null && accessibleAccountIds.Contains(x.LinkedAccountId.Value)))
            .OrderBy(x => x.TargetDate)
            .Select(x => new GoalSummaryDto(
                x.Id,
                x.Name,
                x.CurrentAmount,
                x.TargetAmount,
                x.TargetAmount <= 0 ? 0 : (int)Math.Round((x.CurrentAmount / x.TargetAmount) * 100, MidpointRounding.AwayFromZero),
                x.TargetDate))
            .ToListAsync(cancellationToken);

        var upcomingRecurring = await dbContext.RecurringTransactions
            .Where(x => x.AccountId != null && accessibleAccountIds.Contains(x.AccountId.Value) && !x.IsPaused)
            .OrderBy(x => x.NextRunDate)
            .Take(5)
            .Select(x => new RecurringPreviewDto(x.Id, x.Title, x.Amount, x.NextRunDate))
            .ToListAsync(cancellationToken);

        var trends = new List<TrendPointDto>();
        for (var offset = 5; offset >= 0; offset--)
        {
            var month = monthStart.AddMonths(-offset);
            var nextMonth = month.AddMonths(1);
            var monthItems = await dbContext.Transactions
                .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.TransactionDate >= month && x.TransactionDate < nextMonth)
                .ToListAsync(cancellationToken);

            trends.Add(new TrendPointDto(
                month.ToString("MMM yyyy"),
                monthItems.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount),
                monthItems.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount)));
        }

        var currentBudgets = await dbContext.Budgets
            .Include(x => x.Category)
            .Where(x =>
                x.Month == today.Month &&
                x.Year == today.Year &&
                ((x.AccountId == null && x.UserId == userId) || (x.AccountId != null && accessibleAccountIds.Contains(x.AccountId.Value))))
            .ToListAsync(cancellationToken);

        var alerts = new List<AlertDto>();
        foreach (var budget in currentBudgets)
        {
            var spent = monthTransactions
                .Where(x => x.Type == TransactionType.Expense && x.CategoryId == budget.CategoryId)
                .Sum(x => x.Amount);

            var usedPercent = budget.Amount <= 0 ? 0 : (int)Math.Round((spent / budget.Amount) * 100, MidpointRounding.AwayFromZero);
            if (usedPercent >= 120)
            {
                alerts.Add(new AlertDto("danger", $"{budget.Category.Name} budget is at {usedPercent}%."));
            }
            else if (usedPercent >= 100)
            {
                alerts.Add(new AlertDto("warning", $"{budget.Category.Name} budget has been exceeded."));
            }
            else if (usedPercent >= 80)
            {
                alerts.Add(new AlertDto("info", $"{budget.Category.Name} budget is at {usedPercent}%."));
            }
        }

        foreach (var recurring in upcomingRecurring.Where(x => x.NextRunDate <= today.AddDays(3)))
        {
            alerts.Add(new AlertDto("info", $"{recurring.Title} is due on {recurring.NextRunDate:dd MMM}."));
        }

        foreach (var goal in goals.Where(x => x.ProgressPercent >= 100))
        {
            alerts.Add(new AlertDto("success", $"{goal.Name} goal reached."));
        }

        var previousMonthStart = monthStart.AddMonths(-1);
        var previousMonthEnd = monthStart.AddDays(-1);
        var previousExpense = await dbContext.Transactions
            .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.Type == TransactionType.Expense && x.TransactionDate >= previousMonthStart && x.TransactionDate <= previousMonthEnd)
            .SumAsync(x => x.Amount, cancellationToken);

        var insights = new List<InsightDto>();
        if (previousExpense > 0)
        {
            var delta = expense - previousExpense;
            var trendWord = delta > 0 ? "up" : "down";
            var percentage = Math.Abs(delta / previousExpense * 100);
            insights.Add(new InsightDto("Monthly trend", $"Spending is {trendWord} {percentage:0.#}% compared to last month."));
        }

        return new DashboardSummaryDto(
            new DashboardCardDto("Income", income),
            new DashboardCardDto("Expense", expense),
            new DashboardCardDto("Net Balance", income - expense),
            categorySpend,
            trends,
            monthTransactions.Take(6).Select(x => x.ToDto()).ToList(),
            upcomingRecurring,
            goals,
            categorySpend.Select(x => x.CategoryName).Take(3).ToList(),
            alerts,
            insights);
    }
}

[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class DashboardController(IDashboardService dashboardService) : ControllerBase
{
    [HttpGet("summary")]
    public Task<DashboardSummaryDto> GetSummary(CancellationToken cancellationToken)
        => dashboardService.GetSummaryAsync(cancellationToken);
}
