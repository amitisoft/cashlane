import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { ACCOUNT_TYPE_OPTIONS } from "../lib/select-options";
import { useToasts } from "../lib/toasts";

const emptyAccountForm = {
  name: "",
  type: "BankAccount",
  openingBalance: "",
  institutionName: ""
};

export function OnboardingPage() {
  const { authorizedFetch, logout, updateUser, user } = useAuth();
  const { pushToast } = useToasts();
  const navigate = useNavigate();
  const today = new Date();
  const [step, setStep] = useState("account");
  const [categories, setCategories] = useState([]);
  const [isSaving, setSaving] = useState(false);
  const [accountForm, setAccountForm] = useState(emptyAccountForm);
  const [budgetForm, setBudgetForm] = useState({
    categoryId: "",
    month: today.getMonth() + 1,
    year: today.getFullYear(),
    amount: "",
    alertThresholdPercent: 80
  });
  const budgetCategoryOptions = [{ value: "", label: "Select category" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];

  useEffect(() => {
    if (step !== "budget") {
      return;
    }

    authorizedFetch("/categories")
      .then((items) => setCategories(items.filter((item) => item.type === "Expense" && !item.isArchived)))
      .catch((error) => {
        pushToast({ kind: "danger", title: "Categories unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast, step]);

  async function saveAccount(event) {
    event.preventDefault();
    setSaving(true);

    try {
      await authorizedFetch("/accounts", {
        method: "POST",
        body: {
          ...accountForm,
          openingBalance: Number(accountForm.openingBalance || 0)
        }
      });

      setStep("budget");
      pushToast({ kind: "success", title: "First account ready", message: "Set a first budget or skip to the dashboard." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Account setup failed", message: error.message });
    } finally {
      setSaving(false);
    }
  }

  async function saveBudget(event) {
    event.preventDefault();
    setSaving(true);

    try {
      await authorizedFetch("/budgets", {
        method: "POST",
        body: {
          ...budgetForm,
          month: Number(budgetForm.month),
          year: Number(budgetForm.year),
          amount: Number(budgetForm.amount),
          alertThresholdPercent: Number(budgetForm.alertThresholdPercent)
        }
      });

      completeOnboarding("First budget saved. Your dashboard is ready.");
    } catch (error) {
      pushToast({ kind: "danger", title: "Budget setup failed", message: error.message });
    } finally {
      setSaving(false);
    }
  }

  function completeOnboarding(message) {
    updateUser({ needsOnboarding: false });
    pushToast({ kind: "success", title: "Workspace ready", message });
    navigate("/dashboard", { replace: true });
  }

  return (
    <div className="onboarding-shell">
      <div className="onboarding-topbar">
        <div className="profile-pill">
          <div>
            <span>{user?.displayName}</span>
          </div>
          <button type="button" className="ghost-button" onClick={logout}>
            Logout
          </button>
        </div>
      </div>

      <div className="onboarding-stack">
        <section className="onboarding-card">
          <span className="eyebrow">Onboarding</span>
          <h1>Set up your first account, then decide whether to start with a budget.</h1>
          <p>
            Cashlane already has your categories and core workspace waiting. This flow only asks for the
            first details needed to unlock the full dashboard.
          </p>

          <div className="onboarding-steps">
            <span className={step === "account" ? "is-active" : "is-complete"}>1. First account</span>
            <span className={step === "budget" ? "is-active" : ""}>2. Optional budget</span>
          </div>

          {step === "account" ? (
            <form className="stacked-form" onSubmit={saveAccount}>
              <label className="field">
                <span>Account name</span>
                <input
                  required
                  value={accountForm.name}
                  onChange={(event) => setAccountForm((current) => ({ ...current, name: event.target.value }))}
                  placeholder="Main salary account"
                />
              </label>
              <label className="field">
                <span>Account type</span>
                <SelectField
                  ariaLabel="Onboarding account type"
                  options={ACCOUNT_TYPE_OPTIONS}
                  value={accountForm.type}
                  onChange={(nextValue) => setAccountForm((current) => ({ ...current, type: nextValue }))}
                />
              </label>
              <label className="field">
                <span>Opening balance</span>
                <input
                  type="number"
                  value={accountForm.openingBalance}
                  onChange={(event) => setAccountForm((current) => ({ ...current, openingBalance: event.target.value }))}
                  placeholder="0"
                />
              </label>
              <label className="field">
                <span>Institution name</span>
                <input
                  value={accountForm.institutionName}
                  onChange={(event) => setAccountForm((current) => ({ ...current, institutionName: event.target.value }))}
                  placeholder="HDFC Bank"
                />
              </label>
              <button className="primary-button" type="submit" disabled={isSaving}>
                {isSaving ? "Saving..." : "Save first account"}
              </button>
            </form>
          ) : (
            <form className="stacked-form" onSubmit={saveBudget}>
              <label className="field">
                <span>Expense category</span>
                <SelectField
                  ariaLabel="Onboarding budget category"
                  options={budgetCategoryOptions}
                  value={budgetForm.categoryId}
                  onChange={(nextValue) => setBudgetForm((current) => ({ ...current, categoryId: nextValue }))}
                />
              </label>
              <label className="field">
                <span>Monthly budget</span>
                <input
                  type="number"
                  required
                  value={budgetForm.amount}
                  onChange={(event) => setBudgetForm((current) => ({ ...current, amount: event.target.value }))}
                  placeholder="15000"
                />
              </label>
              <div className="inline-actions">
                <button className="primary-button" type="submit" disabled={isSaving}>
                  {isSaving ? "Saving..." : "Create budget and continue"}
                </button>
                <button
                  type="button"
                  className="ghost-button"
                  onClick={() => completeOnboarding("You can add budgets later from the Budgets page.")}
                >
                  Skip for now
                </button>
              </div>
            </form>
          )}
        </section>
      </div>
    </div>
  );
}
