import { useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";

export function LandingPage() {
  const { login, register, verifyRegistration, forgotPassword, resetPassword } = useAuth();
  const { pushToast } = useToasts();
  const [searchParams, setSearchParams] = useSearchParams();
  const resetToken = searchParams.get("token");
  const registrationToken = searchParams.get("registrationToken");
  const initialEmail = searchParams.get("email") || "";
  const [mode, setMode] = useState(registrationToken ? "verify" : resetToken ? "reset" : "login");
  const [form, setForm] = useState({
    email: initialEmail,
    password: "",
    displayName: "",
    confirmPassword: ""
  });
  const [isSubmitting, setSubmitting] = useState(false);
  const didVerifyRegistration = useRef(false);

  const title = useMemo(() => {
    switch (mode) {
      case "register":
        return "Start building calm money habits";
      case "verify":
        return "Verify your email";
      case "forgot":
        return "Reset access without losing momentum";
      case "reset":
        return "Create a fresh password";
      default:
        return "Welcome back";
    }
  }, [mode]);

  useEffect(() => {
    if (!registrationToken || didVerifyRegistration.current) {
      return;
    }

    didVerifyRegistration.current = true;
    setMode("verify");
    setSubmitting(true);

    verifyRegistration(registrationToken)
      .catch((error) => {
        pushToast({ kind: "danger", title: "Verification failed", message: error.message });
        setMode("login");
        setSearchParams(initialEmail ? { email: initialEmail } : {});
      })
      .finally(() => setSubmitting(false));
  }, [initialEmail, pushToast, registrationToken, setSearchParams, verifyRegistration]);

  async function handleSubmit(event) {
    event.preventDefault();
    setSubmitting(true);

    try {
      if (mode === "register") {
        await register({
          email: form.email,
          password: form.password,
          displayName: form.displayName
        });
        pushToast({ kind: "success", title: "Verify your email", message: "Check your email or demo logs to finish creating your account." });
        setMode("login");
        setSearchParams(form.email ? { email: form.email } : {});
        setForm((current) => ({ ...current, password: "", confirmPassword: "" }));
      } else if (mode === "forgot") {
        await forgotPassword(form.email);
        pushToast({ kind: "success", title: "Reset sent", message: "Check your email or demo logs." });
      } else if (mode === "reset") {
        if (form.password !== form.confirmPassword) {
          throw new Error("Passwords do not match.");
        }

        await resetPassword(resetToken, form.password);
        pushToast({ kind: "success", title: "Password updated", message: "You can sign in now." });
        setMode("login");
        setSearchParams(form.email ? { email: form.email } : {});
      } else {
        await login(form.email, form.password);
      }
    } catch (error) {
      pushToast({ kind: "danger", title: "Action failed", message: error.message });
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="landing-page">
      <section className="landing-hero">
        <span className="eyebrow landing-brand">Cashlane</span>
        <h1>Personal finance, designed like a crafted ledger.</h1>
        <p>Track income, spending, budgets, goals, and recurring bills.</p>

        <div className="hero-metrics">
          <article>
            <strong>15s</strong>
            <span>quick expense entry</span>
          </article>
          <article>
            <strong>INR</strong>
            <span>default locale</span>
          </article>
        </div>
      </section>

      <section className="auth-panel">
        <div className="auth-card">
          <span className="eyebrow">{mode}</span>
          <h2>{title}</h2>

          <form className="auth-form" onSubmit={handleSubmit}>
            {mode === "verify" && (
              <p>Checking your email link and preparing your workspace for {form.email || "your account"}.</p>
            )}

            {mode === "register" && (
              <label className="field">
                <span>Display name</span>
                <input
                  required
                  value={form.displayName}
                  onChange={(event) => setForm((current) => ({ ...current, displayName: event.target.value }))}
                />
              </label>
            )}

            {mode !== "reset" && mode !== "verify" && (
              <label className="field">
                <span>Email</span>
                <input
                  required
                  type="email"
                  value={form.email}
                  onChange={(event) => setForm((current) => ({ ...current, email: event.target.value }))}
                />
              </label>
            )}

            {mode !== "forgot" && mode !== "verify" && (
              <label className="field">
                <span>Password</span>
                <input
                  required
                  type="password"
                  value={form.password}
                  onChange={(event) => setForm((current) => ({ ...current, password: event.target.value }))}
                />
              </label>
            )}

            {mode === "reset" && (
              <label className="field">
                <span>Confirm password</span>
                <input
                  required
                  type="password"
                  value={form.confirmPassword}
                  onChange={(event) => setForm((current) => ({ ...current, confirmPassword: event.target.value }))}
                />
              </label>
            )}

            {mode !== "verify" && (
              <button className="primary-button" type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Working..." : "Continue"}
              </button>
            )}
          </form>

          <div className="auth-links">
            {mode !== "login" && mode !== "verify" && (
              <button
                type="button"
                className="ghost-button"
                onClick={() => {
                  setMode("login");
                  if (resetToken || registrationToken) {
                    setSearchParams(form.email ? { email: form.email } : {});
                  }
                }}
              >
                Back to login
              </button>
            )}
            {mode === "login" && (
              <>
                <button type="button" className="ghost-button" onClick={() => setMode("forgot")}>
                  Forgot password?
                </button>
                <button type="button" className="ghost-button" onClick={() => setMode("register")}>
                  Create account
                </button>
              </>
            )}
          </div>

          <p className="demo-caption">
            Demo login: <strong>demo@cashlane.app</strong> / <strong>Cashlane123</strong>
          </p>
        </div>
      </section>
    </div>
  );
}
