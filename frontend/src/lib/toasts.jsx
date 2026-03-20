import { createContext, useContext, useMemo, useState } from "react";

const ToastContext = createContext(null);

export function ToastProvider({ children }) {
  const [toasts, setToasts] = useState([]);
  const [notifications, setNotifications] = useState([]);
  const [unreadCount, setUnreadCount] = useState(0);

  const value = useMemo(
    () => ({
      notifications,
      unreadCount,
      pushToast(toast) {
        const id = crypto.randomUUID();
        const notification = {
          id,
          kind: "info",
          createdAt: new Date().toISOString(),
          isRead: false,
          ...toast
        };

        setToasts((current) => [...current, notification]);
        setNotifications((current) => [notification, ...current].slice(0, 18));
        setUnreadCount((current) => current + 1);
        window.setTimeout(() => {
          setToasts((current) => current.filter((item) => item.id !== id));
        }, 3200);
      },
      markNotificationsSeen() {
        setNotifications((current) => current.map((item) => ({ ...item, isRead: true })));
        setUnreadCount(0);
      },
      dismissNotification(id) {
        setNotifications((current) => current.filter((item) => item.id !== id));
      }
    }),
    [notifications, unreadCount]
  );

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="toast-stack" aria-live="polite">
        {toasts.map((toast) => (
          <div key={toast.id} className={`toast toast-${toast.kind}`}>
            <strong>{toast.title}</strong>
            <span>{toast.message}</span>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToasts() {
  return useContext(ToastContext);
}
