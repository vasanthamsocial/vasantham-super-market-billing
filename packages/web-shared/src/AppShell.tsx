import type { ReactNode } from 'react';

export interface PlannedModule {
  name: string;
  description: string;
  stage: number;
}

/** Common page frame: product name, application name and main content area. */
export function AppShell({
  appTitle,
  variant = 'desktop',
  children,
}: {
  appTitle: string;
  variant?: 'desktop' | 'phone';
  children: ReactNode;
}) {
  return (
    <div className={`sb-shell sb-shell--${variant}`}>
      <header className="sb-topbar">
        <span className="sb-topbar__product">SupermarketBilling</span>
        <span className="sb-topbar__app">{appTitle}</span>
      </header>
      <main className="sb-main">{children}</main>
    </div>
  );
}

/**
 * Lists the modules this application will provide and the delivery stage for each.
 * Modules are shown as planned until their stage is complete; nothing here pretends to work.
 */
export function PlannedModules({ modules }: { modules: PlannedModule[] }) {
  return (
    <section className="sb-card" aria-labelledby="modules-heading">
      <header className="sb-card__header">
        <h2 id="modules-heading">Modules</h2>
      </header>
      <ul className="sb-module-list">
        {modules.map((module) => (
          <li key={module.name} className="sb-module">
            <div>
              <strong>{module.name}</strong>
              <p className="sb-muted">{module.description}</p>
            </div>
            <span className="sb-badge sb-badge--planned">Stage {module.stage}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
