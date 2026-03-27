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
    Guid? AccountId,
    string? AccountName,
    int Month,
    int Year,
    decimal Amount,
    decimal SpentAmount,
    decimal RemainingAmount,
    int AlertThresholdPercent,
    int UsedPercent);

public sealed record SaveBudgetRequest(Guid CategoryId, Guid? AccountId, int Month, int Year, decimal Amount, int AlertThresholdPercent);
public sealed record DuplicateBudgetRequest(Guid? AccountId, int Month, int Year);

public interface IBudgetService
{
    Task<IReadOnlyList<BudgetDto>> GetBudgetsAsync(Guid? accountId, int month, int year, CancellationToken cancellationToken = default);
    Task<BudgetDto> CreateBudgetAsync(SaveBudgetRequest request, CancellationToken cancellationToken = default);
    Task<BudgetDto> UpdateBudgetAsync(Guid id, SaveBudgetRequest request, CancellationToken cancellationToken = default);
    Task DeleteBudgetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BudgetDto>> DuplicateLastMonthAsync(DuplicateBudgetRequest request, CancellationToken cancellationToken = default);
}

public sealed class BudgetService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService,
    IAccountAccessService accountAccessService) : UserScopedService(currentUserService), IBudgetService
{
    public async Task<IReadOnlyList<BudgetDto>> GetBudgetsAsync(Guid? accountId, int month, int year, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await EnsureScopeAccessAsync(accountId, AccountRole.Viewer, cancellationToken);

        var budgets = await dbContext.Budgets
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.Account)
            .Where(x => x.Month == month && x.Year == year)
            .Where(x => accountId == null ? x.UserId == userId && x.AccountId == null : x.AccountId == accountId)
            .OrderBy(x => x.Category.Name)
            .ToListAsync(cancellationToken);

        var startDate = new DateOnly(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        return budgets.Select(budget =>
        {
            var spent = dbContext.Transactions
                .Where(x =>
                    x.Type == TransactionType.Expense &&
                    x.CategoryId == budget.CategoryId &&
                    x.TransactionDate >= startDate &&
                    x.TransactionDate <= endDate &&
                    (budget.AccountId == null
                        ? x.UserId == userId
                        : x.AccountId == budget.AccountId))
                .Sum(x => x.Amount);

            var usedPercent = budget.Amount <= 0 ? 0 : (int)Math.Round((spent / budget.Amount) * 100, MidpointRounding.AwayFromZero);
            return new BudgetDto(
                budget.Id,
                budget.CategoryId,
                budget.Category.Name,
                budget.AccountId,
                budget.Account?.Name,
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
        await EnsureScopeAccessAsync(request.AccountId, request.AccountId == null ? AccountRole.Viewer : AccountRole.Owner, cancellationToken);

        var category = await dbContext.Categories.FirstOrDefaultAsync(
            x =>
                x.Id == request.CategoryId &&
                !x.IsArchived &&
                (request.AccountId == null
                    ? x.UserId == userId && x.AccountId == null
                    : x.AccountId == request.AccountId),
            cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        if (category.Type != CategoryType.Expense)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid budget", "Budgets can only be created for expense categories.");
        }

        if (await BudgetExistsAsync(userId, request, cancellationToken))
        {
            throw new AppException(HttpStatusCode.Conflict, "Duplicate budget", "A budget already exists for this category and month.");
        }

        var budget = new Budget
        {
            UserId = userId,
            CategoryId = request.CategoryId,
            AccountId = request.AccountId,
            Month = request.Month,
            Year = request.Year,
            Amount = request.Amount,
            AlertThresholdPercent = request.AlertThresholdPercent
        };

        dbContext.Budgets.Add(budget);
        await dbContext.SaveChangesAsync(cancellationToken);
        await telemetryService.TrackAsync("budget_created", userId, new { budget.Id, budget.AccountId }, cancellationToken);
        await auditLogService.WriteAsync("budget.created", nameof(Budget), budget.Id, new { budget.Amount }, cancellationToken, budget.AccountId);

        return (await GetBudgetsAsync(request.AccountId, request.Month, request.Year, cancellationToken)).First(x => x.Id == budget.Id);
    }

    public async Task<BudgetDto> UpdateBudgetAsync(Guid id, SaveBudgetRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateBudgetRequest(request);

        var budget = await dbContext.Budgets.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Budget not found", "Budget does not exist.");

        await EnsureScopeAccessAsync(budget.AccountId, budget.AccountId == null ? AccountRole.Viewer : AccountRole.Owner, cancellationToken);
        if (budget.AccountId != request.AccountId)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid budget", "Budget scope cannot be changed.");
        }

        var category = await dbContext.Categories.FirstOrDefaultAsync(
            x =>
                x.Id == request.CategoryId &&
                !x.IsArchived &&
                (request.AccountId == null
                    ? x.UserId == userId && x.AccountId == null
                    : x.AccountId == request.AccountId),
            cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        if (category.Type != CategoryType.Expense)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid budget", "Budgets can only be created for expense categories.");
        }

        budget.CategoryId = request.CategoryId;
        budget.Month = request.Month;
        budget.Year = request.Year;
        budget.Amount = request.Amount;
        budget.AlertThresholdPercent = request.AlertThresholdPercent;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("budget.updated", nameof(Budget), budget.Id, new { budget.Amount }, cancellationToken, budget.AccountId);

        return (await GetBudgetsAsync(request.AccountId, request.Month, request.Year, cancellationToken)).First(x => x.Id == budget.Id);
    }

    public async Task DeleteBudgetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var budget = await dbContext.Budgets.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Budget not found", "Budget does not exist.");

        await EnsureScopeAccessAsync(budget.AccountId, budget.AccountId is null ? AccountRole.Viewer : AccountRole.Owner, cancellationToken);

        dbContext.Budgets.Remove(budget);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("budget.deleted", nameof(Budget), budget.Id, new { budget.Month, budget.Year }, cancellationToken, budget.AccountId);
    }

    public async Task<IReadOnlyList<BudgetDto>> DuplicateLastMonthAsync(DuplicateBudgetRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await EnsureScopeAccessAsync(request.AccountId, request.AccountId == null ? AccountRole.Viewer : AccountRole.Owner, cancellationToken);

        var current = new DateOnly(request.Year, request.Month, 1);
        var previous = current.AddMonths(-1);

        var existing = await dbContext.Budgets
            .Where(x =>
                request.AccountId == null
                    ? x.UserId == userId && x.AccountId == null && x.Month == request.Month && x.Year == request.Year
                    : x.AccountId == request.AccountId && x.Month == request.Month && x.Year == request.Year)
            .Select(x => x.CategoryId)
            .ToListAsync(cancellationToken);

        var previousBudgets = await dbContext.Budgets
            .Where(x =>
                request.AccountId == null
                    ? x.UserId == userId && x.AccountId == null && x.Month == previous.Month && x.Year == previous.Year && !existing.Contains(x.CategoryId)
                    : x.AccountId == request.AccountId && x.Month == previous.Month && x.Year == previous.Year && !existing.Contains(x.CategoryId))
            .ToListAsync(cancellationToken);

        if (previousBudgets.Count == 0)
        {
            return await GetBudgetsAsync(request.AccountId, request.Month, request.Year, cancellationToken);
        }

        dbContext.Budgets.AddRange(previousBudgets.Select(budget => new Budget
        {
            UserId = userId,
            CategoryId = budget.CategoryId,
            AccountId = budget.AccountId,
            Month = request.Month,
            Year = request.Year,
            Amount = budget.Amount,
            AlertThresholdPercent = budget.AlertThresholdPercent
        }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("budget.duplicated-last-month", nameof(Budget), null, new { request.AccountId, request.Month, request.Year }, cancellationToken, request.AccountId);

        return await GetBudgetsAsync(request.AccountId, request.Month, request.Year, cancellationToken);
    }

    private async Task<bool> BudgetExistsAsync(Guid userId, SaveBudgetRequest request, CancellationToken cancellationToken)
    {
        return await dbContext.Budgets.AnyAsync(
            x =>
                x.CategoryId == request.CategoryId &&
                x.Month == request.Month &&
                x.Year == request.Year &&
                (request.AccountId == null
                    ? x.UserId == userId && x.AccountId == null
                    : x.AccountId == request.AccountId),
            cancellationToken);
    }

    private async Task EnsureScopeAccessAsync(Guid? accountId, AccountRole minimumRole, CancellationToken cancellationToken)
    {
        if (accountId is null)
        {
            return;
        }

        await accountAccessService.EnsureAccessAsync(accountId.Value, minimumRole, cancellationToken);
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
    public Task<IReadOnlyList<BudgetDto>> GetBudgets([FromQuery] Guid? accountId, [FromQuery] int month, [FromQuery] int year, CancellationToken cancellationToken)
        => budgetService.GetBudgetsAsync(accountId, month, year, cancellationToken);

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
