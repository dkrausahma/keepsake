# Keepsake

Encrypted, compressed, deduplicated file backup for Windows Server 2008 R2 SP1 and Windows 10/11.

- Requirements: [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md)
- Repository format: [docs/FORMAT.md](docs/FORMAT.md)

## Status

| Milestone | Content | State |
|---|---|---|
| 1 | Core + format: chunking, AES-256-GCM, key slots, file system destination, CLI | **done** |
| 2 | Windows service, scheduler, job store, run logs, named-pipe API | open |
| 3 | WPF GUI | open |
| 4 | Destinations: share credentials, USB by volume ID, SFTP | open |
| 5 | VSS, retention/prune, scheduled checks, notifications, Event Log | open |
| 6 | 2008 R2 test pass, installer, DR runbook | open |
| 7 | PostgreSQL / MariaDB sources | open |

## Layout

```
src/Keepsake.Core     .NET Standard 2.0 – engine (backup, restore, check, repository format)
src/Keepsake.Cli      .NET Framework 4.8 – keepsake.exe
tests/                xUnit tests (net48)
docs/                 requirements and format specification
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
