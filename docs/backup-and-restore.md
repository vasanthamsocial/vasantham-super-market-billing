# Backup and restore guide

For the owner, manager or support person responsible for protecting the store's data.

## What a backup is

Each backup is one file, for example `supermarketbilling_20260930T064307Z.sbbak`, with two companions:

| File | Purpose |
|---|---|
| `*.sbbak` | The whole database, **encrypted** (AES-256-GCM, key derived from the backup passphrase). Unreadable without the passphrase. |
| `*.sbbak.sha256` | SHA-256 checksum, to detect a damaged copy (for example a failing pendrive). |
| `*.sbbak.restore-test.json` | Result of the automatic restore test: when it ran, tables and rows compared, and any problems. |

Guarantees:

- **Consistent:** the backup is one snapshot of the database, taken safely while billing continues.
- **Tamper-evident:** changing, removing, reordering or truncating any part of the file is detected.
- **Proven:** every new backup is restored into a temporary database straight away. Every table's row count must
  match, and the full database verification (permissions, immutability, exact-decimal rules) must pass.
  The temporary database is then deleted.
- **Private:** no unencrypted copy of the data is ever written to disk during backup or restore.

## The backup passphrase - read this first

The passphrase is `SB_BACKUP_PASSPHRASE` in `.env`. **Without it, no backup can ever be restored.**

- Write it down and keep it somewhere safe **away from the store server**, for example printed in a sealed
  envelope in a safe, or with the owner.
- If the server's disk fails, `.env` is lost with it. The written copy is then the only way back.
- If you change the passphrase, keep the old one too, for restoring older backups.

## Commands (Windows PowerShell, from `E:\Billing Software`)

```powershell
# Create a backup of the live database, then restore-test it automatically
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup.ps1

# Back up straight onto an encrypted pendrive (the drive letter will differ)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\backup.ps1 -OutDir F:\SB-Backups

# Re-check a copy (for example after copying it to a pendrive): checksum + full decryption, no restore
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-test.ps1 -File F:\SB-Backups\<file>.sbbak -VerifyOnly

# Full restore test of the newest backup (or of -File <path>)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore-test.ps1
```

The same actions are in VS Code under **Terminal > Run Task > Backup: ...**.

## Restoring

### Into a new database (safe, for checking or recovering individual records)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore.ps1 -File backups\<file>.sbbak -Target supermarketbilling_copy
```

### Replacing the live database (disaster recovery)

1. Stop billing: close the API (`Ctrl+C` in its window or stop the service).
2. Run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\restore.ps1 -File <file>.sbbak -Target supermarketbilling -Replace -ConfirmDatabase supermarketbilling
   ```

3. What happens:
   1. The backup's checksum is checked.
   2. It is restored into a staging database and fully verified.
   3. Only if everything passes is the current live database **renamed** to
      `supermarketbilling_pre_restore_<time>`. It is never deleted.
   4. The staging database becomes `supermarketbilling`.
   5. If anything fails, the live database is untouched.
4. Start the API and check `http://localhost:5080/health/ready`.
5. When you are sure the restore is correct, an administrator can drop the `_pre_restore_` copy.

## Recommended routine

| When | What |
|---|---|
| Every day at closing | `backup.ps1`. Keep it on the server's second disk if there is one. |
| Every day | Copy the three files to an **encrypted** pendrive and run `-VerifyOnly` on the copy. Rotate at least two pendrives and keep one off-site. |
| Every month | Run a full `restore-test.ps1` on the pendrive copy, on a different computer if possible. |
| Before upgrades or tax-mode changes | Take a backup first. |

Never run the live database from a pendrive. Pendrives are for backup copies only.

## Current limitations

See [known-limitations.md](known-limitations.md):

- Scheduling is manual for now (KL-014).
- Old backups are not pruned automatically (KL-015).
- There is no point-in-time recovery between backups (KL-016).
- On store servers without Docker, the native PostgreSQL tools come with the Stage 17 installer (KL-017).
