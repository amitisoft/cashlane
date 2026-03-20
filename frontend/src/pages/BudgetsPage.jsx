import { useEffect, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";
import { formatCurrency } from "../components/charts";

export function BudgetsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [budgets, setBudgets] = useState([]);
  const [categories, setCategories] = useState([]);
  const today = new Date();
  const [form, setForm] = useState({
    categoryId: "",
    month: today.getMonth() + 1,
    year: today.getFullYear(),
    amount: "",
    alertThresholdPercent: 80
  });
  const categoryOptions = [{ value: "", label: "Select category" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];

  useEffect(() => {
    Promise.all([
      authorizedFetch("/budgets", {
        params: { month: form.month, year: form.year }
      }),
      authorizedFetch("/categories")
    ])
      .then(([budgetData, categoryData]) => {
        setBudgets(budgetData);
        setCategories(categoryData.filter((item) => item.type === "Expense" && !item.isArchived));
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Budgets unavailable", message: error.message });
      });
  }, [authorizedFetch, form.month, form.year, pushToast]);

  async function handleCreate(event) {
    event.preventDefault();
    try {
      const created = await authorizedFetch("/budgets", {
        method: "POST",
        body: {
          ...form,
          amount: Number(form.amount),
          month: Number(form.month),
          year: Number(form.year),
          alertThresholdPercent: Number(form.alertThresholdPercent)
        }
      });
      setBudgets((current) => [...current, created]);
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
        body: { month: Number(form.month), year: Number(form.year) }
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
        <div className="list-stack">
          {budgets.map((budget) => (
            <div key={budget.id} className="budget-card">
              <div className="budget-copy">
                <strong>{budget.categoryName}</strong>
                <span>
                  {formatCurrency(budget.spentAmount)} / {formatCurrency(budget.amount)}
                </span>
              </div>
              <div className="progress-rail">
                <div style={{ width: `${Math.min(budget.usedPercent, 100)}%` }} />
              </div>
              <small>{budget.usedPercent}% used</small>
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">New Budget</span>
            <h2>Set category limits</h2>
          </div>
          <button type="button" className="ghost-button" onClick={duplicateLastMonth}>
            Duplicate last month
          </button>
        </header>
        <form className="stacked-form" onSubmit={handleCreate}>
          <label className="field">
            <span>Category</span>
            <SelectField
              ariaLabel="Budget category"
              options={categoryOptions}
              value={form.categoryId}
              onChange={(nextValue) => setForm((current) => ({ ...current, categoryId: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Amount</span>
            <input type="number" value={form.amount} onChange={(event) => setForm((current) => ({ ...current, amount: event.target.value }))} />
          </label>
          <button className="primary-button" type="submit">
            Save budget
          </button>
        </form>
      </section>
    </div>
  );
}
