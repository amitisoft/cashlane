import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { ACCOUNT_TYPE_OPTIONS } from "../lib/select-options";
import { useToasts } from "../lib/toasts";
import { formatCurrency } from "../components/charts";

const emptyForm = {
  name: "",
  type: "BankAccount",
  openingBalance: 0,
  institutionName: ""
};

const accountTypeLabels = {
  BankAccount: "Bank account",
  CreditCard: "Credit card",
  CashWallet: "Cash wallet",
  SavingsAccount: "Savings account"
};

export function AccountsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState([]);
  const [form, setForm] = useState(emptyForm);

  useEffect(() => {
    authorizedFetch("/accounts")
      .then(setAccounts)
      .catch((error) => {
        pushToast({ kind: "danger", title: "Accounts unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  const sharedSummary = useMemo(() => {
    const ownedShared = accounts.filter((account) => account.isShared && account.role === "Owner");
    const memberShared = accounts.filter((account) => account.isShared && account.role !== "Owner");
    return {
      ownedSharedCount: ownedShared.length,
      memberSharedCount: memberShared.length
    };
  }, [accounts]);

  async function handleSubmit(event) {
    event.preventDefault();
    try {
      const created = await authorizedFetch("/accounts", {
        method: "POST",
        body: {
          ...form,
          openingBalance: Number(form.openingBalance)
        }
      });

      setAccounts((current) => [...current, created]);
      setForm(emptyForm);
      pushToast({ kind: "success", title: "Account created", message: `${created.name} is ready.` });
    } catch (error) {
      pushToast({ kind: "danger", title: "Create failed", message: error.message });
    }
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Accounts</span>
            <h2>Cash, cards, and bank balances</h2>
          </div>
        </header>
        <div className="score-strip">
          <article className="detail-card">
            <strong>{sharedSummary.ownedSharedCount}</strong>
            <p>Shared accounts you manage</p>
          </article>
          <article className="detail-card">
            <strong>{sharedSummary.memberSharedCount}</strong>
            <p>Shared accounts you can access</p>
          </article>
          <button type="button" className="ghost-button" onClick={() => navigate("/shared-accounts")}>
            Open shared account management
          </button>
        </div>
        <div className="list-stack">
          {accounts.map((account) => (
            <article key={account.id} className="detail-card">
              <div className="metric-row">
                <div className="account-copy">
                  <strong>{account.name}</strong>
                  <span>
                    {accountTypeLabels[account.type] || account.type}
                    {account.institutionName ? ` · ${account.institutionName}` : ""}
                  </span>
                </div>
                <strong>{formatCurrency(account.currentBalance)}</strong>
              </div>
              <div className="inline-actions">
                <span className="pill pill-high">{account.role}</span>
                {account.isShared && <span className="pill pill-medium">{account.memberCount} members</span>}
                <span className="pill">{account.ownerName}</span>
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">New Account</span>
            <h2>Add another wallet</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={handleSubmit}>
          <label className="field">
            <span>Name</span>
            <input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} />
          </label>
          <label className="field">
            <span>Type</span>
            <SelectField ariaLabel="Account type" options={ACCOUNT_TYPE_OPTIONS} value={form.type} onChange={(nextValue) => setForm((current) => ({ ...current, type: nextValue }))} />
          </label>
          <label className="field">
            <span>Opening balance</span>
            <input type="number" value={form.openingBalance} onChange={(event) => setForm((current) => ({ ...current, openingBalance: event.target.value }))} />
          </label>
          <label className="field">
            <span>Institution</span>
            <input value={form.institutionName} onChange={(event) => setForm((current) => ({ ...current, institutionName: event.target.value }))} />
          </label>
          <button className="primary-button" type="submit">
            Create account
          </button>
        </form>
      </section>
    </div>
  );
}
