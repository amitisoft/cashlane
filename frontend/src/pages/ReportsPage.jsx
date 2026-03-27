import { useEffect, useMemo, useRef, useState } from "react";
import { useAuth } from "../lib/auth";
import { downloadCsv } from "../lib/api";
import { useToasts } from "../lib/toasts";
import { BarsChart, SingleLineChart, TrendChart, formatCurrency } from "../components/charts";
import { SelectField } from "../components/SelectField";
import { REPORT_TRANSACTION_TYPE_OPTIONS } from "../lib/select-options";

export function ReportsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [categorySpend, setCategorySpend] = useState([]);
  const [trends, setTrends] = useState(null);
  const [netWorth, setNetWorth] = useState([]);
  const [insights, setInsights] = useState([]);
  const [accounts, setAccounts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [filters, setFilters] = useState({
    dateFrom: "",
    dateTo: "",
    accountId: "",
    categoryId: "",
    type: ""
  });
  const pushToastRef = useRef(pushToast);

  useEffect(() => {
    pushToastRef.current = pushToast;
  }, [pushToast]);

  const selectedAccount = useMemo(
    () => accounts.find((account) => account.id === filters.accountId) || null,
    [accounts, filters.accountId]
  );

  useEffect(() => {
    let isActive = true;

    Promise.all([
      authorizedFetch("/reports/category-spend", { params: filters }),
      authorizedFetch("/reports/trends", { params: filters }),
      authorizedFetch("/reports/net-worth", { params: filters }),
      authorizedFetch("/insights"),
      authorizedFetch("/accounts"),
      authorizedFetch("/categories", { params: { accountId: selectedAccount?.isShared ? selectedAccount.id : "" } })
    ])
      .then(([categoryData, trendsData, netWorthData, insightData, accountData, categoryList]) => {
        if (!isActive) {
          return;
        }

        setCategorySpend(categoryData.items);
        setTrends(trendsData);
        setNetWorth(netWorthData.items);
        setInsights(insightData);
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
  }, [authorizedFetch, filters, selectedAccount?.id, selectedAccount?.isShared]);

  const accountOptions = [{ value: "", label: "All accounts" }, ...accounts.map((account) => ({ value: account.id, label: account.name }))];
  const categoryOptions = [{ value: "", label: "All categories" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];

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
          <SelectField ariaLabel="Report account filter" options={accountOptions} value={filters.accountId} onChange={(nextValue) => setFilters((current) => ({ ...current, accountId: nextValue, categoryId: "" }))} />
          <SelectField ariaLabel="Report category filter" options={categoryOptions} value={filters.categoryId} onChange={(nextValue) => setFilters((current) => ({ ...current, categoryId: nextValue }))} />
          <SelectField ariaLabel="Report type filter" options={REPORT_TRANSACTION_TYPE_OPTIONS} value={filters.type} onChange={(nextValue) => setFilters((current) => ({ ...current, type: nextValue }))} />
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
        {trends?.incomeVsExpenseTrend?.length > 0 ? (
          <TrendChart points={trends.incomeVsExpenseTrend} />
        ) : (
          <p className="panel-empty">No trend data for the selected filters.</p>
        )}
      </section>

      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Net worth</span>
            <h2>Snapshot-based balance history</h2>
          </div>
        </header>
        {netWorth.length > 0 ? (
          <SingleLineChart
            ariaLabel="Net worth history"
            color="var(--info)"
            points={netWorth.map((item) => ({ label: item.label, value: item.netWorth }))}
          />
        ) : (
          <p className="panel-empty">Net-worth history appears after balance snapshots are collected.</p>
        )}
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Savings rate</span>
            <h2>Monthly efficiency</h2>
          </div>
        </header>
        <div className="list-stack">
          {(trends?.savingsRateTrend || []).map((item) => (
            <div key={item.label} className="list-row">
              <span>{item.label}</span>
              <strong>{item.savingsRate.toFixed(1)}%</strong>
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Category trends</span>
            <h2>Top spending categories over time</h2>
          </div>
        </header>
        <div className="list-stack">
          {(trends?.categoryTrends || []).map((item) => (
            <article key={item.categoryId} className="detail-card">
              <div className="metric-row">
                <strong>{item.categoryName}</strong>
                <span className="legend-dot" style={{ background: item.color }} />
              </div>
              <div className="mini-grid">
                {item.points.map((point) => (
                  <div key={`${item.categoryId}-${point.label}`} className="mini-grid-item">
                    <span>{point.label}</span>
                    <strong>{formatCurrency(point.amount)}</strong>
                  </div>
                ))}
              </div>
            </article>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Insights</span>
            <h2>Narrative findings</h2>
          </div>
        </header>
        <div className="list-stack">
          {insights.length === 0 ? (
            <p className="panel-empty">Insights will appear when trends emerge across months.</p>
          ) : (
            insights.map((item) => (
              <div key={item.title} className={`alert-card alert-${item.kind}`}>
                <strong>{item.title}</strong>
                <p>{item.body}</p>
              </div>
            ))
          )}
        </div>
      </section>
    </div>
  );
}
