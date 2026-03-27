export const ACCOUNT_TYPE_OPTIONS = [
  { value: "BankAccount", label: "Bank account" },
  { value: "CreditCard", label: "Credit card" },
  { value: "CashWallet", label: "Cash wallet" },
  { value: "SavingsAccount", label: "Savings account" }
];

export const CATEGORY_TYPE_OPTIONS = [
  { value: "Expense", label: "Expense" },
  { value: "Income", label: "Income" }
];

export const REPORT_TRANSACTION_TYPE_OPTIONS = [
  { value: "", label: "All types" },
  { value: "Expense", label: "Expense" },
  { value: "Income", label: "Income" },
  { value: "Transfer", label: "Transfer" }
];

export const RECURRING_FREQUENCY_OPTIONS = [
  { value: "Daily", label: "Daily" },
  { value: "Weekly", label: "Weekly" },
  { value: "Monthly", label: "Monthly" },
  { value: "Yearly", label: "Yearly" }
];

export const ACCOUNT_ROLE_OPTIONS = [
  { value: "Viewer", label: "Viewer" },
  { value: "Editor", label: "Editor" },
  { value: "Owner", label: "Owner" }
];

export const MANAGEABLE_ACCOUNT_ROLE_OPTIONS = ACCOUNT_ROLE_OPTIONS.filter((option) => option.value !== "Owner");

export const RULE_FIELD_OPTIONS = [
  { value: "merchant", label: "Merchant" },
  { value: "amount", label: "Amount" },
  { value: "categoryId", label: "Category" },
  { value: "paymentMethod", label: "Payment method" }
];

export const RULE_OPERATOR_OPTIONS = {
  merchant: [
    { value: "equals", label: "Equals" },
    { value: "contains", label: "Contains" }
  ],
  paymentMethod: [
    { value: "equals", label: "Equals" },
    { value: "contains", label: "Contains" }
  ],
  amount: [
    { value: "equals", label: "Equals" },
    { value: "greaterThan", label: "Greater than" },
    { value: "lessThan", label: "Less than" }
  ],
  categoryId: [{ value: "equals", label: "Equals" }]
};

export const RULE_ACTION_OPTIONS = [
  { value: "set_category", label: "Set category" },
  { value: "add_tag", label: "Add tag" },
  { value: "trigger_alert", label: "Trigger alert" }
];
