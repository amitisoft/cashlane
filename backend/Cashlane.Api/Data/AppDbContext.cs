using Cashlane.Api.Domain.Common;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cashlane.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<RecurringTransaction> RecurringTransactions => Set<RecurringTransaction>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<AccountMember> AccountMembers => Set<AccountMember>();
    public DbSet<AccountBalanceSnapshot> AccountBalanceSnapshots => Set<AccountBalanceSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var converters = new Dictionary<Type, ValueConverter>
        {
            [typeof(AccountType)] = new EnumToStringConverter<AccountType>(),
            [typeof(CategoryType)] = new EnumToStringConverter<CategoryType>(),
            [typeof(TransactionType)] = new EnumToStringConverter<TransactionType>(),
            [typeof(GoalStatus)] = new EnumToStringConverter<GoalStatus>(),
            [typeof(RecurringFrequency)] = new EnumToStringConverter<RecurringFrequency>(),
            [typeof(AccountRole)] = new EnumToStringConverter<AccountRole>()
        };

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(decimal))
                {
                    property.SetPrecision(12);
                    property.SetScale(2);
                }

                if (converters.TryGetValue(property.ClrType, out var converter))
                {
                    property.SetValueConverter(converter);
                }
            }
        }

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(255);
            entity.Property(x => x.DisplayName).HasMaxLength(120);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(256);
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(256);
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.InstitutionName).HasMaxLength(120);
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(100);
            entity.Property(x => x.Color).HasMaxLength(20);
            entity.Property(x => x.Icon).HasMaxLength(50);
            entity.HasIndex(x => new { x.UserId, x.AccountId, x.Name, x.Type });
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.Property(x => x.Merchant).HasMaxLength(200);
            entity.Property(x => x.PaymentMethod).HasMaxLength(50);
            entity.HasIndex(x => new { x.UserId, x.TransactionDate });
            entity.HasIndex(x => new { x.RecurringTransactionId, x.TransactionDate })
                .IsUnique()
                .HasFilter("\"RecurringTransactionId\" IS NOT NULL");
        });

        modelBuilder.Entity<Budget>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.CategoryId, x.Month, x.Year })
                .IsUnique()
                .HasFilter("\"AccountId\" IS NULL");
            entity.HasIndex(x => new { x.AccountId, x.CategoryId, x.Month, x.Year })
                .IsUnique()
                .HasFilter("\"AccountId\" IS NOT NULL");
        });

        modelBuilder.Entity<Goal>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Icon).HasMaxLength(50);
            entity.Property(x => x.Color).HasMaxLength(20);
        });

        modelBuilder.Entity<RecurringTransaction>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(120);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.Property(x => x.Action).HasMaxLength(120);
            entity.Property(x => x.EntityName).HasMaxLength(120);
            entity.HasIndex(x => new { x.AccountId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<Rule>(entity =>
        {
            entity.HasIndex(x => new { x.UserId, x.AccountId, x.Priority });
        });

        modelBuilder.Entity<AccountMember>(entity =>
        {
            entity.HasIndex(x => new { x.AccountId, x.UserId }).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.Role });
        });

        modelBuilder.Entity<AccountBalanceSnapshot>(entity =>
        {
            entity.HasIndex(x => new { x.AccountId, x.CapturedAtUtc });
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TouchEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        TouchEntities();
        return base.SaveChanges();
    }

    private void TouchEntities()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
