'use client';

import { useState } from 'react';
import { api } from '../api';
import { useAuth } from '../auth/AuthContext';
import { Permission, type GrantRoleResponse, type Role, type Store, type User } from '../types';
import { ActionForm, ErrorText, Field, formatDateTime, Notice, text } from '../ui';
import { useApiData } from './useApiData';

export function UsersPanel() {
  const { membership, hasPermission, me } = useAuth();
  const base = membership ? `/api/v1/businesses/${membership.businessId}` : null;
  const users = useApiData<User[]>(base ? `${base}/users` : null);
  const stores = useApiData<Store[]>(base ? `${base}/stores` : null);
  const roles = useApiData<Role[]>('/api/v1/roles');
  const [message, setMessage] = useState<{ tone: 'success' | 'warning'; text: string } | null>(null);
  const [actionError, setActionError] = useState<unknown>(null);
  // Controlled, so the choice is always one of the loaded users (an uncontrolled select rendered before the list loads submits nothing).
  const [grantUserId, setGrantUserId] = useState('');

  if (!hasPermission(Permission.UsersView)) {
    return <Notice tone="warning">You do not have permission to view users.</Notice>;
  }

  async function act(action: () => Promise<string | void>) {
    setActionError(null);
    setMessage(null);
    try {
      const result = await action();
      if (result) setMessage({ tone: 'success', text: result });
      await users.reload();
    } catch (caught) {
      setActionError(caught);
    }
  }

  const canManage = hasPermission(Permission.UsersManage);

  return (
    <section className="sb-card" aria-labelledby="users-heading">
      <header className="sb-card__header">
        <h2 id="users-heading">Users</h2>
      </header>
      {message ? <Notice tone={message.tone}>{message.text}</Notice> : null}
      <ErrorText error={actionError ?? users.error} />

      <table className="sb-table" data-testid="users-table">
        <thead>
          <tr>
            <th>User</th>
            <th>Roles</th>
            <th>Status</th>
            <th>Last sign-in</th>
            {canManage ? <th>Actions</th> : null}
          </tr>
        </thead>
        <tbody>
          {(users.data ?? []).map((user) => (
            <tr key={user.id} data-testid={`user-row-${user.username}`}>
              <td>
                <strong>{user.displayName}</strong>
                <div className="sb-muted">{user.username}</div>
              </td>
              <td>
                {user.roles.map((role) => (
                  <span key={role.assignmentId} className="sb-chip">
                    {role.roleName}
                    {role.storeName ? ` @ ${role.storeName}` : ''}
                    {canManage && user.id !== me?.userId ? (
                      <button
                        type="button"
                        className="sb-chip__remove"
                        aria-label={`Remove ${role.roleName}`}
                        onClick={() => void act(async () => {
                          await api.del(`${base}/users/${user.id}/roles/${role.assignmentId}`);
                          return `${role.roleName} removed from ${user.displayName}.`;
                        })}
                      >
                        x
                      </button>
                    ) : null}
                  </span>
                ))}
                {user.pendingRoles.map((role) => (
                  <span key={`pending-${role}`} className="sb-chip sb-chip--pending" data-testid="pending-role">
                    {role} - awaiting approval
                  </span>
                ))}
              </td>
              <td>
                {user.isActive ? 'Active' : 'Disabled'}
                {user.isLockedOut ? ' (locked)' : ''}
                {user.mfaEnabled ? ' - 2-step on' : ''}
                {user.mustChangePassword ? ' - must change password' : ''}
              </td>
              <td>{formatDateTime(user.lastLoginAtUtc)}</td>
              {canManage ? (
                <td className="sb-actions">
                  {user.id !== me?.userId ? (
                    <>
                      <button type="button" className="sb-button sb-button--small" onClick={() => void act(async () => {
                        await api.put(`${base}/users/${user.id}/active`, { isActive: !user.isActive });
                        return `${user.displayName} ${user.isActive ? 'disabled and signed out' : 'enabled'}.`;
                      })}>
                        {user.isActive ? 'Disable' : 'Enable'}
                      </button>
                      {user.isLockedOut ? (
                        <button type="button" className="sb-button sb-button--small" onClick={() => void act(async () => {
                          await api.post(`${base}/users/${user.id}/unlock`);
                          return `${user.displayName} unlocked.`;
                        })}>
                          Unlock
                        </button>
                      ) : null}
                      <button type="button" className="sb-button sb-button--small" data-testid={`reset-${user.username}`} onClick={() => void act(async () => {
                        const reset = await api.post<{ resetCode: string; expiresAtUtc: string }>(`${base}/users/${user.id}/password-reset`);
                        setMessage({
                          tone: 'warning',
                          text: `Reset code for ${user.displayName}: ${reset.resetCode} (valid until ${formatDateTime(reset.expiresAtUtc)}). Give it to them in person; it is shown only once.`,
                        });
                      })}>
                        Reset password
                      </button>
                      {user.mfaEnabled ? (
                        <button type="button" className="sb-button sb-button--small" onClick={() => void act(async () => {
                          if (!window.confirm(`Turn off two-step verification for ${user.displayName}? Do this only if they have lost their phone and recovery codes.`)) return;
                          await api.post(`${base}/users/${user.id}/mfa-reset`);
                          return `Two-step verification turned off for ${user.displayName}; they have been signed out.`;
                        })}>
                          Reset two-step
                        </button>
                      ) : null}
                    </>
                  ) : (
                    <span className="sb-muted">You</span>
                  )}
                </td>
              ) : null}
            </tr>
          ))}
        </tbody>
      </table>

      {canManage && base ? (
        <details className="sb-details">
          <summary>Add a user</summary>
          <ActionForm
            testId="create-user-form"
            submitLabel="Add user"
            onSubmit={async (data) => {
              const storeId = text(data, 'storeId');
              const result = await api.post<{ user: User; role: GrantRoleResponse }>(`${base}/users`, {
                username: text(data, 'username'),
                displayName: text(data, 'displayName'),
                temporaryPassword: data.get('temporaryPassword'),
                roleCode: text(data, 'roleCode'),
                storeId: storeId || null,
              });
              setMessage({
                tone: result.role.outcome === 'granted' ? 'success' : 'warning',
                text: `${result.user.displayName} added. ${result.role.message} They must choose their own password at first sign-in.`,
              });
              await users.reload();
            }}
          >
            <Field label="Username" name="username" autoComplete="off" required />
            <Field label="Full name" name="displayName" required />
            <Field label="Temporary password" name="temporaryPassword" type="password" autoComplete="new-password" hint="At least 10 characters. The user must change it." required />
            <RoleAndStoreSelect roles={roles.data ?? []} stores={stores.data ?? []} />
          </ActionForm>
        </details>
      ) : null}

      {hasPermission(Permission.RolesAssign) && base ? (
        <details className="sb-details">
          <summary>Give an existing user another role</summary>
          <ActionForm
            testId="grant-role-form"
            submitLabel="Grant role"
            onSubmit={async (data) => {
              const storeId = text(data, 'storeId');
              const userId = grantUserId || (users.data ?? []).find((u) => u.id !== me?.userId)?.id;
              if (!userId) throw new Error('Choose the user.');
              const result = await api.post<GrantRoleResponse>(`${base}/users/${userId}/roles`, {
                roleCode: text(data, 'roleCode'),
                storeId: storeId || null,
                reason: text(data, 'reason') || null,
              });
              setMessage({ tone: result.outcome === 'granted' ? 'success' : 'warning', text: result.message });
              await users.reload();
            }}
          >
            <label className="sb-field">
              <span className="sb-field__label">User</span>
              <select
                className="sb-input"
                name="userId"
                required
                value={grantUserId || (users.data ?? []).find((u) => u.id !== me?.userId)?.id || ''}
                onChange={(e) => setGrantUserId(e.target.value)}
              >
                {(users.data ?? []).filter((u) => u.id !== me?.userId).map((u) => (
                  <option key={u.id} value={u.id}>{u.displayName} ({u.username})</option>
                ))}
              </select>
            </label>
            <RoleAndStoreSelect roles={roles.data ?? []} stores={stores.data ?? []} />
            <Field label="Reason (shown to the approver)" name="reason" />
          </ActionForm>
        </details>
      ) : null}
    </section>
  );
}

function RoleAndStoreSelect({ roles, stores }: { roles: Role[]; stores: Store[] }) {
  const [roleCode, setRoleCode] = useState('cashier');
  const role = roles.find((r) => r.code === roleCode);
  return (
    <>
      <label className="sb-field">
        <span className="sb-field__label">Role</span>
        <select className="sb-input" name="roleCode" value={roleCode} onChange={(e) => setRoleCode(e.target.value)}>
          {roles.map((r) => (
            <option key={r.code} value={r.code}>
              {r.name}{r.isPrivileged ? ' (needs approval)' : ''}
            </option>
          ))}
        </select>
      </label>
      <label className="sb-field">
        <span className="sb-field__label">Store</span>
        <select className="sb-input" name="storeId" disabled={role?.businessWideOnly} defaultValue="">
          <option value="">All stores</option>
          {!role?.businessWideOnly && stores.map((s) => (
            <option key={s.id} value={s.id}>{s.name}</option>
          ))}
        </select>
      </label>
    </>
  );
}
