# Keepsake Backup

Encrypted, compressed, deduplicated file backup for Windows Server 2008 R2 SP1 and Windows 10/11.

- Requirements: [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)
- Repository format: [docs/FORMAT.md](docs/FORMAT.md)

## Status

| Milestone | Content | State |
|---|---|---|
| 1 | Core + format: chunking, AES-256-GCM, key slots, file system destination, CLI | **done** |
| 2 | Windows service, scheduler, job store, run logs, named-pipe API | **done** (console mode tested; not yet installed as a real service) |
| 3 | WPF GUI | **done** (tested on Windows 11; disaster-recovery mode without the service still CLI-only) |
| 4 | Destinations: share credentials, USB by volume ID, SFTP | **done** (SFTP tested against OpenSSH in Docker; share credentials not tested against a real domain share) |
| 5 | VSS, retention/prune, scheduled checks, notifications, Event Log | **done** (VSS fallback tested; real shadow copies need an elevated test on 2008 R2) |
| 6 | 2008 R2 test pass, installer, DR runbook | **done except the 2008 R2 test pass** (MSI, portable ZIP, doctor, repair, runbook; test plan in docs/WINDOWS-2008R2.md) |
| 7 | PostgreSQL / MariaDB sources | open |

## Layout

```
src/Keepsake.Core      .NET Standard 2.0 – engine (backup, restore, check, repository format)
src/Keepsake.Protocol  .NET Standard 2.0 – job model, schedules, service API client (shared by CLI and GUI)
src/Keepsake.Service   .NET Framework 4.8 – keepsake-service.exe (Windows service)
src/Keepsake.Cli       .NET Framework 4.8 – keepsake.exe
src/Keepsake.Gui       .NET Framework 4.8 – KeepsakeGui.exe (WPF GUI, runs as administrator)
tests/                 xUnit tests (net48)
docs/                  requirements and format specification
```

## Service

The service runs as LocalSystem, executes the jobs on their schedules and keeps a log of every run.
Its data lives in `%ProgramData%\Keepsake` (only SYSTEM and Administrators have access):

```
config\jobs.json       job definitions; repository passwords DPAPI-encrypted (machine scope)
config\settings.json   application password (Argon2id hash), parallel jobs, log retention
state\scheduler.json   last run time of each schedule (for missed-run detection)
runs\<job>\<run>.*     run record, issue list and readable log of every run
logs\service.log       diagnostic log (10 MB × 5)
```

GUI and CLI talk to it through the local named pipe `keepsake-service` (Administrators only, no
network access) and need the **application password**, which is separate from the repository passwords.

From an elevated prompt:

```bash
keepsake-service install
```

```bash
keepsake-service start
```

```bash
keepsake setup
```

```bash
keepsake jobs import fileserver-job.json --repository-password-file repo-password.txt
```

```bash
keepsake run fileserver --wait
```

```bash
keepsake status
```

### Robustness features

- **VSS**: each backup takes a shadow copy of the source volumes (per job, default on), so open and
  locked files are backed up consistently. If that fails, files are read directly and the run gets a
  warning. Shadow copies left over after a crash are removed at the next service start.
- **Backup privilege**: the service enables SeBackupPrivilege and opens files with backup semantics,
  so files are read even when their permissions exclude administrators.
- **Retention and cleanup**: per job "keep last / daily / weekly / monthly / yearly / within N days";
  protected snapshots are never removed. After a backup, cleanup runs when it is due
  (default every 7 days) and frees the space of removed snapshots (repacking mostly-empty packs).
- **Integrity checks**: after a backup when due (default every 7 days), verifying the structure and
  decrypting a random share of the data (default 10 %).
- **E-mail**: on failure / warning / success per job, plus "no successful backup for N hours".
- **Event Log** (Application, source `Keepsake`): 1000 success, 1001 failed, 1002 warnings,
  1003 scheduled run missed or skipped, 1004 no successful backup for too long, 1005 mail failed.

## GUI

`KeepsakeGui.exe` (elevated, Start menu "Keepsake Backup") connects to the service. First start asks for the application password; after
that it offers:

- **Backup jobs** – status, last result, next run, run now / full backup / cancel; job editor with folder
  tree (tick to include, untick inside a ticked folder to exclude), exclusion patterns, destination
  (creates the repository and shows the recovery key, which must be confirmed), schedules and options
- **Restore** – pick job and snapshot, tick folders/files, restore to another folder or the original
  location with a conflict policy, dry run
- **History & logs** – every run; details show everything that was not backed up (filter, search,
  CSV export), phase timings and the full log
- **Settings** – parallel jobs, log retention, application password

Debug builds run without elevation and accept `--pipe <name>` to talk to a console-mode service.

`keepsake-service console --data <folder> --pipe <name>` runs the service in a console window for testing
(then pass `--pipe <name>` to the CLI as well). A job file looks like this:

```json
{
  "name": "fileserver",
  "sources": ["D:\\Data"],
  "excludes": ["*.tmp", "D:\\Data\\**\\cache\\"],
  "destination": { "type": "folder", "path": "\\\\nas\\backup\\fileserver" },
  "schedules": [
    { "kind": "daily", "times": ["22:00"], "mode": "incremental" },
    { "kind": "daily", "times": ["23:30"], "weekdays": ["sunday"], "mode": "full" }
  ],
  "retryCount": 2,
  "retryDelayMinutes": 15,
  "preCommand": null,
  "postCommand": null
}
```

## Release

```bash
powershell -ExecutionPolicy Bypass -File toolsuild-release.ps1
```

builds, tests and produces `artifacts\Keepsake-<version>.msi` and `artifacts\Keepsake-<version>-portable.zip`.
The MSI installs to `C:\Program Files\dkait\keepsake` (the setup lets you choose another folder), registers the service (LocalSystem, automatic,
restart on failure), puts `keepsake` on the PATH and adds *Keepsake Backup* to the Start menu.
It checks for .NET Framework 4.8 and Service Pack 1.

- Disaster recovery: [docs/DISASTER-RECOVERY.md](docs/DISASTER-RECOVERY.md)
- Windows Server 2008 R2 prerequisites and test plan: [docs/WINDOWS-2008R2.md](docs/WINDOWS-2008R2.md)
- `keepsake doctor` checks a machine; `tools\Enable-Tls12.ps1` enables TLS 1.2 on 2008 R2 (for e-mail).

## Build and test

Needs the .NET SDK (any recent version; .NET Framework reference assemblies come from NuGet).

```bash
dotnet build Keepsake.slnx -c Release
```

```bash
dotnet test tests/Keepsake.Core.Tests
```

The SFTP tests run when a server is reachable on `localhost:2222` (otherwise they pass without doing
anything):

```bash
docker run -d --name keepsake-sftp -p 2222:22 atmoz/sftp:alpine kuser:Sftp-Pass-1:1001:1001:backups
```

The e-mail test needs an SMTP test server (Mailpit) on `localhost:1025`:

```bash
docker run -d --name keepsake-mail -p 1025:1025 -p 8025:8025 axllent/mailpit
```

`src/Keepsake.Cli/bin/Release/net48/` is a portable folder: copy it to any machine with
.NET Framework 4.8 and run `keepsake.exe`.

## CLI quick start

```bash
keepsake init --repo E:\Backups\fileserver
```

```bash
keepsake backup --repo E:\Backups\fileserver --job fileserver D:\Data --exclude "*.tmp" --log D:\Logs\backup.log
```

```bash
keepsake snapshots --repo E:\Backups\fileserver
```

```bash
keepsake restore latest --repo E:\Backups\fileserver --target D:\Restore --include D:\Data\Finance --flat
```

```bash
keepsake check --repo E:\Backups\fileserver --read-data-percent 10
```

Restore from an SFTP repository on any machine (disaster recovery, no service needed):

```bash
keepsake restore latest --repo sftp://backup@nas.example.org/backups/fileserver --sftp-password-file sftp.txt --recovery-key-file key.txt --target D:\Restore
```

The first connection shows the server's host key fingerprint and asks to trust it; pass
`--sftp-host-key SHA256:...` to pin it for unattended use.

For automation use `--password-file` (or `KEEPSAKE_PASSWORD`), `--non-interactive` and `--json`.
Exit codes: 0 success, 1 warnings, 2 failed, 3 usage error, 4 wrong password/key, 5 locked, 6 cancelled.

## Dependencies

| Package | Why |
|---|---|
| BouncyCastle.Cryptography 2.7.0 | AES-256-GCM and Argon2id (not in .NET Framework 4.8) |
| SSH.NET 2026.0.0 | SFTP destinations |
| MailKit 4.18.0 | E-mail notifications (STARTTLS, SSL, plain) |
| ZstdSharp.Port 0.8.1 | Zstandard compression (managed) |
| System.Text.Json 8.0.5 | JSON serialization |
