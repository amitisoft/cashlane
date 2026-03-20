import { useEffect, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";
import { formatCurrency } from "../components/charts";

const emptyForm = {
  id: null,
  name: "",
  targetAmount: "",
  targetDate: "",
  linkedAccountId: "",
  icon: "target",
  color: "#C49A3A",
  status: "Active"
};

export function GoalsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [goals, setGoals] = useState([]);
  const [accounts, setAccounts] = useState([]);
  const [form, setForm] = useState(emptyForm);
  const linkedAccountOptions = [{ value: "", label: "None" }, ...accounts.map((account) => ({ value: account.id, label: account.name }))];

  useEffect(() => {
    Promise.all([authorizedFetch("/goals"), authorizedFetch("/accounts")])
      .then(([goalData, accountData]) => {
        setGoals(goalData);
        setAccounts(accountData);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Goals unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  async function handleSave(event) {
    event.preventDefault();

    try {
      const payload = {
        name: form.name,
        targetAmount: Number(form.targetAmount),
        targetDate: form.targetDate || null,
        linkedAccountId: form.linkedAccountId || null,
        icon: form.icon,
        color: form.color,
        status: form.status
      };

      const saved = form.id
        ? await authorizedFetch(`/goals/${form.id}`, { method: "PUT", body: payload })
        : await authorizedFetch("/goals", { method: "POST", body: payload });

      setGoals((current) =>
        form.id ? current.map((item) => (item.id === saved.id ? saved : item)) : [...current, saved]
      );
      setForm(emptyForm);
      pushToast({
        kind: "success",
        title: form.id ? "Goal updated" : "Goal saved",
        message: form.id ? "Goal details updated." : "Goal added to your roadmap."
      });
    } catch (error) {
      pushToast({ kind: "danger", title: "Save failed", message: error.message });
    }
  }

  async function handleMove(goal, mode) {
    const amount = window.prompt(`Enter ${mode} amount for ${goal.name}`);
    if (!amount) return;

    try {
      const updated = await authorizedFetch(`/goals/${goal.id}/${mode}`, {
        method: "POST",
        body: { amount: Number(amount), accountId: goal.linkedAccountId || null }
      });
      setGoals((current) => current.map((item) => (item.id === goal.id ? updated : item)));
      pushToast({
        kind: updated.status === "Completed" ? "success" : "success",
        title: updated.status === "Completed" ? "Goal reached" : mode === "contribute" ? "Contribution added" : "Withdrawal recorded",
        message: updated.status === "Completed" ? `${goal.name} hit its target.` : `${goal.name} updated.`
      });
    } catch (error) {
      pushToast({ kind: "danger", title: "Update failed", message: error.message });
    }
  }

  function startEdit(goal) {
    setForm({
      id: goal.id,
      name: goal.name,
      targetAmount: goal.targetAmount,
      targetDate: goal.targetDate || "",
      linkedAccountId: goal.linkedAccountId || "",
      icon: goal.icon,
      color: goal.color,
      status: goal.status
    });
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Goals</span>
            <h2>Savings that feel visible and motivating</h2>
          </div>
        </header>
        <div className="goal-grid">
          {goals.map((goal) => (
            <article key={goal.id} className="goal-tile">
              <span className="goal-icon">{goal.icon}</span>
              <strong>{goal.name}</strong>
              <span>
                {formatCurrency(goal.currentAmount)} / {formatCurrency(goal.targetAmount)}
              </span>
              <div className="progress-rail">
                <div style={{ width: `${Math.min(goal.progressPercent, 100)}%` }} />
              </div>
              <small>
                {goal.progressPercent}% funded
                {goal.targetDate ? ` · Due ${goal.targetDate}` : ""}
                {goal.status === "Completed" ? " · Completed" : ""}
              </small>
              <div className="inline-actions">
                <button type="button" className="ghost-button" onClick={() => handleMove(goal, "contribute")}>
                  Add
                </button>
                <button type="button" className="ghost-button" onClick={() => handleMove(goal, "withdraw")}>
                  Withdraw
                </button>
                <button type="button" className="ghost-button" onClick={() => startEdit(goal)}>
                  Edit
                </button>
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">{form.id ? "Edit goal" : "New goal"}</span>
            <h2>{form.id ? "Update this savings target" : "Add a savings target"}</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={handleSave}>
          <label className="field">
            <span>Name</span>
            <input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} />
          </label>
          <label className="field">
            <span>Target amount</span>
            <input type="number" value={form.targetAmount} onChange={(event) => setForm((current) => ({ ...current, targetAmount: event.target.value }))} />
          </label>
          <label className="field">
            <span>Target date</span>
            <input type="date" value={form.targetDate} onChange={(event) => setForm((current) => ({ ...current, targetDate: event.target.value }))} />
          </label>
          <label className="field">
            <span>Linked account</span>
            <SelectField
              ariaLabel="Linked account"
              options={linkedAccountOptions}
              value={form.linkedAccountId}
              onChange={(nextValue) => setForm((current) => ({ ...current, linkedAccountId: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Icon</span>
            <input value={form.icon} onChange={(event) => setForm((current) => ({ ...current, icon: event.target.value }))} />
          </label>
          <label className="field">
            <span>Color</span>
            <input value={form.color} onChange={(event) => setForm((current) => ({ ...current, color: event.target.value }))} />
          </label>
          <div className="inline-actions">
            <button className="primary-button" type="submit">
              {form.id ? "Save changes" : "Save goal"}
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
