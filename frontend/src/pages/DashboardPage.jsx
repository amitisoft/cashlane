import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";
import { DonutChart, TrendChart, formatCurrency } from "../components/charts";

export function DashboardPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const navigate = useNavigate();
  const [summary, setSummary] = useState(null);

  useEffect(() => {
    authorizedFetch("/dashboard/summary")
      .then(setSummary)
      .catch((error) => {
        pushToast({ kind: "danger", title: "Dashboard unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  if (!summary) {
    return <div className="page-loading">Loading dashboard...</div>;
  }

  return (
    <div className="page-grid dashboard-grid">
      <section className="hero-strip">
        {[summary.netBalance, summary.income, summary.expense].map((card) => (
          <article key={card.label} className="stat-card">
            <span className="eyebrow">{card.label}</span>
            <strong>{formatCurrency(card.amount)}</strong>
          </article>
        ))}
      </section>

      <section className="panel panel-actions">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Shortcuts</span>
            <h2>Jump straight into the next money task</h2>
          </div>
        </header>
        <div className="action-grid">
          <button type="button" className="ghost-button" onClick={() => navigate("/transactions")}>
            View all transactions
          </button>
          <button type="button" className="ghost-button" onClick={() => navigate("/budgets")}>
            Create budget
          </button>
          <button type="button" className="ghost-button" onClick={() => navigate("/recurring")}>
            Add recurring bill
          </button>
          <button type="button" className="ghost-button" onClick={() => navigate("/goals")}>
            Update goal contribution
          </button>
        </div>
        <div className="tag-list">
          {summary.topSpendingCategories.map((item) => (
            <span key={item} className="dashboard-tag">
              {item}
            </span>
          ))}
        </div>
      </section>

      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">This Month</span>
            <h2>Spending by category</h2>
          </div>
        </header>
        <DonutChart items={summary.spendingByCategory} />
      </section>

      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Trend</span>
            <h2>Income vs expense</h2>
          </div>
        </header>
        <TrendChart points={summary.trend} />
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Recent</span>
            <h2>Transactions</h2>
          </div>
        </header>
        <div className="list-stack">
          {summary.recentTransactions.map((item) => (
            <div key={item.id} className="list-row">
              <div className="dashboard-transaction-copy">
                <strong>{item.merchant || item.categoryName || item.type}</strong>
                <span>{item.accountName}</span>
              </div>
              <strong>{formatCurrency(item.amount)}</strong>
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Upcoming</span>
            <h2>Recurring bills</h2>
          </div>
        </header>
        <div className="list-stack">
          {summary.upcomingRecurring.map((item) => (
            <div key={item.id} className="list-row">
              <div className="dashboard-upcoming-copy">
                <strong>{item.title}</strong>
                <span>{item.nextRunDate}</span>
              </div>
              <strong>{formatCurrency(item.amount)}</strong>
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Goals</span>
            <h2>Savings progress</h2>
          </div>
        </header>
        <div className="list-stack">
          {summary.goals.map((goal) => (
            <div key={goal.id} className="dashboard-goal-row">
              <div className="dashboard-goal-copy">
                <strong>{goal.name}</strong>
                <span>
                  {formatCurrency(goal.currentAmount)} / {formatCurrency(goal.targetAmount)}
                </span>
              </div>
              <div className="progress-rail">
                <div style={{ width: `${Math.min(goal.progressPercent, 100)}%` }} />
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Signals</span>
            <h2>Alerts & insights</h2>
          </div>
        </header>
        <div className="list-stack">
          {summary.alerts.map((item, index) => (
            <div key={`alert-${index}`} className={`alert-card alert-${item.kind}`}>
              {item.message}
            </div>
          ))}
          {summary.insights.map((item) => (
            <div key={item.title} className="insight-card">
              <strong>{item.title}</strong>
              <p>{item.body}</p>
            </div>
          ))}
        </div>
      </section>
    </div>
  );
}
