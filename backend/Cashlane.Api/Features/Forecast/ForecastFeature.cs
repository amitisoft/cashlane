using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Forecast;

public sealed record ForecastKnownExpenseDto(string Kind, string Merchant, decimal Amount, DateOnly Date, string AccountName);
public sealed record ForecastMonthDto(
    decimal ForecastedBalance,
    decimal LowestProjectedBalance,
    decimal SafeToSpend,
    ForecastConfidence Confidence,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ForecastKnownExpenseDto> KnownExpenses);
public sealed record ForecastDailyPointDto(DateOnly Date, decimal ProjectedBalance, decimal ProjectedIncome, decimal ProjectedExpense);

public interface IForecastService
{
    Task<ForecastMonthDto> GetMonthForecastAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ForecastDailyPointDto>> GetDailyForecastAsync(CancellationToken cancellationToken = default);
}

public sealed class ForecastService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAccountAccessService accountAccessService) : UserScopedService(currentUserService), IForecastService
{
    public async Task<ForecastMonthDto> GetMonthForecastAsync(CancellationToken cancellationToken = default)
    {
        var forecast = await BuildForecastAsync(cancellationToken);
        var warnings = new List<string>();
        if (forecast.LowestProjectedBalance < 0)
        {
            warnings.Add("Negative balance likely before month-end.");
        }

        if (forecast.Confidence == ForecastConfidence.Low)
        {
            warnings.Add("Forecast confidence is low because historical data is limited.");
        }

        return new ForecastMonthDto(
            forecast.Points.LastOrDefault()?.ProjectedBalance ?? forecast.StartingBalance,
            forecast.LowestProjectedBalance,
            Math.Max(0m, forecast.LowestProjectedBalance),
            forecast.Confidence,
            warnings,
            forecast.KnownExpenses.Where(x => x.Amount > 0).ToList());
    }

    public async Task<IReadOnlyList<ForecastDailyPointDto>> GetDailyForecastAsync(CancellationToken cancellationToken = default)
    {
        var forecast = await BuildForecastAsync(cancellationToken);
        return forecast.Points;
    }

    private async Task<ForecastComputation> BuildForecastAsync(CancellationToken cancellationToken)
    {
        GetRequiredUserId();
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        if (accessibleAccountIds.Count == 0)
        {
            return new ForecastComputation(0m, ForecastConfidence.Low, 0m, new List<ForecastKnownExpenseDto>(), new List<ForecastDailyPointDto>());
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthEnd = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));
        var historyStart = new DateOnly(today.Year, today.Month, 1).AddMonths(-6);

        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Where(x => accessibleAccountIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        var history = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Where(x => accessibleAccountIds.Contains(x.AccountId) && x.TransactionDate >= historyStart && x.TransactionDate <= today)
            .ToListAsync(cancellationToken);

        var recurring = await dbContext.RecurringTransactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Where(x => x.AccountId != null && accessibleAccountIds.Contains(x.AccountId.Value) && !x.IsPaused)
            .ToListAsync(cancellationToken);

        var confidence = ComputeConfidence(history);
        var recurringKnownExpenses = ExpandRecurringKnownExpenses(recurring, today, monthEnd);
        var patternedKnownExpenses = DetectPatternedExpenses(history, recurringKnownExpenses, today, monthEnd);
        var knownExpenses = recurringKnownExpenses
            .Concat(patternedKnownExpenses)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Merchant)
            .ToList();

        var patternedKeys = patternedKnownExpenses
            .Select(x => $"{x.AccountName}|{x.Merchant.Trim().ToLowerInvariant()}|{x.Date.Day}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var historicalBase = history
            .Where(x =>
                x.RecurringTransactionId is null &&
                !patternedKeys.Contains($"{x.Account.Name}|{x.Merchant.Trim().ToLowerInvariant()}|{x.TransactionDate.Day}"))
            .ToList();

        var startingBalance = accounts.Sum(x => x.CurrentBalance);
        var dailyAverages = BuildDailyAverages(historicalBase, confidence);
        var points = new List<ForecastDailyPointDto>();
        var runningBalance = startingBalance;
        var lowestProjectedBalance = runningBalance;

        foreach (var date in EachDate(today, monthEnd))
        {
            var weekdayKey = date.DayOfWeek;
            dailyAverages.TryGetValue(weekdayKey, out var average);
            average ??= new DailyAverage(0m, 0m);
            var projectedIncome = average.Income;
            var projectedExpense = average.Expense;

            foreach (var expense in knownExpenses.Where(x => x.Date == date))
            {
                if (expense.Kind.Equals("Recurring", StringComparison.OrdinalIgnoreCase) && expense.Amount > 0)
                {
                    if (recurring.FirstOrDefault(x => x.Title == expense.Merchant && x.Amount == expense.Amount)?.Type == TransactionType.Income)
                    {
                        projectedIncome += expense.Amount;
                    }
                    else
                    {
                        projectedExpense += expense.Amount;
                    }
                }
                else
                {
                    projectedExpense += expense.Amount;
                }
            }

            runningBalance += projectedIncome - projectedExpense;
            lowestProjectedBalance = Math.Min(lowestProjectedBalance, runningBalance);
            points.Add(new ForecastDailyPointDto(date, runningBalance, projectedIncome, projectedExpense));
        }

        return new ForecastComputation(startingBalance, confidence, lowestProjectedBalance, knownExpenses, points);
    }

    private static ForecastConfidence ComputeConfidence(IReadOnlyCollection<Transaction> history)
    {
        var monthCount = history
            .Select(x => $"{x.TransactionDate.Year:D4}-{x.TransactionDate.Month:D2}")
            .Distinct()
            .Count();

        return history.Count switch
        {
            >= 90 when monthCount >= 5 => ForecastConfidence.High,
            >= 30 when monthCount >= 3 => ForecastConfidence.Medium,
            _ => ForecastConfidence.Low
        };
    }

    private static IReadOnlyList<ForecastKnownExpenseDto> ExpandRecurringKnownExpenses(IEnumerable<RecurringTransaction> recurring, DateOnly today, DateOnly monthEnd)
    {
        var items = new List<ForecastKnownExpenseDto>();
        foreach (var item in recurring)
        {
            var nextRun = item.NextRunDate < today ? today : item.NextRunDate;
            while (nextRun <= monthEnd && (item.EndDate is null || nextRun <= item.EndDate))
            {
                items.Add(new ForecastKnownExpenseDto("Recurring", item.Title, item.Amount, nextRun, item.Account?.Name ?? "Account"));
                nextRun = item.Frequency switch
                {
                    RecurringFrequency.Daily => nextRun.AddDays(1),
                    RecurringFrequency.Weekly => nextRun.AddDays(7),
                    RecurringFrequency.Monthly => nextRun.AddMonths(1),
                    RecurringFrequency.Yearly => nextRun.AddYears(1),
                    _ => nextRun.AddMonths(1)
                };
            }
        }

        return items;
    }

    private static IReadOnlyList<ForecastKnownExpenseDto> DetectPatternedExpenses(
        IEnumerable<Transaction> history,
        IReadOnlyCollection<ForecastKnownExpenseDto> recurringKnownExpenses,
        DateOnly today,
        DateOnly monthEnd)
    {
        var recurringMerchants = recurringKnownExpenses
            .Select(x => x.Merchant.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return history
            .Where(x =>
                x.Type == TransactionType.Expense &&
                x.RecurringTransactionId is null &&
                !string.IsNullOrWhiteSpace(x.Merchant) &&
                !recurringMerchants.Contains(x.Merchant.Trim().ToLowerInvariant()))
            .GroupBy(x => new { Merchant = x.Merchant.Trim().ToLowerInvariant(), x.AccountId, Day = x.TransactionDate.Day })
            .Where(x => x.Count() >= 2)
            .Select(x =>
            {
                var sample = x.OrderByDescending(y => y.TransactionDate).First();
                var nextDay = Math.Min(x.Key.Day, DateTime.DaysInMonth(today.Year, today.Month));
                return new ForecastKnownExpenseDto(
                    "Pattern",
                    sample.Merchant,
                    Math.Round(x.Average(y => y.Amount), 2),
                    new DateOnly(today.Year, today.Month, nextDay),
                    sample.Account.Name);
            })
            .Where(x => x.Date >= today && x.Date <= monthEnd)
            .OrderBy(x => x.Date)
            .ToList();
    }

    private static Dictionary<DayOfWeek, DailyAverage> BuildDailyAverages(IReadOnlyCollection<Transaction> history, ForecastConfidence confidence)
    {
        var allDays = Enum.GetValues<DayOfWeek>().ToDictionary(x => x, _ => new DailyAverage(0m, 0m));
        if (history.Count == 0)
        {
            return allDays;
        }

        if (confidence == ForecastConfidence.Low)
        {
            var distinctDays = history.Select(x => x.TransactionDate).Distinct().Count();
            var income = distinctDays == 0 ? 0m : history.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount) / distinctDays;
            var expense = distinctDays == 0 ? 0m : history.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount) / distinctDays;
            return allDays.ToDictionary(x => x.Key, _ => new DailyAverage(income, expense));
        }

        foreach (var dayGroup in history.GroupBy(x => x.TransactionDate.DayOfWeek))
        {
            var distinctDates = dayGroup.Select(x => x.TransactionDate).Distinct().Count();
            var income = distinctDates == 0 ? 0m : dayGroup.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount) / distinctDates;
            var expense = distinctDates == 0 ? 0m : dayGroup.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount) / distinctDates;
            allDays[dayGroup.Key] = new DailyAverage(income, expense);
        }

        return allDays;
    }

    private static IEnumerable<DateOnly> EachDate(DateOnly from, DateOnly to)
    {
        for (var value = from; value <= to; value = value.AddDays(1))
        {
            yield return value;
        }
    }

    private sealed record DailyAverage(decimal Income, decimal Expense);
    private sealed record ForecastComputation(
        decimal StartingBalance,
        ForecastConfidence Confidence,
        decimal LowestProjectedBalance,
        IReadOnlyList<ForecastKnownExpenseDto> KnownExpenses,
        IReadOnlyList<ForecastDailyPointDto> Points);
}

[ApiController]
[Authorize]
[Route("api/forecast")]
public sealed class ForecastController(IForecastService forecastService) : ControllerBase
{
    [HttpGet("month")]
    public Task<ForecastMonthDto> GetMonth(CancellationToken cancellationToken)
        => forecastService.GetMonthForecastAsync(cancellationToken);

    [HttpGet("daily")]
    public Task<IReadOnlyList<ForecastDailyPointDto>> GetDaily(CancellationToken cancellationToken)
        => forecastService.GetDailyForecastAsync(cancellationToken);
}
