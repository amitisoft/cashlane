import { useEffect, useMemo, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";
import { formatCurrency } from "../components/charts";

export function BudgetsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [budgets, setBudgets] = useState([]);
  const [accounts, setAccounts] = useState([]);
  const [categories, setCategories] = useState([]);
  const today = new Date();
  const [form, setForm] = useState({
    categoryId: "",
    accountId: "",
    month: today.getMonth() + 1,
    year: today.getFullYear(),
    amount: "",
    alertThresholdPercent: 80
  });

  const selectedAccount = useMemo(
    () => accounts.find((account) => account.id === form.accountId) || null,
    [accounts, form.accountId]
  );
  const canManageSelectedScope = !selectedAccount || selectedAccount.role === "Owner";
  const sharedAccounts = accounts.filter((account) => account.isShared);
  const scopeOptions = [{ value: "", label: "Personal budgets" }, ...sharedAccounts.map((account) => ({ value: account.id, label: account.name }))];
  const categoryOptions = [{ value: "", label: "Select category" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];

  useEffect(() => {
    Promise.all([
      authorizedFetch("/accounts"),
      authorizedFetch("/budgets", {
        params: { accountId: form.accountId, month: form.month, year: form.year }
      }),
      authorizedFetch("/categories", { params: { accountId: selectedAccount?.isShared ? selectedAccount.id : "" } })
    ])
      .then(([accountData, budgetData, categoryData]) => {
        setAccounts(accountData);
        setBudgets(budgetData);
        setCategories(categoryData.filter((item) => item.type === "Expense" && !item.isArchived));
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Budgets unavailable", message: error.message });
      });
  }, [authorizedFetch, form.accountId, form.month, form.year, pushToast, selectedAccount?.id, selectedAccount?.isShared]);

  async function handleCreate(event) {
    event.preventDefault();
    try {
      const created = await authorizedFetch("/budgets", {
        method: "POST",
        body: {
          ...form,
          accountId: form.accountId || null,
          amount: Number(form.amount),
          month: Number(form.month),
          year: Number(form.year),
          alertThresholdPercent: Number(form.alertThresholdPercent)
        }
      });
      setBudgets((current) => [...current.filter((item) => item.id !== created.id), created]);
      setForm((current) => ({ ...current, amount: "" }));
      pushToast({ kind: "success", title: "Budget saved", message: "Budget added for this month." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Save failed", message: error.message });
    }
  }

  async function duplicateLastMonth() {
    try {
      const duplicated = await authorizedFetch("/budgets/duplicate-last-month", {
        method: "POST",
        body: { accountId: form.accountId || null, month: Number(form.month), year: Number(form.year) }
      });
      setBudgets(duplicated);
      pushToast({ kind: "success", title: "Budgets duplicated", message: "Last month copied forward." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Duplicate failed", message: error.message });
    }
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Budgets</span>
            <h2>Monthly control with threshold visibility</h2>
          </div>
        </header>
        <div className="filters-grid">
          <SelectField ariaLabel="Budget scope" options={scopeOptions} value={form.accountId} onChange={(nextValue) => setForm((current) => ({ ...current, accountId: nextValue, categoryId: "" }))} />
          <input type="number" min="1" max="12" value={form.month} onChange={(event) => setForm((current) => ({ ...current, month: event.target.value }))} />
          <input type="number" value={form.year} onChange={(event) => setForm((current) => ({ ...current, year: event.target.value }))} />
        </div>
        <div className="list-stack">
          {budgets.length === 0 ? (
            <p className="panel-empty">No budgets exist for this month and scope.</p>
          ) : (
            budgets.map((budget) => (
              <div key={budget.id} className="budget-card">
                <div className="budget-copy">
                  <strong>{budget.categoryName}</strong>
                  <span>
                    {formatCurrency(budget.spentAmount)} / {formatCurrency(budget.amount)}
                  </span>
                </div>
                {budget.accountName && <small>{budget.accountName}</small>}
                <div className="progress-rail">
                  <div style={{ width: `${Math.min(budget.usedPercent, 100)}%` }} />
                </div>
                <small>{budget.usedPercent}% used</small>
              </div>
            ))
          )}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">New Budget</span>
            <h2>Set category limits</h2>
          </div>
          <button type="button" className="ghost-button" onClick={duplicateLastMonth} disabled={!canManageSelectedScope}>
            Duplicate last month
          </button>
        </header>
        <form className="stacked-form" onSubmit={handleCreate}>
          <label className="field">
            <span>Category</span>
            <SelectField ariaLabel="Budget category" options={categoryOptions} value={form.categoryId} onChange={(nextValue) => setForm((current) => ({ ...current, categoryId: nextValue }))} disabled={!canManageSelectedScope} />
          </label>
          <label className="field">
            <span>Amount</span>
            <input type="number" value={form.amount} onChange={(event) => setForm((current) => ({ ...current, amount: event.target.value }))} disabled={!canManageSelectedScope} />
          </label>
          <button className="primary-button" type="submit" disabled={!canManageSelectedScope}>
            Save budget
          </button>
          {!canManageSelectedScope && <p className="panel-empty">Only account owners can manage shared budgets.</p>}
        </form>
      </section>
    </div>
  );
}
