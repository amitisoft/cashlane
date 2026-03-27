import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";
import { DonutChart, SingleLineChart, TrendChart, formatCurrency } from "../components/charts";

export function DashboardPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const navigate = useNavigate();
  const [summary, setSummary] = useState(null);
  const [forecastMonth, setForecastMonth] = useState(null);
  const [forecastDaily, setForecastDaily] = useState([]);
  const [healthScore, setHealthScore] = useState(null);

  useEffect(() => {
    Promise.all([
      authorizedFetch("/dashboard/summary"),
      authorizedFetch("/forecast/month"),
      authorizedFetch("/forecast/daily"),
      authorizedFetch("/insights/health-score")
    ])
      .then(([summaryData, monthData, dailyData, healthData]) => {
        setSummary(summaryData);
        setForecastMonth(monthData);
        setForecastDaily(dailyData);
        setHealthScore(healthData);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Dashboard unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  if (!summary || !forecastMonth || !healthScore) {
    return <div className="page-loading">Loading dashboard...</div>;
  }

  const heroCards = [
    summary.netBalance,
    summary.income,
    summary.expense,
    { label: "Projected balance", amount: forecastMonth.forecastedBalance }
  ];

  return (
    <div className="page-grid dashboard-grid">
      <section className="hero-strip">
        {heroCards.map((card) => (
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
          <button type="button" className="ghost-button" onClick={() => navigate("/rules")}>
            Manage rules
          </button>
          <button type="button" className="ghost-button" onClick={() => navigate("/shared-accounts")}>
            Shared account access
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
            <span className="eyebrow">Forecast</span>
            <h2>Projected balance to month-end</h2>
          </div>
          <div className="dashboard-badge-row">
            <span className={`pill pill-${forecastMonth.confidence.toLowerCase()}`}>{forecastMonth.confidence} confidence</span>
            <strong>{formatCurrency(forecastMonth.lowestProjectedBalance)} lowest point</strong>
          </div>
        </header>
        {forecastDaily.length > 0 ? (
          <SingleLineChart
            ariaLabel="Projected balance forecast"
            color="var(--accent-strong)"
            points={forecastDaily.map((item) => ({
              label: item.date.slice(5),
              value: item.projectedBalance
            }))}
          />
        ) : (
          <p className="panel-empty">Forecast data will appear after you add more account activity.</p>
        )}
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Health</span>
            <h2>Financial health score</h2>
          </div>
          <button type="button" className="ghost-button" onClick={() => navigate("/insights")}>
            Open insights
          </button>
        </header>
        <div className="score-card">
          <strong>{Math.round(healthScore.score)}</strong>
          <span>out of 100</span>
        </div>
        <div className="list-stack">
          {healthScore.factors.map((factor) => (
            <div key={factor.key} className="metric-row">
              <div>
                <strong>{factor.label}</strong>
                <p>{factor.summary}</p>
              </div>
              <strong>{Math.round(factor.score)}</strong>
            </div>
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
            <span className="eyebrow">Known expenses</span>
            <h2>Upcoming expected outflow</h2>
          </div>
        </header>
        <div className="list-stack">
          {forecastMonth.knownExpenses.length === 0 ? (
            <p className="panel-empty">No recurring or patterned expenses detected yet.</p>
          ) : (
            forecastMonth.knownExpenses.map((item, index) => (
              <div key={`${item.merchant}-${item.date}-${index}`} className="list-row">
                <div className="dashboard-upcoming-copy">
                  <strong>{item.merchant}</strong>
                  <span>
                    {item.kind} · {item.accountName} · {item.date}
                  </span>
                </div>
                <strong>{formatCurrency(item.amount)}</strong>
              </div>
            ))
          )}
        </div>
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
          {forecastMonth.warnings.map((warning, index) => (
            <div key={`forecast-${index}`} className="alert-card alert-warning">
              {warning}
            </div>
          ))}
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
