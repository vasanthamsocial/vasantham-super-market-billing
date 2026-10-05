'use client';

import { DashboardPanel, RequirePermission, SystemStatus } from '@sb/web-shared';

export default function DashboardPage() {
  return (
    <>
      <h1>Owner Dashboard</h1>
      <SystemStatus />
      <RequirePermission permission="reports.view">
        <DashboardPanel />
      </RequirePermission>
    </>
  );
}
