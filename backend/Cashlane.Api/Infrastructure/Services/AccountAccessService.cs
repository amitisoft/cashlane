using System.Net;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Infrastructure.Logging;
using Cashlane.Api.Infrastructure.Middleware;
using Microsoft.EntityFrameworkCore;

namespace Cashlane.Api.Infrastructure.Services;

public interface IAccountAccessService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetAccessibleAccountIdsAsync(AccountRole minimumRole = AccountRole.Viewer, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> GetOwnedAccountIdsAsync(CancellationToken cancellationToken = default);
    Task<AccountRole?> GetRoleAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task EnsureAccessAsync(Guid accountId, AccountRole minimumRole = AccountRole.Viewer, CancellationToken cancellationToken = default);
}

public sealed class AccountAccessService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService) : IAccountAccessService
{
    private readonly Dictionary<Guid, AccountRole> _roles = new();
    private bool _isLoaded;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        var userId = currentUserService.UserId;
        if (userId is null)
        {
            return;
        }

        var ownedAccountIds = await dbContext.Accounts
            .Where(x => x.UserId == userId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var accountId in ownedAccountIds)
        {
            _roles[accountId] = AccountRole.Owner;
        }

        var memberships = await dbContext.AccountMembers
            .Where(x => x.UserId == userId)
            .Select(x => new { x.AccountId, x.Role })
            .ToListAsync(cancellationToken);

        foreach (var membership in memberships)
        {
            if (_roles.TryGetValue(membership.AccountId, out var existing))
            {
                _roles[membership.AccountId] = MaxRole(existing, membership.Role);
                continue;
            }

            _roles[membership.AccountId] = membership.Role;
        }
    }

    public async Task<IReadOnlyList<Guid>> GetAccessibleAccountIdsAsync(AccountRole minimumRole = AccountRole.Viewer, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return _roles
            .Where(x => x.Value >= minimumRole)
            .Select(x => x.Key)
            .ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetOwnedAccountIdsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return _roles
            .Where(x => x.Value == AccountRole.Owner)
            .Select(x => x.Key)
            .ToList();
    }

    public async Task<AccountRole?> GetRoleAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return _roles.GetValueOrDefault(accountId);
    }

    public async Task EnsureAccessAsync(Guid accountId, AccountRole minimumRole = AccountRole.Viewer, CancellationToken cancellationToken = default)
    {
        var role = await GetRoleAsync(accountId, cancellationToken);
        if (role is null || role < minimumRole)
        {
            throw new AppException(
                minimumRole == AccountRole.Viewer ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden,
                minimumRole == AccountRole.Viewer ? "Account not found" : "Forbidden",
                minimumRole == AccountRole.Viewer
                    ? "The requested account does not exist or is not accessible."
                    : "You do not have permission to modify this account.");
        }
    }

    private static AccountRole MaxRole(AccountRole left, AccountRole right)
        => left >= right ? left : right;
}

public interface IAccountBalanceSnapshotService
{
    void QueueSnapshot(Account account);
    void QueueSnapshots(IEnumerable<Account> accounts);
    Task EnsureDailySnapshotsAsync(CancellationToken cancellationToken = default);
}

public sealed class AccountBalanceSnapshotService(AppDbContext dbContext) : IAccountBalanceSnapshotService
{
    public void QueueSnapshot(Account account)
    {
        dbContext.AccountBalanceSnapshots.Add(new AccountBalanceSnapshot
        {
            AccountId = account.Id,
            Balance = account.CurrentBalance,
            CapturedAtUtc = DateTime.UtcNow
        });
    }

    public void QueueSnapshots(IEnumerable<Account> accounts)
    {
        foreach (var account in accounts
                     .Where(x => x.Id != Guid.Empty)
                     .GroupBy(x => x.Id)
                     .Select(x => x.First()))
        {
            QueueSnapshot(account);
        }
    }

    public async Task EnsureDailySnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var todayStart = DateTime.UtcNow.Date;
        var snappedToday = await dbContext.AccountBalanceSnapshots
            .Where(x => x.CapturedAtUtc >= todayStart)
            .Select(x => x.AccountId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var accounts = await dbContext.Accounts
            .Where(x => !snappedToday.Contains(x.Id))
            .ToListAsync(cancellationToken);

        if (accounts.Count == 0)
        {
            return;
        }

        QueueSnapshots(accounts);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
