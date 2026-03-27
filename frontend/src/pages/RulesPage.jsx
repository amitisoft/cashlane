import { useEffect, useMemo, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import {
  RULE_ACTION_OPTIONS,
  RULE_FIELD_OPTIONS,
  RULE_OPERATOR_OPTIONS
} from "../lib/select-options";
import { useToasts } from "../lib/toasts";

const emptyRuleForm = {
  id: null,
  isActive: true,
  priority: 100,
  field: "merchant",
  operator: "contains",
  conditionValue: "",
  actionType: "add_tag",
  actionValue: ""
};

const emptySimulation = {
  categoryId: "",
  amount: "",
  merchant: "",
  paymentMethod: "",
  tags: ""
};

export function RulesPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [accounts, setAccounts] = useState([]);
  const [scope, setScope] = useState("");
  const [rules, setRules] = useState([]);
  const [categories, setCategories] = useState([]);
  const [form, setForm] = useState(emptyRuleForm);
  const [simulation, setSimulation] = useState(emptySimulation);
  const [simulationResult, setSimulationResult] = useState(null);

  const ownedSharedAccounts = useMemo(
    () => accounts.filter((account) => account.isShared && account.role === "Owner"),
    [accounts]
  );

  const scopeOptions = [
    { value: "", label: "Personal rules" },
    ...ownedSharedAccounts.map((account) => ({ value: account.id, label: `${account.name} shared rules` }))
  ];

  useEffect(() => {
    authorizedFetch("/accounts")
      .then(setAccounts)
      .catch((error) => {
        pushToast({ kind: "danger", title: "Rules unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  useEffect(() => {
    Promise.all([
      authorizedFetch("/rules", { params: { accountId: scope || "" } }),
      authorizedFetch("/categories", { params: { accountId: scope || "" } })
    ])
      .then(([ruleData, categoryData]) => {
        setRules(ruleData);
        setCategories(categoryData.filter((item) => !item.isArchived));
        setForm(emptyRuleForm);
        setSimulation((current) => ({ ...current, categoryId: "" }));
        setSimulationResult(null);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Rule scope failed", message: error.message });
      });
  }, [authorizedFetch, pushToast, scope]);

  const operatorOptions = RULE_OPERATOR_OPTIONS[form.field] || [];
  const categoryOptions = [{ value: "", label: "Select category" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];

  async function handleSubmit(event) {
    event.preventDefault();

    try {
      const payload = {
        accountId: scope || null,
        isActive: form.isActive,
        priority: Number(form.priority),
        condition: {
          field: form.field,
          operator: form.operator,
          value: form.conditionValue
        },
        action: {
          type: form.actionType,
          value: form.actionValue
        }
      };

      const saved = form.id
        ? await authorizedFetch(`/rules/${form.id}`, { method: "PUT", body: payload })
        : await authorizedFetch("/rules", { method: "POST", body: payload });

      setRules((current) =>
        form.id ? current.map((item) => (item.id === saved.id ? saved : item)) : [...current, saved]
      );
      setForm(emptyRuleForm);
      pushToast({ kind: "success", title: form.id ? "Rule updated" : "Rule created", message: "Rule saved successfully." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Rule save failed", message: error.message });
    }
  }

  function startEdit(rule) {
    setForm({
      id: rule.id,
      isActive: rule.isActive,
      priority: rule.priority,
      field: rule.condition.field,
      operator: rule.condition.operator,
      conditionValue: rule.condition.value,
      actionType: rule.action.type,
      actionValue: rule.action.value
    });
    setSimulationResult(null);
  }

  async function handleDelete(ruleId) {
    try {
      await authorizedFetch(`/rules/${ruleId}`, { method: "DELETE" });
      setRules((current) => current.filter((item) => item.id !== ruleId));
      if (form.id === ruleId) {
        setForm(emptyRuleForm);
      }
      pushToast({ kind: "success", title: "Rule deleted", message: "Rule removed." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Delete failed", message: error.message });
    }
  }

  async function toggleActive(rule) {
    try {
      const saved = await authorizedFetch(`/rules/${rule.id}`, {
        method: "PUT",
        body: {
          accountId: scope || null,
          isActive: !rule.isActive,
          priority: rule.priority,
          condition: rule.condition,
          action: rule.action
        }
      });
      setRules((current) => current.map((item) => (item.id === saved.id ? saved : item)));
    } catch (error) {
      pushToast({ kind: "danger", title: "Toggle failed", message: error.message });
    }
  }

  async function runSimulation(event) {
    event.preventDefault();

    try {
      const result = await authorizedFetch("/rules/simulate", {
        method: "POST",
        body: {
          accountId: scope || null,
          categoryId: simulation.categoryId || null,
          amount: Number(simulation.amount || 0),
          merchant: simulation.merchant,
          paymentMethod: simulation.paymentMethod,
          tags: simulation.tags
            ? simulation.tags.split(",").map((item) => item.trim()).filter(Boolean)
            : []
        }
      });
      setSimulationResult(result);
    } catch (error) {
      pushToast({ kind: "danger", title: "Simulation failed", message: error.message });
    }
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Rules</span>
            <h2>Automatic categorization and tagging</h2>
          </div>
        </header>
        <div className="filters-grid">
          <SelectField ariaLabel="Rule scope" options={scopeOptions} value={scope} onChange={setScope} />
        </div>
        <div className="list-stack">
          {rules.length === 0 ? (
            <p className="panel-empty">No rules exist for this scope yet.</p>
          ) : (
            rules.map((rule) => (
              <article key={rule.id} className="detail-card">
                <div className="metric-row">
                  <strong>
                    {rule.condition.field} {rule.condition.operator} {rule.condition.value}
                  </strong>
                  <span>Priority {rule.priority}</span>
                </div>
                <p>
                  {rule.action.type} {rule.action.value}
                </p>
                <div className="inline-actions">
                  <button type="button" className="ghost-button" onClick={() => startEdit(rule)}>
                    Edit
                  </button>
                  <button type="button" className="ghost-button" onClick={() => toggleActive(rule)}>
                    {rule.isActive ? "Disable" : "Enable"}
                  </button>
                  <button type="button" className="ghost-button" onClick={() => handleDelete(rule.id)}>
                    Delete
                  </button>
                </div>
              </article>
            ))
          )}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">{form.id ? "Edit rule" : "New rule"}</span>
            <h2>{form.id ? "Change rule logic" : "Create an automation rule"}</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={handleSubmit}>
          <label className="field">
            <span>Priority</span>
            <input type="number" value={form.priority} onChange={(event) => setForm((current) => ({ ...current, priority: event.target.value }))} />
          </label>
          <label className="field">
            <span>Condition field</span>
            <SelectField ariaLabel="Rule field" options={RULE_FIELD_OPTIONS} value={form.field} onChange={(nextValue) => setForm((current) => ({ ...current, field: nextValue, operator: RULE_OPERATOR_OPTIONS[nextValue][0].value, conditionValue: "" }))} />
          </label>
          <label className="field">
            <span>Operator</span>
            <SelectField ariaLabel="Rule operator" options={operatorOptions} value={form.operator} onChange={(nextValue) => setForm((current) => ({ ...current, operator: nextValue }))} />
          </label>
          <label className="field">
            <span>Condition value</span>
            {form.field === "categoryId" ? (
              <SelectField ariaLabel="Rule condition category" options={categoryOptions} value={form.conditionValue} onChange={(nextValue) => setForm((current) => ({ ...current, conditionValue: nextValue }))} />
            ) : (
              <input value={form.conditionValue} onChange={(event) => setForm((current) => ({ ...current, conditionValue: event.target.value }))} />
            )}
          </label>
          <label className="field">
            <span>Action</span>
            <SelectField ariaLabel="Rule action" options={RULE_ACTION_OPTIONS} value={form.actionType} onChange={(nextValue) => setForm((current) => ({ ...current, actionType: nextValue, actionValue: "" }))} />
          </label>
          <label className="field">
            <span>Action value</span>
            {form.actionType === "set_category" ? (
              <SelectField ariaLabel="Rule action category" options={categoryOptions} value={form.actionValue} onChange={(nextValue) => setForm((current) => ({ ...current, actionValue: nextValue }))} />
            ) : (
              <input value={form.actionValue} onChange={(event) => setForm((current) => ({ ...current, actionValue: event.target.value }))} />
            )}
          </label>
          <label className="field checkbox-field">
            <input type="checkbox" checked={form.isActive} onChange={(event) => setForm((current) => ({ ...current, isActive: event.target.checked }))} />
            <span>Rule is active</span>
          </label>
          <div className="inline-actions">
            <button className="primary-button" type="submit">
              {form.id ? "Save changes" : "Create rule"}
            </button>
            {form.id && (
              <button type="button" className="ghost-button" onClick={() => setForm(emptyRuleForm)}>
                Cancel edit
              </button>
            )}
          </div>
        </form>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Simulation</span>
            <h2>Test the current scope rules</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={runSimulation}>
          <label className="field">
            <span>Category</span>
            <SelectField ariaLabel="Simulation category" options={categoryOptions} value={simulation.categoryId} onChange={(nextValue) => setSimulation((current) => ({ ...current, categoryId: nextValue }))} />
          </label>
          <label className="field">
            <span>Amount</span>
            <input type="number" value={simulation.amount} onChange={(event) => setSimulation((current) => ({ ...current, amount: event.target.value }))} />
          </label>
          <label className="field">
            <span>Merchant</span>
            <input value={simulation.merchant} onChange={(event) => setSimulation((current) => ({ ...current, merchant: event.target.value }))} />
          </label>
          <label className="field">
            <span>Payment method</span>
            <input value={simulation.paymentMethod} onChange={(event) => setSimulation((current) => ({ ...current, paymentMethod: event.target.value }))} />
          </label>
          <label className="field">
            <span>Tags</span>
            <input value={simulation.tags} onChange={(event) => setSimulation((current) => ({ ...current, tags: event.target.value }))} placeholder="comma,separated,tags" />
          </label>
          <button className="primary-button" type="submit">
            Run simulation
          </button>
        </form>
        {simulationResult && (
          <div className="list-stack top-gap">
            <div className="detail-card">
              <strong>Category result</strong>
              <p>{categories.find((category) => category.id === simulationResult.categoryId)?.name || "Unchanged"}</p>
            </div>
            <div className="detail-card">
              <strong>Tags</strong>
              <p>{simulationResult.tags.length > 0 ? simulationResult.tags.join(", ") : "No tags added."}</p>
            </div>
            <div className="detail-card">
              <strong>Alerts</strong>
              <p>{simulationResult.alerts.length > 0 ? simulationResult.alerts.join(", ") : "No alerts triggered."}</p>
            </div>
          </div>
        )}
      </section>
    </div>
  );
}
