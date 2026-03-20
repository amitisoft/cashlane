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

namespace Cashlane.Api.Features.Transactions;

public sealed record TransactionDto(
    Guid Id,
    Guid AccountId,
    string AccountName,
    Guid? CategoryId,
    string? CategoryName,
    TransactionType Type,
    decimal Amount,
    DateOnly TransactionDate,
    string Merchant,
    string Note,
    string PaymentMethod,
    string[] Tags,
    Guid? RecurringTransactionId,
    Guid? TransferGroupId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SaveTransactionRequest(
    Guid AccountId,
    Guid? CategoryId,
    TransactionType Type,
    decimal Amount,
    DateOnly TransactionDate,
    string Merchant,
    string Note,
    string PaymentMethod,
    string[]? Tags);

public sealed record TransactionQuery(
    DateOnly? DateFrom,
    DateOnly? DateTo,
    Guid? CategoryId,
    Guid? AccountId,
    TransactionType? Type,
    decimal? MinAmount,
    decimal? MaxAmount,
    string? Search,
    int Page = 1,
    int PageSize = 20);

public sealed record TransactionListResponse(IReadOnlyList<TransactionDto> Items, int TotalCount, int Page, int PageSize);

public interface ITransactionService
{
    Task<TransactionListResponse> GetTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default);
    Task<TransactionDto> GetTransactionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TransactionDto> CreateTransactionAsync(SaveTransactionRequest request, CancellationToken cancellationToken = default);
    Task<TransactionDto> UpdateTransactionAsync(Guid id, SaveTransactionRequest request, CancellationToken cancellationToken = default);
    Task DeleteTransactionAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class TransactionService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService) : UserScopedService(currentUserService), ITransactionService
{
    public async Task<TransactionListResponse> GetTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var transactionQuery = dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Category)
            .Where(x => x.UserId == userId);

        if (query.DateFrom is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.TransactionDate >= query.DateFrom);
        }

        if (query.DateTo is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.TransactionDate <= query.DateTo);
        }

        if (query.CategoryId is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.CategoryId == query.CategoryId);
        }

        if (query.AccountId is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.AccountId == query.AccountId);
        }

        if (query.Type is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.Type == query.Type);
        }

        if (query.MinAmount is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.Amount >= query.MinAmount);
        }

        if (query.MaxAmount is not null)
        {
            transactionQuery = transactionQuery.Where(x => x.Amount <= query.MaxAmount);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim().ToLowerInvariant();
            transactionQuery = transactionQuery.Where(x =>
                x.Merchant.ToLower().Contains(search) ||
                x.Note.ToLower().Contains(search));
        }

        var totalCount = await transactionQuery.CountAsync(cancellationToken);
        var items = await transactionQuery
            .OrderByDescending(x => x.TransactionDate)
            .ThenByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new TransactionListResponse(items.Select(x => x.ToDto()).ToList(), totalCount, page, pageSize);
    }

    public async Task<TransactionDto> GetTransactionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var transaction = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Transaction not found", "The requested transaction does not exist.");

        return transaction.ToDto();
    }

    public async Task<TransactionDto> CreateTransactionAsync(SaveTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateRequest(request);

        if (request.Type == TransactionType.Transfer)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transaction", "Use the transfer endpoint for transfer transactions.");
        }

        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == request.AccountId && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Account does not exist.");

        Category? category = null;
        if (request.CategoryId is not null)
        {
            category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == request.CategoryId && x.UserId == userId, cancellationToken)
                ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");
        }

        ValidateCategoryForType(request.Type, category);

        var hasExistingTransactions = await dbContext.Transactions.AnyAsync(x => x.UserId == userId, cancellationToken);
        var transaction = new Transaction
        {
            UserId = userId,
            AccountId = account.Id,
            CategoryId = category?.Id,
            Type = request.Type,
            Amount = request.Amount,
            TransactionDate = request.TransactionDate,
            Merchant = request.Merchant.Trim(),
            Note = request.Note.Trim(),
            PaymentMethod = request.PaymentMethod.Trim(),
            Tags = request.Tags?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray() ?? Array.Empty<string>()
        };

        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Transactions.Add(transaction);
        ApplyTransactionImpact(account, transaction.Type, transaction.Amount, reverse: false);

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        if (!hasExistingTransactions)
        {
            await telemetryService.TrackAsync("first_transaction_added", userId, new { transaction.Id }, cancellationToken);
        }

        await auditLogService.WriteAsync("transaction.created", nameof(Transaction), transaction.Id, new
        {
            transaction.Amount,
            transaction.Type
        }, cancellationToken);

        transaction.Account = account;
        transaction.Category = category;
        return transaction.ToDto();
    }

    public async Task<TransactionDto> UpdateTransactionAsync(Guid id, SaveTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateRequest(request);

        var transaction = await dbContext.Transactions
            .Include(x => x.Account)
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Transaction not found", "The requested transaction does not exist.");

        if (transaction.Type == TransactionType.Transfer || request.Type == TransactionType.Transfer)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transaction", "Transfer transactions cannot be edited here.");
        }

        var newAccount = await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == request.AccountId && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Account does not exist.");

        Category? category = null;
        if (request.CategoryId is not null)
        {
            category = await dbContext.Categories.FirstOrDefaultAsync(x => x.Id == request.CategoryId && x.UserId == userId, cancellationToken)
                ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");
        }

        ValidateCategoryForType(request.Type, category);

        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        ApplyTransactionImpact(transaction.Account, transaction.Type, transaction.Amount, reverse: true);

        transaction.AccountId = newAccount.Id;
        transaction.Account = newAccount;
        transaction.CategoryId = category?.Id;
        transaction.Category = category;
        transaction.Type = request.Type;
        transaction.Amount = request.Amount;
        transaction.TransactionDate = request.TransactionDate;
        transaction.Merchant = request.Merchant.Trim();
        transaction.Note = request.Note.Trim();
        transaction.PaymentMethod = request.PaymentMethod.Trim();
        transaction.Tags = request.Tags?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct().ToArray() ?? Array.Empty<string>();

        ApplyTransactionImpact(newAccount, transaction.Type, transaction.Amount, reverse: false);

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync("transaction.updated", nameof(Transaction), transaction.Id, new
        {
            transaction.Amount,
            transaction.Type
        }, cancellationToken);

        return transaction.ToDto();
    }

    public async Task DeleteTransactionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var transaction = await dbContext.Transactions
            .Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Transaction not found", "The requested transaction does not exist.");

        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        ApplyTransactionImpact(transaction.Account, transaction.Type, transaction.Amount, reverse: true);
        dbContext.Transactions.Remove(transaction);

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync("transaction.deleted", nameof(Transaction), transaction.Id, new
        {
            transaction.Amount,
            transaction.Type
        }, cancellationToken);
    }

    private static void ValidateRequest(SaveTransactionRequest request)
    {
        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transaction", "Amount must be greater than zero.");
        }

        if (request.AccountId == Guid.Empty)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transaction", "Account is required.");
        }

        if (request.Type != TransactionType.Transfer && request.CategoryId is null)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transaction", "Category is required.");
        }
    }

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

    private static void ApplyTransactionImpact(Account account, TransactionType type, decimal amount, bool reverse)
    {
        var delta = type switch
        {
            TransactionType.Income => amount,
            TransactionType.Expense => -amount,
            _ => 0m
        };

        account.CurrentBalance += reverse ? -delta : delta;
        account.LastUpdatedAtUtc = DateTime.UtcNow;
    }
}

[ApiController]
[Authorize]
[Route("api/transactions")]
public sealed class TransactionsController(ITransactionService transactionService) : ControllerBase
{
    [HttpGet]
    public Task<TransactionListResponse> GetTransactions([FromQuery] TransactionQuery query, CancellationToken cancellationToken)
        => transactionService.GetTransactionsAsync(query, cancellationToken);

    [HttpGet("{id:guid}")]
    public Task<TransactionDto> GetTransaction(Guid id, CancellationToken cancellationToken)
        => transactionService.GetTransactionAsync(id, cancellationToken);

    [HttpPost]
    public Task<TransactionDto> CreateTransaction([FromBody] SaveTransactionRequest request, CancellationToken cancellationToken)
        => transactionService.CreateTransactionAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<TransactionDto> UpdateTransaction(Guid id, [FromBody] SaveTransactionRequest request, CancellationToken cancellationToken)
        => transactionService.UpdateTransactionAsync(id, request, cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<SimpleMessageResponse>> DeleteTransaction(Guid id, CancellationToken cancellationToken)
    {
        await transactionService.DeleteTransactionAsync(id, cancellationToken);
        return Ok(new SimpleMessageResponse("Transaction deleted."));
    }
}

internal static class TransactionMappings
{
    public static TransactionDto ToDto(this Transaction transaction)
        => new(
            transaction.Id,
            transaction.AccountId,
            transaction.Account.Name,
            transaction.CategoryId,
            transaction.Category?.Name,
            transaction.Type,
            transaction.Amount,
            transaction.TransactionDate,
            transaction.Merchant,
            transaction.Note,
            transaction.PaymentMethod,
            transaction.Tags,
            transaction.RecurringTransactionId,
            transaction.TransferGroupId,
            transaction.CreatedAtUtc,
            transaction.UpdatedAtUtc);
}
