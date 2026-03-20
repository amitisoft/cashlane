import { useEffect, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { CATEGORY_TYPE_OPTIONS, RECURRING_FREQUENCY_OPTIONS } from "../lib/select-options";
import { useToasts } from "../lib/toasts";
import { formatCurrency } from "../components/charts";

const emptyForm = {
  id: null,
  title: "",
  type: "Expense",
  amount: "",
  categoryId: "",
  accountId: "",
  frequency: "Monthly",
  startDate: new Date().toISOString().slice(0, 10),
  endDate: "",
  nextRunDate: new Date().toISOString().slice(0, 10),
  autoCreateTransaction: true,
  isPaused: false
};

export function RecurringPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [items, setItems] = useState([]);
  const [accounts, setAccounts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [form, setForm] = useState(emptyForm);
  const accountOptions = [{ value: "", label: "Select account" }, ...accounts.map((account) => ({ value: account.id, label: account.name }))];
  const categoryOptions = [
    { value: "", label: "Select category" },
    ...categories
      .filter((item) => item.type === form.type)
      .map((category) => ({ value: category.id, label: category.name }))
  ];

  useEffect(() => {
    Promise.all([authorizedFetch("/recurring"), authorizedFetch("/accounts"), authorizedFetch("/categories")])
      .then(([itemData, accountData, categoryData]) => {
        setItems(itemData);
        setAccounts(accountData);
        setCategories(categoryData.filter((item) => !item.isArchived));
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Recurring unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  async function handleSave(event) {
    event.preventDefault();

    try {
      const payload = {
        ...form,
        amount: Number(form.amount),
        endDate: form.endDate || null
      };

      const saved = form.id
        ? await authorizedFetch(`/recurring/${form.id}`, { method: "PUT", body: payload })
        : await authorizedFetch("/recurring", { method: "POST", body: payload });

      setItems((current) =>
        form.id ? current.map((item) => (item.id === saved.id ? saved : item)) : [...current, saved]
      );
      setForm(emptyForm);
      pushToast({
        kind: "success",
        title: form.id ? "Recurring updated" : "Recurring saved",
        message: form.id ? "Recurring item updated." : "Upcoming bill added."
      });
    } catch (error) {
      pushToast({ kind: "danger", title: "Save failed", message: error.message });
    }
  }

  function startEdit(item) {
    setForm({
      id: item.id,
      title: item.title,
      type: item.type,
      amount: item.amount,
      categoryId: item.categoryId || "",
      accountId: item.accountId || "",
      frequency: item.frequency,
      startDate: item.startDate,
      endDate: item.endDate || "",
      nextRunDate: item.nextRunDate,
      autoCreateTransaction: item.autoCreateTransaction,
      isPaused: item.isPaused
    });
  }

  async function handleTogglePause(item) {
    try {
      const updated = await authorizedFetch(`/recurring/${item.id}`, {
        method: "PUT",
        body: { ...item, endDate: item.endDate || null, isPaused: !item.isPaused }
      });
      setItems((current) => current.map((entry) => (entry.id === updated.id ? updated : entry)));
      if (form.id === item.id) {
        setForm((current) => ({ ...current, isPaused: updated.isPaused }));
      }
      pushToast({
        kind: "success",
        title: updated.isPaused ? "Recurring paused" : "Recurring resumed",
        message: `${updated.title} has been ${updated.isPaused ? "paused" : "resumed"}.`
      });
    } catch (error) {
      pushToast({ kind: "danger", title: "Update failed", message: error.message });
    }
  }

  async function handleDelete(id) {
    try {
      await authorizedFetch(`/recurring/${id}`, { method: "DELETE" });
      setItems((current) => current.filter((item) => item.id !== id));
      if (form.id === id) {
        setForm(emptyForm);
      }
      pushToast({ kind: "success", title: "Recurring deleted", message: "Recurring item removed." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Delete failed", message: error.message });
    }
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Recurring</span>
            <h2>Upcoming bills and subscriptions</h2>
          </div>
        </header>
        <div className="list-stack">
          {items.map((item) => (
            <div key={item.id} className="recurring-card">
              <div className="recurring-card-header">
                <div className="recurring-card-copy">
                  <strong>{item.title}</strong>
                  <span>
                    {item.frequency} - {item.nextRunDate}
                    {item.isPaused ? " - Paused" : ""}
                  </span>
                </div>
                <strong className="recurring-card-amount">{formatCurrency(item.amount)}</strong>
              </div>
              <div className="inline-actions">
                <button type="button" className="ghost-button" onClick={() => startEdit(item)}>
                  Edit
                </button>
                <button type="button" className="ghost-button" onClick={() => handleTogglePause(item)}>
                  {item.isPaused ? "Resume" : "Pause"}
                </button>
                <button type="button" className="ghost-button" onClick={() => handleDelete(item.id)}>
                  Delete
                </button>
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="panel recurring-editor-panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">{form.id ? "Edit recurring" : "New recurring"}</span>
            <h2>{form.id ? "Update this schedule" : "Add a subscription or bill"}</h2>
          </div>
        </header>
        <form className="recurring-form" onSubmit={handleSave}>
          <label className="field field-wide">
            <span>Title</span>
            <input value={form.title} onChange={(event) => setForm((current) => ({ ...current, title: event.target.value }))} />
          </label>
          <label className="field">
            <span>Type</span>
            <SelectField
              ariaLabel="Recurring transaction type"
              options={CATEGORY_TYPE_OPTIONS}
              value={form.type}
              onChange={(nextValue) => setForm((current) => ({ ...current, type: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Amount</span>
            <input type="number" value={form.amount} onChange={(event) => setForm((current) => ({ ...current, amount: event.target.value }))} />
          </label>
          <label className="field">
            <span>Account</span>
            <SelectField
              ariaLabel="Recurring account"
              options={accountOptions}
              value={form.accountId}
              onChange={(nextValue) => setForm((current) => ({ ...current, accountId: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Category</span>
            <SelectField
              ariaLabel="Recurring category"
              options={categoryOptions}
              value={form.categoryId}
              onChange={(nextValue) => setForm((current) => ({ ...current, categoryId: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Frequency</span>
            <SelectField
              ariaLabel="Recurring frequency"
              options={RECURRING_FREQUENCY_OPTIONS}
              value={form.frequency}
              onChange={(nextValue) => setForm((current) => ({ ...current, frequency: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Start date</span>
            <input type="date" value={form.startDate} onChange={(event) => setForm((current) => ({ ...current, startDate: event.target.value }))} />
          </label>
          <label className="field">
            <span>Next run date</span>
            <input type="date" value={form.nextRunDate} onChange={(event) => setForm((current) => ({ ...current, nextRunDate: event.target.value }))} />
          </label>
          <label className="field">
            <span>End date</span>
            <input type="date" value={form.endDate} onChange={(event) => setForm((current) => ({ ...current, endDate: event.target.value }))} />
          </label>
          <div className="recurring-form-options">
            <label className="field checkbox-field recurring-toggle">
              <input
                type="checkbox"
                checked={form.autoCreateTransaction}
                onChange={(event) => setForm((current) => ({ ...current, autoCreateTransaction: event.target.checked }))}
              />
              <span>Auto-create transactions</span>
            </label>
            <label className="field checkbox-field recurring-toggle">
              <input
                type="checkbox"
                checked={form.isPaused}
                onChange={(event) => setForm((current) => ({ ...current, isPaused: event.target.checked }))}
              />
              <span>Paused</span>
            </label>
          </div>
          <div className="inline-actions recurring-form-actions">
            <button className="primary-button" type="submit">
              {form.id ? "Save changes" : "Save recurring item"}
            </button>
            {form.id && (
              <button type="button" className="ghost-button" onClick={() => setForm(emptyForm)}>
                Cancel edit
              </button>
            )}
          </div>
        </form>
      </section>
    </div>
  );
}
