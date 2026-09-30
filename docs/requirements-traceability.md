# Requirements traceability matrix

Maps every section of the SupermarketBilling specification to its delivery stage, current status and the
evidence that proves it. Status values: **Done** (implemented and tested), **Partial** (foundation in place,
more to come), **Planned** (not started).

A requirement is marked Done only when it is covered by an automated test or a verification script.
Placeholder screens or empty tables never count.

## Stage plan

| Stage | Scope | Status |
|---|---|---|
| 1 | Repository, VS Code workspace, local database, API/web skeletons, health checks | **Done** |
| 2 | Database and local deployment hardening (backup/restore scripts) | **Done** |
| 3 | Authentication, businesses, stores, users, permissions, maker-checker | **Done** |
| 3b | Multi-tenancy for the hybrid SaaS model (D-013) | **Done** |
| 4 | Products, rates, inventory (FIFO default), tax registration mode | Planned |
| 5 | Multiple counters, online POS | Planned |
| 6 | Shifts and reconciliation | Planned |
| 7 | Purchases and GRN | Planned |
| 8 | Suppliers and debtors | Planned |
| 9 | Collections and routes | Planned |
| 10 | WhatsApp and SMS | Planned |
| 11 | Lorry service and packing | Planned |
| 12 | Reports and Owner Dashboard | Planned |
| 13 | Controlled offline operation | Planned |
| S1 | SaaS: edge-to-cloud sync, licensing and subscriptions (O-007, O-008) | Planned |
| 14 | Optional Owner Archive Web | Planned |
| 15 | Security hardening | Planned |
| 16 | End-to-end certification | Planned |
| 17 | Documentation and release package | Planned |

## Requirements

| ID | Spec § | Requirement | Stage | Status | Evidence |
|---|---|---|---|---|---|
| R-SaaS.1 | owner | Sellable as a hybrid SaaS: many companies isolated from each other | 3b | Done | `tenant_id` on every tenant table, row-level security, composite tenant keys; tests `CloudTenancyTests` (verified to fail without RLS), `TenancyMigrationTests`; `verify-database.ps1` RLS checks |
| R-SaaS.2 | owner | Edge (in-store) and cloud modes from the same code | 3b | Partial | `Deployment__Mode`; edge setup binds the installation; cloud provisioning key and company-code sign-in. Sync and licensing in stage S1 |
| R-01.1 | 1 | Billing and Operations Web | 1, 3-12 | Partial | `apps/billing-web`: sign-in, setup, stores, users, approvals, audit, account; e2e `01-setup.spec.ts`, `identity.spec.ts` |
| R-01.2 | 1 | Owner Dashboard (browser, not PWA per D-003) | 1, 3, 12 | Partial | `apps/owner-dashboard`: approvals, users, stores, audit; e2e approval performed in the Owner Dashboard |
| R-01.3 | 1 | Collection App, phone-optimized | 1, 9, 13 | Partial | `apps/collection-app`; e2e `collection-app.spec.ts` (no horizontal overflow at phone width) |
| R-01.4 | 1 | Optional Owner Archive Web, disabled by default | 1, 14 | Partial | `apps/owner-archive-web`; `ARCHIVE_WEB_ENABLED=false`; e2e archive-disabled test; `run-web.ps1` refuses to start it unless enabled |
| R-01.5 | 1 | No customer, rider, store-mobile, ecommerce or marketplace apps | all | Done | Repository contains only the four apps |
| R-02.1 | 2 | ASP.NET Core Web API with EF Core (on .NET 10, D-001) | 1 | Done | `src/`; integration tests |
| R-02.2 | 2 | PostgreSQL (D-002) with exact decimals | 1 | Done | `numeric(18,4)` convention; `verify-database.ps1` rejects float and unbounded numeric |
| R-02.3 | 2 | Next.js with TypeScript | 1 | Done | `apps/*`; strict type checks |
| R-02.4 | 2 | Docker Compose for development | 1 | Done | `docker-compose.yml` |
| R-02.5 | 2 | OpenAPI/Swagger | 1 | Done | `/openapi/v1.json`, `/swagger`; integration test `OpenApi_document_describes_the_system_endpoint` |
| R-02.6 | 2 | Unit, integration and end-to-end tests | 1 | Done | `tests/unit`, `tests/integration`, `tests/end-to-end` |
| R-02.7 | 2 | Git, VS Code workspace, tasks, debugging | 1 | Done | `.git`, `SupermarketBilling.code-workspace`, `.vscode/` |
| R-03.1 | 3 | Operates without internet; no CDNs, online fonts or external services | 1, 13 | Partial | System fonts only; e2e asserts zero non-localhost requests |
| R-03.2 | 3 | Live DB on internal SSD, never on a pendrive | 17 | Planned | Installer checks and guide |
| R-03.3 | 3 | Encrypted, checksum-verified backups with restore testing | 2, 17 | Partial | `sb-backup` (AES-256-GCM stream, SHA-256 sidecar, snapshot manifest, automatic restore test, rename-not-drop restore). Tests: `BackupCryptoTests` (tamper/truncate/reorder/wrong passphrase), `BackupRestoreTests` (real pg_dump/pg_restore). Scheduling, retention and PITR in Stage 17. |
| R-03.4 | 3 | Pendrives only for backup/restore/export, never the live DB | 2, 17 | Partial | `backup.ps1 -OutDir`; `docs/backup-and-restore.md` |
| R-04.1 | 4 | Multiple legal businesses (licensed) and multiple stores per business | 3 | Done | `businesses`/`stores` tables with GSTIN/state checks; `Security:MaxBusinesses`; tests `LicenceTests`, `Other_businesses_are_invisible_not_just_forbidden`, `Duplicate_store_codes_are_rejected_with_a_readable_conflict` |
| R-04.2 | 4 | Counters, trusted devices, counter sequences, shifts, concurrency protections | 5, 6 | Planned | |
| R-05.1 | 5 | Named accounts, secure hashing, reset, MFA (mandatory for privileged where configured), HTTP-only sessions, expiry, revocation, throttling and lockout | 3 | Done | `AuthService`, `SessionService`; tests `AuthenticationTests` (incl. parallel-guessing lockout), `MfaTests` (incl. manager MFA reset), `MfaPolicyTests`, `TotpTests` (RFC 6238 vectors), `SecretHandlingTests`; e2e `identity.spec.ts` |
| R-05.2 | 5 | Permission-based authorization, business/store isolation, maker-checker | 3 | Done (routes in Stage 9) | Fallback policy requires sign-in; `AccessControl`, `GrantPolicy`; DB check `ck_approval_requests_maker_checker`; tests `AuthorizationTests`, `MakerCheckerTests`, `SeparationOfDutiesTests`, `GrantPolicyTests` |
| R-05.3 | 5 | Immutable audit events | 1, 3 | Done | Append-only triggers; every sign-in, change and approval staged in the same transaction; audit API and screen; tests `AuditTrailImmutabilityTests`, audit assertions in `MakerCheckerTests` |
| R-05.4 | 5 | Passwords, MFA codes and session tokens never in URLs or logs | 1, 3, 15 | Done | Tokens only in cookies/bodies; captured-log assertions in `Passwords_and_session_tokens_never_appear_in_logs`, reset-code and MFA tests; setup code logged by file path only |
| R-06.x | 6 | Effective-dated tax registration mode with approvals and history protection | 4, 5 | Planned | Not built in Stage 3; will use the Stage 3 maker-checker engine |
| R-07.x | 7 | Purchase-document classification and labelled report views | 7, 12 | Planned | |
| R-08.x | 8 | Keyboard-first POS, payments, returns, printing, shifts | 5, 6 | Planned | |
| R-09.x | 9 | Multiple product rates with effective dating and invoice snapshot | 4, 5 | Planned | |
| R-10.x | 10 | AllowNegativeStock setting with approval trail | 4 | Planned | |
| R-11.x | 11 | Products, batches, valuation, immutable stock ledger, idempotent commands | 4 | Planned | Append-only trigger helper `AppendOnlySql` ready |
| R-12.x | 12 | GRN, cost-change warnings, below-cost block, freight allocation | 7 | Planned | |
| R-13.x | 13 | Supplier/debtor masters with ledger-derived balances | 8 | Planned | |
| R-14.x | 14 | Credit period with stored due date; collection schedules | 8, 9 | Planned | |
| R-15.x | 15 | Collection App daily list, allocation, receipts, reversal-only corrections | 9 | Planned | |
| R-16.x | 16 | Offline collections: encrypted queue, provisional receipts, ordered sync | 13 | Planned | |
| R-17.x | 17 | Cash and cheque custody, bounce reversal | 9 | Planned | |
| R-18.x | 18 | WhatsApp Business Platform / SMS with consent, outbox, webhooks | 10 | Planned | |
| R-19.x | 19 | Lorry service master and dispatch details | 11 | Planned | |
| R-20.x | 20 | Packing delivery challan without cost/profit/balance | 11 | Planned | |
| R-21.1 | 21 | Owner Dashboard live metrics | 12 | Planned | |
| R-21.2 | 21 | Distinguish live, delayed, unavailable and cached data | 1, 12 | Partial | `packages/web-shared/src/freshness.ts`, `FreshnessBadge`; e2e asserts `live` |
| R-22.x | 22 | Owner Archive: separate DB, roles, monthly signed packages, retention gates | 14 | Partial | Separate `archive-db` compose service (profile `archive`, own volume, port 5443) |
| R-23.x | 23 | Reports with exact reconciliation | 12 | Planned | |
| R-24.1 | 24 | Server-side authorization, CSRF | 3 | Done | Fallback authorization policy; `CsrfMiddleware` (synchronizer token); test `State_changing_requests_without_the_csrf_header_are_rejected` (verified to fail when the middleware is removed) |
| R-24.2 | 24 | Secure headers | 1, 15 | Partial | API middleware + Next headers; integration and e2e header tests |
| R-24.3 | 24 | Rate limiting | 1, 3 | Done | Global per-client limit, stricter `auth` policy on sign-in/MFA/reset/setup, per-account lockout (`Account_locks_after_repeated_failures...`); proxy forwarding test pending (KL-012) |
| R-24.4 | 24 | No secrets in source control | 1 | Done | `.gitignore`, generated `.env`, secret scan in `security-audit.ps1` |
| R-24.5 | 24 | Least-privilege database accounts | 1 | Done | `database/init/01-roles-and-databases.sh`; tests `Runtime_account_cannot_change_the_schema`; `verify-database.ps1` |
| R-24.6 | 24 | Append-only ledgers, controlled corrections | 1, 4-9 | Partial | Trigger mechanism + audit table |
| R-24.7 | 24 | SQL-injection protection | 1 | Partial | EF Core / parameterised Npgsql only |
| R-25.x | 25 | Required test catalogue | each stage | Partial | Foundation, health, constraints, permissions (DB), layering |
| R-26.1 | 26 | Workspace, build/DB/API/app/test/audit/production tasks, debug configs | 1 | Done | `.vscode/tasks.json`, `.vscode/launch.json` |
| R-26.2 | 26 | PowerShell commands without a permanent execution-policy change | 1 | Done | All tasks use `-ExecutionPolicy Bypass -File` per process; `docs/development.md` |
| R-27.1 | 27 | `.env.example` without secrets | 1 | Done | `.env.example` contains only `CHANGE_ME` placeholders |
| R-27.2 | 27 | Architecture document, traceability matrix, known-limitations register | 1 | Done | `docs/` |
| R-27.3 | 27 | Release manifest with SHA-256 checksums | 1, 17 | Partial | `scripts/build-production.ps1` writes `release/SHA256SUMS.txt` |
| R-27.4 | 27 | Cashier, owner, collector and archive-admin guides; offline installer | 17 | Planned | |
| R-27.5 | 27 | Backup and restore guide | 2 | Done | `docs/backup-and-restore.md` |
