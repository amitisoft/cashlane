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

namespace Cashlane.Api.Features.Recurring;

public sealed record RecurringDto(
    Guid Id,
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
    ITelemetryService telemetryService) : UserScopedService(currentUserService), IRecurringService
{
    public async Task<IReadOnlyList<RecurringDto>> GetRecurringAsync(CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        return await dbContext.RecurringTransactions
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.NextRunDate)
            .Select(x => new RecurringDto(
                x.Id,
                x.Title,
                x.Type,
                x.Amount,
                x.CategoryId,
                x.AccountId,
                x.Frequency,
                x.StartDate,
                x.EndDate,
                x.NextRunDate,
                x.AutoCreateTransaction,
                x.IsPaused))
            .ToListAsync(cancellationToken);
    }

    public async Task<RecurringDto> CreateRecurringAsync(SaveRecurringRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await ValidateRequestAsync(request, userId, cancellationToken);

        var recurring = new RecurringTransaction
        {
            UserId = userId,
            Title = request.Title.Trim(),
            Type = request.Type,
            Amount = request.Amount,
            CategoryId = request.CategoryId,
            AccountId = request.AccountId,
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
        await auditLogService.WriteAsync("recurring.created", nameof(RecurringTransaction), recurring.Id, new { recurring.Title }, cancellationToken);

        return recurring.ToDto();
    }

    public async Task<RecurringDto> UpdateRecurringAsync(Guid id, SaveRecurringRequest request, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        await ValidateRequestAsync(request, userId, cancellationToken);

        var recurring = await dbContext.RecurringTransactions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Recurring item not found", "Recurring item does not exist.");

        recurring.Title = request.Title.Trim();
        recurring.Type = request.Type;
        recurring.Amount = request.Amount;
        recurring.CategoryId = request.CategoryId;
        recurring.AccountId = request.AccountId;
        recurring.Frequency = request.Frequency;
        recurring.StartDate = request.StartDate;
        recurring.EndDate = request.EndDate;
        recurring.NextRunDate = request.NextRunDate;
        recurring.AutoCreateTransaction = request.AutoCreateTransaction;
        recurring.IsPaused = request.IsPaused;

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("recurring.updated", nameof(RecurringTransaction), recurring.Id, new { recurring.Title }, cancellationToken);

        return recurring.ToDto();
    }

    public async Task DeleteRecurringAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = GetRequiredUserId();
        var recurring = await dbContext.RecurringTransactions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new AppException(HttpStatusCode.NotFound, "Recurring item not found", "Recurring item does not exist.");

        dbContext.RecurringTransactions.Remove(recurring);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditLogService.WriteAsync("recurring.deleted", nameof(RecurringTransaction), recurring.Id, new { recurring.Title }, cancellationToken);
    }

    public async Task ProcessDueRecurringTransactionsAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var dueItems = await dbContext.RecurringTransactions
            .Include(x => x.Account)
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
                    dbContext.Transactions.Add(new Transaction
                    {
                        UserId = recurring.UserId,
                        AccountId = recurring.AccountId!.Value,
                        CategoryId = recurring.CategoryId,
                        RecurringTransactionId = recurring.Id,
                        Type = recurring.Type,
                        Amount = recurring.Amount,
                        TransactionDate = recurring.NextRunDate,
                        Merchant = recurring.Title,
                        Note = $"Recurring transaction: {recurring.Title}",
                        PaymentMethod = "auto-recurring"
                    });

                    ApplyTransactionImpact(recurring.Account, recurring.Type, recurring.Amount);
                }

                recurring.NextRunDate = GetNextRunDate(recurring.NextRunDate, recurring.Frequency);
            }
        }

        if (dueItems.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ValidateRequestAsync(SaveRecurringRequest request, Guid userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid recurring item", "Title is required.");
        }

        if (request.Amount <= 0)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid recurring item", "Amount must be greater than zero.");
        }

        if (request.AccountId is null)
        {
            throw new AppException(HttpStatusCode.BadRequest, "Invalid recurring item", "Account is required.");
        }

        var accountExists = await dbContext.Accounts.AnyAsync(x => x.Id == request.AccountId && x.UserId == userId, cancellationToken);
        if (!accountExists)
        {
            throw new AppException(HttpStatusCode.NotFound, "Account not found", "Account does not exist.");
        }
    }

    private static DateOnly GetNextRunDate(DateOnly current, RecurringFrequency frequency)
    {
        return frequency switch
        {
            RecurringFrequency.Daily => current.AddDays(1),
            RecurringFrequency.Weekly => current.AddDays(7),
            RecurringFrequency.Monthly => current.AddMonths(1),
            RecurringFrequency.Yearly => current.AddYears(1),
            _ => current.AddMonths(1)
        };
    }

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
    public static RecurringDto ToDto(this RecurringTransaction recurring)
        => new(
            recurring.Id,
            recurring.Title,
            recurring.Type,
            recurring.Amount,
            recurring.CategoryId,
            recurring.AccountId,
            recurring.Frequency,
            recurring.StartDate,
            recurring.EndDate,
            recurring.NextRunDate,
            recurring.AutoCreateTransaction,
            recurring.IsPaused);
}
