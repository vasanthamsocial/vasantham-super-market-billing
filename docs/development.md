# Development guide (Windows)

All commands are Windows PowerShell 5.1 compatible and run from the repository root (`E:\Billing Software`).
Scripts are started with `powershell -NoProfile -ExecutionPolicy Bypass -File ...`. The bypass applies only
to that one process. **Do not** run `Set-ExecutionPolicy`; it is not needed.

## One-time setup

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\setup-dev.ps1
npm run install-browsers --workspace tests/end-to-end
```

`setup-dev.ps1` creates `.env` from `.env.example` with freshly generated random database passwords. `.env` is
git-ignored. If you need to start over, stop the database, delete the Docker volume
(`docker volume rm supermarketbilling-db-data`, **which destroys dev data**), delete `.env`, and run setup again.

## First run and signing in

1. Start the API (`scripts\run-api.ps1`). While no users exist, it writes a one-time code to
   `src\SupermarketBilling.Api\App_Data\setup-code.txt` and logs that path (never the code).
2. Open Billing Web (http://localhost:3000) and complete **First-time setup** with that code. This creates the
   business, the first store and the owner account, and deletes the code file.
3. Sign in as the owner. Add stores and users under **Stores** and **Users**. New users sign in with the temporary
   password and must choose their own.
4. Granting a privileged role (Owner, Manager, Accountant, Auditor, Support administrator) needs a second person
   to approve it under **Approvals**. In a business where nobody else could approve, the grant is applied and the
   waiver is recorded in the audit trail.

## Test isolation

- **Integration tests** create a throw-away database (`supermarketbilling_test_it_<random>`) per test fixture
  and drop it afterwards. They never touch the development database.
- **End-to-end tests** (`npm run test:e2e`) recreate `supermarketbilling_e2e`, start their own API on :5181 and
  the web apps on :3100-3103 (built into `.next-e2e`). They can run while your development servers are running.

## Ports

| Port | Service |
|---|---|
| 5442 | PostgreSQL 16 (Docker, bound to 127.0.0.1) - operational + `_test` databases |
| 5443 | PostgreSQL 16 archive database (only with `-Archive`) |
| 5080 | API |
| 3000-3003 | Billing Web, Owner Dashboard, Collection App, Owner Archive Web |
| 5181, 3100-3103 | End-to-end test API and web apps (started and stopped by Playwright) |
| 47800 | Counter agent on a counter PC (127.0.0.1 only; also started by Playwright for end-to-end tests) |

Port 5432 is intentionally **not** used, because this machine already runs a separate PostgreSQL 17 service
on it that belongs to another system. SupermarketBilling never connects to it.

## Counter hardware (counter agent)

On a billing counter PC, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-counter-agent.ps1`.
The first run creates `%LOCALAPPDATA%\SupermarketBilling\counter-agent.json` with a pairing token and every
device off. Edit it:

- `AllowedOrigins`: the billing web address used on this PC, for example `http://store-server:3000`.
- `Printer`: `Transport` = `WindowsPrinter` (with `Name` as shown in Windows), `Tcp` (`Host`, `Port` 9100) or
  `Serial` (`SerialPort`, `BaudRate`); `Columns` 48 for 80 mm paper, 32 for 58 mm. `DrawerEnabled` if a cash drawer
  is plugged into the printer.
- `Scale` and `Display`: `Serial` with their COM ports (the display uses the CD5220 command set).

Restart the agent, then in the POS press F11, paste the pairing token, Test connection, Save. The agent listens on
127.0.0.1:47800 only and answers only the allowed billing address with the right token (D-022).

## Database accounts

| Account | Purpose | Rights |
|---|---|---|
| `sb_admin` | Container superuser | Administration only; never used by the application |
| `sb_migrator` | Schema owner | Applies migrations (DDL) |
| `sb_app` | API runtime | SELECT/INSERT/UPDATE/DELETE on tables; cannot create, alter, drop or truncate |

Append-only tables (for example `audit_events`) also carry triggers that reject UPDATE, DELETE and TRUNCATE for every role.

## Migrations

```powershell
# Create a new migration after changing the model
dotnet ef migrations add <Name> --project src/SupermarketBilling.Infrastructure --output-dir Persistence/Migrations
# Apply to dev and test databases and export database/migrations/supermarketbilling.sql
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\db-migrate.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\db-migrate.ps1 -Target Test
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-database.ps1
```

Rules for every migration:

- Money, quantity and rate columns are `decimal` → `numeric(18,4)` by default; override explicitly when needed.
  `verify-database.ps1` fails on any `real`/`double precision` column or unbounded `numeric`.
- Ledgers and audit tables call `AppendOnlySql.Protect("<table>")`.
- Never edit a migration that has been applied anywhere outside your machine; add a new one.

## Useful manual checks

```powershell
Invoke-RestMethod http://localhost:5080/health/live
Invoke-RestMethod http://localhost:5080/health/ready
Invoke-RestMethod http://localhost:5080/api/v1/system/info
Invoke-RestMethod http://localhost:3000/api/v1/system/info   # same call through the Billing Web proxy
```
