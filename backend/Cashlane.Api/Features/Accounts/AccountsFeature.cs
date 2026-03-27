using System.Net;
using System.Text.Json;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Infrastructure.Email;
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
    DateTime LastUpdatedAtUtc,
    AccountRole Role,
    bool IsShared,
    string OwnerName,
    int MemberCount);

public sealed record SaveAccountRequest(string Name, AccountType Type, decimal OpeningBalance, string? InstitutionName);
public sealed record TransferRequest(Guid SourceAccountId, Guid DestinationAccountId, decimal Amount, DateOnly Date, string? Note);
public sealed record TransferResultDto(Guid TransferGroupId, decimal SourceBalance, decimal DestinationBalance);
public sealed record InviteAccountMemberRequest(string Email, AccountRole Role);
public sealed record UpdateAccountMemberRequest(AccountRole Role);
public sealed record AccountMemberDto(Guid UserId, string Email, string DisplayName, AccountRole Role, bool IsOwner);
public sealed record AccountActivityDto(Guid Id, string ActorDisplayName, string Action, DateTime CreatedAtUtc, string Summary);

public interface IAccountService
{
    Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default);
    Task<AccountDto> UpdateAccountAsync(Guid id, SaveAccountRequest request, CancellationToken cancellationToken = default);
    Task<TransferResultDto> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountMemberDto>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task InviteMemberAsync(Guid accountId, InviteAccountMemberRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountMemberDto>> UpdateMemberAsync(Guid accountId, Guid userId, UpdateAccountMemberRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountActivityDto>> GetActivityAsync(Guid accountId, CancellationToken cancellationToken = default);
}

public sealed class AccountService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    IAccountAccessService accountAccessService,
    IAccountBalanceSnapshotService snapshotService,
    IEmailService emailService) : UserScopedService(currentUserService), IAccountService
{
    public async Task<IReadOnlyList<AccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        GetRequiredUserId();
        var accountIds = await accountAccessService.GetAccessibleAccountIdsAsync(AccountRole.Viewer, cancellationToken);
        var roleMap = accountIds.Count == 0
            ? new Dictionary<Guid, AccountRole>()
            : accountIds.ToDictionary(x => x, _ => AccountRole.Viewer);

        foreach (var accountId in accountIds)
        {
            roleMap[accountId] = await accountAccessService.GetRoleAsync(accountId, cancellationToken) ?? AccountRole.Viewer;
        }

        var accounts = await dbContext.Accounts
            .AsNoTracking()
            .Include(x => x.User)
            .Include(x => x.Members)
            .Where(x => accountIds.Contains(x.Id))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return accounts
            .Select(x => x.ToDto(roleMap.GetValueOrDefault(x.Id, AccountRole.Viewer)))
            .ToList();
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
        snapshotService.QueueSnapshot(account);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("account.created", nameof(Account), account.Id, new { account.Name }, cancellationToken, account.Id);

        account.User = await dbContext.Users.FirstAsync(x => x.Id == userId, cancellationToken);
        return account.ToDto(AccountRole.Owner);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid id, SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        await accountAccessService.EnsureAccessAsync(id, AccountRole.Owner, cancellationToken);
        var account = await dbContext.Accounts
            .Include(x => x.User)
            .Include(x => x.Members)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "The requested account does not exist.");

        account.Name = request.Name.Trim();
        account.Type = request.Type;
        account.InstitutionName = request.InstitutionName?.Trim();
        account.LastUpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("account.updated", nameof(Account), account.Id, new { account.Name }, cancellationToken, account.Id);

        return account.ToDto(AccountRole.Owner);
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

        await accountAccessService.EnsureAccessAsync(request.SourceAccountId, AccountRole.Editor, cancellationToken);
        await accountAccessService.EnsureAccessAsync(request.DestinationAccountId, AccountRole.Editor, cancellationToken);

        var accounts = await dbContext.Accounts
            .Where(x => x.Id == request.SourceAccountId || x.Id == request.DestinationAccountId)
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

        snapshotService.QueueSnapshots(new[] { source, destination });
        await dbContext.SaveChangesAsync(cancellationToken);
        await databaseTransaction.CommitAsync(cancellationToken);

        await auditLogService.WriteAsync(
            "account.transfer-out",
            nameof(Account),
            transferGroupId,
            new { request.SourceAccountId, request.DestinationAccountId, request.Amount },
            cancellationToken,
            source.Id);
        await auditLogService.WriteAsync(
            "account.transfer-in",
            nameof(Account),
            transferGroupId,
            new { request.SourceAccountId, request.DestinationAccountId, request.Amount },
            cancellationToken,
            destination.Id);

        return new TransferResultDto(transferGroupId, source.CurrentBalance, destination.CurrentBalance);
    }

    public async Task<IReadOnlyList<AccountMemberDto>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await accountAccessService.EnsureAccessAsync(accountId, AccountRole.Viewer, cancellationToken);
        var account = await dbContext.Accounts
            .AsNoTracking()
            .Include(x => x.User)
            .Include(x => x.Members)
            .ThenInclude(x => x.User)
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "The requested account does not exist.");

        var members = new List<AccountMemberDto>
        {
            new(account.UserId, account.User.Email, account.User.DisplayName, AccountRole.Owner, true)
        };

        members.AddRange(account.Members
            .Where(x => x.UserId != account.UserId)
            .OrderBy(x => x.User.DisplayName)
            .Select(x => new AccountMemberDto(x.UserId, x.User.Email, x.User.DisplayName, x.Role, false)));

        return members;
    }

    public async Task InviteMemberAsync(Guid accountId, InviteAccountMemberRequest request, CancellationToken cancellationToken = default)
    {
        await accountAccessService.EnsureAccessAsync(accountId, AccountRole.Owner, cancellationToken);
        if (request.Role == AccountRole.Owner)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid role", "Invited members cannot be owners.");
        }

        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var account = await dbContext.Accounts
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "The requested account does not exist.");

        if (string.Equals(account.User.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid invite", "The owner already has access to this account.");
        }

        var invitedUser = await dbContext.Users.FirstOrDefaultAsync(x => x.Email == normalizedEmail, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "User not found", "Only existing Cashlane users can be invited.");

        var membership = await dbContext.AccountMembers.FirstOrDefaultAsync(x => x.AccountId == accountId && x.UserId == invitedUser.Id, cancellationToken);
        if (membership is null)
        {
            dbContext.AccountMembers.Add(new AccountMember
            {
                AccountId = accountId,
                UserId = invitedUser.Id,
                Role = request.Role
            });
        }
        else
        {
            membership.Role = request.Role;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            "account.member-invited",
            nameof(AccountMember),
            invitedUser.Id,
            new { invitedUser.Email, request.Role },
            cancellationToken,
            accountId);

        try
        {
            await emailService.SendSharedAccountInviteAsync(invitedUser.Email, invitedUser.DisplayName, account.Name, request.Role, cancellationToken);
        }
        catch
        {
            // best effort; SMTP may be disabled locally
        }
    }

    public async Task<IReadOnlyList<AccountMemberDto>> UpdateMemberAsync(Guid accountId, Guid userId, UpdateAccountMemberRequest request, CancellationToken cancellationToken = default)
    {
        await accountAccessService.EnsureAccessAsync(accountId, AccountRole.Owner, cancellationToken);
        if (request.Role == AccountRole.Owner)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid role", "Only the account owner can hold the owner role.");
        }

        var account = await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "The requested account does not exist.");
        if (account.UserId == userId)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid member", "The owner role cannot be modified here.");
        }

        var membership = await dbContext.AccountMembers.FirstOrDefaultAsync(x => x.AccountId == accountId && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Member not found", "That member does not have access to this account.");

        membership.Role = request.Role;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync(
            "account.member-updated",
            nameof(AccountMember),
            userId,
            new { request.Role },
            cancellationToken,
            accountId);

        return await GetMembersAsync(accountId, cancellationToken);
    }

    public async Task<IReadOnlyList<AccountActivityDto>> GetActivityAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await accountAccessService.EnsureAccessAsync(accountId, AccountRole.Viewer, cancellationToken);
        var logs = await dbContext.AuditLogs
            .AsNoTracking()
            .Where(x => x.AccountId == accountId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        var actorIds = logs.Where(x => x.UserId != null).Select(x => x.UserId!.Value).Distinct().ToList();
        var actorNames = await dbContext.Users
            .Where(x => actorIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);

        return logs.Select(x => new AccountActivityDto(
            x.Id,
            x.UserId is not null && actorNames.TryGetValue(x.UserId.Value, out var actorName) ? actorName : "System",
            x.Action,
            x.CreatedAtUtc,
            SummarizeActivity(x.Action, x.MetadataJson)))
            .ToList();
    }

    private static string SummarizeActivity(string action, string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var values = document.RootElement.EnumerateObject()
                    .Take(2)
                    .Select(x => $"{x.Name}: {x.Value.ToString()}")
                    .ToList();
                if (values.Count > 0)
                {
                    return $"{action.Replace('.', ' ')} ({string.Join(", ", values)})";
                }
            }
        }
        catch
        {
            // ignore invalid metadata
        }

        return action.Replace('.', ' ');
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

    [HttpGet("{id:guid}/members")]
    public Task<IReadOnlyList<AccountMemberDto>> GetMembers(Guid id, CancellationToken cancellationToken)
        => accountService.GetMembersAsync(id, cancellationToken);

    [HttpPost("{id:guid}/invite")]
    public async Task<ActionResult<SimpleMessageResponse>> InviteMember(Guid id, [FromBody] InviteAccountMemberRequest request, CancellationToken cancellationToken)
    {
        await accountService.InviteMemberAsync(id, request, cancellationToken);
        return Ok(new SimpleMessageResponse("Member invited."));
    }

    [HttpPut("{id:guid}/members/{userId:guid}")]
    public Task<IReadOnlyList<AccountMemberDto>> UpdateMember(Guid id, Guid userId, [FromBody] UpdateAccountMemberRequest request, CancellationToken cancellationToken)
        => accountService.UpdateMemberAsync(id, userId, request, cancellationToken);

    [HttpGet("{id:guid}/activity")]
    public Task<IReadOnlyList<AccountActivityDto>> GetActivity(Guid id, CancellationToken cancellationToken)
        => accountService.GetActivityAsync(id, cancellationToken);
}

internal static class AccountMappings
{
    public static AccountDto ToDto(this Account account, AccountRole role)
        => new(
            account.Id,
            account.Name,
            account.Type,
            account.OpeningBalance,
            account.CurrentBalance,
            account.InstitutionName,
            account.LastUpdatedAtUtc,
            role,
            role != AccountRole.Owner || account.Members.Count > 0,
            account.User.DisplayName,
            account.Members.Count + 1);
}
