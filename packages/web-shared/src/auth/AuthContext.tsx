'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, ApiError } from '../api';
import { forgetCollector, loadDevice, offlineCollector, rememberCollector, unreachable } from '../offline/collectionQueue';
import type { Me, Membership } from '../types';

/** 'offline': the server cannot be reached and the collector who last signed in on this enrolled phone works from it. */
type AuthStatus = 'loading' | 'setup-required' | 'signed-out' | 'signed-in' | 'unavailable' | 'offline';

interface AuthContextValue {
  status: AuthStatus;
  /** 'edge' = in-store server (one company); 'cloud' = hosted service, where sign-in needs a company code. */
  deploymentMode: 'edge' | 'cloud';
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

/** `offline`: the Collection App, which opens without signal on an enrolled phone for its collector. */
export function AuthProvider({ children, offline = false }: { children: ReactNode; offline?: boolean }) {
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [me, setMeState] = useState<Me | null>(null);
  const [businessId, setBusinessId] = useState<string | null>(null);
  const [deploymentMode, setDeploymentMode] = useState<'edge' | 'cloud'>('edge');

  const setMe = useCallback((value: Me | null) => {
    setMeState(value);
    setStatus(value ? 'signed-in' : 'signed-out');
  }, []);

  /** The server is out of reach: work offline when this phone allows it, otherwise say it is unavailable. */
  const unavailable = useCallback(async (error: unknown) => {
    const cached = offline && unreachable(error) ? await offlineCollector().catch(() => null) : null;
    if (cached) {
      setMeState(cached);
      setStatus('offline');
    } else {
      setStatus('unavailable');
    }
  }, [offline]);

  const refresh = useCallback(async () => {
    let setup: { setupRequired: boolean; deploymentMode: 'edge' | 'cloud' };
    try {
      setup = await api.get('/api/v1/setup/status');
      setDeploymentMode(setup.deploymentMode);
    } catch (error) {
      await unavailable(error);
      return;
    }

    try {
      setMe(await api.get<Me>('/api/v1/auth/me'));
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setMeState(null);
        setStatus(setup.setupRequired ? 'setup-required' : 'signed-out');
      } else {
        await unavailable(error);
      }
    }
  }, [setMe, unavailable]);

  useEffect(() => {
    setBusinessId(storedBusiness());
    void refresh();
  }, [refresh]);

  // A fully signed-in collector on an enrolled phone is remembered (encrypted) so the app opens for them without signal.
  useEffect(() => {
    if (!offline || status !== 'signed-in' || me?.sessionState !== 'active') return;
    void loadDevice()
      .then(({ device }) => rememberCollector(me, device))
      .catch(() => undefined);
  }, [offline, status, me]);

  // Offline: try the server again when the signal returns, and every half minute.
  useEffect(() => {
    if (status !== 'offline') return;
    const retry = () => void refresh();
    window.addEventListener('online', retry);
    const timer = window.setInterval(retry, 30_000);
    return () => {
      window.removeEventListener('online', retry);
      window.clearInterval(timer);
    };
  }, [status, refresh]);

  const logout = useCallback(async () => {
    try {
      if (offline) await forgetCollector();
      await api.post('/api/v1/auth/logout');
    } catch (error) {
      if (!unreachable(error)) throw error;
    } finally {
      setMe(null);
    }
  }, [setMe, offline]);

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
    () => ({ status, deploymentMode, me, membership, selectBusiness, hasPermission, setMe, refresh, logout }),
    [status, deploymentMode, me, membership, selectBusiness, hasPermission, setMe, refresh, logout],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}
