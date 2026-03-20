import { createContext, useContext, useMemo, useState } from "react";

const DateRangeContext = createContext(null);

const presets = {
  thisMonth: { label: "This Month", from: null, to: null },
  last30: { label: "Last 30 Days", from: null, to: null },
  thisYear: { label: "This Year", from: null, to: null }
};

export function DateRangeProvider({ children }) {
  const [preset, setPreset] = useState("thisMonth");

  const value = useMemo(
    () => ({
      preset,
      setPreset,
      presets
    }),
    [preset]
  );

  return <DateRangeContext.Provider value={value}>{children}</DateRangeContext.Provider>;
}

export function useDateRange() {
  return useContext(DateRangeContext);
}
