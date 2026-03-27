using Cashlane.Api.Data;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Reports;

public sealed record ReportFilter(
    DateOnly? DateFrom,
    DateOnly? DateTo,
    Guid? AccountId,
    Guid? CategoryId,
    TransactionType? Type);

public sealed record CategoryReportItem(Guid CategoryId, string CategoryName, decimal Amount);
public sealed record CategorySpendReportDto(IReadOnlyList<CategoryReportItem> Items);
public sealed record IncomeVsExpenseItem(string Label, decimal Income, decimal Expense);
public sealed record IncomeVsExpenseReportDto(IReadOnlyList<IncomeVsExpenseItem> Items);
public sealed record AccountBalanceTrendItem(Guid AccountId, string AccountName, decimal CurrentBalance);
public sealed record AccountBalanceTrendReportDto(IReadOnlyList<AccountBalanceTrendItem> Items);
public sealed record TrendPoint(string Label, decimal Amount);
public sealed record CategoryTrendItem(Guid CategoryId, string CategoryName, string Color, IReadOnlyList<TrendPoint> Points);
public sealed record SavingsRateTrendItem(string Label, decimal SavingsRate);
public sealed record TrendsReportDto(
    IReadOnlyList<CategoryTrendItem> CategoryTrends,
    IReadOnlyList<SavingsRateTrendItem> SavingsRateTrend,
    IReadOnlyList<IncomeVsExpenseItem> IncomeVsExpenseTrend);
public sealed record NetWorthPointDto(string Label, decimal NetWorth);
public sealed record NetWorthReportDto(IReadOnlyList<NetWorthPointDto> Items);

public interface IReportService
{
    Task<CategorySpendReportDto> GetCategorySpendAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<IncomeVsExpenseReportDto> GetIncomeVsExpenseAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<AccountBalanceTrendReportDto> GetAccountBalanceTrendAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<TrendsReportDto> GetTrendsAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<NetWorthReportDto> GetNetWorthAsync(ReportFilter filter, CancellationToken cancellationToken = default);
}

public sealed class ReportService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    ITelemetryService telemetryService,
    IAccountAccessService accountAccessService) : UserScopedService(currentUserService), IReportService
{
    public async Task<CategorySpendReportDto> GetCategorySpendAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        GetRequiredUserId();
        var accountIds = await ResolveAccountIdsAsync(filter.AccountId, cancellationToken);
        var transactions = ApplyFilter(filter, accountIds);
        var spendByCategory = await transactions
            .Where(x => x.Type == TransactionType.Expense && x.CategoryId != null)
            .GroupBy(x => x.CategoryId!.Value)
            .Select(x => new
            {
                CategoryId = x.Key,
                Amount = x.Sum(y => y.Amount)
            })
            .OrderByDescending(x => x.Amount)
            .ToListAsync(cancellationToken);

        var categoryIds = spendByCategory.Select(y => y.CategoryId).ToList();
        var categories = await dbContext.Categories
            .Where(x => categoryIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var items = spendByCategory
            .Select(x => new CategoryReportItem(
                x.CategoryId,
                categories.GetValueOrDefault(x.CategoryId, "Unknown category"),
                x.Amount))
            .ToList();

        await telemetryService.TrackAsync("report_exported", GetRequiredUserId(), new { report = "category-spend" }, cancellationToken);
        return new CategorySpendReportDto(items);
    }

    public async Task<IncomeVsExpenseReportDto> GetIncomeVsExpenseAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        var transactions = ApplyFilter(filter, await ResolveAccountIdsAsync(filter.AccountId, cancellationToken));
        var grouped = await transactions
            .GroupBy(x => new { x.TransactionDate.Year, x.TransactionDate.Month })
            .Select(x => new
            {
                x.Key.Year,
                x.Key.Month,
                Income = x.Where(y => y.Type == TransactionType.Income).Sum(y => y.Amount),
                Expense = x.Where(y => y.Type == TransactionType.Expense).Sum(y => y.Amount)
            })
            .OrderBy(x => x.Year)
            .ThenBy(x => x.Month)
            .ToListAsync(cancellationToken);

        return new IncomeVsExpenseReportDto(grouped.Select(x => new IncomeVsExpenseItem($"{x.Month:00}/{x.Year}", x.Income, x.Expense)).ToList());
    }

    public async Task<AccountBalanceTrendReportDto> GetAccountBalanceTrendAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        GetRequiredUserId();
        var accountIds = await ResolveAccountIdsAsync(filter.AccountId, cancellationToken);
        var accounts = await dbContext.Accounts
            .Where(x => accountIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .Select(x => new AccountBalanceTrendItem(x.Id, x.Name, x.CurrentBalance))
            .ToListAsync(cancellationToken);

        return new AccountBalanceTrendReportDto(accounts);
    }

    public async Task<TrendsReportDto> GetTrendsAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        var accountIds = await ResolveAccountIdsAsync(filter.AccountId, cancellationToken);
        var query = ApplyFilter(filter, accountIds);
        var items = await query
            .Include(x => x.Category)
            .ToListAsync(cancellationToken);

        var monthSeries = BuildMonthlySeries(filter, items);
        var topCategories = items
            .Where(x => x.Type == TransactionType.Expense && x.CategoryId != null)
            .GroupBy(x => x.CategoryId!.Value)
            .OrderByDescending(x => x.Sum(y => y.Amount))
            .Take(5)
            .Select(x => x.Key)
            .ToHashSet();

        var categoryTrends = items
            .Where(x => x.Type == TransactionType.Expense && x.CategoryId != null && topCategories.Contains(x.CategoryId.Value))
            .GroupBy(x => new { x.CategoryId, x.Category!.Name, x.Category.Color })
            .Select(group => new CategoryTrendItem(
                group.Key.CategoryId!.Value,
                group.Key.Name,
                group.Key.Color,
                monthSeries.Select(month => new TrendPoint(
                    month.Label,
                    group.Where(x => x.TransactionDate >= month.Start && x.TransactionDate < month.End).Sum(x => x.Amount)))
                    .ToList()))
            .ToList();

        var savingsRateTrend = monthSeries
            .Select(month =>
            {
                var monthItems = items.Where(x => x.TransactionDate >= month.Start && x.TransactionDate < month.End).ToList();
                var income = monthItems.Where(x => x.Type == TransactionType.Income).Sum(x => x.Amount);
                var expense = monthItems.Where(x => x.Type == TransactionType.Expense).Sum(x => x.Amount);
                var savingsRate = income <= 0 ? 0m : Math.Clamp(((income - expense) / income) * 100m, 0m, 100m);
                return new SavingsRateTrendItem(month.Label, Math.Round(savingsRate, 1));
            })
            .ToList();

        var incomeVsExpenseTrend = monthSeries
            .Select(month => new IncomeVsExpenseItem(
                month.Label,
                items.Where(x => x.TransactionDate >= month.Start && x.TransactionDate < month.End && x.Type == TransactionType.Income).Sum(x => x.Amount),
                items.Where(x => x.TransactionDate >= month.Start && x.TransactionDate < month.End && x.Type == TransactionType.Expense).Sum(x => x.Amount)))
            .ToList();

        return new TrendsReportDto(categoryTrends, savingsRateTrend, incomeVsExpenseTrend);
    }

    public async Task<NetWorthReportDto> GetNetWorthAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        var accountIds = await ResolveAccountIdsAsync(filter.AccountId, cancellationToken);
        var defaultEnd = DateOnly.FromDateTime(DateTime.UtcNow);
        var defaultStart = defaultEnd.AddMonths(-6);
        var dateFrom = filter.DateFrom ?? defaultStart;
        var dateTo = filter.DateTo ?? defaultEnd;
        var startUtc = dateFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endUtcExclusive = dateTo.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var snapshots = await dbContext.AccountBalanceSnapshots
            .AsNoTracking()
            .Where(x => accountIds.Contains(x.AccountId) && x.CapturedAtUtc >= startUtc && x.CapturedAtUtc < endUtcExclusive)
            .OrderBy(x => x.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        var points = snapshots
            .GroupBy(x => x.CapturedAtUtc.Date)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var netWorth = group
                    .GroupBy(x => x.AccountId)
                    .Sum(accountGroup => accountGroup.OrderByDescending(x => x.CapturedAtUtc).First().Balance);
                return new NetWorthPointDto(group.Key.ToString("dd MMM yyyy"), netWorth);
            })
            .ToList();

        return new NetWorthReportDto(points);
    }

    private IQueryable<Cashlane.Api.Domain.Entities.Transaction> ApplyFilter(ReportFilter filter, IReadOnlyCollection<Guid> accountIds)
    {
        var query = dbContext.Transactions.Where(x => accountIds.Contains(x.AccountId));
        if (filter.DateFrom is not null)
        {
            query = query.Where(x => x.TransactionDate >= filter.DateFrom);
        }

        if (filter.DateTo is not null)
        {
            query = query.Where(x => x.TransactionDate <= filter.DateTo);
        }

        if (filter.AccountId is not null)
        {
            query = query.Where(x => x.AccountId == filter.AccountId);
        }

        if (filter.CategoryId is not null)
        {
            query = query.Where(x => x.CategoryId == filter.CategoryId);
        }

        if (filter.Type is not null)
        {
            query = query.Where(x => x.Type == filter.Type);
        }

        return query;
    }

    private async Task<IReadOnlyList<Guid>> ResolveAccountIdsAsync(Guid? requestedAccountId, CancellationToken cancellationToken)
    {
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        if (requestedAccountId is null)
        {
            return accessibleAccountIds;
        }

        return accessibleAccountIds.Contains(requestedAccountId.Value)
            ? new[] { requestedAccountId.Value }
            : Array.Empty<Guid>();
    }

    private static IReadOnlyList<MonthRange> BuildMonthlySeries(ReportFilter filter, IReadOnlyCollection<Cashlane.Api.Domain.Entities.Transaction> items)
    {
        var fallbackEnd = DateOnly.FromDateTime(DateTime.UtcNow);
        var fallbackStart = fallbackEnd.AddMonths(-5);
        var start = new DateOnly((filter.DateFrom ?? fallbackStart).Year, (filter.DateFrom ?? fallbackStart).Month, 1);
        var end = new DateOnly((filter.DateTo ?? fallbackEnd).Year, (filter.DateTo ?? fallbackEnd).Month, 1);

        var ranges = new List<MonthRange>();
        for (var month = start; month <= end; month = month.AddMonths(1))
        {
            ranges.Add(new MonthRange(month.ToString("MMM yyyy"), month, month.AddMonths(1)));
        }

        if (ranges.Count == 0 && items.Count > 0)
        {
            var first = items.Min(x => x.TransactionDate);
            var last = items.Max(x => x.TransactionDate);
            for (var month = new DateOnly(first.Year, first.Month, 1); month <= new DateOnly(last.Year, last.Month, 1); month = month.AddMonths(1))
            {
                ranges.Add(new MonthRange(month.ToString("MMM yyyy"), month, month.AddMonths(1)));
            }
        }

        return ranges;
    }

    private sealed record MonthRange(string Label, DateOnly Start, DateOnly End);
}

[ApiController]
[Authorize]
[Route("api/reports")]
public sealed class ReportsController(IReportService reportService) : ControllerBase
{
    [HttpGet("category-spend")]
    public Task<CategorySpendReportDto> GetCategorySpend([FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => reportService.GetCategorySpendAsync(filter, cancellationToken);

    [HttpGet("income-vs-expense")]
    public Task<IncomeVsExpenseReportDto> GetIncomeVsExpense([FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => reportService.GetIncomeVsExpenseAsync(filter, cancellationToken);

    [HttpGet("account-balance-trend")]
    public Task<AccountBalanceTrendReportDto> GetAccountBalanceTrend([FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => reportService.GetAccountBalanceTrendAsync(filter, cancellationToken);

    [HttpGet("trends")]
    public Task<TrendsReportDto> GetTrends([FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => reportService.GetTrendsAsync(filter, cancellationToken);

    [HttpGet("net-worth")]
    public Task<NetWorthReportDto> GetNetWorth([FromQuery] ReportFilter filter, CancellationToken cancellationToken)
        => reportService.GetNetWorthAsync(filter, cancellationToken);
}
