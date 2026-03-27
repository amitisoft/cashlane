import { Navigate, Route, Routes } from "react-router-dom";
import { useAuth } from "./lib/auth";
import { AppShell } from "./components/AppShell";
import { LandingPage } from "./pages/LandingPage";
import { DashboardPage } from "./pages/DashboardPage";
import { TransactionsPage } from "./pages/TransactionsPage";
import { BudgetsPage } from "./pages/BudgetsPage";
import { GoalsPage } from "./pages/GoalsPage";
import { ReportsPage } from "./pages/ReportsPage";
import { RecurringPage } from "./pages/RecurringPage";
import { AccountsPage } from "./pages/AccountsPage";
import { CategoriesPage } from "./pages/CategoriesPage";
import { InsightsPage } from "./pages/InsightsPage";
import { RulesPage } from "./pages/RulesPage";
import { SettingsPage } from "./pages/SettingsPage";
import { SharedAccountsPage } from "./pages/SharedAccountsPage";
import { OnboardingPage } from "./pages/OnboardingPage";

function ProtectedAppLayout() {
  const { isAuthenticated, user } = useAuth();

  if (!isAuthenticated) {
    return <Navigate to="/" replace />;
  }

  if (user?.needsOnboarding) {
    return <Navigate to="/onboarding" replace />;
  }

  return <AppShell />;
}

function ProtectedOnboardingRoute() {
  const { isAuthenticated, user } = useAuth();

  if (!isAuthenticated) {
    return <Navigate to="/" replace />;
  }

  if (!user?.needsOnboarding) {
    return <Navigate to="/dashboard" replace />;
  }

  return <OnboardingPage />;
}

export default function App() {
  const { isAuthenticated, isBootstrapping, user } = useAuth();

  if (isBootstrapping) {
    return <div className="app-loading">Preparing your finance workspace...</div>;
  }

  return (
    <Routes>
      <Route
        path="/"
        element={
          isAuthenticated ? (
            <Navigate to={user?.needsOnboarding ? "/onboarding" : "/dashboard"} replace />
          ) : (
            <LandingPage />
          )
        }
      />
      <Route path="/onboarding" element={<ProtectedOnboardingRoute />} />
      <Route element={<ProtectedAppLayout />}>
        <Route path="/dashboard" element={<DashboardPage />} />
        <Route path="/transactions" element={<TransactionsPage />} />
        <Route path="/budgets" element={<BudgetsPage />} />
        <Route path="/goals" element={<GoalsPage />} />
        <Route path="/reports" element={<ReportsPage />} />
        <Route path="/insights" element={<InsightsPage />} />
        <Route path="/rules" element={<RulesPage />} />
        <Route path="/recurring" element={<RecurringPage />} />
        <Route path="/accounts" element={<AccountsPage />} />
        <Route path="/shared-accounts" element={<SharedAccountsPage />} />
        <Route path="/categories" element={<CategoriesPage />} />
        <Route path="/settings" element={<SettingsPage />} />
      </Route>
    </Routes>
  );
}
