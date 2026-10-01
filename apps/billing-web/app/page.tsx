import { PlannedModules, SystemStatus, type PlannedModule } from '@sb/web-shared';

const modules: PlannedModule[] = [
  { name: 'POS billing', description: 'Keyboard-first billing across multiple counters.', stage: 5 },
  { name: 'Shifts', description: 'Opening cash, denominations and till reconciliation.', stage: 6 },
  { name: 'Purchases and GRN', description: 'Purchase documents, landed cost and cost-change warnings.', stage: 7 },
  { name: 'Suppliers and debtors', description: 'Ledgers, credit periods and collection schedules.', stage: 8 },
  { name: 'Collections', description: 'Routes, receipts, cash and cheque custody.', stage: 9 },
  { name: 'Dispatch and packing', description: 'Lorry service, packing challans and dispatch.', stage: 11 },
  { name: 'Accounts and reports', description: 'GST, stock, credit, collection and audit reports.', stage: 12 },
];

export default function HomePage() {
  return (
    <>
      <h1>Billing and Operations</h1>
      <SystemStatus />
      <PlannedModules modules={modules} />
    </>
  );
}
