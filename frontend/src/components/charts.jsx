export function DonutChart({ items }) {
  const total = items.reduce((sum, item) => sum + item.amount, 0);
  let offset = 0;

  return (
    <div className="chart-frame">
      <svg viewBox="0 0 120 120" className="donut-chart" aria-label="Spending by category">
        <circle cx="60" cy="60" r="42" className="donut-track" />
        {items.map((item) => {
          const value = total > 0 ? item.amount / total : 0;
          const strokeDasharray = `${value * 264} 264`;
          const strokeDashoffset = -offset * 264;
          offset += value;

          return (
            <circle
              key={item.categoryId}
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
        {items.map((item) => (
          <div key={item.categoryId} className="chart-legend-item">
            <span className="legend-dot" style={{ background: item.color }} />
            <span>{item.categoryName}</span>
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
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const maxValue = Math.max(
    1,
    ...points.flatMap((point) => [point.income, point.expense])
  );
  const getPointX = (index) =>
    padding.left + ((points.length === 1 ? 0.5 : index / Math.max(points.length - 1, 1)) * plotWidth);

  const buildPath = (field) =>
    points
      .map((point, index) => {
        const x = getPointX(index);
        const y = padding.top + (1 - point[field] / maxValue) * plotHeight;
        return `${index === 0 ? "M" : "L"} ${x} ${y}`;
      })
      .join(" ");

  const guideLines = Array.from({ length: 4 }, (_, index) => padding.top + (plotHeight / 3) * index);

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
        <path d={buildPath("income")} className="trend-line income-line" />
        <path d={buildPath("expense")} className="trend-line expense-line" />
        {points.map((point, index) => {
          const x = getPointX(index);
          const incomeY = padding.top + (1 - point.income / maxValue) * plotHeight;
          const expenseY = padding.top + (1 - point.expense / maxValue) * plotHeight;

          return (
            <g key={point.label}>
              <circle cx={x} cy={incomeY} r="4.5" className="trend-point income-point" />
              <circle cx={x} cy={expenseY} r="4.5" className="trend-point expense-point" />
            </g>
          );
        })}
      </svg>
      <div
        className="trend-labels"
        style={{ gridTemplateColumns: `repeat(${Math.max(points.length, 1)}, minmax(0, 1fr))` }}
      >
        {points.map((point) => (
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
      {items.map((item) => (
        <div key={item.categoryId} className="bar-row">
          <div className="bar-label">
            <span>{item.categoryName}</span>
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
