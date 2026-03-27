import { useEffect, useRef, useState } from "react";
import { NavLink, Outlet, useNavigate } from "react-router-dom";
import { useAuth } from "../lib/auth";
import { useDateRange } from "../lib/date-range";
import { useToasts } from "../lib/toasts";
import { QuickEntrySheet } from "./QuickEntrySheet";
import { SelectField } from "./SelectField";

const navItems = [
  { to: "/dashboard", label: "Dashboard" },
  { to: "/transactions", label: "Transactions" },
  { to: "/budgets", label: "Budgets" },
  { to: "/goals", label: "Goals" },
  { to: "/reports", label: "Reports" },
  { to: "/insights", label: "Insights" },
  { to: "/rules", label: "Rules" },
  { to: "/recurring", label: "Recurring" },
  { to: "/accounts", label: "Accounts" },
  { to: "/shared-accounts", label: "Shared" },
  { to: "/categories", label: "Categories" },
  { to: "/settings", label: "Settings" }
];

export function AppShell() {
  const { logout, user } = useAuth();
  const { preset, setPreset, presets } = useDateRange();
  const { notifications, unreadCount, markNotificationsSeen, dismissNotification } = useToasts();
  const [isQuickEntryOpen, setQuickEntryOpen] = useState(false);
  const [isNotificationsOpen, setNotificationsOpen] = useState(false);
  const [search, setSearch] = useState("");
  const notificationsRef = useRef(null);
  const navigate = useNavigate();

  useEffect(() => {
    if (!isNotificationsOpen) {
      return undefined;
    }

    function handlePointerDown(event) {
      if (notificationsRef.current && !notificationsRef.current.contains(event.target)) {
        setNotificationsOpen(false);
      }
    }

    function handleKeyDown(event) {
      if (event.key === "Escape") {
        setNotificationsOpen(false);
      }
    }

    document.addEventListener("mousedown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);

    return () => {
      document.removeEventListener("mousedown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [isNotificationsOpen]);

  function handleSearchSubmit(event) {
    event.preventDefault();
    navigate(`/transactions?q=${encodeURIComponent(search)}`);
  }

  function toggleNotifications() {
    setNotificationsOpen((current) => {
      const next = !current;
      if (next) {
        markNotificationsSeen();
      }

      return next;
    });
  }

  const presetOptions = Object.entries(presets).map(([key, value]) => ({
    value: key,
    label: value.label
  }));

  return (
    <div className="app-shell">
      <aside className="app-nav">
        <div className="brand-block">
          <span className="eyebrow brand-mark">Cashlane</span>
        </div>

        <nav className="nav-links">
          {navItems.map((item) => (
            <NavLink key={item.to} to={item.to} className={({ isActive }) => (isActive ? "nav-link is-active" : "nav-link")}>
              {item.label}
            </NavLink>
          ))}
        </nav>

        <button className="primary-button nav-action" type="button" onClick={() => setQuickEntryOpen(true)}>
          Add Transaction
        </button>
      </aside>

      <div className="app-main">
        <header className="topbar">
          <form className="search-bar" onSubmit={handleSearchSubmit}>
            <input
              placeholder="Search transactions"
              value={search}
              onChange={(event) => setSearch(event.target.value)}
            />
          </form>

          <SelectField
            ariaLabel="Date range preset"
            className="preset-select"
            options={presetOptions}
            value={preset}
            onChange={setPreset}
          />

          <div className="notifications-wrap" ref={notificationsRef}>
            <button
              type="button"
              className="ghost-button notifications-button"
              onClick={toggleNotifications}
              aria-expanded={isNotificationsOpen}
            >
              Notifications
              {unreadCount > 0 && <span className="notification-badge">{unreadCount}</span>}
            </button>
            {isNotificationsOpen && (
              <div className="notifications-panel">
                <div className="notifications-panel-header">
                  <strong>Recent activity</strong>
                  <button type="button" className="ghost-button notifications-close" onClick={() => setNotificationsOpen(false)}>
                    Close
                  </button>
                </div>
                {notifications.length === 0 ? (
                  <p className="notifications-empty">No recent alerts yet.</p>
                ) : (
                  <div className="notifications-list">
                    {notifications.map((item) => (
                      <article key={item.id} className={`notification-card notification-${item.kind}`}>
                        <div>
                          <strong>{item.title}</strong>
                          <p>{item.message}</p>
                        </div>
                        <button
                          type="button"
                          className="ghost-button notification-dismiss"
                          onClick={() => dismissNotification(item.id)}
                        >
                          Dismiss
                        </button>
                      </article>
                    ))}
                  </div>
                )}
              </div>
            )}
          </div>

          <div className="profile-pill">
            <div>
              <span>{user?.displayName}</span>
            </div>
            <button type="button" className="ghost-button" onClick={logout}>
              Logout
            </button>
          </div>
        </header>

        <main className="page-frame">
          <Outlet />
        </main>
      </div>

      <button className="floating-action" type="button" onClick={() => setQuickEntryOpen(true)}>
        Add
      </button>
      <QuickEntrySheet isOpen={isQuickEntryOpen} onClose={() => setQuickEntryOpen(false)} />
    </div>
  );
}
