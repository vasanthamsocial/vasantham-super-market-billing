import { AppShell, PlannedModules, SystemStatus, type PlannedModule } from '@sb/web-shared';

const modules: PlannedModule[] = [
  { name: 'Users, stores and configuration', description: 'Named users, roles, stores, counters and settings.', stage: 3 },
  { name: 'Approvals', description: 'Maker-checker queue for price, stock, tax-mode and reversal approvals.', stage: 3 },
  { name: 'Live sales', description: 'Revenue, margin, taxes, discounts, returns and payment mix.', stage: 12 },
  { name: 'Counters and cashiers', description: 'Counter comparison, cashier performance and cash variance.', stage: 12 },
  { name: 'Stock', description: 'Valuation, low and negative stock, expiry.', stage: 12 },
  { name: 'Receivables and payables', description: 'Debtor and supplier balances, collections, routes.', stage: 12 },
  { name: 'Operations health', description: 'Dispatch, backup status and device health.', stage: 12 },
];

export default function DashboardPage() {
  return (
    <AppShell appTitle="Owner Dashboard">
      <h1>Owner Dashboard</h1>
      <SystemStatus />
      <PlannedModules modules={modules} />
    </AppShell>
  );
}
