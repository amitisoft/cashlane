using System.Net;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Budgets;

public sealed record BudgetDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    int Month,
    int Year,
    decimal Amount,
    decimal SpentAmount,
    decimal RemainingAmount,
    int AlertThresholdPercent,
    int UsedPercent);

public sealed record SaveBudgetRequest(Guid CategoryId, int Month, int Year, decimal Amount, int AlertThresholdPercent);
public sealed record DuplicateBudgetRequest(int Month, int Year);

public interface IBudgetService
{
    Task<IReadOnlyList<BudgetDto>> GetBudgetsAsync(int month, int year, CancellationToken cancellationToken = default);
    Task<BudgetDto> CreateBudgetAsync(SaveBudgetRequest request, CancellationToken cancellationToken = default);
    Task<BudgetDto> UpdateBudgetAsync(Guid id, SaveBudgetRequest request, CancellationToken cancellationToken = default);
    Task DeleteBudgetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDto>> DuplicateLastMonthAsync(DuplicateBudgetRequest request, CancellationToken cancellationToken = default);
}

public sealed class BudgetService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService) : UserScopedService(currentUserService), IBudgetService
{
    public async Task<IReadOnlyList<BudgetDto>> GetBudgetsAsync(int month, int year, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var budgets = await dbContext.Budgets
            .Include(x => x.Category)
            .Where(x => x.UserId == userId && x.Month == month && x.Year == year)
            .OrderBy(x => x.Category.Name)
            .ToListAsync(cancellationToken);

        var startDate = new DateOnly(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        var spentByCategory = await dbContext.Transactions
            .Where(x =>
                x.UserId == userId &&
                x.Type == TransactionType.Expense &&
                x.TransactionDate >= startDate &&
                x.TransactionDate <= endDate &&
                x.CategoryId != null)
            .GroupBy(x => x.CategoryId!.Value)
            .Select(x => new { CategoryId = x.Key, Amount = x.Sum(y => y.Amount) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Amount, cancellationToken);

        return budgets.Select(budget =>
        {
            var spent = spentByCategory.GetValueOrDefault(budget.CategoryId);
            var usedPercent = budget.Amount <= 0 ? 0 : (int)Math.Round((spent / budget.Amount) * 100, MidpointRounding.AwayFromZero);
            return new BudgetDto(
                budget.Id,
                budget.CategoryId,
                budget.Category.Name,
                budget.Month,
                budget.Year,
                budget.Amount,
                spent,
                budget.Amount - spent,
                budget.AlertThresholdPercent,
                usedPercent);
        }).ToList();
    }

    public async Task<BudgetDto> CreateBudgetAsync(SaveBudgetRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateBudgetRequest(request);

        var category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == request.CategoryId && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        if (category.Type != CategoryType.Expense)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid budget", "Budgets can only be created for expense categories.");
        }

        if (await dbContext.Budgets.AnyAsync(x => x.UserId == userId && x.CategoryId == request.CategoryId && x.Month == request.Month && x.Year == request.Year, cancellationToken))
        {
            throw new AppException(HttpStatusCode.Conflict, "Duplicate budget", "A budget already exists for this category and month.");
        }

        var budget = new Budget
        {
            UserId = userId,
            CategoryId = request.CategoryId,
            Month = request.Month,
            Year = request.Year,
            Amount = request.Amount,
            AlertThresholdPercent = request.AlertThresholdPercent
        };

        dbContext.Budgets.Add(budget);
        await dbContext.SaveChangesAsync(cancellationToken);
        await telemetryService.TrackAsync("budget_created", userId, new { budget.Id }, cancellationToken);
        await auditLogService.WriteAsync("budget.created", nameof(Budget), budget.Id, new { budget.Amount }, cancellationToken);

        return (await GetBudgetsAsync(request.Month, request.Year, cancellationToken)).First(x => x.Id == budget.Id);
    }

    public async Task<BudgetDto> UpdateBudgetAsync(Guid id, SaveBudgetRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateBudgetRequest(request);

        var budget = await dbContext.Budgets.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Budget not found", "Budget does not exist.");

        budget.CategoryId = request.CategoryId;
        budget.Month = request.Month;
        budget.Year = request.Year;
        budget.Amount = request.Amount;
        budget.AlertThresholdPercent = request.AlertThresholdPercent;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("budget.updated", nameof(Budget), budget.Id, new { budget.Amount }, cancellationToken);

        return (await GetBudgetsAsync(request.Month, request.Year, cancellationToken)).First(x => x.Id == budget.Id);
    }

    public async Task DeleteBudgetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var budget = await dbContext.Budgets.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Budget not found", "Budget does not exist.");

        dbContext.Budgets.Remove(budget);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("budget.deleted", nameof(Budget), budget.Id, new { budget.Month, budget.Year }, cancellationToken);
    }

    public async Task<IReadOnlyList<BudgetDto>> DuplicateLastMonthAsync(DuplicateBudgetRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var current = new DateOnly(request.Year, request.Month, 1);
        var previous = current.AddMonths(-1);

        var existing = await dbContext.Budgets
            .Where(x => x.UserId == userId && x.Month == request.Month && x.Year == request.Year)
            .Select(x => x.CategoryId)
            .ToListAsync(cancellationToken);

        var previousBudgets = await dbContext.Budgets
            .Where(x => x.UserId == userId && x.Month == previous.Month && x.Year == previous.Year && !existing.Contains(x.CategoryId))
            .ToListAsync(cancellationToken);

        if (previousBudgets.Count == 0)
        {
            return await GetBudgetsAsync(request.Month, request.Year, cancellationToken);
        }

        dbContext.Budgets.AddRange(previousBudgets.Select(budget => new Budget
        {
            UserId = budget.UserId,
            CategoryId = budget.CategoryId,
            Month = request.Month,
            Year = request.Year,
            Amount = budget.Amount,
            AlertThresholdPercent = budget.AlertThresholdPercent
        }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("budget.duplicated-last-month", nameof(Budget), null, new { request.Month, request.Year }, cancellationToken);

        return await GetBudgetsAsync(request.Month, request.Year, cancellationToken);
    }

    private static void ValidateBudgetRequest(SaveBudgetRequest request)
    {
        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid budget", "Budget amount must be greater than zero.");
        }

        if (request.Month is < 1 or > 12)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid budget", "Month must be between 1 and 12.");
        }
    }
}

[ApiController]
[Authorize]
[Route("api/budgets")]
public sealed class BudgetsController(IBudgetService budgetService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BudgetDto>> GetBudgets([FromQuery] int month, [FromQuery] int year, CancellationToken cancellationToken)
        => budgetService.GetBudgetsAsync(month, year, cancellationToken);

    [HttpPost]
    public Task<BudgetDto> CreateBudget([FromBody] SaveBudgetRequest request, CancellationToken cancellationToken)
        => budgetService.CreateBudgetAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<BudgetDto> UpdateBudget(Guid id, [FromBody] SaveBudgetRequest request, CancellationToken cancellationToken)
        => budgetService.UpdateBudgetAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<SimpleMessageResponse>> DeleteBudget(Guid id, CancellationToken cancellationToken)
    {
        await budgetService.DeleteBudgetAsync(id, cancellationToken);
        return Ok(new SimpleMessageResponse("Budget deleted."));
    }

    [HttpPost("duplicate-last-month")]
    public Task<IReadOnlyList<BudgetDto>> DuplicateLastMonth([FromBody] DuplicateBudgetRequest request, CancellationToken cancellationToken)
        => budgetService.DuplicateLastMonthAsync(request, cancellationToken);
}
