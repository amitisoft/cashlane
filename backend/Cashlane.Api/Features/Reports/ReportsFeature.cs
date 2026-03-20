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

public interface IReportService
{
    Task<CategorySpendReportDto> GetCategorySpendAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<IncomeVsExpenseReportDto> GetIncomeVsExpenseAsync(ReportFilter filter, CancellationToken cancellationToken = default);
    Task<AccountBalanceTrendReportDto> GetAccountBalanceTrendAsync(ReportFilter filter, CancellationToken cancellationToken = default);
}

public sealed class ReportService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    ITelemetryService telemetryService) : UserScopedService(currentUserService), IReportService
{
    public async Task<CategorySpendReportDto> GetCategorySpendAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var transactions = ApplyFilter(filter, userId);
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

        var categoryNames = await dbContext.Categories
            .Where(x => x.UserId == userId && spendByCategory.Select(y => y.CategoryId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var items = spendByCategory
            .Select(x => new CategoryReportItem(
                x.CategoryId,
                categoryNames.GetValueOrDefault(x.CategoryId, "Unknown category"),
                x.Amount))
            .ToList();

        await telemetryService.TrackAsync("report_exported", userId, new { report = "category-spend" }, cancellationToken);
        return new CategorySpendReportDto(items);
    }

    public async Task<IncomeVsExpenseReportDto> GetIncomeVsExpenseAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        var transactions = ApplyFilter(filter, GetRequiredUserId());
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
        var userId = GetRequiredUserId();
        var accounts = await dbContext.Accounts
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .Select(x => new AccountBalanceTrendItem(x.Id, x.Name, x.CurrentBalance))
            .ToListAsync(cancellationToken);

        return new AccountBalanceTrendReportDto(accounts);
    }

    private IQueryable<Cashlane.Api.Domain.Entities.Transaction> ApplyFilter(ReportFilter filter, Guid userId)
    {
        var query = dbContext.Transactions.Where(x => x.UserId == userId);
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
}
