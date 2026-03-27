using System.Net;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Features.Rules;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Recurring;

public sealed record RecurringDto(
    Guid Id,
    string Title,
    TransactionType Type,
    decimal Amount,
    Guid? CategoryId,
    Guid? AccountId,
    string? CategoryName,
    string? AccountName,
    RecurringFrequency Frequency,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly NextRunDate,
    bool AutoCreateTransaction,
    bool IsPaused,
    bool IsShared,
    bool CanManage);

public sealed record SaveRecurringRequest(
    string Title,
    TransactionType Type,
    decimal Amount,
    Guid? CategoryId,
    Guid? AccountId,
    RecurringFrequency Frequency,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateOnly NextRunDate,
    bool AutoCreateTransaction,
    bool IsPaused);

public interface IRecurringService
{
    Task<IReadOnlyList<RecurringDto>> GetRecurringAsync(CancellationToken cancellationToken = default);
    Task<RecurringDto> CreateRecurringAsync(SaveRecurringRequest request, CancellationToken cancellationToken = default);
    Task<RecurringDto> UpdateRecurringAsync(Guid id, SaveRecurringRequest request, CancellationToken cancellationToken = default);
    Task DeleteRecurringAsync(Guid id, CancellationToken cancellationToken = default);
    Task ProcessDueRecurringTransactionsAsync(CancellationToken cancellationToken = default);
}

public sealed class RecurringService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService,
    IAccountAccessService accountAccessService,
    IAccountBalanceSnapshotService snapshotService,
    IRuleService ruleService) : UserScopedService(currentUserService), IRecurringService
{
    public async Task<IReadOnlyList<RecurringDto>> GetRecurringAsync(CancellationToken cancellationToken = default)
    {
        GetRequiredUserId();
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        var roleMap = accessibleAccountIds.ToDictionary(x => x, _ => AccountRole.Viewer);
        foreach (var accountId in accessibleAccountIds)
        {
            roleMap[accountId] = await accountAccessService.GetRoleAsync(accountId, cancellationToken) ?? AccountRole.Viewer;
        }

        var items = await dbContext.RecurringTransactions
            .AsNoTracking()
            .Include(x => x.Account)
            .ThenInclude(x => x!.Members)
            .Include(x => x.Category)
            .Where(x => x.AccountId != null && accessibleAccountIds.Contains(x.AccountId.Value))
            .OrderBy(x => x.NextRunDate)
            .ToListAsync(cancellationToken);

        return items.Select(x => x.ToDto(roleMap.GetValueOrDefault(x.AccountId ?? Guid.Empty, AccountRole.Viewer))).ToList();
    }

    public async Task<RecurringDto> CreateRecurringAsync(SaveRecurringRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var account = await ValidateRequestAsync(request, AccountRole.Editor, userId, cancellationToken);
        var category = await ResolveCategoryAsync(account, request.CategoryId, request.Type, userId, cancellationToken);

        var recurring = new RecurringTransaction
        {
            UserId = userId,
            Title = request.Title.Trim(),
            Type = request.Type,
            Amount = request.Amount,
            CategoryId = category?.Id,
            AccountId = account.Id,
            Frequency = request.Frequency,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            NextRunDate = request.NextRunDate,
            AutoCreateTransaction = request.AutoCreateTransaction,
            IsPaused = request.IsPaused
        };

        dbContext.RecurringTransactions.Add(recurring);
        await dbContext.SaveChangesAsync(cancellationToken);
        await telemetryService.TrackAsync("recurring_created", userId, new { recurring.Id }, cancellationToken);
        await auditLogService.WriteAsync("recurring.created", nameof(RecurringTransaction), recurring.Id, new { recurring.Title }, cancellationToken, account.Id);

        recurring.Account = account;
        recurring.Category = category;
        return recurring.ToDto(IsSharedAccount(account) ? await accountAccessService.GetRoleAsync(account.Id, cancellationToken) ?? AccountRole.Viewer : AccountRole.Owner);
    }

    public async Task<RecurringDto> UpdateRecurringAsync(Guid id, SaveRecurringRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var recurring = await dbContext.RecurringTransactions
            .Include(x => x.Account)
            .ThenInclude(x => x!.Members)
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Recurring item not found", "Recurring item does not exist.");

        await accountAccessService.EnsureAccessAsync(recurring.AccountId ?? Guid.Empty, AccountRole.Editor, cancellationToken);
        var account = await ValidateRequestAsync(request, AccountRole.Editor, userId, cancellationToken);
        var category = await ResolveCategoryAsync(account, request.CategoryId, request.Type, userId, cancellationToken);

        recurring.Title = request.Title.Trim();
        recurring.Type = request.Type;
        recurring.Amount = request.Amount;
        recurring.CategoryId = category?.Id;
        recurring.Category = category;
        recurring.AccountId = account.Id;
        recurring.Account = account;
        recurring.Frequency = request.Frequency;
        recurring.StartDate = request.StartDate;
        recurring.EndDate = request.EndDate;
        recurring.NextRunDate = request.NextRunDate;
        recurring.AutoCreateTransaction = request.AutoCreateTransaction;
        recurring.IsPaused = request.IsPaused;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("recurring.updated", nameof(RecurringTransaction), recurring.Id, new { recurring.Title }, cancellationToken, account.Id);

        return recurring.ToDto(IsSharedAccount(account) ? await accountAccessService.GetRoleAsync(account.Id, cancellationToken) ?? AccountRole.Viewer : AccountRole.Owner);
    }

    public async Task DeleteRecurringAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var recurring = await dbContext.RecurringTransactions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Recurring item not found", "Recurring item does not exist.");

        await accountAccessService.EnsureAccessAsync(recurring.AccountId ?? Guid.Empty, AccountRole.Editor, cancellationToken);

        dbContext.RecurringTransactions.Remove(recurring);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("recurring.deleted", nameof(RecurringTransaction), recurring.Id, new { recurring.Title }, cancellationToken, recurring.AccountId);
    }

    public async Task ProcessDueRecurringTransactionsAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dueItems = await dbContext.RecurringTransactions
            .Include(x => x.Account)
            .ThenInclude(x => x!.Members)
            .Where(x =>
                !x.IsPaused &&
                x.AutoCreateTransaction &&
                x.AccountId != null &&
                x.NextRunDate <= today &&
                (x.EndDate == null || x.NextRunDate <= x.EndDate))
            .ToListAsync(cancellationToken);

        foreach (var recurring in dueItems)
        {
            while (recurring.NextRunDate <= today && (recurring.EndDate is null || recurring.NextRunDate <= recurring.EndDate))
            {
                var alreadyExists = await dbContext.Transactions.AnyAsync(x =>
                    x.RecurringTransactionId == recurring.Id &&
                    x.TransactionDate == recurring.NextRunDate, cancellationToken);

                if (!alreadyExists && recurring.Account is not null)
                {
                    var evaluation = await ruleService.EvaluateAsync(
                        new RuleEvaluationInput(
                            recurring.UserId,
                            recurring.Account.Id,
                            recurring.CategoryId,
                            recurring.Amount,
                            recurring.Title,
                            "auto-recurring",
                            Array.Empty<string>()),
                        cancellationToken);

                    var category = await ResolveCategoryForProcessingAsync(recurring.UserId, recurring.Account, evaluation.CategoryId ?? recurring.CategoryId, recurring.Type, cancellationToken);

                    dbContext.Transactions.Add(new Transaction
                    {
                        UserId = recurring.UserId,
                        AccountId = recurring.AccountId!.Value,
                        CategoryId = category?.Id,
                        RecurringTransactionId = recurring.Id,
                        Type = recurring.Type,
                        Amount = recurring.Amount,
                        TransactionDate = recurring.NextRunDate,
                        Merchant = recurring.Title,
                        Note = $"Recurring transaction: {recurring.Title}",
                        PaymentMethod = "auto-recurring",
                        Tags = evaluation.Tags
                    });

                    ApplyTransactionImpact(recurring.Account, recurring.Type, recurring.Amount);
                    snapshotService.QueueSnapshot(recurring.Account);
                }

                recurring.NextRunDate = GetNextRunDate(recurring.NextRunDate, recurring.Frequency);
            }
        }

        if (dueItems.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Account> ValidateRequestAsync(SaveRecurringRequest request, AccountRole minimumRole, Guid userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid recurring item", "Title is required.");
        }

        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid recurring item", "Amount must be greater than zero.");
        }

        if (request.AccountId is null || request.AccountId == Guid.Empty)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid recurring item", "Account is required.");
        }

        var account = await GetAccountAsync(request.AccountId.Value, minimumRole, cancellationToken);
        if (!IsSharedAccount(account) && account.UserId != userId)
        {
            throw new AppException(HttpStatusCode.NotFound, "Account not found", "Account does not exist.");
        }

        return account;
    }

    private async Task<Account> GetAccountAsync(Guid accountId, AccountRole minimumRole, CancellationToken cancellationToken)
    {
        await accountAccessService.EnsureAccessAsync(accountId, minimumRole, cancellationToken);
        return await dbContext.Accounts
            .Include(x => x.Members)
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Account does not exist.");
    }

    private async Task<Category?> ResolveCategoryAsync(Account account, Guid? categoryId, TransactionType type, Guid userId, CancellationToken cancellationToken)
    {
        if (categoryId is null)
        {
            return null;
        }

        var query = dbContext.Categories.Where(x => x.Id == categoryId && !x.IsArchived);
        query = IsSharedAccount(account)
            ? query.Where(x => x.AccountId == account.Id)
            : query.Where(x => x.UserId == userId && x.AccountId == null);

        var category = await query.FirstOrDefaultAsync(cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        ValidateCategoryForType(type, category);
        return category;
    }

    private async Task<Category?> ResolveCategoryForProcessingAsync(Guid userId, Account account, Guid? categoryId, TransactionType type, CancellationToken cancellationToken)
    {
        if (categoryId is null)
        {
            return null;
        }

        var query = dbContext.Categories.Where(x => x.Id == categoryId && !x.IsArchived);
        query = IsSharedAccount(account)
            ? query.Where(x => x.AccountId == account.Id)
            : query.Where(x => x.UserId == userId && x.AccountId == null);

        var category = await query.FirstOrDefaultAsync(cancellationToken);
        if (category is null)
        {
            return null;
        }

        ValidateCategoryForType(type, category);
        return category;
    }

    private static DateOnly GetNextRunDate(DateOnly current, RecurringFrequency frequency)
        => frequency switch
        {
            RecurringFrequency.Daily => current.AddDays(1),
            RecurringFrequency.Weekly => current.AddDays(7),
            RecurringFrequency.Monthly => current.AddMonths(1),
            RecurringFrequency.Yearly => current.AddYears(1),
            _ => current.AddMonths(1)
        };

    private static void ApplyTransactionImpact(Account account, TransactionType type, decimal amount)
    {
        account.CurrentBalance += type switch
        {
            TransactionType.Income => amount,
            TransactionType.Expense => -amount,
            _ => 0m
        };
        account.LastUpdatedAtUtc = DateTime.UtcNow;
    }

    private static bool IsSharedAccount(Account account)
        => account.Members.Count > 0;

    private static void ValidateCategoryForType(TransactionType type, Category? category)
    {
        if (category is null)
        {
            return;
        }

        if (type == TransactionType.Expense && category.Type != CategoryType.Expense)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid category", "Expense transactions require an expense category.");
        }

        if (type == TransactionType.Income && category.Type != CategoryType.Income)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid category", "Income transactions require an income category.");
        }
    }
}

[ApiController]
[Authorize]
[Route("api/recurring")]
public sealed class RecurringController(IRecurringService recurringService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<RecurringDto>> GetRecurring(CancellationToken cancellationToken)
        => recurringService.GetRecurringAsync(cancellationToken);

    [HttpPost]
    public Task<RecurringDto> CreateRecurring([FromBody] SaveRecurringRequest request, CancellationToken cancellationToken)
        => recurringService.CreateRecurringAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<RecurringDto> UpdateRecurring(Guid id, [FromBody] SaveRecurringRequest request, CancellationToken cancellationToken)
        => recurringService.UpdateRecurringAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<SimpleMessageResponse>> DeleteRecurring(Guid id, CancellationToken cancellationToken)
    {
        await recurringService.DeleteRecurringAsync(id, cancellationToken);
        return Ok(new SimpleMessageResponse("Recurring item deleted."));
    }
}

internal static class RecurringMappings
{
    public static RecurringDto ToDto(this RecurringTransaction recurring, AccountRole role)
        => new(
            recurring.Id,
            recurring.Title,
            recurring.Type,
            recurring.Amount,
            recurring.CategoryId,
            recurring.AccountId,
            recurring.Category?.Name,
            recurring.Account?.Name,
            recurring.Frequency,
            recurring.StartDate,
            recurring.EndDate,
            recurring.NextRunDate,
            recurring.AutoCreateTransaction,
            recurring.IsPaused,
            role != AccountRole.Owner,
            role >= AccountRole.Editor);
}
