import { useEffect, useState } from "react";
import { useAuth } from "../lib/auth";
import { useToasts } from "../lib/toasts";

export function SettingsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [profile, setProfile] = useState(null);
  const [sessions, setSessions] = useState([]);
  const [passwords, setPasswords] = useState({ currentPassword: "", newPassword: "" });

  useEffect(() => {
    Promise.all([authorizedFetch("/settings/profile"), authorizedFetch("/settings/sessions")])
      .then(([profileData, sessionData]) => {
        setProfile(profileData);
        setSessions(sessionData);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Settings unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  if (!profile) {
    return <div className="page-loading">Loading settings...</div>;
  }

  const isDemoUser = profile.email === "demo@cashlane.app";

  async function saveProfile(event) {
    event.preventDefault();
    try {
      const updated = await authorizedFetch("/settings/profile", {
        method: "PUT",
        body: { displayName: profile.displayName }
      });
      setProfile(updated);
      pushToast({ kind: "success", title: "Profile saved", message: "Display name updated." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Save failed", message: error.message });
    }
  }

  async function changePassword(event) {
    event.preventDefault();
    try {
      await authorizedFetch("/settings/change-password", {
        method: "POST",
        body: passwords
      });
      setPasswords({ currentPassword: "", newPassword: "" });
      pushToast({ kind: "success", title: "Password changed", message: "All active sessions were revoked." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Update failed", message: error.message });
    }
  }

  return (
    <div className="page-grid">
      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Profile</span>
            <h2>Your account</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={saveProfile}>
          <label className="field">
            <span>Display name</span>
            <input value={profile.displayName} onChange={(event) => setProfile((current) => ({ ...current, displayName: event.target.value }))} />
          </label>
          <label className="field">
            <span>Email</span>
            <input value={profile.email} readOnly />
          </label>
          <button className="primary-button" type="submit">
            Save profile
          </button>
        </form>
      </section>

      {!isDemoUser && (
        <section className="panel">
          <header className="panel-header">
            <div>
              <span className="eyebrow">Security</span>
              <h2>Change password</h2>
            </div>
          </header>
          <form className="stacked-form" onSubmit={changePassword}>
            <label className="field">
              <span>Current password</span>
              <input type="password" value={passwords.currentPassword} onChange={(event) => setPasswords((current) => ({ ...current, currentPassword: event.target.value }))} />
            </label>
            <label className="field">
              <span>New password</span>
              <input type="password" value={passwords.newPassword} onChange={(event) => setPasswords((current) => ({ ...current, newPassword: event.target.value }))} />
            </label>
            <button className="primary-button" type="submit">
              Change password
            </button>
          </form>
        </section>
      )}

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Security</span>
            <h2>Active sessions</h2>
          </div>
        </header>
        <div className="list-stack">
          {sessions.map((session) => (
            <div key={session.id} className="list-row">
              <div>
                <strong>{new Date(session.createdAtUtc).toLocaleString()}</strong>
              </div>
              <span className="session-status">{session.revokedAtUtc ? "Revoked" : "Active"}</span>
            </div>
          ))}
        </div>
      </section>
    </div>
  );
}
