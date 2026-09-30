# Known-limitations register

Updated at the end of every stage. "Planned fix" names the stage expected to resolve the item.

| ID | Limitation | Impact | Planned fix |
|---|---|---|---|
| KL-001 | ~~No authentication or authorization.~~ Resolved in Stage 3 for the API, Billing Web, Owner Dashboard and Collection App. Owner Archive Web still has no sign-in (see KL-021). | - | Closed |
| KL-002 | Development runs over HTTP on localhost. Cookies are sent without the Secure flag in Development only (`Security:SecureCookies=false`). | Not suitable for LAN use yet. | Stage 15 (LAN TLS; Secure cookies are already the default outside Development) |
| KL-003 | Web CSP allows `'unsafe-inline'` scripts (needed by Next.js without nonces). | Weaker XSS defence-in-depth. | Stage 15 (nonce-based CSP) |
| KL-004 | ~~No backup or restore tooling.~~ Resolved in Stage 2. | - | Closed |
| KL-005 | ESLint is not configured for the web apps; only TypeScript strict type checks run. | Style and some bug classes not linted. | Stage 5 |
| KL-006 | ~~Integration tests share one test database.~~ Resolved in Stage 3: each test fixture creates and drops its own database. | - | Closed |
| KL-007 | ~~Playwright tests use the development database.~~ Resolved in Stage 3: they use a recreated `supermarketbilling_e2e` database, an API on :5181 and web apps on :3100-3103. | - | Closed |
| KL-008 | Production installation without Docker (Windows services, bundled runtimes) is not written yet. | Only development setup is available. | Stage 17 |
| KL-009 | The target framework is .NET 10, not .NET 8 as originally specified (see D-001). | None functionally. | Accepted |
| KL-010 | Counter peripherals (printer, drawer, scale, customer display) need an integration approach decision (O-001). | POS hardware not yet supported. | Stage 5 |
| KL-011 | GST logic will need review by the business's chartered accountant before live use. | Legal compliance risk if skipped. | Each tax-related stage |
| KL-012 | Forwarding of the client address (`X-Forwarded-For`) from the web apps to the API is configured but not covered by an automated test. | Behind the web proxy, per-client rate limits could silently become one shared bucket. | Stage 15 |
| KL-013 | Windows Smart App Control blocks some unsigned, locally built assemblies (seen on the development PC, now turned off there). | The same risk applies to unsigned release binaries on a store server with Smart App Control on. | Stage 17 (code-signed releases) |
| KL-014 | Backups are not scheduled automatically. | A forgotten day means no backup for that day. | Stage 17 (scheduled task, with a failure alert on the Owner Dashboard in Stage 12) |
| KL-015 | Old backups are never deleted automatically (no retention policy). | The backup folder grows. | Stage 17 |
| KL-016 | No point-in-time recovery (WAL archiving). Recovery is to the last backup only. | Transactions after the last backup would be lost if the disk failed. | Stage 17 |
| KL-017 | `sb-backup` runs pg_dump/pg_restore inside the Docker container; the native mode for servers without Docker is not built. | Production servers need the Stage 17 installer. | Stage 17 |
| KL-018 | Backup key derivation uses PBKDF2-SHA256 (600,000 iterations), not a memory-hard KDF. | A weak passphrase is easier to brute-force from a stolen backup. Generated passphrases are 40 random characters, which makes this impractical. | Accepted |
| KL-019 | A locked account gets a distinct "temporarily locked" message (HTTP 423), which reveals that the username exists. | Minor username enumeration by someone on the LAN, limited by the sign-in rate limit (10 per minute per client). Chosen so staff understand why they cannot sign in. | Accepted |
| KL-020 | MFA secrets are encrypted with `Security__DataProtectionKey` from `.env`. There is no key-rotation tool, and a database restored without the same key makes existing MFA enrolments unusable. | Losing the key means every MFA user must enrol again (a manager can reset). | Key kept with the backup passphrase (see backup guide); rotation in Stage 15 |
| KL-021 | Owner Archive Web has no sign-in yet; it only shows status and the "not enabled" notice. It has no data and no archive API. | None today. It must not be enabled until Stage 14. | Stage 14 |
| KL-022 | Rate limits are kept in the API's memory, per client address. They reset when the API restarts and are not shared between several API instances. | Adequate for the single store server design. | Accepted |
| KL-023 | Approval notes and rejection reasons are entered with a basic browser prompt. | Functional but plain. | Stage 12 (Owner Dashboard approvals UI) |
| KL-024 | If a new user's first (privileged) role is rejected, the account remains with no roles. It can sign in but sees no business. | A manager should disable such accounts. | Stage 12 ("reject and disable" option) |
| KL-025 | Session idle timeout (30 minutes) and absolute lifetime (12 hours) are the same for every role and device. | Counters may want shorter timeouts, and the owner's phone longer ones. | Stage 5 (per-device and per-role session policy) |
