import { useEffect, useMemo, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { MANAGEABLE_ACCOUNT_ROLE_OPTIONS } from "../lib/select-options";
import { useToasts } from "../lib/toasts";

const emptyInvite = {
  email: "",
  role: "Viewer"
};

export function SharedAccountsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [accounts, setAccounts] = useState([]);
  const [selectedAccountId, setSelectedAccountId] = useState("");
  const [members, setMembers] = useState([]);
  const [activity, setActivity] = useState([]);
  const [invite, setInvite] = useState(emptyInvite);

  const ownedAccounts = useMemo(
    () => accounts.filter((account) => account.role === "Owner"),
    [accounts]
  );

  useEffect(() => {
    authorizedFetch("/accounts")
      .then((accountData) => {
        setAccounts(accountData);
        if (!selectedAccountId) {
          setSelectedAccountId(accountData.find((account) => account.role === "Owner")?.id || "");
        }
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Shared accounts unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast, selectedAccountId]);

  useEffect(() => {
    if (!selectedAccountId) {
      setMembers([]);
      setActivity([]);
      return;
    }

    Promise.all([
      authorizedFetch(`/accounts/${selectedAccountId}/members`),
      authorizedFetch(`/accounts/${selectedAccountId}/activity`)
    ])
      .then(([memberData, activityData]) => {
        setMembers(memberData);
        setActivity(activityData);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Shared account unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast, selectedAccountId]);

  async function handleInvite(event) {
    event.preventDefault();
    if (!selectedAccountId) {
      return;
    }

    try {
      await authorizedFetch(`/accounts/${selectedAccountId}/invite`, {
        method: "POST",
        body: {
          email: invite.email,
          role: invite.role
        }
      });
      const nextMembers = await authorizedFetch(`/accounts/${selectedAccountId}/members`);
      setMembers(nextMembers);
      setInvite(emptyInvite);
      pushToast({ kind: "success", title: "Invite sent", message: "Shared account member updated." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Invite failed", message: error.message });
    }
  }

  async function updateRole(member, role) {
    try {
      const nextMembers = await authorizedFetch(`/accounts/${selectedAccountId}/members/${member.userId}`, {
        method: "PUT",
        body: { role }
      });
      setMembers(nextMembers);
      pushToast({ kind: "success", title: "Role updated", message: `${member.displayName} now has ${role} access.` });
    } catch (error) {
      pushToast({ kind: "danger", title: "Role update failed", message: error.message });
    }
  }

  const accountOptions = [{ value: "", label: "Select an account" }, ...ownedAccounts.map((account) => ({ value: account.id, label: account.name }))];

  return (
    <div className="page-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Shared accounts</span>
            <h2>Invite members and review access</h2>
          </div>
        </header>
        <div className="filters-grid">
          <SelectField ariaLabel="Shared account selector" options={accountOptions} value={selectedAccountId} onChange={setSelectedAccountId} />
        </div>
        {selectedAccountId ? (
          <div className="list-stack">
            {members.map((member) => (
              <article key={member.userId} className="detail-card">
                <div className="metric-row">
                  <div>
                    <strong>{member.displayName}</strong>
                    <p>{member.email}</p>
                  </div>
                  {member.isOwner ? (
                    <span className="pill pill-high">Owner</span>
                  ) : (
                    <div className="shared-role-picker">
                      <SelectField
                        ariaLabel={`Role for ${member.displayName}`}
                        options={MANAGEABLE_ACCOUNT_ROLE_OPTIONS}
                        value={member.role}
                        onChange={(nextValue) => updateRole(member, nextValue)}
                      />
                    </div>
                  )}
                </div>
              </article>
            ))}
          </div>
        ) : (
          <p className="panel-empty">Create a shared account first, then manage members here.</p>
        )}
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Invite</span>
            <h2>Add an existing Cashlane user</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={handleInvite}>
          <label className="field">
            <span>Email</span>
            <input value={invite.email} onChange={(event) => setInvite((current) => ({ ...current, email: event.target.value }))} />
          </label>
          <label className="field">
            <span>Role</span>
            <SelectField ariaLabel="Invite role" options={MANAGEABLE_ACCOUNT_ROLE_OPTIONS} value={invite.role} onChange={(nextValue) => setInvite((current) => ({ ...current, role: nextValue }))} />
          </label>
          <button className="primary-button" type="submit" disabled={!selectedAccountId}>
            Send invite
          </button>
        </form>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Activity</span>
            <h2>Recent shared-account changes</h2>
          </div>
        </header>
        <div className="list-stack">
          {activity.length === 0 ? (
            <p className="panel-empty">No shared-account activity yet.</p>
          ) : (
            activity.map((item) => (
              <div key={item.id} className="detail-card">
                <strong>{item.actorDisplayName}</strong>
                <p>{item.summary}</p>
                <small>{new Date(item.createdAtUtc).toLocaleString()}</small>
              </div>
            ))
          )}
        </div>
      </section>
    </div>
  );
}
