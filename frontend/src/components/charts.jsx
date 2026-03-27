function getLinePoints(points, { width, height, padding, maxValue, valueKey }) {
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;

  return points.map((point, index) => {
    const x = padding.left + ((points.length === 1 ? 0.5 : index / Math.max(points.length - 1, 1)) * plotWidth);
    const y = padding.top + (1 - point[valueKey] / maxValue) * plotHeight;
    return { x, y };
  });
}

function buildPath(points) {
  return points.map((point, index) => `${index === 0 ? "M" : "L"} ${point.x} ${point.y}`).join(" ");
}

export function DonutChart({ items }) {
  const total = items.reduce((sum, item) => sum + item.amount, 0);
  let offset = 0;

  return (
    <div className="chart-frame">
      <svg viewBox="0 0 120 120" className="donut-chart" aria-label="Spending by category">
        <circle cx="60" cy="60" r="42" className="donut-track" />
        {items.map((item, index) => {
          const value = total > 0 ? item.amount / total : 0;
          const strokeDasharray = `${value * 264} 264`;
          const strokeDashoffset = -offset * 264;
          offset += value;

          return (
            <circle
              key={item.categoryId || item.label || index}
              cx="60"
              cy="60"
              r="42"
              className="donut-segment"
              stroke={item.color}
              strokeDasharray={strokeDasharray}
              strokeDashoffset={strokeDashoffset}
            />
          );
        })}
      </svg>
      <div className="chart-legend">
        {items.map((item, index) => (
          <div key={item.categoryId || item.label || index} className="chart-legend-item">
            <span className="legend-dot" style={{ background: item.color }} />
            <span>{item.categoryName || item.label}</span>
            <strong>{formatCurrency(item.amount)}</strong>
          </div>
        ))}
      </div>
    </div>
  );
}

export function TrendChart({ points }) {
  const width = 720;
  const height = 260;
  const padding = { top: 20, right: 18, bottom: 24, left: 18 };
  const maxValue = Math.max(1, ...points.flatMap((point) => [point.income, point.expense]));
  const guideLines = Array.from({ length: 4 }, (_, index) => padding.top + ((height - padding.top - padding.bottom) / 3) * index);
  const incomePoints = getLinePoints(points, { width, height, padding, maxValue, valueKey: "income" });
  const expensePoints = getLinePoints(points, { width, height, padding, maxValue, valueKey: "expense" });

  return (
    <div className="trend-frame">
      <svg viewBox={`0 0 ${width} ${height}`} className="trend-chart" aria-label="Income versus expense trend">
        {guideLines.map((lineY) => (
          <line
            key={lineY}
            x1={padding.left}
            y1={lineY}
            x2={width - padding.right}
            y2={lineY}
            className="trend-grid-line"
          />
        ))}
        <path d={buildPath(incomePoints)} className="trend-line income-line" />
        <path d={buildPath(expensePoints)} className="trend-line expense-line" />
        {points.map((point, index) => (
          <g key={point.label}>
            <circle cx={incomePoints[index].x} cy={incomePoints[index].y} r="4.5" className="trend-point income-point" />
            <circle cx={expensePoints[index].x} cy={expensePoints[index].y} r="4.5" className="trend-point expense-point" />
          </g>
        ))}
      </svg>
      <div className="trend-labels" style={{ gridTemplateColumns: `repeat(${Math.max(points.length, 1)}, minmax(0, 1fr))` }}>
        {points.map((point) => (
          <span key={point.label}>{point.label}</span>
        ))}
      </div>
    </div>
  );
}

export function SingleLineChart({ points, ariaLabel, color = "var(--accent-strong)" }) {
  const normalized = points.map((point) => ({ label: point.label, value: point.value }));
  const width = 720;
  const height = 260;
  const padding = { top: 20, right: 18, bottom: 24, left: 18 };
  const maxValue = Math.max(1, ...normalized.map((point) => point.value));
  const guideLines = Array.from({ length: 4 }, (_, index) => padding.top + ((height - padding.top - padding.bottom) / 3) * index);
  const linePoints = getLinePoints(normalized, { width, height, padding, maxValue, valueKey: "value" });

  return (
    <div className="trend-frame">
      <svg viewBox={`0 0 ${width} ${height}`} className="trend-chart" aria-label={ariaLabel}>
        {guideLines.map((lineY) => (
          <line
            key={lineY}
            x1={padding.left}
            y1={lineY}
            x2={width - padding.right}
            y2={lineY}
            className="trend-grid-line"
          />
        ))}
        <path d={buildPath(linePoints)} className="trend-line" style={{ stroke: color }} />
        {linePoints.map((point, index) => (
          <circle key={`${normalized[index].label}-${index}`} cx={point.x} cy={point.y} r="4.5" className="trend-point" style={{ stroke: color }} />
        ))}
      </svg>
      <div className="trend-labels" style={{ gridTemplateColumns: `repeat(${Math.max(normalized.length, 1)}, minmax(0, 1fr))` }}>
        {normalized.map((point) => (
          <span key={point.label}>{point.label}</span>
        ))}
      </div>
    </div>
  );
}

export function BarsChart({ items }) {
  const max = Math.max(1, ...items.map((item) => item.amount));
  return (
    <div className="bars-chart">
      {items.map((item, index) => (
        <div key={item.categoryId || item.label || index} className="bar-row">
          <div className="bar-label">
            <span>{item.categoryName || item.label}</span>
            <strong>{formatCurrency(item.amount)}</strong>
          </div>
          <div className="bar-track">
            <div
              className="bar-fill"
              style={{ width: `${(item.amount / max) * 100}%`, background: item.color || "var(--accent-strong)" }}
            />
          </div>
        </div>
      ))}
    </div>
  );
}

export function formatCurrency(value) {
  return new Intl.NumberFormat("en-IN", {
    style: "currency",
    currency: "INR",
    maximumFractionDigits: 0
  }).format(value || 0);
}
