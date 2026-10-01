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
| KL-010 | ~~Counter peripherals are not supported yet.~~ Resolved in Stage 5c (counter agent, D-022). | - | Closed |
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
| KL-026 | Edge-to-cloud sync is not built. Edge and cloud installations exist as modes, but nothing flows between them yet. | The Owner Dashboard only sees the installation it is connected to. | SaaS stage S1 |
| KL-027 | Licensing and subscriptions are not enforced. `Security:MaxBusinesses` is a per-company limit set in configuration, not a signed licence. | No commercial enforcement yet. | SaaS stage S1 |
| KL-028 | Cloud companies can be created only through the API with the provisioning key. There is no vendor admin console, and no company suspension or deletion. | Vendor operations are manual. | SaaS stage S1 |
| KL-029 | `sb-backup` backs up the whole database. In the cloud that means all companies together; there is no per-company export or restore yet. | Cloud restores are all-or-nothing. | SaaS stage S1 |
| KL-030 | A product's GST rate or HSN change applies immediately; scheduled future tax-rate changes (for example a notified GST rate change on a date) are not supported yet. | Changes must be made on the day they apply; invoices keep the rate they were issued with. | Stage 5 |
| KL-031 | Batch-specific prices are not available yet (batches now exist; price rules cannot target one). | Use MRP-specific prices meanwhile. | Stage 5 |
| KL-032 | A price or tax request whose approval expires unanswered stays pending (unused) until someone rejects or cancels it. | Clutter only; expired requests never apply. | Stage 12 |
| KL-033 | The 'backup taken' requirement for a tax-registration change is a declared confirmation, not checked against the backup reports. | Relies on the accountant's honesty (recorded in the audit trail). | Stage 17 (backup status reported to the API) |
| KL-034 | The catalogue is not shared between businesses of the same company; each business keeps its own products. | Duplicate entry for companies with several legal businesses. | Later, if needed |
| KL-035 | Serial numbers are not tracked per unit yet (the product flag exists; no serial register). | Serialised goods (electronics) cannot be traced unit by unit. | Stage 7 (captured at GRN) |
| KL-036 | Stock documents cannot be cancelled or reversed as a pair; a correction is a new opposite document. | Slightly more work to undo a mistake; the trail stays complete. | Stage 12 (one-click reversal document) |
| KL-037 | A transfer moves stock in one step (out of the source and into the destination at once). There is no in-transit state or receiving confirmation at the destination. | Goods lost on the way would be found only at the next count. | Stage 11 (dispatch and receiving) |
| KL-038 | Stock reports show at most 2,000 items per store, 1,000 ledger lines per item and 200 recent documents, without paging or export. | Large catalogues need the search box. | Stage 12 (reports with paging and export) |
| KL-039 | A count posts the difference from the book quantity at the moment it is posted; sales made between counting the shelf and posting are not frozen out. | Count during quiet hours, or post per aisle soon after counting. | Stage 5 (count freeze with POS) |
| KL-040 | Ageing buckets use the date stock was received into the store (a transfer restarts the age). | Ageing after transfers looks younger than the goods are. | Stage 12 |
| KL-041 | Credit sales, customer accounts, and member and customer-group prices at the counter are not available yet; the POS bills walk-in (retail or wholesale) customers and records buyer details for GST. | Credit customers must pay at the counter for now. | Stage 8 |
| KL-042 | Invoices are not yet tied to a shift, opening cash or a till count. | Cash at a counter cannot be reconciled per shift yet. | Stage 6 |
| KL-043 | The grand total is always rounded to the nearest rupee; this is not a setting. | Businesses that bill to the paisa cannot turn it off. | Later, if requested |
| KL-044 | GST e-invoicing (IRN and signed QR code) and e-way bills are not supported. | Needed only above the turnover thresholds and for certain consignments; such businesses must generate them separately. | Not scheduled (needs GSP access) |
| KL-045 | ~~The POS screen, parked bills, receipt printing and PDF invoices are not built yet.~~ Resolved in Stage 5b. | - | Closed |
| KL-046 | ~~Returns, exchanges and refunds are not built yet.~~ Resolved in Stage 5c (credit notes, D-021). | - | Closed |
| KL-047 | A B2C inter-state invoice above Rs. 2.5 lakh does not enforce the buyer's address. | The cashier must enter it. | Stage 12 (GST report validation) |
| KL-048 | PDF invoices show Latin text only (the built-in PDF fonts); names or addresses in other scripts print as '?'. Receipts printed from the browser show every script. | Tamil customer names appear as '?' on the PDF. | Later (embed a Unicode font) |
| KL-049 | ~~Receipts print only through the browser's print dialog.~~ Resolved in Stage 5c: with the counter agent, receipts print directly. | - | Closed |
| KL-050 | Parked bills never expire on their own. | Old parked bills stay listed until retrieved. | Stage 6 (cleared at shift close) |
| KL-051 | The POS needs the store server to be reachable; billing during a network outage is not supported yet. | A LAN failure stops billing. | Stage 13 (controlled offline operation) |
| KL-052 | The counter agent runs from `scripts\run-counter-agent.ps1` (or the published `sb-counter-agent.exe`); it is not yet installed as a Windows service that starts with the PC. | Someone must start it on each counter PC. | Stage 17 (installer) |
| KL-053 | Receipts printed by the agent use code page 1252: names in other scripts print as '?'. The browser receipt shows every script. | Tamil names on printed receipts appear as '?'. | Later (raster printing) |
| KL-054 | The scale reader understands scales that send text lines with the weight (for example `ST,GS,+  1.235kg`); scales that need a request command or a binary protocol are not supported. Customer displays must use the CD5220 command set. | Some scale and display models need configuration or are unsupported. | When a specific model is chosen |
| KL-055 | Returns must be taken in the store that issued the invoice; there is no time limit on returns, store credit never expires, and the refund method is not tied to how the bill was paid. | Store policy must be applied by staff. | Stage 12 (return policy settings) |
| KL-056 | A returned batch-tracked item goes back into the batch the sale took most from; if one line was sold from several batches the split is not reproduced. | Batch quantities can drift slightly after such returns (the next count corrects them). | Later, if needed |
| KL-057 | Credit notes are not printed on the receipt printer; they are given as a PDF. | The customer gets an A4 credit note. | Later |
