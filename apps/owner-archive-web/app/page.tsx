import { AppShell, PlannedModules, type PlannedModule } from '@sb/web-shared';
import { ArchiveStatus } from './ArchiveStatus';

const modules: PlannedModule[] = [
  { name: 'Archive users and roles', description: 'Owner administrator, archive manager, accountant, auditor, report user.', stage: 14 },
  { name: 'Monthly package import', description: 'Signed, encrypted, checksum-verified idempotent import.', stage: 14 },
  { name: 'Historical reports', description: 'Read-only reporting by business, store and financial year.', stage: 14 },
];

export default function ArchiveHomePage() {
  return (
    <AppShell appTitle="Owner Archive">
      <h1>Owner Archive</h1>
      <ArchiveStatus />
      <PlannedModules modules={modules} />
    </AppShell>
  );
}
