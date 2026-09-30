'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, ApiError } from '../api';
import type { Me, Membership } from '../types';

type AuthStatus = 'loading' | 'setup-required' | 'signed-out' | 'signed-in' | 'unavailable';

interface AuthContextValue {
  status: AuthStatus;
  me: Me | null;
  /** The business currently being worked in (users with several businesses can switch). */
  membership: Membership | null;
  selectBusiness: (businessId: string) => void;
  hasPermission: (permission: string) => boolean;
  setMe: (me: Me | null) => void;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);
const BusinessKey = 'sb.selectedBusiness';

function storedBusiness(): string | null {
  try {
    return window.localStorage.getItem(BusinessKey);
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [me, setMeState] = useState<Me | null>(null);
  const [businessId, setBusinessId] = useState<string | null>(null);

  const setMe = useCallback((value: Me | null) => {
    setMeState(value);
    setStatus(value ? 'signed-in' : 'signed-out');
  }, []);

  const refresh = useCallback(async () => {
    try {
      setMe(await api.get<Me>('/api/v1/auth/me'));
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        try {
          const setup = await api.get<{ setupRequired: boolean }>('/api/v1/setup/status');
          setMeState(null);
          setStatus(setup.setupRequired ? 'setup-required' : 'signed-out');
        } catch {
          setStatus('unavailable');
        }
      } else {
        setStatus('unavailable');
      }
    }
  }, [setMe]);

  useEffect(() => {
    setBusinessId(storedBusiness());
    void refresh();
  }, [refresh]);

  const logout = useCallback(async () => {
    try {
      await api.post('/api/v1/auth/logout');
    } finally {
      setMe(null);
    }
  }, [setMe]);

  const membership = useMemo(() => {
    const memberships = me?.memberships ?? [];
    return memberships.find((m) => m.businessId === businessId) ?? memberships[0] ?? null;
  }, [me, businessId]);

  const selectBusiness = useCallback((id: string) => {
    setBusinessId(id);
    try {
      window.localStorage.setItem(BusinessKey, id);
    } catch {
      // Storage unavailable: the choice lasts for this page only.
    }
  }, []);

  const hasPermission = useCallback((permission: string) => membership?.permissions.includes(permission) ?? false, [membership]);

  const value = useMemo(
    () => ({ status, me, membership, selectBusiness, hasPermission, setMe, refresh, logout }),
    [status, me, membership, selectBusiness, hasPermission, setMe, refresh, logout],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}
