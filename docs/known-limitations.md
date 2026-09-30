# Known-limitations register

Updated at the end of every stage. "Planned fix" names the stage expected to resolve the item.

| ID | Limitation | Impact | Planned fix |
|---|---|---|---|
| KL-001 | No authentication or authorization yet. The only API endpoints are health and system info, and they are public. | Do not expose the development API on a network. | Stage 3 |
| KL-002 | Development runs over HTTP on localhost. | Not suitable for LAN use yet. | Stage 15 (LAN TLS) |
| KL-003 | Web CSP allows `'unsafe-inline'` scripts (needed by Next.js without nonces). | Weaker XSS defence-in-depth. | Stage 15 (nonce-based CSP) |
| KL-004 | ~~No backup or restore tooling yet.~~ Resolved in Stage 2 (`sb-backup`, see backup-and-restore.md). | - | Closed |
| KL-005 | ESLint is not configured for the web apps; only TypeScript strict type checks run. | Style and some bug classes not linted. | Stage 5 |
| KL-006 | Integration tests share the `supermarketbilling_test` database and append audit rows that cannot be deleted (by design). | Test DB grows slowly; recreate the volume if needed. | Accepted |
| KL-007 | Playwright tests run against the development database through the running API. | They only read health and system info today; tests that write data will need an isolated database. | Stage 5 |
| KL-008 | Production installation without Docker (Windows services, bundled runtimes) is not written yet. | Only development setup is available. | Stage 17 |
| KL-009 | The target framework is .NET 10, not .NET 8 as originally specified (see D-001). | None functionally. | Accepted |
| KL-010 | Counter peripherals (printer, drawer, scale, customer display) need an integration approach decision (O-001). | POS hardware not yet supported. | Stage 5 |
| KL-011 | GST logic will need review by the business's chartered accountant before live use. | Legal compliance risk if skipped. | Each tax-related stage |
| KL-013 | Windows 11 Smart App Control is enforcing on the development PC and blocks some locally built, unsigned assemblies (seen: `SupermarketBilling.UnitTests.dll`, error 0x800711C7). The block is reputation-based, so which files get blocked is unpredictable. | Test runs fail with "Catastrophic failure ... An Application Control policy has blocked this file". The same risk applies to unsigned release binaries on a store server with Smart App Control on. | Dev: owner decision (see O-006). Production: code-signed releases (Stage 17) |
| KL-014 | Backups are not scheduled automatically; someone must run `backup.ps1` (or the VS Code task). | A forgotten day means no backup for that day. | Stage 17 (Windows scheduled task with failure alert on the Owner Dashboard, Stage 12) |
| KL-015 | Old backups are never deleted automatically (no retention policy). | Backup folder grows about 10 KB per backup today, more once real data exists. | Stage 17 |
| KL-016 | No point-in-time recovery (WAL archiving). Recovery is to the last backup only. | Transactions after the last backup would be lost if the disk failed. | Stage 17 |
| KL-017 | `sb-backup` runs pg_dump/pg_restore inside the Docker container. The native-tools mode for store servers without Docker is not built yet. | Production servers need the Stage 17 installer. | Stage 17 |
| KL-018 | Backup key derivation uses PBKDF2-SHA256 (600,000 iterations), not a memory-hard KDF such as Argon2id. | A weak passphrase is easier to brute-force from a stolen backup. Generated passphrases are 40 random characters, which makes this impractical. | Accepted |
| KL-012 | Forwarding of the client address (`X-Forwarded-For`) from web apps to the API is configured but not yet covered by an automated test. | Per-client rate limits could silently degrade to one shared bucket. | Stage 3 (with login throttling tests) |
