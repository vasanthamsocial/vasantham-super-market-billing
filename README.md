# SupermarketBilling

Offline-first wholesale and retail billing system for supermarkets: POS billing across multiple counters,
purchasing and GRN, inventory, suppliers and debtors, route collections, dispatch and packing, accounts,
reports, and an optional owner archive.

> **Status: Stage 1 of 17 complete (foundation).** The repository, database, API and web app skeletons,
> health checks, audit-trail foundation, tests and tooling are in place. No business modules exist yet.
> See [docs/requirements-traceability.md](docs/requirements-traceability.md) for exactly what is done.

## Applications

| Application | Folder | Dev URL | Audience |
|---|---|---|---|
| API (ASP.NET Core, .NET 10) | `src/SupermarketBilling.Api` | http://localhost:5080 (Swagger: `/swagger`) | All apps |
| Billing and Operations Web | `apps/billing-web` | http://localhost:3000 | Cashiers, operators, accountants, managers |
| Owner Dashboard (browser, not a PWA) | `apps/owner-dashboard` | http://localhost:3001 | Owner, managers |
| Collection App (phone) | `apps/collection-app` | http://localhost:3002 | Collection personnel |
| Owner Archive Web (optional, licensed) | `apps/owner-archive-web` | http://localhost:3003 | Owner, accountant, auditor |

## Requirements

- Windows 10/11 with Windows PowerShell 5.1 (PowerShell 7 also works)
- .NET SDK 10.0.x
- Node.js 20.9 or later (developed on Node 24)
- Docker Desktop (development database only; production does not need Docker)
- Git

## Quick start (Windows PowerShell)

Every script runs with a per-process execution-policy bypass, so no permanent policy change is needed.

```powershell
cd "E:\Billing Software"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\setup-dev.ps1      # tools check, .env with random passwords, restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\db-up.ps1          # PostgreSQL 16 on 127.0.0.1:5442
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\db-migrate.ps1     # apply migrations (dev)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-database.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-api.ps1        # terminal 1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-web.ps1 -App billing-web   # terminal 2
```

Then open http://localhost:3000. The System status card should show **API: Connected**, **Database: Healthy**
and **Schema: Healthy**, with a **Live** badge.

In VS Code, open `SupermarketBilling.code-workspace`. Use **Terminal > Run Task** for every script above.
**Run and Debug** has configurations for the API, each web app and the combined full stack.

## Tests

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1            # unit + integration + DB verification + type checks
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1 -E2E       # plus Playwright browser tests
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\security-audit.ps1  # vulnerable packages + secret scan
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-production.ps1 # release/ with SHA256SUMS.txt
```

The first end-to-end run needs the browser: `npm run install-browsers --workspace tests/end-to-end`.

## Repository layout

```text
src/            ASP.NET Core API and Domain / Application / Infrastructure layers
apps/           Next.js web applications
packages/       Shared web code (@sb/web-shared)
database/       init (roles), migrations (exported SQL), seeds, verification SQL
tests/          unit, integration (real PostgreSQL), end-to-end (Playwright)
scripts/        PowerShell scripts used by VS Code tasks
docs/           Architecture, decisions, traceability, limitations, development guide
```

## Documentation

- [Architecture](docs/architecture.md)
- [Decisions](docs/decisions.md)
- [Requirements traceability matrix](docs/requirements-traceability.md)
- [Known limitations](docs/known-limitations.md)
- [Development guide](docs/development.md)
