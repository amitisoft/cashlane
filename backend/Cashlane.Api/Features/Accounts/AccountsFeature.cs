using System.Net;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;
using Cashlane.Api.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Features.Accounts;

public sealed record AccountDto(
    Guid Id,
    string Name,
    AccountType Type,
    decimal OpeningBalance,
    decimal CurrentBalance,
    string? InstitutionName,
    DateTime LastUpdatedAtUtc);

public sealed record SaveAccountRequest(string Name, AccountType Type, decimal OpeningBalance, string? InstitutionName);
public sealed record TransferRequest(Guid SourceAccountId, Guid DestinationAccountId, decimal Amount, DateOnly Date, string? Note);
public sealed record TransferResultDto(Guid TransferGroupId, decimal SourceBalance, decimal DestinationBalance);

public interface IAccountService
{
    Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default);
    Task<AccountDto> UpdateAccountAsync(Guid id, SaveAccountRequest request, CancellationToken cancellationToken = default);
    Task<TransferResultDto> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default);
}

public sealed class AccountService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService) : UserScopedService(currentUserService), IAccountService
{
    public async Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        return await dbContext.Accounts
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Name)
            .Select(x => new AccountDto(x.Id, x.Name, x.Type, x.OpeningBalance, x.CurrentBalance, x.InstitutionName, x.LastUpdatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid account", "Account name is required.");
        }

        var account = new Account
        {
            UserId = userId,
            Name = request.Name.Trim(),
            Type = request.Type,
            OpeningBalance = request.OpeningBalance,
            CurrentBalance = request.OpeningBalance,
            InstitutionName = request.InstitutionName?.Trim(),
            LastUpdatedAtUtc = DateTime.UtcNow
        };

        dbContext.Accounts.Add(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("account.created", nameof(Account), account.Id, new { account.Name }, cancellationToken);

        return account.ToDto();
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid id, SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "The requested account does not exist.");

        account.Name = request.Name.Trim();
        account.Type = request.Type;
        account.InstitutionName = request.InstitutionName?.Trim();
        account.LastUpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("account.updated", nameof(Account), account.Id, new { account.Name }, cancellationToken);

        return account.ToDto();
    }

    public async Task<TransferResultDto> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transfer", "Transfer amount must be greater than zero.");
        }

        if (request.SourceAccountId == request.DestinationAccountId)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid transfer", "Source and destination accounts must be different.");
        }

        var accounts = await dbContext.Accounts
            .Where(x => x.UserId == userId && (x.Id == request.SourceAccountId || x.Id == request.DestinationAccountId))
            .ToListAsync(cancellationToken);

        var source = accounts.FirstOrDefault(x => x.Id == request.SourceAccountId)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Source account was not found.");
        var destination = accounts.FirstOrDefault(x => x.Id == request.DestinationAccountId)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Destination account was not found.");

        if (source.CurrentBalance < request.Amount)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Insufficient balance", "Source account does not have enough balance.");
        }

        await using var databaseTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        source.CurrentBalance -= request.Amount;
        destination.CurrentBalance += request.Amount;
        source.LastUpdatedAtUtc = DateTime.UtcNow;
        destination.LastUpdatedAtUtc = DateTime.UtcNow;

        var transferGroupId = Guid.NewGuid();
        dbContext.Transactions.AddRange(
            new Transaction
            {
                UserId = userId,
                AccountId = source.Id,
                Type = TransactionType.Transfer,
                Amount = request.Amount,
                TransactionDate = request.Date,
                Merchant = destination.Name,
                Note = request.Note ?? $"Transfer to {destination.Name}",
                PaymentMethod = "transfer",
                TransferGroupId = transferGroupId
            },
            new Transaction
            {
                UserId = userId,
                AccountId = destination.Id,
                Type = TransactionType.Transfer,
                Amount = request.Amount,
                TransactionDate = request.Date,
                Merchant = source.Name,
                Note = request.Note ?? $"Transfer from {source.Name}",
                PaymentMethod = "transfer",
                TransferGroupId = transferGroupId
            });

        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync("account.transfer", nameof(Account), transferGroupId, new
        {
            request.SourceAccountId,
            request.DestinationAccountId,
            request.Amount
        }, cancellationToken);

        return new TransferResultDto(transferGroupId, source.CurrentBalance, destination.CurrentBalance);
    }
}

[ApiController]
[Authorize]
[Route("api/accounts")]
public sealed class AccountsController(IAccountService accountService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AccountDto>> GetAccounts(CancellationToken cancellationToken)
        => accountService.GetAccountsAsync(cancellationToken);

    [HttpPost]
    public Task<AccountDto> CreateAccount([FromBody] SaveAccountRequest request, CancellationToken cancellationToken)
        => accountService.CreateAccountAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<AccountDto> UpdateAccount(Guid id, [FromBody] SaveAccountRequest request, CancellationToken cancellationToken)
        => accountService.UpdateAccountAsync(id, request, cancellationToken);

    [HttpPost("transfer")]
    public Task<TransferResultDto> Transfer([FromBody] TransferRequest request, CancellationToken cancellationToken)
        => accountService.TransferAsync(request, cancellationToken);
}

internal static class AccountMappings
{
    public static AccountDto ToDto(this Account account)
        => new(account.Id, account.Name, account.Type, account.OpeningBalance, account.CurrentBalance, account.InstitutionName, account.LastUpdatedAtUtc);
}
