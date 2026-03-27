using System.Globalization;
using System.Net;
using System.Text;
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
public sealed record TransactionImportRequest(string CsvContent);
public sealed record TransactionImportRowDto(
    int RowNumber,
    string Status,
    IReadOnlyList<string> ValidationErrors,
    Guid? AccountId,
    string? AccountName,
    Guid? CategoryId,
    string? CategoryName,
    TransactionType? Type,
    decimal? Amount,
    DateOnly? TransactionDate,
    string Merchant,
    string Note,
    string PaymentMethod,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Alerts);
public sealed record TransactionImportPreviewResponse(bool CanCommit, IReadOnlyList<TransactionImportRowDto> Rows);

public interface ITransactionService
{
    Task<TransactionListResponse> GetTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default);
    Task<TransactionDto> GetTransactionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TransactionDto> CreateTransactionAsync(SaveTransactionRequest request, CancellationToken cancellationToken = default);
    Task<TransactionDto> UpdateTransactionAsync(Guid id, SaveTransactionRequest request, CancellationToken cancellationToken = default);
    Task DeleteTransactionAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TransactionImportPreviewResponse> PreviewImportAsync(TransactionImportRequest request, CancellationToken cancellationToken = default);
    Task<SimpleMessageResponse> CommitImportAsync(TransactionImportRequest request, CancellationToken cancellationToken = default);
}

public sealed class TransactionService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService,
    IAccountAccessService accountAccessService,
    IAccountBalanceSnapshotService snapshotService,
    IRuleService ruleService) : UserScopedService(currentUserService), ITransactionService
{
    private static readonly string[] ImportHeaders =
    {
        "transactionDate",
        "type",
        "amount",
        "account",
        "category",
        "merchant",
        "note",
        "paymentMethod",
        "tags"
    };

    public async Task<TransactionListResponse> GetTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default)
    {
        GetRequiredUserId();
        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var transactionQuery = dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Category)
            .Where(x => accessibleAccountIds.Contains(x.AccountId));

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
        var transaction = await dbContext.Transactions
            .AsNoTracking()
            .Include(x => x.Account)
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Transaction not found", "The requested transaction does not exist.");

        await accountAccessService.EnsureAccessAsync(transaction.AccountId, AccountRole.Viewer, cancellationToken);
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

        var account = await GetAccountAsync(request.AccountId, AccountRole.Editor, cancellationToken);
        var prepared = await PrepareTransactionAsync(userId, account, request, cancellationToken);
        var hasExistingTransactions = await dbContext.Transactions.AnyAsync(x => x.UserId == userId, cancellationToken);
        var transaction = new Transaction
        {
            UserId = userId,
            AccountId = account.Id,
            CategoryId = prepared.Category?.Id,
            Type = request.Type,
            Amount = request.Amount,
            TransactionDate = request.TransactionDate,
            Merchant = prepared.Merchant,
            Note = prepared.Note,
            PaymentMethod = prepared.PaymentMethod,
            Tags = prepared.Tags
        };

        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        dbContext.Transactions.Add(transaction);
        ApplyTransactionImpact(account, transaction.Type, transaction.Amount, reverse: false);
        snapshotService.QueueSnapshot(account);

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        if (!hasExistingTransactions)
        {
            await telemetryService.TrackAsync("first_transaction_added", userId, new { transaction.Id }, cancellationToken);
        }

        await auditLogService.WriteAsync("transaction.created", nameof(Transaction), transaction.Id, new
        {
            transaction.Amount,
            transaction.Type,
            prepared.Alerts
        }, cancellationToken, account.Id);

        transaction.Account = account;
        transaction.Category = prepared.Category;
        return transaction.ToDto();
    }

    public async Task<TransactionDto> UpdateTransactionAsync(Guid id, SaveTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateRequest(request);

        var transaction = await dbContext.Transactions
            .Include(x => x.Account)
            .Include(x => x.Account.Members)
            .Include(x => x.Category)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Transaction not found", "The requested transaction does not exist.");

        await accountAccessService.EnsureAccessAsync(transaction.AccountId, AccountRole.Editor, cancellationToken);

        if (transaction.Type == TransactionType.Transfer || request.Type == TransactionType.Transfer)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transaction", "Transfer transactions cannot be edited here.");
        }

        var priorAccount = transaction.Account;
        var newAccount = await GetAccountAsync(request.AccountId, AccountRole.Editor, cancellationToken);
        var prepared = await PrepareTransactionAsync(userId, newAccount, request, cancellationToken);

        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        ApplyTransactionImpact(priorAccount, transaction.Type, transaction.Amount, reverse: true);

        transaction.AccountId = newAccount.Id;
        transaction.Account = newAccount;
        transaction.CategoryId = prepared.Category?.Id;
        transaction.Category = prepared.Category;
        transaction.Type = request.Type;
        transaction.Amount = request.Amount;
        transaction.TransactionDate = request.TransactionDate;
        transaction.Merchant = prepared.Merchant;
        transaction.Note = prepared.Note;
        transaction.PaymentMethod = prepared.PaymentMethod;
        transaction.Tags = prepared.Tags;

        ApplyTransactionImpact(newAccount, transaction.Type, transaction.Amount, reverse: false);
        snapshotService.QueueSnapshots(new[] { priorAccount, newAccount });

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync("transaction.updated", nameof(Transaction), transaction.Id, new
        {
            transaction.Amount,
            transaction.Type,
            prepared.Alerts
        }, cancellationToken, newAccount.Id);

        return transaction.ToDto();
    }

    public async Task DeleteTransactionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var transaction = await dbContext.Transactions
            .Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Transaction not found", "The requested transaction does not exist.");

        await accountAccessService.EnsureAccessAsync(transaction.AccountId, AccountRole.Editor, cancellationToken);
        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (transaction.Type == TransactionType.Transfer && transaction.TransferGroupId is not null)
        {
            var transferItems = await dbContext.Transactions
                .Include(x => x.Account)
                .Where(x => x.TransferGroupId == transaction.TransferGroupId)
                .ToListAsync(cancellationToken);

            foreach (var item in transferItems)
            {
                await accountAccessService.EnsureAccessAsync(item.AccountId, AccountRole.Editor, cancellationToken);
                ApplyTransactionImpact(item.Account, item.Type, item.Amount, reverse: true);
            }

            dbContext.Transactions.RemoveRange(transferItems);
            snapshotService.QueueSnapshots(transferItems.Select(x => x.Account));
        }
        else
        {
            ApplyTransactionImpact(transaction.Account, transaction.Type, transaction.Amount, reverse: true);
            dbContext.Transactions.Remove(transaction);
            snapshotService.QueueSnapshot(transaction.Account);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync("transaction.deleted", nameof(Transaction), transaction.Id, new
        {
            transaction.Amount,
            transaction.Type
        }, cancellationToken, transaction.AccountId);
    }

    public async Task<TransactionImportPreviewResponse> PreviewImportAsync(TransactionImportRequest request, CancellationToken cancellationToken = default)
    {
        var rows = await ParseImportAsync(request, cancellationToken);
        return new TransactionImportPreviewResponse(rows.All(x => x.Status == "Valid"), rows);
    }

    public async Task<SimpleMessageResponse> CommitImportAsync(TransactionImportRequest request, CancellationToken cancellationToken = default)
    {
        var rows = await ParseImportAsync(request, cancellationToken);
        if (rows.Any(x => x.Status != "Valid"))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Import invalid", "Fix the invalid rows in preview before importing.");
        }

        var importedRows = await ParseImportInternalAsync(request, cancellationToken);
        return await SaveImportedTransactionsAsync(importedRows, cancellationToken);
    }

    private async Task<Account> GetAccountAsync(Guid accountId, AccountRole minimumRole, CancellationToken cancellationToken)
    {
        await accountAccessService.EnsureAccessAsync(accountId, minimumRole, cancellationToken);
        return await dbContext.Accounts
            .Include(x => x.Members)
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Account does not exist.");
    }

    private async Task<PreparedTransaction> PrepareTransactionAsync(Guid userId, Account account, SaveTransactionRequest request, CancellationToken cancellationToken)
    {
        var merchant = request.Merchant?.Trim() ?? string.Empty;
        var note = request.Note?.Trim() ?? string.Empty;
        var paymentMethod = request.PaymentMethod?.Trim() ?? string.Empty;
        var evaluation = await ruleService.EvaluateAsync(
            new RuleEvaluationInput(
                userId,
                account.Id,
                request.CategoryId,
                request.Amount,
                merchant,
                paymentMethod,
                request.Tags ?? Array.Empty<string>()),
            cancellationToken);

        var category = await ResolveCategoryAsync(account, evaluation.CategoryId ?? request.CategoryId, request.Type, userId, cancellationToken);
        return new PreparedTransaction(
            category,
            merchant,
            note,
            paymentMethod,
            evaluation.Tags,
            evaluation.Alerts);
    }

    private async Task<Category?> ResolveCategoryAsync(Account account, Guid? categoryId, TransactionType type, Guid userId, CancellationToken cancellationToken)
    {
        if (categoryId is null)
        {
            return null;
        }

        var query = dbContext.Categories
            .Where(x => x.Id == categoryId && !x.IsArchived);

        query = IsSharedAccount(account)
            ? query.Where(x => x.AccountId == account.Id)
            : query.Where(x => x.UserId == userId && x.AccountId == null);

        var category = await query.FirstOrDefaultAsync(cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Category not found", "Category does not exist.");

        ValidateCategoryForType(type, category);
        return category;
    }

    private async Task<IReadOnlyList<TransactionImportRowDto>> ParseImportAsync(TransactionImportRequest request, CancellationToken cancellationToken)
    {
        var rows = await ParseImportInternalAsync(request, cancellationToken);
        return rows.Select(x => new TransactionImportRowDto(
                x.RowNumber,
                x.Errors.Count == 0 ? "Valid" : "Invalid",
                x.Errors,
                x.Account?.Id,
                x.Account?.Name,
                x.Category?.Id,
                x.Category?.Name,
                x.Type,
                x.Amount,
                x.TransactionDate,
                x.Merchant,
                x.Note,
                x.PaymentMethod,
                x.Tags,
                x.Alerts))
            .ToList();
    }

    private async Task<List<ParsedImportRow>> ParseImportInternalAsync(TransactionImportRequest request, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(request.CsvContent))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid import", "CSV content is required.");
        }

        var csvRows = ParseCsvRows(request.CsvContent);
        if (csvRows.Count == 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid import", "CSV content is empty.");
        }

        ValidateHeaders(csvRows[0]);

        var accessibleAccountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Editor, cancellationToken);
        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Include(x => x.Members)
            .Where(x => accessibleAccountIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var categories = await dbContext.Categories
            .AsNoTracking()
            .Where(x => !x.IsArchived && ((x.UserId == userId && x.AccountId == null) || (x.AccountId != null && accessibleAccountIds.Contains(x.AccountId.Value))))
            .ToListAsync(cancellationToken);

        var duplicateAccountNames = accounts
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var parsedRows = new List<ParsedImportRow>();
        for (var index = 1; index < csvRows.Count; index++)
        {
            var values = csvRows[index];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var errors = new List<string>();
            if (values.Length != ImportHeaders.Length)
            {
                parsedRows.Add(new ParsedImportRow(index + 1, new[] { "Row does not match the Cashlane import template." }));
                continue;
            }

            var dateText = values[0].Trim();
            var typeText = values[1].Trim();
            var amountText = values[2].Trim();
            var accountText = values[3].Trim();
            var categoryText = values[4].Trim();
            var merchant = values[5].Trim();
            var note = values[6].Trim();
            var paymentMethod = values[7].Trim();
            var tags = ParseTags(values[8]);

            DateOnly? transactionDate = null;
            if (DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                transactionDate = parsedDate;
            }
            else
            {
                errors.Add("Transaction date must be a valid ISO date.");
            }

            TransactionType? type = typeText.Equals("Income", StringComparison.OrdinalIgnoreCase)
                ? TransactionType.Income
                : typeText.Equals("Expense", StringComparison.OrdinalIgnoreCase)
                    ? TransactionType.Expense
                    : null;
            if (type is null)
            {
                errors.Add("Type must be Income or Expense.");
            }

            decimal? amount = null;
            if (decimal.TryParse(amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedAmount) && parsedAmount > 0)
            {
                amount = parsedAmount;
            }
            else
            {
                errors.Add("Amount must be greater than zero.");
            }

            Account? account = null;
            if (string.IsNullOrWhiteSpace(accountText))
            {
                errors.Add("Account is required.");
            }
            else if (duplicateAccountNames.Contains(accountText))
            {
                errors.Add("Duplicate account names are not supported for import. Rename the account first.");
            }
            else
            {
                account = accounts.FirstOrDefault(x => x.Name.Equals(accountText, StringComparison.OrdinalIgnoreCase));
                if (account is null)
                {
                    errors.Add("Account was not found or is not editable.");
                }
            }

            Category? initialCategory = null;
            if (account is not null && !string.IsNullOrWhiteSpace(categoryText))
            {
                initialCategory = categories.FirstOrDefault(x =>
                    x.Name.Equals(categoryText, StringComparison.OrdinalIgnoreCase) &&
                    (IsSharedAccount(account) ? x.AccountId == account.Id : x.UserId == userId && x.AccountId == null));

                if (initialCategory is null)
                {
                    errors.Add("Category was not found in the selected account scope.");
                }
                else if (type is not null)
                {
                    try
                    {
                        ValidateCategoryForType(type.Value, initialCategory);
                    }
                    catch (AppException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
            }

            Guid? finalCategoryId = initialCategory?.Id;
            IReadOnlyList<string> alerts = Array.Empty<string>();
            IReadOnlyList<string> finalTags = tags;
            if (errors.Count == 0 && account is not null && type is not null && amount is not null && transactionDate is not null)
            {
                var evaluation = await ruleService.EvaluateAsync(
                    new RuleEvaluationInput(
                        userId,
                        account.Id,
                        initialCategory?.Id,
                        amount.Value,
                        merchant,
                        paymentMethod,
                        tags.ToArray()),
                    cancellationToken);

                finalCategoryId = evaluation.CategoryId ?? initialCategory?.Id;
                finalTags = evaluation.Tags;
                alerts = evaluation.Alerts;
            }

            Category? finalCategory = null;
            if (account is not null && type is not null && finalCategoryId is not null)
            {
                finalCategory = categories.FirstOrDefault(x => x.Id == finalCategoryId.Value);
                if (finalCategory is null)
                {
                    errors.Add("Rule-selected category no longer exists.");
                }
                else
                {
                    try
                    {
                        ValidateCategoryForType(type.Value, finalCategory);
                    }
                    catch (AppException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
            }

            if (type is not null && finalCategory is null)
            {
                errors.Add("A valid category is required after rules are applied.");
            }

            parsedRows.Add(new ParsedImportRow(
                index + 1,
                errors,
                account,
                finalCategory,
                type,
                amount,
                transactionDate,
                merchant,
                note,
                paymentMethod,
                finalTags.ToArray(),
                alerts.ToArray()));
        }

        return parsedRows;
    }

    private async Task<SimpleMessageResponse> SaveImportedTransactionsAsync(IReadOnlyCollection<ParsedImportRow> rows, CancellationToken cancellationToken)
    {
        var userId = GetRequiredUserId();
        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (row.Account is null || row.Category is null || row.Type is null || row.Amount is null || row.TransactionDate is null)
            {
                throw new AppException(HttpStatusCode.BadRequest, "Import invalid", "Preview data is incomplete.");
            }

            var account = await dbContext.Accounts.FirstAsync(x => x.Id == row.Account.Id, cancellationToken);
            dbContext.Transactions.Add(new Transaction
            {
                UserId = userId,
                AccountId = account.Id,
                CategoryId = row.Category.Id,
                Type = row.Type.Value,
                Amount = row.Amount.Value,
                TransactionDate = row.TransactionDate.Value,
                Merchant = row.Merchant,
                Note = row.Note,
                PaymentMethod = row.PaymentMethod,
                Tags = row.Tags
            });
            ApplyTransactionImpact(account, row.Type.Value, row.Amount.Value, reverse: false);
        }

        snapshotService.QueueSnapshots(
            dbContext.ChangeTracker.Entries<Account>()
                .Where(x => x.State is EntityState.Modified or EntityState.Unchanged)
                .Select(x => x.Entity));

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync(
            "transaction.imported",
            nameof(Transaction),
            null,
            new { Count = rows.Count },
            cancellationToken);

        return new SimpleMessageResponse($"Imported {rows.Count} transactions.");
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

    private static bool IsSharedAccount(Account account)
        => account.Members.Count > 0;

    private static void ValidateHeaders(string[] headers)
    {
        if (headers.Length != ImportHeaders.Length)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid import", "The CSV header does not match the Cashlane template.");
        }

        for (var index = 0; index < ImportHeaders.Length; index++)
        {
            if (!headers[index].Trim().Equals(ImportHeaders[index], StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(HttpStatusCode.BadRequest, "Invalid import", "The CSV header does not match the Cashlane template.");
            }
        }
    }

    private static List<string[]> ParseCsvRows(string csvContent)
    {
        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentValue = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < csvContent.Length; index++)
        {
            var character = csvContent[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < csvContent.Length && csvContent[index + 1] == '"')
                    {
                        currentValue.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentValue.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    currentRow.Add(currentValue.ToString());
                    currentValue.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    currentRow.Add(currentValue.ToString());
                    currentValue.Clear();
                    rows.Add(currentRow.ToArray());
                    currentRow = new List<string>();
                    break;
                default:
                    currentValue.Append(character);
                    break;
            }
        }

        if (currentValue.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentValue.ToString());
            rows.Add(currentRow.ToArray());
        }

        return rows;
    }

    private static List<string> ParseTags(string value)
        => (value ?? string.Empty)
            .Split(['|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed record PreparedTransaction(
        Category? Category,
        string Merchant,
        string Note,
        string PaymentMethod,
        string[] Tags,
        IReadOnlyList<string> Alerts);

    private sealed record ParsedImportRow(
        int RowNumber,
        IReadOnlyList<string> Errors,
        Account? Account = null,
        Category? Category = null,
        TransactionType? Type = null,
        decimal? Amount = null,
        DateOnly? TransactionDate = null,
        string Merchant = "",
        string Note = "",
        string PaymentMethod = "",
        string[]? Tags = null,
        string[]? Alerts = null)
    {
        public string[] Tags { get; init; } = Tags ?? Array.Empty<string>();
        public string[] Alerts { get; init; } = Alerts ?? Array.Empty<string>();
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

    [HttpPost("import/preview")]
    public Task<TransactionImportPreviewResponse> PreviewImport([FromBody] TransactionImportRequest request, CancellationToken cancellationToken)
        => transactionService.PreviewImportAsync(request, cancellationToken);

    [HttpPost("import/commit")]
    public Task<SimpleMessageResponse> CommitImport([FromBody] TransactionImportRequest request, CancellationToken cancellationToken)
        => transactionService.CommitImportAsync(request, cancellationToken);
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
