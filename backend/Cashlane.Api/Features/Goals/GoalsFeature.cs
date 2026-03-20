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

namespace Cashlane.Api.Features.Goals;

public sealed record GoalDto(
    Guid Id,
    string Name,
    decimal TargetAmount,
    decimal CurrentAmount,
    DateOnly? TargetDate,
    Guid? LinkedAccountId,
    string Icon,
    string Color,
    GoalStatus Status,
    int ProgressPercent);

public sealed record SaveGoalRequest(string Name, decimal TargetAmount, DateOnly? TargetDate, Guid? LinkedAccountId, string Icon, string Color, GoalStatus Status);
public sealed record GoalContributionRequest(decimal Amount, Guid? AccountId);

public interface IGoalService
{
    Task<IReadOnlyList<GoalDto>> GetGoalsAsync(CancellationToken cancellationToken = default);
    Task<GoalDto> CreateGoalAsync(SaveGoalRequest request, CancellationToken cancellationToken = default);
    Task<GoalDto> UpdateGoalAsync(Guid id, SaveGoalRequest request, CancellationToken cancellationToken = default);
    Task<GoalDto> ContributeAsync(Guid id, GoalContributionRequest request, CancellationToken cancellationToken = default);
    Task<GoalDto> WithdrawAsync(Guid id, GoalContributionRequest request, CancellationToken cancellationToken = default);
}

public sealed class GoalService(
    AppDbContext dbContext,
    ICurrentUserService currentUserService,
    IAuditLogService auditLogService,
    ITelemetryService telemetryService) : UserScopedService(currentUserService), IGoalService
{
    public async Task<IReadOnlyList<GoalDto>> GetGoalsAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        return await dbContext.Goals
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.TargetDate)
            .Select(x => new GoalDto(
                x.Id,
                x.Name,
                x.TargetAmount,
                x.CurrentAmount,
                x.TargetDate,
                x.LinkedAccountId,
                x.Icon,
                x.Color,
                x.Status,
                x.TargetAmount <= 0 ? 0 : (int)Math.Round((x.CurrentAmount / x.TargetAmount) * 100, MidpointRounding.AwayFromZero)))
            .ToListAsync(cancellationToken);
    }

    public async Task<GoalDto> CreateGoalAsync(SaveGoalRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateGoalRequest(request);
        if (request.LinkedAccountId is not null)
        {
            await EnsureAccountOwnershipAsync(request.LinkedAccountId.Value, userId, cancellationToken);
        }

        var goal = new Goal
        {
            UserId = userId,
            Name = request.Name.Trim(),
            TargetAmount = request.TargetAmount,
            CurrentAmount = 0,
            TargetDate = request.TargetDate,
            LinkedAccountId = request.LinkedAccountId,
            Icon = string.IsNullOrWhiteSpace(request.Icon) ? "target" : request.Icon,
            Color = string.IsNullOrWhiteSpace(request.Color) ? "#C49A3A" : request.Color,
            Status = request.Status
        };

        dbContext.Goals.Add(goal);
        await dbContext.SaveChangesAsync(cancellationToken);
        await telemetryService.TrackAsync("goal_created", userId, new { goal.Id }, cancellationToken);
        await auditLogService.WriteAsync("goal.created", nameof(Goal), goal.Id, new { goal.Name }, cancellationToken);

        return goal.ToDto();
    }

    public async Task<GoalDto> UpdateGoalAsync(Guid id, SaveGoalRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        ValidateGoalRequest(request);

        var goal = await dbContext.Goals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Goal not found", "Goal does not exist.");

        if (request.LinkedAccountId is not null)
        {
            await EnsureAccountOwnershipAsync(request.LinkedAccountId.Value, userId, cancellationToken);
        }

        goal.Name = request.Name.Trim();
        goal.TargetAmount = request.TargetAmount;
        goal.TargetDate = request.TargetDate;
        goal.LinkedAccountId = request.LinkedAccountId;
        goal.Icon = request.Icon;
        goal.Color = request.Color;
        goal.Status = request.Status;
        if (goal.CurrentAmount >= goal.TargetAmount)
        {
            goal.Status = GoalStatus.Completed;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("goal.updated", nameof(Goal), goal.Id, new { goal.Name }, cancellationToken);

        return goal.ToDto();
    }

    public async Task<GoalDto> ContributeAsync(Guid id, GoalContributionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid contribution", "Contribution amount must be greater than zero.");
        }

        var goal = await dbContext.Goals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Goal not found", "Goal does not exist.");

        await using var dbTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var accountId = request.AccountId ?? goal.LinkedAccountId;
        if (accountId is not null)
        {
            var account = await EnsureAccountOwnershipAsync(accountId.Value, userId, cancellationToken);
            if (account.CurrentBalance < request.Amount)
            {
                throw new AppException(HttpStatusCode.BadRequest, "Insufficient balance", "Contribution exceeds available account balance.");
            }

            account.CurrentBalance -= request.Amount;
            account.LastUpdatedAtUtc = DateTime.UtcNow;
            dbContext.Transactions.Add(new Transaction
            {
                UserId = userId,
                AccountId = account.Id,
                Type = TransactionType.Transfer,
                Amount = request.Amount,
                TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Merchant = goal.Name,
                Note = $"Goal contribution: {goal.Name}",
                PaymentMethod = "goal-transfer"
            });
        }

        goal.CurrentAmount += request.Amount;
        if (goal.CurrentAmount >= goal.TargetAmount)
        {
            goal.Status = GoalStatus.Completed;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);
        await auditLogService.WriteAsync("goal.contributed", nameof(Goal), goal.Id, new { request.Amount }, cancellationToken);

        return goal.ToDto();
    }

    public async Task<GoalDto> WithdrawAsync(Guid id, GoalContributionRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid withdrawal", "Withdrawal amount must be greater than zero.");
        }

        var goal = await dbContext.Goals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Goal not found", "Goal does not exist.");

        if (goal.CurrentAmount < request.Amount)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid withdrawal", "Withdrawal exceeds current goal balance.");
        }

        await using var dbTransaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var accountId = request.AccountId ?? goal.LinkedAccountId;
        if (accountId is not null)
        {
            var account = await EnsureAccountOwnershipAsync(accountId.Value, userId, cancellationToken);
            account.CurrentBalance += request.Amount;
            account.LastUpdatedAtUtc = DateTime.UtcNow;
            dbContext.Transactions.Add(new Transaction
            {
                UserId = userId,
                AccountId = account.Id,
                Type = TransactionType.Transfer,
                Amount = request.Amount,
                TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow),
                Merchant = goal.Name,
                Note = $"Goal withdrawal: {goal.Name}",
                PaymentMethod = "goal-transfer"
            });
        }

        goal.CurrentAmount -= request.Amount;
        if (goal.CurrentAmount < goal.TargetAmount && goal.Status == GoalStatus.Completed)
        {
            goal.Status = GoalStatus.Active;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);
        await auditLogService.WriteAsync("goal.withdrawn", nameof(Goal), goal.Id, new { request.Amount }, cancellationToken);

        return goal.ToDto();
    }

    private static void ValidateGoalRequest(SaveGoalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid goal", "Goal name is required.");
        }

        if (request.TargetAmount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid goal", "Target amount must be greater than zero.");
        }
    }

    private async Task<Account> EnsureAccountOwnershipAsync(Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.Accounts.FirstOrDefaultAsync(x => x.Id == accountId && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Account not found", "Linked account does not exist.");
    }
}

[ApiController]
[Authorize]
[Route("api/goals")]
public sealed class GoalsController(IGoalService goalService) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<GoalDto>> GetGoals(CancellationToken cancellationToken)
        => goalService.GetGoalsAsync(cancellationToken);

    [HttpPost]
    public Task<GoalDto> CreateGoal([FromBody] SaveGoalRequest request, CancellationToken cancellationToken)
        => goalService.CreateGoalAsync(request, cancellationToken);

    [HttpPut("{id:guid}")]
    public Task<GoalDto> UpdateGoal(Guid id, [FromBody] SaveGoalRequest request, CancellationToken cancellationToken)
        => goalService.UpdateGoalAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/contribute")]
    public Task<GoalDto> Contribute(Guid id, [FromBody] GoalContributionRequest request, CancellationToken cancellationToken)
        => goalService.ContributeAsync(id, request, cancellationToken);

    [HttpPost("{id:guid}/withdraw")]
    public Task<GoalDto> Withdraw(Guid id, [FromBody] GoalContributionRequest request, CancellationToken cancellationToken)
        => goalService.WithdrawAsync(id, request, cancellationToken);
}

internal static class GoalMappings
{
    public static GoalDto ToDto(this Goal goal)
    {
        var progress = goal.TargetAmount <= 0
            ? 0
            : (int)Math.Round((goal.CurrentAmount / goal.TargetAmount) * 100, MidpointRounding.AwayFromZero);

        return new GoalDto(
            goal.Id,
            goal.Name,
            goal.TargetAmount,
            goal.CurrentAmount,
            goal.TargetDate,
            goal.LinkedAccountId,
            goal.Icon,
            goal.Color,
            goal.Status,
            progress);
    }
}
