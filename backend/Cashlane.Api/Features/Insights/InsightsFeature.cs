using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Insights;

public sealed record HealthFactorDto(string Key, string Label, decimal Value, decimal Score, decimal Weight, string Summary);
public sealed record HealthScoreDto(decimal Score, IReadOnlyList<HealthFactorDto> Factors, IReadOnlyList<string> Suggestions);
public sealed record InsightCardDto(string Title, string Body, string Kind);

public interface IInsightsService
{
    Task<HealthScoreDto> GetHealthScoreAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InsightCardDto>> GetInsightsAsync(CancellationToken cancellationToken = default);
}

public sealed class InsightsService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAccountAccessService accountAccessService) : UserScopedService(currentUserService), IInsightsService
{
    public async Task<HealthScoreDto> GetHealthScoreAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        var closedMonths = Enumerable.Range(1, 6)
            .Select(offset => monthStart.AddMonths(-offset))
            .ToList();

        var monthTransactions = await dbContext.Transactions
            .AsNoTracking()
            .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.TransactionDate >= monthStart && x.TransactionDate <= monthEnd)
            .ToListAsync(cancellationToken);

        var income = monthTransactions.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount);
        var expense = monthTransactions.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount);

        var savingsRate = income <= 0 ? 0m : Math.Clamp(((income - expense) / income) * 100m, 0m, 100m);

        var monthlyExpenseSeries = new List<decimal>();
        foreach (var month in closedMonths)
        {
            var nextMonth = month.AddMonths(1);
            var value = await dbContext.Transactions
                .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.Type == TransactionType.Expense && x.TransactionDate >= month && x.TransactionDate < nextMonth)
                .SumAsync(x => x.Amount, cancellationToken);
            monthlyExpenseSeries.Add(value);
        }

        var expenseStability = monthlyExpenseSeries.Count(x => x > 0) < 3
            ? 50m
            : ComputeExpenseStability(monthlyExpenseSeries);

        var budgets = await dbContext.Budgets
            .AsNoTracking()
            .Where(x =>
                (x.AccountId == null && x.UserId == userId) ||
                (x.AccountId != null && accessibleAccountIds.Contains(x.AccountId.Value)))
            .Where(x => x.Month == today.Month && x.Year == today.Year)
            .ToListAsync(cancellationToken);

        var budgetAdherence = budgets.Count == 0
            ? 60m
            : ComputeBudgetAdherence(budgets, monthTransactions, userId);

        var balances = await dbContext.Accounts
            .Where(x => accessibleAccountIds.Contains(x.Id))
            .SumAsync(x => x.CurrentBalance, cancellationToken);
        var averageMonthlyExpense = monthlyExpenseSeries.Where(x => x > 0).DefaultIfEmpty(expense > 0 ? expense : 1m).Average();
        var monthsCovered = averageMonthlyExpense <= 0 ? 0m : balances / averageMonthlyExpense;
        var cashBuffer = Math.Clamp(monthsCovered / 6m * 100m, 0m, 100m);

        var factors = new[]
        {
            new HealthFactorDto("savingsRate", "Savings rate", savingsRate, savingsRate, 30m, $"{savingsRate:0.#}% of current-month income remains after expenses."),
            new HealthFactorDto("expenseStability", "Expense stability", expenseStability, expenseStability, 20m, "Lower month-to-month volatility increases this score."),
            new HealthFactorDto("budgetAdherence", "Budget adherence", budgetAdherence, budgetAdherence, 25m, "This reflects how well current budgets are holding."),
            new HealthFactorDto("cashBuffer", "Cash buffer", Math.Round(monthsCovered, 2), cashBuffer, 25m, $"{monthsCovered:0.##} months of expenses covered by current balances.")
        };

        var score = Math.Clamp(
            factors.Sum(x => x.Score * (x.Weight / 100m)),
            0m,
            100m);

        var suggestions = factors
            .OrderBy(x => x.Score)
            .Take(2)
            .Select(x => x.Key switch
            {
                "savingsRate" => "Reduce discretionary spending or increase income to improve savings rate.",
                "expenseStability" => "Stabilize variable categories such as food, transport, and shopping.",
                "budgetAdherence" => "Set or tighten monthly budgets in your highest-spend categories.",
                "cashBuffer" => "Build more reserves in liquid accounts to improve your cash buffer.",
                _ => "Improve this factor to raise the overall score."
            })
            .ToList();

        return new HealthScoreDto(Math.Round(score, 1), factors, suggestions);
    }

    public async Task<IReadOnlyList<InsightCardDto>> GetInsightsAsync(CancellationToken cancellationToken = default)
    {
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var previousMonthStart = monthStart.AddMonths(-1);
        var previousMonthEnd = monthStart.AddDays(-1);

        var thisMonth = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Category)
            .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.TransactionDate >= monthStart && x.TransactionDate <= today)
            .ToListAsync(cancellationToken);
        var previousMonth = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Category)
            .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.TransactionDate >= previousMonthStart && x.TransactionDate <= previousMonthEnd)
            .ToListAsync(cancellationToken);

        var cards = new List<InsightCardDto>();
        var thisExpense = thisMonth.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount);
        var previousExpense = previousMonth.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount);
        if (previousExpense > 0)
        {
            var delta = thisExpense - previousExpense;
            var direction = delta >= 0 ? "increased" : "decreased";
            cards.Add(new InsightCardDto(
                "Monthly spending",
                $"Total spending {direction} {Math.Abs(delta / previousExpense * 100m):0.#}% compared with last month.",
                delta >= 0 ? "warning" : "success"));
        }

        var thisSavings = thisMonth.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount) - thisExpense;
        var previousSavings = previousMonth.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount) - previousExpense;
        if (previousMonth.Any())
        {
            cards.Add(new InsightCardDto(
                "Savings trend",
                thisSavings >= previousSavings
                    ? "You saved more than last month."
                    : "You saved less than last month.",
                thisSavings >= previousSavings ? "success" : "info"));
        }

        var foodThisMonth = thisMonth.Where(x => x.Category?.Name == "Food" && x.Type == TransactionType.Expense).Sum(x => x.Amount);
        var foodPreviousMonth = previousMonth.Where(x => x.Category?.Name == "Food" && x.Type == TransactionType.Expense).Sum(x => x.Amount);
        if (foodPreviousMonth > 0)
        {
            var change = (foodThisMonth - foodPreviousMonth) / foodPreviousMonth * 100m;
            cards.Add(new InsightCardDto(
                "Food spending",
                $"Food spending changed {change:0.#}% compared with last month.",
                change > 10 ? "warning" : "info"));
        }

        return cards;
    }

    private static decimal ComputeExpenseStability(IReadOnlyList<decimal> values)
    {
        var average = values.Average();
        if (average <= 0)
        {
            return 50m;
        }

        var variance = values.Sum(value => (value - average) * (value - average)) / values.Count;
        var standardDeviation = Math.Sqrt((double)variance);
        var coefficient = (decimal)standardDeviation / average;
        return Math.Clamp(100m - coefficient * 100m, 0m, 100m);
    }

    private static decimal ComputeBudgetAdherence(IReadOnlyList<Budget> budgets, IReadOnlyList<Transaction> monthTransactions, Guid userId)
    {
        var weightedScore = 0m;
        var totalWeight = 0m;
        foreach (var budget in budgets)
        {
            var spent = monthTransactions
                .Where(x =>
                    x.Type == TransactionType.Expense &&
                    x.CategoryId == budget.CategoryId &&
                    (budget.AccountId is null
                        ? x.UserId == userId
                        : x.AccountId == budget.AccountId))
                .Sum(x => x.Amount);

            var usedPercent = budget.Amount <= 0 ? 0m : spent / budget.Amount * 100m;
            var score = usedPercent <= 100m ? 100m : Math.Max(0m, 100m - (usedPercent - 100m));
            weightedScore += score * budget.Amount;
            totalWeight += budget.Amount;
        }

        return totalWeight <= 0 ? 60m : Math.Clamp(weightedScore / totalWeight, 0m, 100m);
    }
}

[ApiController]
[Authorize]
[Route("api/insights")]
public sealed class InsightsController(IInsightsService insightsService) : ControllerBase
{
    [HttpGet("health-score")]
    public Task<HealthScoreDto> GetHealthScore(CancellationToken cancellationToken)
        => insightsService.GetHealthScoreAsync(cancellationToken);

    [HttpGet]
    public Task<IReadOnlyList<InsightCardDto>> GetInsights(CancellationToken cancellationToken)
        => insightsService.GetInsightsAsync(cancellationToken);
}
