namespace Cashlane.Api.Domain.Enums;

public enum AccountType
{
    BankAccount = 1,
    CreditCard = 2,
    CashWallet = 3,
    SavingsAccount = 4
}

public enum CategoryType
{
    Expense = 1,
    Income = 2
}

public enum TransactionType
{
    Expense = 1,
    Income = 2,
    Transfer = 3
}

public enum GoalStatus
{
    Active = 1,
    Completed = 2,
    Archived = 3
}

public enum RecurringFrequency
{
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Yearly = 4
}
