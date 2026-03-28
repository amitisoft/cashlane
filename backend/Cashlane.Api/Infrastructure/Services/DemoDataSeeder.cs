using Cashlane.Api.Configuration;
using Cashlane.Api.Data;
using Cashlane.Api.Domain.Entities;
using Cashlane.Api.Domain.Enums;
using Cashlane.Api.Features.Auth;
using Cashlane.Api.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cashlane.Api.Infrastructure.Services;

public sealed class DemoDataSeeder(
    AppDbContext dbContext,
    IOptions<DemoOptions> options,
    IPasswordHasher passwordHasher) : IDemoDataSeeder
{
    private const int StarterTransactionThreshold = 12;
    private readonly DemoOptions _options = options.Value;

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnableDemoSeed)
        {
            return;
        }

        var demoEmail = _options.DemoEmail.ToLowerInvariant();
        var user = await dbContext.Users
            .AsSplitQuery()
            .Include(x => x.Accounts)
            .Include(x => x.Categories)
            .FirstOrDefaultAsync(x => x.Email == demoEmail, cancellationToken);

        var existingTransactionCount = user is null
            ? 0
            : await dbContext.Transactions.CountAsync(x => x.UserId == user.Id, cancellationToken);

        if (user is not null && existingTransactionCount > StarterTransactionThreshold)
        {
            return;
        }

        user ??= CreateDemoUser();
        if (dbContext.Entry(user).State == EntityState.Detached)
        {
            dbContext.Users.Add(user);
        }

        var categories = EnsureDemoCategories(user);
        var accounts = EnsureDemoAccounts(user);

        await ResetDemoWorkspaceAsync(user.Id, cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var transactions = BuildYearlyTransactions(user.Id, categories, accounts, today);
        dbContext.Transactions.AddRange(transactions);
        dbContext.Budgets.AddRange(BuildBudgets(user.Id, categories, today));
        dbContext.Goals.Add(BuildGoal(user.Id, accounts.BankAccount, today));
        dbContext.RecurringTransactions.AddRange(BuildRecurringTransactions(user.Id, categories, accounts, today));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private User CreateDemoUser()
    {
        return new User
        {
            Email = _options.DemoEmail.ToLowerInvariant(),
            DisplayName = _options.DemoDisplayName,
            PasswordHash = passwordHasher.Hash(_options.DemoPassword)
        };
    }

    private Dictionary<string, Category> EnsureDemoCategories(User user)
    {
        var categoriesByName = user.Categories
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var template in DefaultCategoryFactory.Create(user.Id))
        {
            if (categoriesByName.TryGetValue(template.Name, out var existing))
            {
                existing.Type = template.Type;
                existing.Color = template.Color;
                existing.Icon = template.Icon;
                existing.IsArchived = false;
                continue;
            }

            dbContext.Categories.Add(template);
            user.Categories.Add(template);
            categoriesByName[template.Name] = template;
        }

        return categoriesByName;
    }

    private DemoAccounts EnsureDemoAccounts(User user)
    {
        var accountsByName = user.Accounts
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var bankAccount = EnsureAccount(
            user,
            accountsByName,
            "HDFC Bank",
            AccountType.BankAccount,
            125000m,
            "HDFC Bank");

        var creditCard = EnsureAccount(
            user,
            accountsByName,
            "ICICI Credit Card",
            AccountType.CreditCard,
            0m,
            "ICICI");

        var wallet = EnsureAccount(
            user,
            accountsByName,
            "Cash Wallet",
            AccountType.CashWallet,
            3000m,
            null);

        return new DemoAccounts(bankAccount, creditCard, wallet);
    }

    private Account EnsureAccount(
        User user,
        IDictionary<string, Account> accountsByName,
        string name,
        AccountType type,
        decimal openingBalance,
        string? institutionName)
    {
        if (!accountsByName.TryGetValue(name, out var account))
        {
            account = new Account
            {
                UserId = user.Id
            };

            dbContext.Accounts.Add(account);
            user.Accounts.Add(account);
            accountsByName[name] = account;
        }

        account.Name = name;
        account.Type = type;
        account.OpeningBalance = openingBalance;
        account.CurrentBalance = openingBalance;
        account.InstitutionName = institutionName;
        account.LastUpdatedAtUtc = DateTime.UtcNow;

        return account;
    }

    private async Task ResetDemoWorkspaceAsync(Guid userId, CancellationToken cancellationToken)
    {
        await dbContext.Transactions
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.RecurringTransactions
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.Budgets
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        await dbContext.Goals
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static IReadOnlyList<Transaction> BuildYearlyTransactions(
        Guid userId,
        IReadOnlyDictionary<string, Category> categories,
        DemoAccounts accounts,
        DateOnly today)
    {
        var transactions = new List<Transaction>();
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-11);

        for (var monthIndex = 0; monthIndex < 12; monthIndex++)
        {
            var month = firstMonth.AddMonths(monthIndex);
            SeedMonth(transactions, userId, categories, accounts, today, month, monthIndex);
        }

        return transactions;
    }

    private static void SeedMonth(
        ICollection<Transaction> transactions,
        Guid userId,
        IReadOnlyDictionary<string, Category> categories,
        DemoAccounts accounts,
        DateOnly today,
        DateOnly month,
        int monthIndex)
    {
        var currentCardBalance = Math.Max(0m, -accounts.CreditCard.CurrentBalance);
        if (currentCardBalance > 0)
        {
            AddTransfer(
                transactions,
                userId,
                accounts.BankAccount,
                accounts.CreditCard,
                currentCardBalance,
                InMonth(month, 2),
                today,
                "Credit card bill payment");
        }

        if (accounts.CashWallet.CurrentBalance < 1500m)
        {
            var topUpAmount = Math.Max(0m, 3500m - accounts.CashWallet.CurrentBalance);
            AddTransfer(
                transactions,
                userId,
                accounts.BankAccount,
                accounts.CashWallet,
                topUpAmount,
                InMonth(month, 4),
                today,
                "Cash wallet top-up");
        }

        AddTransaction(
            transactions,
            userId,
            accounts.BankAccount,
            categories["Salary"],
            TransactionType.Income,
            86000m + (monthIndex % 3) * 1250m,
            InMonth(month, 1),
            today,
            "Acme Labs",
            "Monthly salary",
            "bank-transfer",
            "salary",
            "fixed");

        if (monthIndex % 2 == 1)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Freelance"],
                TransactionType.Income,
                11500m + monthIndex * 425m,
                InMonth(month, 16),
                today,
                "Side Gig Co",
                "Freelance product work",
                "bank-transfer",
                "freelance");
        }

        if (monthIndex % 3 == 2)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Bonus"],
                TransactionType.Income,
                18000m + monthIndex * 500m,
                InMonth(month, 21),
                today,
                "Acme Labs",
                "Quarterly performance bonus",
                "bank-transfer",
                "bonus");
        }

        if (monthIndex % 4 == 0)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Investment"],
                TransactionType.Income,
                4200m + monthIndex * 175m,
                InMonth(month, 24),
                today,
                "HDFC Securities",
                "Dividend payout",
                "bank-transfer",
                "investment");
        }

        if (monthIndex % 4 == 1)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.CreditCard,
                categories["Refund"],
                TransactionType.Income,
                900m + monthIndex * 65m,
                InMonth(month, 14),
                today,
                "Amazon",
                "Order refund",
                "card",
                "refund");
        }

        AddTransaction(
            transactions,
            userId,
            accounts.BankAccount,
            categories["Rent"],
            TransactionType.Expense,
            28000m + (monthIndex >= 6 ? 1500m : 0m),
            InMonth(month, 3),
            today,
            "Landlord",
            "Apartment rent",
            "bank-transfer",
            "housing");

        AddTransaction(
            transactions,
            userId,
            accounts.BankAccount,
            categories["Utilities"],
            TransactionType.Expense,
            1900m + (monthIndex % 5) * 175m,
            InMonth(month, 6),
            today,
            "Tata Power",
            "Electricity bill",
            "upi",
            "utilities");

        AddTransaction(
            transactions,
            userId,
            accounts.BankAccount,
            categories["Utilities"],
            TransactionType.Expense,
            999m,
            InMonth(month, 18),
            today,
            "Airtel Fiber",
            "Internet bill",
            "auto-debit",
            "utilities");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Subscriptions"],
            TransactionType.Expense,
            649m,
            InMonth(month, 7),
            today,
            "Netflix",
            "Streaming subscription",
            "card",
            "subscription");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Subscriptions"],
            TransactionType.Expense,
            499m,
            InMonth(month, 9),
            today,
            "Spotify",
            "Music subscription",
            "card",
            "subscription");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Food"],
            TransactionType.Expense,
            2350m + (monthIndex % 3) * 120m,
            InMonth(month, 5),
            today,
            "Fresh Basket",
            "Groceries",
            "card",
            "groceries");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Food"],
            TransactionType.Expense,
            1740m + (monthIndex % 4) * 95m,
            InMonth(month, 12),
            today,
            "Fresh Basket",
            "Top-up groceries",
            "card",
            "groceries");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Food"],
            TransactionType.Expense,
            950m + (monthIndex % 5) * 70m,
            InMonth(month, 19),
            today,
            "Blue Tokai",
            "Coffee and snacks",
            "card",
            "food",
            "social");

        AddTransaction(
            transactions,
            userId,
            accounts.CashWallet,
            categories["Food"],
            TransactionType.Expense,
            520m + (monthIndex % 3) * 45m,
            InMonth(month, 26),
            today,
            "Street Bites",
            "Quick dinner",
            "cash",
            "food");

        AddTransaction(
            transactions,
            userId,
            accounts.CashWallet,
            categories["Transport"],
            TransactionType.Expense,
            240m + (monthIndex % 4) * 20m,
            InMonth(month, 8),
            today,
            "Metro",
            "Weekly commute",
            "cash",
            "transport");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Transport"],
            TransactionType.Expense,
            420m + (monthIndex % 5) * 35m,
            InMonth(month, 11),
            today,
            "Uber",
            "Office commute",
            "card",
            "transport");

        AddTransaction(
            transactions,
            userId,
            accounts.CashWallet,
            categories["Transport"],
            TransactionType.Expense,
            180m + (monthIndex % 3) * 25m,
            InMonth(month, 17),
            today,
            "Metro",
            "Commute recharge",
            "cash",
            "transport");

        AddTransaction(
            transactions,
            userId,
            accounts.CreditCard,
            categories["Transport"],
            TransactionType.Expense,
            560m + (monthIndex % 4) * 30m,
            InMonth(month, 23),
            today,
            "Ola",
            "Late night ride",
            "card",
            "transport");

        AddTransaction(
            transactions,
            userId,
            accounts.CashWallet,
            categories["Entertainment"],
            TransactionType.Expense,
            900m + (monthIndex % 4) * 140m,
            InMonth(month, 20),
            today,
            monthIndex % 2 == 0 ? "PVR Cinemas" : "BookMyShow",
            "Weekend entertainment",
            "cash",
            "entertainment");

        AddTransaction(
            transactions,
            userId,
            accounts.CashWallet,
            categories["Miscellaneous"],
            TransactionType.Expense,
            340m + (monthIndex % 5) * 40m,
            InMonth(month, 27),
            today,
            "Local Market",
            "Cash spends",
            "cash",
            "misc");

        if (monthIndex % 2 == 0)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.CreditCard,
                categories["Shopping"],
                TransactionType.Expense,
                2600m + monthIndex * 180m,
                InMonth(month, 22),
                today,
                monthIndex % 4 == 0 ? "Uniqlo" : "Amazon",
                "Planned shopping",
                "card",
                "shopping");
        }

        if (monthIndex % 3 == 1)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Health"],
                TransactionType.Expense,
                1850m + monthIndex * 110m,
                InMonth(month, 15),
                today,
                "Apollo Pharmacy",
                "Health and wellness",
                "upi",
                "health");
        }

        if (monthIndex is 2 or 7)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Education"],
                TransactionType.Expense,
                monthIndex == 2 ? 4800m : 7200m,
                InMonth(month, 13),
                today,
                monthIndex == 2 ? "Coursera" : "Udemy",
                "Skill upgrade",
                "card",
                "education");
        }

        if (monthIndex is 3 or 8 or 10)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Travel"],
                TransactionType.Expense,
                monthIndex == 8 ? 18400m : 9600m + monthIndex * 350m,
                InMonth(month, 25),
                today,
                monthIndex == 8 ? "IndiGo" : "MakeMyTrip",
                "Travel booking",
                "card",
                "travel");
        }

        if (monthIndex is 7 or 11)
        {
            AddTransaction(
                transactions,
                userId,
                accounts.BankAccount,
                categories["Gift"],
                TransactionType.Expense,
                monthIndex == 7 ? 2200m : 5400m,
                InMonth(month, 10),
                today,
                monthIndex == 7 ? "FlowerAura" : "Croma",
                "Gift purchase",
                "upi",
                "gift");
        }
    }

    private static IReadOnlyList<Budget> BuildBudgets(Guid userId, IReadOnlyDictionary<string, Category> categories, DateOnly today)
    {
        return new[]
        {
            new Budget
            {
                UserId = userId,
                CategoryId = categories["Food"].Id,
                Month = today.Month,
                Year = today.Year,
                Amount = 12000m,
                AlertThresholdPercent = 80
            },
            new Budget
            {
                UserId = userId,
                CategoryId = categories["Transport"].Id,
                Month = today.Month,
                Year = today.Year,
                Amount = 4500m,
                AlertThresholdPercent = 80
            },
            new Budget
            {
                UserId = userId,
                CategoryId = categories["Entertainment"].Id,
                Month = today.Month,
                Year = today.Year,
                Amount = 3500m,
                AlertThresholdPercent = 80
            },
            new Budget
            {
                UserId = userId,
                CategoryId = categories["Shopping"].Id,
                Month = today.Month,
                Year = today.Year,
                Amount = 8000m,
                AlertThresholdPercent = 85
            }
        };
    }

    private static Goal BuildGoal(Guid userId, Account linkedAccount, DateOnly today)
    {
        return new Goal
        {
            UserId = userId,
            Name = "Emergency Fund",
            TargetAmount = 300000m,
            CurrentAmount = 145000m,
            TargetDate = today.AddMonths(9),
            LinkedAccountId = linkedAccount.Id,
            Icon = "shield",
            Color = "#C49A3A",
            Status = GoalStatus.Active
        };
    }

    private static IReadOnlyList<RecurringTransaction> BuildRecurringTransactions(
        Guid userId,
        IReadOnlyDictionary<string, Category> categories,
        DemoAccounts accounts,
        DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        return new[]
        {
            new RecurringTransaction
            {
                UserId = userId,
                Title = "Netflix",
                Type = TransactionType.Expense,
                Amount = 649m,
                CategoryId = categories["Subscriptions"].Id,
                AccountId = accounts.CreditCard.Id,
                Frequency = RecurringFrequency.Monthly,
                StartDate = monthStart.AddMonths(-8),
                NextRunDate = today.AddDays(3),
                AutoCreateTransaction = true
            },
            new RecurringTransaction
            {
                UserId = userId,
                Title = "Rent",
                Type = TransactionType.Expense,
                Amount = 29500m,
                CategoryId = categories["Rent"].Id,
                AccountId = accounts.BankAccount.Id,
                Frequency = RecurringFrequency.Monthly,
                StartDate = monthStart.AddMonths(-8),
                NextRunDate = today.AddDays(5),
                AutoCreateTransaction = false
            },
            new RecurringTransaction
            {
                UserId = userId,
                Title = "Spotify",
                Type = TransactionType.Expense,
                Amount = 499m,
                CategoryId = categories["Subscriptions"].Id,
                AccountId = accounts.CreditCard.Id,
                Frequency = RecurringFrequency.Monthly,
                StartDate = monthStart.AddMonths(-6),
                NextRunDate = today.AddDays(8),
                AutoCreateTransaction = true
            }
        };
    }

    private static void AddTransaction(
        ICollection<Transaction> transactions,
        Guid userId,
        Account account,
        Category category,
        TransactionType type,
        decimal amount,
        DateOnly date,
        DateOnly today,
        string merchant,
        string note,
        string paymentMethod,
        params string[] tags)
    {
        if (date > today)
        {
            return;
        }

        transactions.Add(new Transaction
        {
            UserId = userId,
            AccountId = account.Id,
            CategoryId = category.Id,
            Type = type,
            Amount = amount,
            TransactionDate = date,
            Merchant = merchant,
            Note = note,
            PaymentMethod = paymentMethod,
            Tags = tags
        });

        account.CurrentBalance += type switch
        {
            TransactionType.Income => amount,
            TransactionType.Expense => -amount,
            _ => 0m
        };
        account.LastUpdatedAtUtc = DateTime.UtcNow;
    }

    private static void AddTransfer(
        ICollection<Transaction> transactions,
        Guid userId,
        Account sourceAccount,
        Account destinationAccount,
        decimal amount,
        DateOnly date,
        DateOnly today,
        string note)
    {
        if (amount <= 0 || date > today)
        {
            return;
        }

        var transferGroupId = Guid.NewGuid();
        transactions.Add(new Transaction
        {
            UserId = userId,
            AccountId = sourceAccount.Id,
            Type = TransactionType.Transfer,
            Amount = amount,
            TransactionDate = date,
            Merchant = destinationAccount.Name,
            Note = note,
            PaymentMethod = "transfer",
            TransferGroupId = transferGroupId,
            Tags = new[] { "transfer" }
        });
        transactions.Add(new Transaction
        {
            UserId = userId,
            AccountId = destinationAccount.Id,
            Type = TransactionType.Transfer,
            Amount = amount,
            TransactionDate = date,
            Merchant = sourceAccount.Name,
            Note = note,
            PaymentMethod = "transfer",
            TransferGroupId = transferGroupId,
            Tags = new[] { "transfer" }
        });

        sourceAccount.CurrentBalance -= amount;
        destinationAccount.CurrentBalance += amount;
        sourceAccount.LastUpdatedAtUtc = DateTime.UtcNow;
        destinationAccount.LastUpdatedAtUtc = DateTime.UtcNow;
    }

    private static DateOnly InMonth(DateOnly monthStart, int day)
    {
        var lastDay = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        return new DateOnly(monthStart.Year, monthStart.Month, Math.Min(day, lastDay));
    }

    private sealed record DemoAccounts(Account BankAccount, Account CreditCard, Account CashWallet)
    {
        public Account Bank => BankAccount;
    }
}
