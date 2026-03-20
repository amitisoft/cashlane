import { createContext, useContext, useEffect, useMemo, useState } from "react";
import { apiFetch } from "./api";

const STORAGE_KEY = "cashlane-session";
const AuthContext = createContext(null);

function loadSession() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY) || "null");
  } catch {
    return null;
  }
}

export function AuthProvider({ children }) {
  const [session, setSession] = useState(loadSession);
  const [isBootstrapping, setIsBootstrapping] = useState(true);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
  }, [session]);

  useEffect(() => {
    let ignore = false;

    async function bootstrap() {
      if (!session?.refreshToken) {
        setIsBootstrapping(false);
        return;
      }

      try {
        const nextSession = await apiFetch("/auth/refresh", {
          method: "POST",
          body: { refreshToken: session.refreshToken }
        });

        if (!ignore) {
          setSession(nextSession);
        }
      } catch {
        if (!ignore) {
          setSession(null);
        }
      } finally {
        if (!ignore) {
          setIsBootstrapping(false);
        }
      }
    }

    bootstrap();
    return () => {
      ignore = true;
    };
  }, []);

  const value = useMemo(
    () => ({
      session,
      isAuthenticated: Boolean(session?.accessToken),
      isBootstrapping,
      user: session?.user || null,
      async login(email, password) {
        const nextSession = await apiFetch("/auth/login", {
          method: "POST",
          body: { email, password }
        });
        setSession(nextSession);
        return nextSession;
      },
      async register(payload) {
        return apiFetch("/auth/register", {
          method: "POST",
          body: payload
        });
      },
      async verifyRegistration(token) {
        const nextSession = await apiFetch("/auth/verify-registration", {
          method: "POST",
          body: { token }
        });
        setSession(nextSession);
        return nextSession;
      },
      async loginDemo() {
        const nextSession = await apiFetch("/auth/login", {
          method: "POST",
          body: { email: "demo@cashlane.app", password: "Cashlane123" }
        });
        setSession(nextSession);
        return nextSession;
      },
      async logout() {
        if (session?.refreshToken) {
          try {
            await apiFetch("/auth/logout", {
              method: "POST",
              body: { refreshToken: session.refreshToken }
            });
          } catch {
            // best effort
          }
        }

        setSession(null);
      },
      async forgotPassword(email) {
        return apiFetch("/auth/forgot-password", {
          method: "POST",
          body: { email }
        });
      },
      async resetPassword(token, password) {
        return apiFetch("/auth/reset-password", {
          method: "POST",
          body: { token, password }
        });
      },
      updateUser(nextUser) {
        setSession((current) => (current ? { ...current, user: { ...current.user, ...nextUser } } : current));
      },
      async authorizedFetch(path, options = {}) {
        try {
          return await apiFetch(path, { ...options, token: session?.accessToken });
        } catch (error) {
          if (error.status !== 401 || !session?.refreshToken) {
            throw error;
          }

          const nextSession = await apiFetch("/auth/refresh", {
            method: "POST",
            body: { refreshToken: session.refreshToken }
          });
          setSession(nextSession);
          return apiFetch(path, { ...options, token: nextSession.accessToken });
        }
      }
    }),
    [isBootstrapping, session]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  return useContext(AuthContext);
}
