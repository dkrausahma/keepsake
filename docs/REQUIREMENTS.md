# Keepsake – Requirements

Status: **Draft v0.2** · 2026-10-17
Product name: **Keepsake** (service: *Keepsake Backup Service*, CLI: `keepsake.exe`, code: `Keepsake.*`)

Priorities: **M** = must have in v1 · **S** = should have in v1 · **L** = later

---

## 1. Purpose and scope

Keepsake is an internal file backup application for Windows that backs up selected folders into
encrypted, compressed, deduplicated backup repositories on file systems or SFTP servers,
and restores them – also on a different machine, without the original Windows domain.

### 1.1 Constraints

| ID | Constraint |
|---|---|
| C-01 | Runs on **Windows Server 2008 R2 Enterprise SP1** and **Windows 10 / 11** (x64). |
| C-02 | Internal use only. No licensing, multi-tenancy or commercial distribution. |
| C-03 | Windows only. No Linux / macOS support required. |
| C-04 | Storage targets in v1: local disks, network shares (UNC), removable USB disks, SFTP. **No S3** for now; the storage layer must allow adding it later. |
| C-05 | A restore must work on any machine that has this software, access to the repository and the key – **independent of the original server, its configuration and its Windows domain**. |

### 1.2 Environment and sizing

| Item | Value |
|---|---|
| Current source data | ~130 GB, ~50,000 files, ~6,500 folders |
| Expected initial backup | ~200 GB (growth included) |
| Daily change rate | Small compared to the total (exact figure unknown) |
| Service account | **LocalSystem** |
| SMTP server | Available, details to be provided |
| Recovery key storage | **KeePass** (by the administrator) |
| Users | One application password is sufficient |
| GUI language | English only |

### 1.3 Out of scope (v1)

- Bare-metal / system image backup, system state, Active Directory
- Backing up and restoring NTFS permissions (ACLs) – see §4.11
- S3 and other cloud storage
- Database backups (planned, see §4.16)
- Linux / macOS
- Central management of multiple backup servers

---

## 2. Architecture overview

```
 ┌───────────────┐   ┌───────────────┐
 │  GUI (WPF)    │   │  CLI (.exe)   │
 └──────┬────────┘   └──────┬────────┘
        │   named pipe (authenticated)
 ┌──────▼─────────────────────▼────────┐
 │  Windows Service                    │
 │  scheduler · job runner · logs      │
 └──────────────────┬──────────────────┘
 ┌──────────────────▼──────────────────┐
 │  Core library (.NET Standard 2.0)   │
 │  scanner · chunker · crypto ·       │
 │  repository format · restore        │
 └───────┬────────────────────┬────────┘
   Sources (files, VSS;   Storage backends
   later: Postgres,       (file system, SFTP;
   MariaDB)               later: S3)
```

- The **service** runs backups independently of a logged-on user. Closing the GUI never stops a backup.
- **GUI and CLI** are clients of the service. They share the same API and the same authorization.
- The CLI additionally offers a **standalone restore mode** that works without an installed service (disaster recovery on a fresh machine).
- Sources and storage backends are **plugins behind interfaces**, so DB sources and S3 can be added without changing the core.

---

## 3. Technology decisions

| Decision | Choice | Rejected alternatives and why |
|---|---|---|
| Language / runtime | C#, **.NET Framework 4.8** (core as .NET Standard 2.0) | .NET 8: needs Server 2012+. Go: stuck on the unpatched 1.20 toolchain for 2008 R2. Electron: the last version supporting Win7/2008 R2 is old and insecure. Qt 6: no Win7. |
| GUI | **WPF** | WinForms: weaker layout and binding. Web UI: IE11 is the newest browser on 2008 R2. |
| GUI/CLI ↔ service | Local **named pipe** with authentication | Loopback HTTP: needs a port, firewall rules and CSRF/DNS-rebinding protection. |
| SFTP | SSH.NET | – |
| Encryption | AES-256-GCM (BouncyCastle), Argon2id key derivation | .NET Framework 4.8 has no built-in AES-GCM. |
| Compression | Zstandard (ZstdSharp) | Deflate: slower and weaker. |
| Storage format | Content-defined chunking, deduplicated repository (restic/Borg/Duplicati style) | Classic full + incremental archive chains: fragile, expensive retention. |

---

## 4. Functional requirements

### 4.1 Jobs

| ID | Requirement | Prio |
|---|---|---|
| JOB-01 | Any number of backup jobs can be configured, each with its own sources, exclusions, destination, schedule, retention and key. | M |
| JOB-02 | Create, edit, clone, enable/disable and delete jobs. Deleting a job never deletes the repository unless explicitly confirmed separately. | M |
| JOB-03 | Run a job manually ("Run now") and cancel a running job. A cancelled run leaves the repository consistent. | M |
| JOB-04 | Several jobs can run in parallel. Configurable maximum number of concurrent jobs (default 1). | S |
| JOB-05 | Pre- and post-run scripts per job (with timeout; the run is marked failed if the pre-script fails). | S |
| JOB-06 | Export/import job configuration (without secrets). | S |
| JOB-07 | Dry run: shows what would be backed up and the estimated size, without writing. | S |

### 4.2 Sources and exclusions

| ID | Requirement | Prio |
|---|---|---|
| SRC-01 | Select folders (incl. subfolders) and individual files through a folder/file tree picker. | M |
| SRC-02 | Sources can be local paths and UNC paths. Because the service runs as LocalSystem, UNC access uses either the computer account (`DOMAIN\SERVER$`, if the share grants it) or credentials stored per job. | M |
| SRC-03 | Exclusions **by selection** in the tree (uncheck subfolders/files). | M |
| SRC-04 | Exclusions **by pattern**: wildcards on name (`*.tmp`, `~$*`, `Thumbs.db`) and on path (`D:\Data\**\cache\`); optional regex. Case-insensitive. | M |
| SRC-05 | Predefined, switchable exclusion sets (temp files, recycle bin, `System Volume Information`, pagefile/hiberfil). | S |
| SRC-06 | Exclusion by size and age (e.g. files > 10 GB). | L |
| SRC-07 | Preview: show which files a pattern matches before saving. | S |
| SRC-08 | Open/locked files are read from a **VSS snapshot** (per job option, default on). If VSS fails, the run falls back to live files and logs a warning. | M |
| SRC-09 | Files are read with backup semantics (`SeBackupPrivilege`), so files are backed up even if the ACL denies administrators access. | M |
| SRC-10 | Support paths > 260 characters, Unicode names, hidden/system files and sparse files. Junctions and symlinks are recorded as links, not followed (no loops). | M |
| SRC-11 | EFS-encrypted files: backed up in raw encrypted form, or skipped with a warning (to be decided). | L |

### 4.3 Backup modes

The repository stores **snapshots**. Every snapshot is a complete point-in-time image that can be restored on its own. Through deduplication, every snapshot physically only stores new data.

| ID | Requirement | Prio |
|---|---|---|
| BAK-01 | **Incremental** (default): change detection by size, modification time, file ID and attributes. Only changed files are read and chunked. | M |
| BAK-02 | **Full**: every file is re-read and re-hashed regardless of metadata. Protects against changes that kept mtime/size (e.g. some tools, bit rot). | M |
| BAK-03 | Schedule can combine both, e.g. incremental daily + full every Sunday. | M |
| BAK-04 | An interrupted backup can be resumed. Data uploaded so far is reused. | M |
| BAK-05 | Bandwidth limit per job (especially for SFTP). | S |
| BAK-06 | Allowed time window (e.g. 22:00–06:00). A run exceeding it is paused or cancelled, as configured. | L |

### 4.4 Repository format, encryption, compression

| ID | Requirement | Prio |
|---|---|---|
| REP-01 | Files are split into variable-size chunks (content-defined chunking) and deduplicated across all snapshots of a repository. | M |
| REP-02 | Chunks are compressed (Zstandard) where compression helps. Already compressed data (zip, jpg, mp4, …) is detected and stored uncompressed. | M |
| REP-03 | Everything in the repository is encrypted with **AES-256-GCM**: chunk data, file names, folder structure, metadata, snapshot list. The storage operator only sees opaque files. | M |
| REP-04 | Chunks are packed into larger **pack files** (e.g. 16–64 MB) to keep the file count on the target manageable. | M |
| REP-05 | The repository is **self-describing**: storage location + key are enough to list snapshots and restore. No local database, configuration or original machine is required. Local caches are optional and rebuildable. | M |
| REP-06 | The encrypted job configuration is stored inside the repository, so a job can be re-created on a new machine. | S |
| REP-07 | The repository has a **format version**. Newer software must always be able to read older formats. | M |
| REP-08 | Writes are crash-safe: data first, index and snapshot last. A crash or network loss can leave unused data, never a corrupt repository. | M |
| REP-09 | Repository locking prevents concurrent writes by two jobs or hosts. Stale locks can be detected and broken. Reading/restoring is possible while a backup is running. | M |
| REP-10 | The format is documented in `docs/` (algorithms, layout, key derivation), so the data does not depend on this software alone. | M |
| REP-11 | A portable copy of the restore tool and a plain-text README with recovery instructions are stored **unencrypted** in the repository root. | S |

### 4.5 Key management

| ID | Requirement | Prio |
|---|---|---|
| KEY-01 | Each repository has a random **master key**. It is stored in the repository, wrapped by one or more **key slots** (password via Argon2id, or key file). | M |
| KEY-02 | Adding, removing and changing passwords/key files is possible without re-encrypting the data. At least one slot must always remain. | M |
| KEY-03 | When creating a repository, the user must export a **recovery key** and confirm it. The GUI keeps warning until this is done. Export formats: a single-line text key that can be pasted into a KeePass entry, and a key file that can be attached to it. Printable sheet optional. | M |
| KEY-04 | For unattended backups the service stores the key locally, encrypted with **DPAPI** (machine scope). The DPAPI copy is a convenience only – recovery must never depend on it. | M |
| KEY-05 | The encryption key is completely independent of the application password (SEC-01) and of Windows/domain accounts. | M |
| KEY-06 | Restoring on another machine only needs the repository location and a password or key file. | M |

### 4.6 Destinations

| ID | Requirement | Prio |
|---|---|---|
| DST-01 | **Local path** (internal disk). | M |
| DST-02 | **Network share** (UNC) with optional stored credentials (used for the connection only, independent of the service account). | M |
| DST-03 | **Removable USB disk**, identified by volume ID/label, **not by drive letter** (letters change). If the disk is missing, the run fails with a clear message. | M |
| DST-04 | Several USB disks can be registered for one job (rotation: one disk at home, one on site). Each disk holds its own repository. | S |
| DST-05 | **SFTP** with password or private key (OpenSSH and PuTTY formats). Host key is pinned on first connect (fingerprint shown, user confirms); changes are rejected. | M |
| DST-06 | "Test connection" checks reachability, authentication, write and delete permission and free space where possible. | M |
| DST-07 | Optional copy job: replicate a repository to a second destination (3-2-1 rule). | L |
| DST-08 | Storage interface prepared for S3 (incl. Object Lock). | L |

### 4.7 Schedules

| ID | Requirement | Prio |
|---|---|---|
| SCH-01 | Per job: manual, every N minutes/hours, daily at time(s), on weekdays, monthly. | M |
| SCH-02 | Several schedule entries per job, each with its mode (incremental/full). | M |
| SCH-03 | Missed runs (machine off, service stopped) run once after startup (configurable). | M |
| SCH-04 | Automatic retry on failure (count and delay configurable). | S |
| SCH-05 | No two runs of the same job at the same time. A due run while one is active is skipped and logged. | M |

### 4.8 Retention and cleanup

| ID | Requirement | Prio |
|---|---|---|
| RET-01 | Retention rules per job: keep last N, keep daily/weekly/monthly/yearly (GFS), keep everything newer than X days. Rules are combinable. | M |
| RET-02 | Snapshots can be marked **protected** (never deleted by retention). | S |
| RET-03 | Cleanup ("prune") removes snapshots and frees unused data in pack files. Runs after backups or on its own schedule. | M |
| RET-04 | Retention preview: shows which snapshots would be deleted. | S |

### 4.9 Verification

| ID | Requirement | Prio |
|---|---|---|
| VER-01 | **Quick check**: repository structure, index and snapshots are consistent and all referenced packs exist. | M |
| VER-02 | **Full check**: read and decrypt all (or a configurable percentage of) pack data and verify checksums. | M |
| VER-03 | **Test restore**: restore a random sample of files to a temp folder and compare them. | S |
| VER-04 | Checks can be scheduled per job (e.g. quick weekly, full monthly). | M |
| VER-05 | Repair: rebuild the index from pack files; remove references to missing data and report affected files. | S |

### 4.10 Restore

| ID | Requirement | Prio |
|---|---|---|
| RST-01 | Browse snapshots by date and navigate the file tree of a snapshot. | M |
| RST-02 | Restore the entire snapshot, selected folders or single files. | M |
| RST-03 | Target: **original location** or **new folder** (with or without the original path structure). | M |
| RST-04 | Conflict handling for existing files: overwrite, skip, overwrite if older, keep both (rename). | M |
| RST-05 | Search a file name/pattern across all snapshots ("which versions of report.xlsx exist?"). | S |
| RST-06 | Restore preserves timestamps and attributes. Permissions are not restored (§4.11). | M |
| RST-07 | **Disaster recovery mode**: on a fresh machine, open a repository by location + password/key file, without existing configuration. Possible in GUI and CLI, without installing the service. | M |
| RST-08 | Restore is logged like a backup run (§4.12). | M |
| RST-09 | Download a single file/folder as a regular file to any location (quick access from the GUI). | S |

### 4.11 Permissions (NTFS ACLs)

Decision: **NTFS permissions are not backed up.** Restored files must always be accessible, even if the original domain no longer exists.

| ID | Requirement | Prio |
|---|---|---|
| ACL-01 | Restored files and folders get **no stored ACL**. They inherit the permissions of the target folder, so they are accessible on any machine and in any domain. | M |
| ACL-02 | Timestamps (created, modified) and basic attributes (read-only, hidden, system, archive) are still backed up and restored. | M |

> The repository format leaves room for optional metadata per file, so ACL support could be added later without a format break. It is not planned.

### 4.12 Logs and reports

| ID | Requirement | Prio |
|---|---|---|
| LOG-01 | A dedicated log per run (backup, restore, check, prune). | M |
| LOG-02 | Run summary: start, end, **duration per phase** (VSS, scan, upload, index, cleanup), files scanned/new/changed/unchanged/removed, bytes read/added/uploaded, dedup and compression ratio, transfer rate. | M |
| LOG-03 | **List of everything not backed up**, with the reason (access denied, locked, path too long, vanished during backup, excluded – optional). Filterable and exportable (CSV). | M |
| LOG-04 | Status per run: success, warning (some files skipped), failed, cancelled. | M |
| LOG-05 | Run history per job with filter by status and date. | M |
| LOG-06 | Log retention configurable (default: 90 days or 200 runs per job). | S |
| LOG-07 | Important events (run failed/warning, missed schedule, key not exported) are also written to the **Windows Event Log**, for existing monitoring tools. | S |
| LOG-08 | Service diagnostic log with rotation. | M |

### 4.13 Notifications

| ID | Requirement | Prio |
|---|---|---|
| NOT-01 | E-mail via SMTP (STARTTLS/SSL, authentication) on: failure, warning, success (each switchable per job). An SMTP server is available. | M |
| NOT-02 | **"No successful backup for X hours/days"** alert per job – also catches a stopped service or disabled schedule. | M |
| NOT-03 | Test-mail button. | S |

### 4.14 Access and security

| ID | Requirement | Prio |
|---|---|---|
| SEC-01 | The GUI and CLI require an **application password**. It is set during installation and is unrelated to encryption keys. | M |
| SEC-02 | The password is stored as a salted Argon2id/PBKDF2 hash. Lockout with increasing delay after failed attempts. | M |
| SEC-03 | The GUI locks after configurable inactivity. | S |
| SEC-04 | The named pipe only accepts local connections and requires a session token after login. | M |
| SEC-05 | All stored secrets (SFTP passwords/keys, share credentials, repository keys, SMTP password) are encrypted with DPAPI. Secrets are never shown again in the GUI or written to logs. | M |
| SEC-06 | Audit log of configuration changes (who/when/what) and of restores. | S |
| SEC-07 | Roles (administrator / operator: can run and restore, cannot edit or delete). | L |
| SEC-08 | Ransomware hardening, as far as the destinations allow: documented recommendation for an SFTP user/share account with restricted delete rights, and for offline USB rotation. | S |

### 4.15 Command line interface

| ID | Requirement | Prio |
|---|---|---|
| CLI-01 | Commands: `jobs list/show`, `run <job> [--full]`, `cancel`, `status`, `snapshots`, `ls`, `restore`, `check`, `prune`, `logs`. | M |
| CLI-02 | Non-interactive: authentication via password, password file or environment variable. No prompts when `--non-interactive` is set. | M |
| CLI-03 | **Defined exit codes** (0 success, 1 warning, 2 failed, 3 config/auth error, …) and `--json` output for all commands. | M |
| CLI-04 | `--wait` blocks until a started run ends and returns its exit code. | M |
| CLI-05 | Standalone restore against a repository without service/config (see RST-07). | M |
| CLI-06 | Job create/edit via CLI or JSON import. | L |

### 4.16 Database backups (later)

| ID | Requirement | Prio |
|---|---|---|
| DB-01 | Source type **PostgreSQL**: `pg_dump`/`pg_dumpall` output is streamed directly into the repository (no temporary dump files). Per database or cluster. | L |
| DB-02 | Source type **MariaDB/MySQL**: `mariadb-dump` streamed in the same way, with consistent snapshot (`--single-transaction`). | L |
| DB-03 | Restore of a DB backup to a file or directly into a database server. | L |
| DB-04 | Point-in-time recovery (WAL archiving / binlogs) – separate topic. | L |

The core must already treat a "source" as a plugin (§2), so file sources and dump streams are both possible.

---

## 5. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-01 | **Reliability**: no run may corrupt an existing repository, including power loss, network loss, full disk and cancellation. |
| NFR-02 | **Performance** (for the sizing in §1.2): an incremental run with few changes over 50,000 files completes in **under 2 minutes** plus transfer time. The initial backup of ~200 GB is limited by the destination (disk or network speed), not by CPU – e.g. ~5 h over SFTP at 100 Mbit/s – and must be resumable (BAK-04 becomes **M**). Memory use under 500 MB. |
| NFR-03 | **Scalability**: headroom of 10× the current sizing – tested with 2 TB / 500,000 files per repository. |
| NFR-04 | **Compatibility**: tested on a real Windows Server 2008 R2 SP1 VM and Windows 10/11 throughout development, not only at the end. |
| NFR-05 | **Installation**: one installer (MSI or setup.exe) installs service, GUI and CLI, and checks the .NET Framework 4.8 prerequisite. Upgrades keep configuration and run history. |
| NFR-06 | **Portable restore**: the restore tool also runs without installation (from a USB stick or the repository root). |
| NFR-07 | **Usability**: a first job can be set up in under 5 minutes without reading documentation. Clear error messages with a hint on what to do. |
| NFR-08 | **Documentation**: user guide, CLI reference, repository format specification, and a **disaster-recovery runbook** ("server is gone – how do I get my files back"). |
| NFR-09 | **Testability**: core logic covered by automated tests, including corruption and interruption tests. |
| NFR-10 | **Language**: GUI, CLI, logs and documentation in English only. |

---

## 6. Decisions log

| # | Question | Decision |
|---|---|---|
| 1 | Product name | **Keepsake** |
| 2 | Data volume | ~130 GB / 50,000 files / 6,500 folders today, ~200 GB initial backup, small daily changes (§1.2, NFR-02/03) |
| 3 | Service account | **LocalSystem** (SRC-02) |
| 4 | E-mail | SMTP server available – notifications become must-have (NOT-01/02) |
| 5 | Key custody | Administrator stores recovery keys in **KeePass** (KEY-03) |
| 6 | ACL support | **Not needed** – files restore with inherited permissions (§4.11) |
| 7 | Multiple admins | One application password is enough; roles stay "later" (SEC-07) |
| 8 | GUI language | **English** only (NFR-10) |

### 6.1 Notes from the decisions

- **KeePass location:** the KeePass database must not live only on the server being backed up – otherwise a lost server takes the recovery key with it. Keep a copy elsewhere (second machine, offline USB stick).
- **Recovery test:** before going live, restore from a second machine using only the KeePass entry – this proves C-05.

---

## 7. Proposed milestones

| # | Milestone | Content |
|---|---|---|
| 1 | Core + format | Repository format, chunking, encryption, key slots, local destination, CLI backup/restore. Format spec written. |
| 2 | Service | Windows service, scheduler, job store, run logs, named-pipe API, CLI on top of the API. |
| 3 | GUI | WPF GUI: login, jobs, source tree, exclusions, schedules, run status, logs, restore browser. |
| 4 | Destinations | Network shares with credentials, USB by volume ID, SFTP. |
| 5 | Robustness | VSS, retention/prune, checks, resume, notifications, Event Log. |
| 6 | Hardening + release | 2008 R2 test pass, installer, DR runbook, recovery test from a second machine. |
| 7 | Databases | PostgreSQL, MariaDB sources. |
