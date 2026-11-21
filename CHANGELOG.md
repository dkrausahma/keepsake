# Changelog

All notable changes to Keepsake Backup. Newest first; versions as in `Directory.Build.props`.

## 0.9.12 – 2026-11-21

- **Check keys** per job (Backup jobs page): test the repository password or the recovery key against the job's
  repository without restoring. Only the key files are read; nothing is locked, written, stored or logged.
  Typos in a recovery key are reported as such (checksum).
- `keepsake key verify --repo <location>` does the same without the service (exit code 4 if not valid).
- Service API: `repo.verifyKey`.

## 0.9.11 – 2026-11-21

- System.Text.Json 8.0.5 → 10.0.15 (.NET 8 support ended in November 2026; SEC-029). Ships one new library,
  `System.IO.Pipelines.dll`.

## 0.9.10 – 2026-10-19

- Job editor: hosts ending in `.your-storagebox.de` on port 22 (ProFTPD mod_sftp, AES-CTR only) show a hint
  and a **Use port 23** button (OpenSSH, AES-GCM). It clears the pinned host key, re-runs the destination check
  and falls back to port 22 if port 23 is not usable. New jobs with a Storage Box host start on port 23.
- The SFTP debug log is now a setting, off by default: *Settings → Write a debug log* (service,
  `logs\debug.log`, 5 MB × 2, applied without restart), `--debug-log <file>` for every CLI command, and a
  checkbox under *Open backup* in recovery mode. `keepsake sftp-test` always writes `%TEMP%\keepsake-debug.log`.
- The temporary always-on SFTP debug logging from 0.9.9 is removed.

## 0.9.9 – 2026-10-18

Versions 0.9.3–0.9.8 were intermediate test builds; their changes are listed here.

- **GUI recovery mode (RST-07):** `KeepsakeGui.exe --recovery` (portable ZIP: `Keepsake Recovery.cmd`), or
  *Restore without the service…* when the service is not reachable, opens a repository directly – folder, USB,
  share or `sftp://` – with password or recovery key, and restores without the service or any configuration.
- **Fixed:** SFTP "MAC error" on Windows Server 2008 R2 with AES-CTR + HMAC servers (e.g. Hetzner Storage Box
  port 22). AES-CTR and all HMACs are now computed with BouncyCastle (`ManagedAesCtrCipher`, `ManagedHmac`).
- Same cipher preference on every Windows version: ChaCha20-Poly1305, AES-GCM, then AES-CTR with
  encrypt-then-MAC. On "MAC error" other algorithm combinations are retried.
- Destination check, run log and `sftp-test` show and rate the negotiated encryption (strong / good / weak).
- New `keepsake sftp-test`: tests each key exchange, cipher and MAC on its own.

## 0.9.2 – 2026-10-18

Hardening after the internal security audit (`audits/SECURITY_AUDIT_REPORT.md`, SEC-001…029).

- Data folder: links refused, ownership and ACLs reset at every service start; the MSI creates and locks
  `%ProgramData%\Keepsake`. Stored secrets use DPAPI for the service account (`dpapi-u:`); old values are
  converted (SEC-001).
- The MSI locks the install folder; system tools are started by full path (SEC-002).
- Restore never follows junctions or links below the target and rejects invalid names (SEC-003, SEC-017);
  folder storage never lists, writes or deletes through links (SEC-004).
- Pipe clients verify the server's identity; the server claims the pipe name exclusively (SEC-005). GUI
  `--pipe` is Debug-only (SEC-016).
- Login: serialised password checks, lockout on every attempt, password change ends other sessions, bounded
  sessions, requests and connections (SEC-013…015).
- Untrusted repositories: bounded Argon2 parameters and key slots, validated listings and pack trailers
  (SEC-007, SEC-009); the tool copy in the repository is verified and repaired after every backup (SEC-006).
- Minimum repository password length, regex timeout for exclusions, owner-only recovery-key files, auto-cleared
  clipboard, SMTP warning without TLS, CSV formula neutralising (SEC-019…023, SEC-029).
- Release script: optional Authenticode signing, `SHA256SUMS.txt`, no local paths in binaries; NuGet source
  mapping and lock files (SEC-008, SEC-025, SEC-028).
- Product name in the GUI and docs is now **Keepsake Backup** (file, service and command names unchanged).

## 0.9.1 – 2026-10-17

- **Fixed:** the "Create the first job" button could not be clicked (the job list lay on top of it).
- The installer lets you choose the install folder; default `%ProgramFiles%\dkait\keepsake`. Upgrades reuse
  the folder of the first install.
- Kraus-Ahma IT Consulting branding (theme, logos, icon) and an About dialog.

## 0.9.0 – 2026-10-17

First complete version (milestones 1–6).

- **Core and format:** repository format v1 – FastCDC chunking, AES-256-GCM, HMAC-SHA256 blob IDs, Zstandard,
  pack files, index, snapshots, locks; Argon2id password slots and `KS1-` recovery keys. Backup (incremental /
  full, exclusions, resume), restore (original location or target, conflict policies, dry run), check.
- **Service:** `keepsake-service.exe` (LocalSystem) with scheduler (interval, daily, monthly, catch-up,
  retries), job runner (parallel jobs, cancel, pre/post commands), run history and a named-pipe API protected
  by an application password.
- **GUI:** WPF – jobs dashboard, job editor with folder tree, restore browser, history with issue list and CSV
  export, settings.
- **Destinations:** folders and UNC shares with credentials, USB disks by volume ID (rotation), SFTP with
  host-key pinning.
- **Robustness:** VSS with fallback, SeBackupPrivilege, retention and prune, scheduled integrity checks,
  e-mail notifications, "no successful backup" alerts, Event Log.
- **Release:** WiX MSI, portable ZIP, `keepsake doctor`, `keepsake repair`, disaster-recovery runbook,
  Windows Server 2008 R2 guide and test plan.
