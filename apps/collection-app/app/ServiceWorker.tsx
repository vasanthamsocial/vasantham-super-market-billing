'use client';

import { useEffect } from 'react';

/** Registers the service worker that lets the app open without signal (production builds only: it would serve stale code in development). */
export function ServiceWorker() {
  useEffect(() => {
    if (process.env.NODE_ENV !== 'production' || !('serviceWorker' in navigator)) return;
    void navigator.serviceWorker.register('/sw.js').catch(() => undefined);
  }, []);
  return null;
}
