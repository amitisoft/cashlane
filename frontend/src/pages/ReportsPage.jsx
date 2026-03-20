import { useEffect, useRef, useState } from "react";
import { useAuth } from "../lib/auth";
import { downloadCsv } from "../lib/api";
import { useToasts } from "../lib/toasts";
import { BarsChart, TrendChart, formatCurrency } from "../components/charts";
import { SelectField } from "../components/SelectField";
import { REPORT_TRANSACTION_TYPE_OPTIONS } from "../lib/select-options";

export function ReportsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [categorySpend, setCategorySpend] = useState([]);
  const [incomeVsExpense, setIncomeVsExpense] = useState([]);
  const [balances, setBalances] = useState([]);
  const [accounts, setAccounts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [filters, setFilters] = useState({
    dateFrom: "",
    dateTo: "",
    accountId: "",
    categoryId: "",
    type: ""
  });
  const accountOptions = [{ value: "", label: "All accounts" }, ...accounts.map((account) => ({ value: account.id, label: account.name }))];
  const categoryOptions = [{ value: "", label: "All categories" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];
  const pushToastRef = useRef(pushToast);

  useEffect(() => {
    pushToastRef.current = pushToast;
  }, [pushToast]);

  useEffect(() => {
    let isActive = true;

    Promise.all([
      authorizedFetch("/reports/category-spend", { params: filters }),
      authorizedFetch("/reports/income-vs-expense", { params: filters }),
      authorizedFetch("/reports/account-balance-trend", { params: filters }),
      authorizedFetch("/accounts"),
      authorizedFetch("/categories")
    ])
      .then(([categoryData, trendData, balanceData, accountData, categoryList]) => {
        if (!isActive) {
          return;
        }

        setCategorySpend(categoryData.items);
        setIncomeVsExpense(trendData.items);
        setBalances(balanceData.items);
        setAccounts(accountData);
        setCategories(categoryList.filter((item) => !item.isArchived));
      })
      .catch((error) => {
        if (!isActive) {
          return;
        }

        pushToastRef.current({ kind: "danger", title: "Reports unavailable", message: error.message });
      });

    return () => {
      isActive = false;
    };
  }, [authorizedFetch, filters]);

  function exportCsv() {
    downloadCsv("cashlane-category-spend.csv", [
      ["Category", "Amount"],
      ...categorySpend.map((item) => [item.categoryName, item.amount])
    ]);
    pushToast({ kind: "success", title: "CSV exported", message: "Report downloaded locally." });
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Reports</span>
            <h2>Category spend</h2>
          </div>
          <button className="primary-button" type="button" onClick={exportCsv}>
            Export CSV
          </button>
        </header>
        <div className="filters-grid">
          <input type="date" value={filters.dateFrom} onChange={(event) => setFilters((current) => ({ ...current, dateFrom: event.target.value }))} />
          <input type="date" value={filters.dateTo} onChange={(event) => setFilters((current) => ({ ...current, dateTo: event.target.value }))} />
          <SelectField
            ariaLabel="Report account filter"
            options={accountOptions}
            value={filters.accountId}
            onChange={(nextValue) => setFilters((current) => ({ ...current, accountId: nextValue }))}
          />
          <SelectField
            ariaLabel="Report category filter"
            options={categoryOptions}
            value={filters.categoryId}
            onChange={(nextValue) => setFilters((current) => ({ ...current, categoryId: nextValue }))}
          />
          <SelectField
            ariaLabel="Report type filter"
            options={REPORT_TRANSACTION_TYPE_OPTIONS}
            value={filters.type}
            onChange={(nextValue) => setFilters((current) => ({ ...current, type: nextValue }))}
          />
        </div>
        <BarsChart items={categorySpend.map((item) => ({ ...item, color: "var(--accent-strong)" }))} />
      </section>

      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Trend</span>
            <h2>Income vs expense</h2>
          </div>
        </header>
        {incomeVsExpense.length > 0 ? (
          <>
            <TrendChart points={incomeVsExpense} />
            <div className="list-stack report-summary-list">
              {incomeVsExpense.map((item) => (
                <div key={item.label} className="list-row">
                  <div className="report-copy">
                    <strong>{item.label}</strong>
                    <span>Monthly totals</span>
                  </div>
                  <div className="report-trend-values">
                    <span>
                      Income <strong>{formatCurrency(item.income)}</strong>
                    </span>
                    <span>
                      Expense <strong>{formatCurrency(item.expense)}</strong>
                    </span>
                  </div>
                </div>
              ))}
            </div>
          </>
        ) : (
          <p className="panel-empty">No income or expense data for the selected filters.</p>
        )}
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Balances</span>
            <h2>Account balances</h2>
          </div>
        </header>
        {balances.length > 0 ? (
          <div className="list-stack">
            {balances.map((item) => (
              <div key={item.accountId} className="list-row">
                <div className="report-copy">
                  <strong>{item.accountName}</strong>
                  <span>Current balance</span>
                </div>
                <strong>{formatCurrency(item.currentBalance)}</strong>
              </div>
            ))}
          </div>
        ) : (
          <p className="panel-empty">No account balances are available for the selected filters.</p>
        )}
      </section>
    </div>
  );
}
