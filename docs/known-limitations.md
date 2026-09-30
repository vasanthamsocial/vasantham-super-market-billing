# Known-limitations register

Updated at the end of every stage. "Planned fix" names the stage expected to resolve the item.

| ID | Limitation | Impact | Planned fix |
|---|---|---|---|
| KL-001 | No authentication or authorization yet. The only API endpoints are health and system info, and they are public. | Do not expose the development API on a network. | Stage 3 |
| KL-002 | Development runs over HTTP on localhost. | Not suitable for LAN use yet. | Stage 15 (LAN TLS) |
| KL-003 | Web CSP allows `'unsafe-inline'` scripts (needed by Next.js without nonces). | Weaker XSS defence-in-depth. | Stage 15 (nonce-based CSP) |
| KL-004 | No backup or restore tooling yet. | Development data is not protected. | Stage 2 hardening / Stage 17 |
| KL-005 | ESLint is not configured for the web apps; only TypeScript strict type checks run. | Style and some bug classes not linted. | Stage 5 |
| KL-006 | Integration tests share the `supermarketbilling_test` database and append audit rows that cannot be deleted (by design). | Test DB grows slowly; recreate the volume if needed. | Accepted |
| KL-007 | Playwright tests run against the development database through the running API. | They only read health and system info today; tests that write data will need an isolated database. | Stage 5 |
| KL-008 | Production installation without Docker (Windows services, bundled runtimes) is not written yet. | Only development setup is available. | Stage 17 |
| KL-009 | The target framework is .NET 10, not .NET 8 as originally specified (see D-001). | None functionally. | Accepted |
| KL-010 | Counter peripherals (printer, drawer, scale, customer display) need an integration approach decision (O-001). | POS hardware not yet supported. | Stage 5 |
| KL-011 | GST logic will need review by the business's chartered accountant before live use. | Legal compliance risk if skipped. | Each tax-related stage |
| KL-013 | Windows 11 Smart App Control is enforcing on the development PC and blocks some locally built, unsigned assemblies (seen: `SupermarketBilling.UnitTests.dll`, error 0x800711C7). The block is reputation-based, so which files get blocked is unpredictable. | Test runs fail with "Catastrophic failure ... An Application Control policy has blocked this file". The same risk applies to unsigned release binaries on a store server with Smart App Control on. | Dev: owner decision (see O-006). Production: code-signed releases (Stage 17) |
| KL-012 | Forwarding of the client address (`X-Forwarded-For`) from web apps to the API is configured but not yet covered by an automated test. | Per-client rate limits could silently degrade to one shared bucket. | Stage 3 (with login throttling tests) |
