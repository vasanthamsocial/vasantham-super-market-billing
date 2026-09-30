# SupermarketBilling - Architecture

Version 0.1 (Stage 1). This document describes the target architecture and marks what exists today.
Decisions and open questions are in [decisions.md](decisions.md).

## 1. Goals and constraints

1. **Billing must never depend on the internet.** Everything needed to bill, receive goods, and reconcile runs
   on the store's own server and LAN. Internet is optional: WhatsApp and SMS, updates, and off-site backups.
2. **Money is exact.** Decimal arithmetic end to end. The server calculates and validates every total.
3. **History is immutable.** Financial and stock records are append-only. Corrections are new, approved entries.
4. **One complete, auditable set of books.** There are no hidden records or alternate ledgers. Report "views"
   (GST-documented, non-GST source, consolidated) are filters over the same data and always labelled.
5. **Least privilege everywhere.** Server-side authorization, scoped by business, store, route and role, plus
   least-privilege database accounts.

## 2. Deployment topology (store)

```text
                 Store LAN (no internet required)
 +---------------------------------------------------------------+
 |  Store server (main billing computer, internal SSD)            |
 |   - PostgreSQL 16 (operational DB, 127.0.0.1 only)             |
 |   - SupermarketBilling API  (Kestrel, HTTPS on LAN)            |
 |   - Next.js servers: Billing Web, Owner Dashboard,             |
 |     Collection App (standalone Node, bundled assets)           |
 |   - Background workers: messaging outbox, backups              |
 |                                                               |
 |  Counter 1..n  -> browser -> Billing Web (+ peripherals, O-001) |
 |  Owner PC/phone -> browser -> Owner Dashboard                  |
 +---------------------------------------------------------------+
        |  optional internet: WhatsApp/SMS, updates, off-site backup
        |  field collectors reach the server via VPN (O-002)
 +---------------------------------------------------------------+
 |  Optional archive server (separate machine, separate disk)     |
 |   - PostgreSQL archive DB + Archive API + Owner Archive Web    |
 +---------------------------------------------------------------+
```

- The live database stays on the server's internal SSD. **Never run it from a pendrive.** Encrypted pendrives are
  used only for backups, restore packages, exports and disaster-recovery copies.
- Development uses Docker Compose for PostgreSQL. Production runs PostgreSQL and the services natively as
  Windows services (Stage 17 installer), so Docker is not needed on the store server.

## 3. Logical architecture

A modular monolith: one API process, with modules separated by namespace and folder, and clean layering.

```text
Api (HTTP, auth, validation, OpenAPI)
  -> Application (use cases, ports such as IAuditTrail, authorization rules)
       -> Domain (entities, value objects, invariants; no framework dependencies)
  -> Infrastructure (EF Core/PostgreSQL, messaging providers, file storage, backups)
```

Layering is enforced by `tests/unit/.../Architecture/LayeringTests.cs`.

Planned modules: Identity and Access, Organisation (businesses, stores, counters, devices), Tax Registration,
Catalog, Pricing, Inventory, Sales/POS, Shifts and Cash, Purchasing/GRN, Parties (suppliers and debtors),
Receivables and Collections, Messaging, Dispatch and Packing, Accounting, Reporting, Archive, Audit.

## 4. Data architecture

| Concern | Rule |
|---|---|
| Identifiers | UUIDv7 primary keys generated in the app (safe offline). Separate human document numbers. |
| Money / quantity | `numeric(18,4)` default; explicit precision per column where different. No float columns (verified by `database/verification`). |
| Time | `timestamptz`, stored in UTC; business dates (invoice date, due date) as `date` in the store's time zone. |
| Ledgers | Stock ledger, party (debtor/supplier) ledger, cash/bank ledger and audit events are append-only, enforced by triggers. Balances are derived from ledgers, with projections for speed. |
| Effective-dated masters | Tax registration mode, prices and settings use validity ranges, with exclusion constraints preventing overlap. Transactions snapshot the rule they used (for example rate ID on invoice lines). |
| Document numbers | Gapless series per business / store / counter / document type / financial year, allocated under a row lock in the posting transaction. |
| Idempotency | Every state-changing command carries an idempotency key, which is unique per device. Retries return the original result. |
| Concurrency | Stock and balance postings lock affected rows in a consistent order within one transaction. Optimistic concurrency tokens on editable masters. |
| Archival | Monthly signed packages with counts, totals and SHA-256. Source deletion is disabled by default and gated by approvals. |

## 5. Tax model

- Tax Registration Mode (`GST_REGULAR`, `GST_COMPOSITION`, `NOT_GST_REGISTERED`) is effective-dated per business.
  Changes need evidence, accountant review, independent approval, a backup and a new document series.
- Each invoice stores the mode, document type (tax invoice, bill of supply, commercial invoice) and all tax
  amounts at posting time. Later mode changes never alter it.
- The tax treatment of a sale follows the product's HSN/SAC and legal classification, not how the stock was bought.
- Purchase document classification (tax invoice, bill of supply, unregistered, import, reverse charge, pending,
  other) is stored on each purchase and used only for filtering and eligibility in reports.

GST rules implemented in code must be reviewed by the business's chartered accountant before production use.

## 6. Security architecture

| Area | Design | Status |
|---|---|---|
| Transport | HTTPS on the LAN with a locally issued certificate | Stage 15 |
| Sessions | Opaque random token in an `HttpOnly; Secure; SameSite=Strict` cookie. Only its hash is stored server-side. Idle and absolute expiry, revocation (D-009). | Done |
| CSRF | SameSite=Strict plus a synchronizer token in `X-CSRF-Token` on state-changing requests | Done |
| Passwords / MFA | PBKDF2-HMAC-SHA512 at 210k iterations (D-008). TOTP MFA with recovery codes, mandatory for privileged roles when configured. Secrets encrypted at rest. | Done |
| Brute force | Global and sign-in rate limits. Per-account lockout, with the user row locked so parallel guesses cannot bypass it. | Done |
| Authorization | Sign-in required by default. Permission-based checks scoped by business and store, with other businesses reported as not found. No granting beyond your own permissions. Maker-checker enforced by the database (D-010). | Done (routes in Stage 9) |
| Headers | CSP, nosniff, frame denial, referrer and permissions policies on API and web apps | Done (CSP nonces in Stage 15) |
| Secrets | `.env` generated locally, git-ignored, secret scan in `security-audit.ps1` | Done |
| Database | Least-privilege roles, append-only triggers, data checksums | Done |
| Audit | Immutable `audit_events`. Secrets never logged or stored in payloads. | Foundation done |

## 7. Offline behaviour

1. **Store level (primary):** the whole system runs on the LAN without internet. This is the normal mode.
2. **Counter level (Stage 13):** if a counter briefly loses the server, a controlled local queue with device
   identity, idempotency keys and limits holds bills. The server remains the authority on numbering and totals.
3. **Collection App (Stage 13):** trusted devices keep an encrypted IndexedDB queue (WebCrypto AES-GCM with a
   non-extractable device key). Receipts are provisional and marked pending until the server confirms. Sync is
   ordered, duplicates are rejected, and conflicts are quarantined for review.
4. **Messaging:** transactional outbox. Messages are queued in the same transaction as the business event and
   sent when connectivity exists. A messaging failure never reverses a financial transaction.

## 8. Web applications

Next.js (App Router, TypeScript), built as standalone Node servers with all assets bundled: no CDNs, online fonts
or third-party scripts (checked by an end-to-end test). Each app forwards `/api/*` and `/health/*` to the API
through runtime route handlers that read `API_INTERNAL_URL` per request, so an installed release can be
re-pointed without rebuilding (D-005). If the API is unreachable, the proxy returns a JSON 502. Shared UI and API client live in `packages/web-shared`. Every data view shows a freshness badge:
Live, Delayed, Cached or Unavailable.

## 9. Observability and health

- `GET /health/live`: process liveness, no dependencies.
- `GET /health/ready`: database reachable and schema current (no pending migrations). Returns 503 otherwise.
  Exception details are never returned.
- `GET /api/v1/system/info`: version, environment, server UTC time, licensed optional features.
- Structured logging with source-generated log messages. Secrets, passwords, MFA codes and tokens are never logged.

## 10. Backup and restore (Stage 2 hardening / Stage 17 guide)

`pg_dump` custom-format backups, encrypted, with SHA-256 checksums and a manifest. Automatic restore test into a
scratch database, and WAL archiving for point-in-time recovery. Copies go to an encrypted pendrive and an
optional off-site target. The archive system is not a substitute for backups.

## 11. Multi-tenancy and the hybrid SaaS model (D-013)

```text
 Vendor cloud (Deployment__Mode=Cloud)            Store A edge server (Mode=Edge)
 +----------------------------------+   sync      +------------------------------+
 | API + PostgreSQL (many tenants)  | <---------> | API + PostgreSQL (tenant A)  |
 | owner dashboard, licensing,      |  (stage S1) | counters on the LAN, works   |
 | backups, updates                 |             | with no internet             |
 +----------------------------------+             +------------------------------+
```

- **Tenant** = customer company. Every tenant-owned row carries `tenant_id`, stamped on insert by the persistence
  layer (`TenantStampingInterceptor`).
- **Tenant context**: resolved per request from the session (via `sb_session_tenant`), from the installation
  (edge, before sign-in), or from the company code (cloud sign-in, via `sb_tenant_by_code`). It is applied to every
  database connection as `sb.tenant_id` (`TenantConnectionInterceptor`).
- **Row-level security** policies compare `tenant_id` with `sb.tenant_id` on every table, for reads and writes.
  Composite foreign keys keep references inside one tenant. `verify-database.ps1` fails if any tenant table lacks
  RLS, or if the runtime account could bypass it.
- **Edge vs cloud**: identical code and schema. An edge database contains exactly one tenant, bound in the
  `installation` table at setup.
