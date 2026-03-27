import { useEffect, useState } from "react";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";

export function InsightsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [healthScore, setHealthScore] = useState(null);
  const [insights, setInsights] = useState([]);

  useEffect(() => {
    Promise.all([authorizedFetch("/insights/health-score"), authorizedFetch("/insights")])
      .then(([scoreData, insightData]) => {
        setHealthScore(scoreData);
        setInsights(insightData);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Insights unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  if (!healthScore) {
    return <div className="page-loading">Loading insights...</div>;
  }

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Health score</span>
            <h2>Weighted financial health breakdown</h2>
          </div>
        </header>
        <div className="score-strip">
          <div className="score-card">
            <strong>{Math.round(healthScore.score)}</strong>
            <span>out of 100</span>
          </div>
          <div className="list-stack">
            {healthScore.suggestions.map((item, index) => (
              <div key={`${item}-${index}`} className="insight-card">
                {item}
              </div>
            ))}
          </div>
        </div>
      </section>

      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Factors</span>
            <h2>How the score is built</h2>
          </div>
        </header>
        <div className="score-factor-grid">
          {healthScore.factors.map((factor) => (
            <article key={factor.key} className="detail-card">
              <div className="metric-row">
                <strong>{factor.label}</strong>
                <span>{factor.weight}% weight</span>
              </div>
              <strong className="factor-score">{Math.round(factor.score)}</strong>
              <p>{factor.summary}</p>
            </article>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Narrative</span>
            <h2>Observed money signals</h2>
          </div>
        </header>
        <div className="list-stack">
          {insights.length === 0 ? (
            <p className="panel-empty">Add more activity to unlock narrative insights.</p>
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
