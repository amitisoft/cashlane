import { useEffect, useMemo, useState } from "react";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";
import { SelectField } from "./SelectField";

const STORAGE_KEY = "cashlane-quick-entry";
const emptyForm = {
  type: "Expense",
  amount: "",
  transactionDate: new Date().toISOString().slice(0, 10),
  accountId: "",
  categoryId: "",
  merchant: "",
  note: "",
  paymentMethod: "upi",
  sourceAccountId: "",
  destinationAccountId: ""
};

function loadPreferences() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) || '{"recentMerchants":[],"lastByType":{}}');
  } catch {
    return { recentMerchants: [], lastByType: {} };
  }
}

export function QuickEntrySheet({ isOpen, onClose }) {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [accounts, setAccounts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [preferences, setPreferences] = useState(loadPreferences);
  const [form, setForm] = useState(emptyForm);
  const [isSaving, setIsSaving] = useState(false);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(preferences));
  }, [preferences]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    async function loadData() {
      const accountData = await authorizedFetch("/accounts");
      setAccounts(accountData);
      setForm((current) => ({
        ...emptyForm,
        ...preferences.lastByType[current.type],
        type: current.type,
        transactionDate: new Date().toISOString().slice(0, 10)
      }));
    }

    loadData().catch((error) => {
      pushToast({ kind: "danger", title: "Quick entry unavailable", message: error.message });
    });
  }, [authorizedFetch, isOpen, preferences.lastByType, pushToast]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const selectedAccount = accounts.find((account) => account.id === form.accountId);
    authorizedFetch("/categories", {
      params: {
        accountId: selectedAccount?.isShared ? selectedAccount.id : ""
      }
    })
      .then((categoryData) => {
        setCategories(categoryData.filter((item) => !item.isArchived));
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Quick entry unavailable", message: error.message });
      });
  }, [accounts, authorizedFetch, form.accountId, isOpen, pushToast]);

  const type = form.type.toLowerCase();
  const categoryOptions = useMemo(
    () =>
      categories.filter((category) =>
        type === "income" ? category.type === "Income" : category.type === "Expense"
      ),
    [categories, type]
  );

  const recentMerchants = preferences.recentMerchants || [];
  const accountOptions = useMemo(
    () => [{ value: "", label: "Select account" }, ...accounts.map((account) => ({ value: account.id, label: account.name }))],
    [accounts]
  );
  const categorySelectOptions = useMemo(
    () => [{ value: "", label: "Select category" }, ...categoryOptions.map((category) => ({ value: category.id, label: category.name }))],
    [categoryOptions]
  );

  function updateField(field, value) {
    if (field === "type") {
      setForm((current) => ({
        ...emptyForm,
        ...preferences.lastByType[value],
        type: value,
        transactionDate: current.transactionDate || emptyForm.transactionDate
      }));
      return;
    }

    setForm((current) => ({ ...current, [field]: value }));
  }

  async function handleSubmit(event) {
    event.preventDefault();
    setIsSaving(true);

    try {
      if (type === "transfer") {
        await authorizedFetch("/accounts/transfer", {
          method: "POST",
          body: {
            sourceAccountId: form.sourceAccountId,
            destinationAccountId: form.destinationAccountId,
            amount: Number(form.amount),
            date: form.transactionDate,
            note: form.note
          }
        });
      } else {
        await authorizedFetch("/transactions", {
          method: "POST",
          body: {
            accountId: form.accountId,
            categoryId: form.categoryId,
            type: form.type,
            amount: Number(form.amount),
            transactionDate: form.transactionDate,
            merchant: form.merchant,
            note: form.note,
            paymentMethod: form.paymentMethod,
            tags: form.merchant ? [form.merchant] : []
          }
        });
      }

      const nextRecentMerchants = form.merchant
        ? [form.merchant.trim(), ...recentMerchants.filter((item) => item.toLowerCase() !== form.merchant.trim().toLowerCase())].slice(0, 5)
        : recentMerchants;

      setPreferences((current) => ({
        recentMerchants: nextRecentMerchants,
        lastByType: {
          ...current.lastByType,
          [form.type]: {
            accountId: form.accountId,
            categoryId: form.categoryId,
            paymentMethod: form.paymentMethod,
            sourceAccountId: form.sourceAccountId,
            destinationAccountId: form.destinationAccountId
          }
        }
      }));

      pushToast({
        kind: "success",
        title: "Transaction saved",
        message: "Transaction saved successfully."
      });
      setForm((current) => ({
        ...emptyForm,
        ...preferences.lastByType[current.type],
        type: current.type
      }));
      onClose();
    } catch (error) {
      pushToast({ kind: "danger", title: "Save failed", message: error.message });
    } finally {
      setIsSaving(false);
    }
  }

  if (!isOpen) {
    return null;
  }

  return (
    <div className="sheet-backdrop" role="presentation" onClick={onClose}>
      <aside className="quick-sheet" role="dialog" aria-modal="true" onClick={(event) => event.stopPropagation()}>
        <div className="sheet-header">
          <div>
            <span className="eyebrow">Quick Entry</span>
            <h2>Add transaction fast</h2>
          </div>
          <button className="ghost-button" type="button" onClick={onClose}>
            Close
          </button>
        </div>

        <form className="sheet-form" onSubmit={handleSubmit}>
          <div className="segmented-control">
            {["Expense", "Income", "Transfer"].map((option) => (
              <button
                key={option}
                type="button"
                className={form.type === option ? "is-active" : ""}
                onClick={() => updateField("type", option)}
              >
                {option}
              </button>
            ))}
          </div>

          <label className="field field-amount">
            <span>Amount</span>
            <input
              required
              inputMode="decimal"
              value={form.amount}
              onChange={(event) => updateField("amount", event.target.value)}
              placeholder="0"
            />
          </label>

          <label className="field">
            <span>Date</span>
            <input
              required
              type="date"
              value={form.transactionDate}
              onChange={(event) => updateField("transactionDate", event.target.value)}
            />
          </label>

          {type === "transfer" ? (
            <>
              <label className="field">
                <span>From</span>
                <SelectField
                  ariaLabel="Transfer source account"
                  options={accountOptions}
                  value={form.sourceAccountId}
                  onChange={(nextValue) => updateField("sourceAccountId", nextValue)}
                />
              </label>
              <label className="field">
                <span>To</span>
                <SelectField
                  ariaLabel="Transfer destination account"
                  options={accountOptions}
                  value={form.destinationAccountId}
                  onChange={(nextValue) => updateField("destinationAccountId", nextValue)}
                />
              </label>
            </>
          ) : (
            <>
              <label className="field">
                <span>Account</span>
                <SelectField
                  ariaLabel="Transaction account"
                  options={accountOptions}
                  value={form.accountId}
                  onChange={(nextValue) => updateField("accountId", nextValue)}
                />
              </label>
              <label className="field">
                <span>Category</span>
                <SelectField
                  ariaLabel="Transaction category"
                  options={categorySelectOptions}
                  value={form.categoryId}
                  onChange={(nextValue) => updateField("categoryId", nextValue)}
                />
              </label>
              <label className="field">
                <span>Merchant</span>
                <input value={form.merchant} onChange={(event) => updateField("merchant", event.target.value)} />
              </label>
              {recentMerchants.length > 0 && (
                <div className="merchant-chip-row">
                  {recentMerchants.map((merchant) => (
                    <button key={merchant} type="button" className="merchant-chip" onClick={() => updateField("merchant", merchant)}>
                      {merchant}
                    </button>
                  ))}
                </div>
              )}
              <label className="field">
                <span>Payment method</span>
                <input value={form.paymentMethod} onChange={(event) => updateField("paymentMethod", event.target.value)} />
              </label>
            </>
          )}

          <label className="field field-wide">
            <span>Note</span>
            <textarea rows="3" value={form.note} onChange={(event) => updateField("note", event.target.value)} />
          </label>

          <div className="sheet-actions">
            <button type="button" className="ghost-button" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="primary-button" disabled={isSaving}>
              {isSaving ? "Saving..." : "Save transaction"}
            </button>
          </div>
        </form>
      </aside>
    </div>
  );
}
