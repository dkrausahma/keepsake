# Keepsake

Encrypted, compressed, deduplicated file backup for Windows Server 2008 R2 SP1 and Windows 10/11.

- Requirements: [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)
- Repository format: [docs/FORMAT.md](docs/FORMAT.md)

## Status

| Milestone | Content | State |
|---|---|---|
| 1 | Core + format: chunking, AES-256-GCM, key slots, file system destination, CLI | **done** |
| 2 | Windows service, scheduler, job store, run logs, named-pipe API | **done** (console mode tested; not yet installed as a real service) |
| 3 | WPF GUI | open |
| 4 | Destinations: share credentials, USB by volume ID, SFTP | open |
| 5 | VSS, retention/prune, scheduled checks, notifications, Event Log | open |
| 6 | 2008 R2 test pass, installer, DR runbook | open |
| 7 | PostgreSQL / MariaDB sources | open |

## Layout

```
src/Keepsake.Core      .NET Standard 2.0 – engine (backup, restore, check, repository format)
src/Keepsake.Protocol  .NET Standard 2.0 – job model, schedules, service API client (shared by CLI and GUI)
src/Keepsake.Service   .NET Framework 4.8 – keepsake-service.exe (Windows service)
src/Keepsake.Cli       .NET Framework 4.8 – keepsake.exe
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

## Build and test

Needs the .NET SDK (any recent version; .NET Framework reference assemblies come from NuGet).

```bash
dotnet build Keepsake.slnx -c Release
```

```bash
dotnet test tests/Keepsake.Core.Tests
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

For automation use `--password-file` (or `KEEPSAKE_PASSWORD`), `--non-interactive` and `--json`.
Exit codes: 0 success, 1 warnings, 2 failed, 3 usage error, 4 wrong password/key, 5 locked, 6 cancelled.

## Dependencies

| Package | Why |
|---|---|
| BouncyCastle.Cryptography 2.6.2 | AES-256-GCM and Argon2id (not in .NET Framework 4.8) |
| ZstdSharp.Port 0.8.1 | Zstandard compression (managed) |
| System.Text.Json 8.0.5 | JSON serialization |
