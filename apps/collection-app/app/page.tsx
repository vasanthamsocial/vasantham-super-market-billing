import { PlannedModules, SystemStatus, type PlannedModule } from '@sb/web-shared';

const modules: PlannedModule[] = [
  { name: "Today's visits", description: 'Parties due today by route, weekday, due date and promises.', stage: 9 },
  { name: 'Party balance', description: 'Due, overdue and not-yet-due balances with oldest invoice.', stage: 9 },
  { name: 'Collect payment', description: 'Cash, cheque, UPI, transfer; full, partial or advance.', stage: 9 },
  { name: 'Cash handover', description: 'Denominations, expected versus actual, variance.', stage: 9 },
  { name: 'Offline queue', description: 'Encrypted pending collections with provisional receipts.', stage: 13 },
];

export default function CollectionHomePage() {
  return (
    <>
      <h1>Collections</h1>
      <SystemStatus />
      <PlannedModules modules={modules} />
    </>
  );
}
