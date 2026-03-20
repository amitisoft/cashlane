using Cashlane.Api.Domain.Common;
using Cashlane.Api.Domain.Enums;

namespace Cashlane.Api.Domain.Entities;

public sealed class User : EntityBase
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = new List<PasswordResetToken>();
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
}

public sealed class RefreshToken : EntityBase
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? DeviceLabel { get; set; }

    public bool IsActive => RevokedAtUtc is null && ExpiresAtUtc > DateTime.UtcNow;
}

public sealed class PasswordResetToken : EntityBase
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }

    public bool IsActive => UsedAtUtc is null && ExpiresAtUtc > DateTime.UtcNow;
}

public sealed class Account : EntityBase
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public AccountType Type { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public string? InstitutionName { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}

public sealed class Category : EntityBase
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public CategoryType Type { get; set; }
    public string Color { get; set; } = "#1F9D74";
    public string Icon { get; set; } = "circle";
    public bool IsArchived { get; set; }
}

public sealed class Transaction : EntityBase
{
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? RecurringTransactionId { get; set; }
    public Guid? TransferGroupId { get; set; }
    public User User { get; set; } = null!;
    public Account Account { get; set; } = null!;
    public Category? Category { get; set; }
    public RecurringTransaction? RecurringTransaction { get; set; }
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public DateOnly TransactionDate { get; set; }
    public string Merchant { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
}

public sealed class Budget : EntityBase
{
    public Guid UserId { get; set; }
    public Guid CategoryId { get; set; }
    public User User { get; set; } = null!;
    public Category Category { get; set; } = null!;
    public int Month { get; set; }
    public int Year { get; set; }
    public decimal Amount { get; set; }
    public int AlertThresholdPercent { get; set; } = 80;
}

public sealed class Goal : EntityBase
{
    public Guid UserId { get; set; }
    public Guid? LinkedAccountId { get; set; }
    public User User { get; set; } = null!;
    public Account? LinkedAccount { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public DateOnly? TargetDate { get; set; }
    public string Icon { get; set; } = "target";
    public string Color { get; set; } = "#C49A3A";
    public GoalStatus Status { get; set; } = GoalStatus.Active;
}

public sealed class RecurringTransaction : EntityBase
{
    public Guid UserId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? AccountId { get; set; }
    public User User { get; set; } = null!;
    public Category? Category { get; set; }
    public Account? Account { get; set; }
    public string Title { get; set; } = string.Empty;
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public RecurringFrequency Frequency { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly NextRunDate { get; set; }
    public bool AutoCreateTransaction { get; set; } = true;
    public bool IsPaused { get; set; }

    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}

public sealed class AuditLog : EntityBase
{
    public Guid? UserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string MetadataJson { get; set; } = "{}";
}
