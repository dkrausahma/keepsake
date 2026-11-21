# Command line

`keepsake.exe` is the command-line interface of Keepsake Backup. It does two jobs. It works directly on a repository without the service: create one, back up, list, restore, check, clean up and manage keys. It also controls the running Keepsake service: import and export jobs, start and cancel runs, read the run history and logs. Every command can print machine-readable JSON and returns a defined exit code, so you can drive Keepsake from scripts, scheduled tasks and monitoring systems.

## Getting started

**Where it is.** The MSI installs `keepsake.exe` into the program folder (by default `C:\Program Files\dkait\keepsake`) and adds that folder to the `PATH`, so `keepsake` works in any new command prompt. The portable ZIP (`Keepsake-<version>-portable.zip`) contains the same `keepsake.exe`. Extract it and run it from that folder. The repository commands need nothing else than .NET Framework 4.8, which makes the portable ZIP useful for restoring on a replacement computer (see [Disaster recovery](../DISASTER-RECOVERY.md)).

**Elevation.** The service accepts connections from elevated administrators only, so run the service commands from an elevated prompt ("Run as administrator"). The repository commands do not need elevation as such. Your account still has to be able to read the files you back up, write to the repository and write to the restore target. Restoring to the original location under `C:\` or into other protected folders usually needs an elevated prompt.

**Two kinds of commands, two kinds of passwords.**

| Kind | Commands | Talks to | Needs |
|---|---|---|---|
| Repository commands | `init`, `backup`, `snapshots`, `ls`, `restore`, `check`, `prune`, `protect`, `repair`, `key`, `unlock`, `sftp-test` | The repository directly (folder, share, USB disk, SFTP). The service is not involved | The **repository password** or a **recovery key** (`KS1-...`). Either one opens the repository on any computer. `sftp-test` needs only the SFTP login |
| Service commands | `setup`, `status`, `jobs`, `run`, `cancel`, `runs`, `log` | The local Keepsake service through its named pipe | The **application password**. It protects the service and cannot decrypt backups |
| Tools | `doctor`, `version`, `help` | Neither (`doctor` only tests whether the service is reachable) | Nothing |

The application password and the repository passwords are separate. Mixing them up is the most common cause of exit code 4. See [Passwords and keys](../PASSWORDS-AND-KEYS.md).

**Syntax.** `keepsake <command> [arguments] [options]`. Options start with `--` and may also be written as `--name=value`. Options and positional arguments can come in any order. Everything after a bare `--` counts as a positional argument. `keepsake help <command>` and `keepsake <command> --help` show the built-in help for a command.

```bash
keepsake help restore
```

## Common options

These options are accepted by every command. A command uses only the ones that apply to it. For example, `--repo` matters only to repository commands.

| Option | Environment variable | Meaning |
|---|---|---|
| `--repo <location>` | `KEEPSAKE_REPOSITORY` | Repository location: a local folder (`E:\Backups\fileserver`), a USB disk folder, a network share (`\\nas\backup\fileserver`) or `sftp://user@host[:port]/path` (port 22 if omitted, for example `sftp://backup@nas.example.org/backups/fileserver`). |
| `--password-file <file>` | `KEEPSAKE_PASSWORD` | Repository password, read from the first line of the file. The environment variable holds the password itself. |
| `--recovery-key-file <file>` | `KEEPSAKE_RECOVERY_KEY` | Unlock with a recovery key instead of the password. The file holds the `KS1-...` text (a file written by `--recovery-key-out` works as is). The environment variable holds the key text itself. |
| `--use-recovery-key` | | When prompting, ask for the recovery key instead of the password. |
| `--sftp-password-file <file>` | `KEEPSAKE_SFTP_PASSWORD` | SFTP password (first line of the file). Without it and without a key, you are prompted. |
| `--sftp-key-file <file>` | | SFTP private key file. When given, the SFTP password is not used. |
| `--sftp-key-passphrase-file <file>` | `KEEPSAKE_SFTP_KEY_PASSPHRASE` | Passphrase of the SFTP private key (first line of the file). |
| `--sftp-host-key <SHA256:...>` | `KEEPSAKE_SFTP_HOST_KEY` | Trusted host key fingerprint of the SFTP server. Without it you are asked to trust the key on first connection. |
| `--json` | | Machine-readable JSON on stdout instead of text. Implies `--quiet`. Errors are printed as JSON too. |
| `--non-interactive` | | Never prompt. A missing password fails with exit code 3. This is automatic when input is redirected or no interactive user is present (scheduled task, service). |
| `--quiet` | | No progress line. Results are still printed. |
| `--log <file>` | | Write a detailed run log (summary, phases, every problem) to the file. Used by `backup`, `restore`, `check`, `prune` and `repair`. The folder is created if needed. |
| `--debug-log <file>` | | Write SFTP connection details (algorithms, errors, no passwords) to the file for support. |
| `--app-password-file <file>` | `KEEPSAKE_APP_PASSWORD` | Application password for the service commands (first line of the file). |
| `--help` | | Show the help of the command instead of running it. |

**Which credential wins.** For repository commands Keepsake uses the first credential it finds, in this order:

1. `--recovery-key-file`
2. `KEEPSAKE_RECOVERY_KEY`
3. `--password-file`
4. `KEEPSAKE_PASSWORD`
5. A prompt for the password, or for the recovery key with `--use-recovery-key` (not with `--non-interactive`)

!!! warning
    A `KEEPSAKE_RECOVERY_KEY` variable that is set in the environment beats `--password-file` on the command line. Clear environment variables you no longer need, or a wrong credential gets used and the command fails with exit code 4.

Password files are read as text. Only the first line counts, a trailing line break is ignored and a UTF-8 byte order mark is removed. Protect these files with NTFS permissions so only the account that runs the script can read them.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Finished with warnings (for example some files could not be read, or `doctor` found warnings) |
| `2` | Failed |
| `3` | Usage error: unknown command or option, missing argument, missing password in non-interactive mode, invalid value, or a job or run the service did not accept or find |
| `4` | Wrong password, recovery key or application password (also when the service has locked out logins after failed attempts) |
| `5` | Repository locked by another process, or the service reports a conflict (for example the job is already running) |
| `6` | Cancelled (Ctrl+C, or the run was cancelled) |

Press Ctrl+C once to cancel cleanly: Keepsake finishes the current block and stops (exit code 6). Press it a second time to abort immediately.

## setup

Sets the application password of the service. This works only once, on a service that has no application password yet. After that, change the password in the GUI under [Settings](settings.md).

```text
keepsake setup [--new-password-file <file>]
```

| Option | Meaning |
|---|---|
| `--new-password-file <file>` | Read the new application password from the first line of the file. Without it you are prompted twice. Interactive entries need at least 8 characters. |

```bash
keepsake setup
```

```bash
keepsake setup --new-password-file C:\Keepsake\app-password.txt
```

## status

Shows the service version, host, data folder and number of parallel jobs, followed by one line per job: state (`idle`, `running`, `waiting`, `disabled`), last run, result and next run. For running jobs it adds a progress line.

```text
keepsake status [--app-password-file <file>]
```

No options of its own besides `--app-password-file` and the common options.

```bash
keepsake status
```

```bash
keepsake status --json --app-password-file C:\Keepsake\app-password.txt
```

## jobs

Lists, shows, exports, imports and removes the backup jobs of the service. `<job>` is the job name (not case-sensitive) or its full ID.

```text
keepsake jobs [list]
keepsake jobs show <job>
keepsake jobs export <job> [--out <file>]
keepsake jobs import <file> [--repository-password-file <file>]
keepsake jobs remove <job>
```

| Subcommand | What it does |
|---|---|
| `list` (default) | One line per job: short ID, name, enabled/disabled, destination, and the schedules (or `manual only`). |
| `show <job>` | Details of one job: destination, sources, exclusions, schedules, next run, missed-run handling, retries and pre/post commands. Shows `(NO PASSWORD STORED)` if the service has no repository password for the job. |
| `export <job>` | Writes the job definition as JSON, without passwords. Writes to stdout, or to the file given with `--out`. |
| `import <file>` | Creates a job from a JSON file. If a job with the same ID or name already exists, it is updated instead. |
| `remove <job>` | Deletes the job from the service. The repository and its backups are not touched. |

| Option | Meaning |
|---|---|
| `--out <file>` | `export`: write the JSON to this file instead of stdout. |
| `--repository-password-file <file>` | `import`: repository password of the job's destination (first line of the file). A new job needs it: without the option you are prompted, and with `--non-interactive` the import fails. When an existing job is updated, the stored password is kept unless you give a new one. |

The destination repository must already exist when you import a new job. Create it with `keepsake init` or in the GUI.

```bash
keepsake jobs
```

```bash
keepsake jobs export fileserver --out D:\Keepsake\fileserver-job.json
```

```bash
keepsake jobs import D:\Keepsake\fileserver-job.json --repository-password-file C:\Keepsake\repo-password.txt
```

### Job file format

The file is the JSON produced by `jobs export`. Property names use camelCase and enum values are lower-case strings. Properties you leave out get their defaults. A minimal job:

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
  "retention": { "keepDaily": 14, "keepWeekly": 8, "keepMonthly": 12 },
  "retryCount": 2,
  "retryDelayMinutes": 15
}
```

| Property | Default | Meaning |
|---|---|---|
| `name` | (required) | Job name. Also used to match an existing job on import. |
| `enabled` | `true` | Disabled jobs do not run on schedule. |
| `sources` | (required) | Absolute paths (`D:\Data`, `\\server\share\folder`). |
| `excludes` | empty | Exclusion patterns, see [What to back up and exclusions](job-sources.md). |
| `useDefaultExcludes` | `true` | Also skip recycle bin, `System Volume Information`, `pagefile.sys`, `hiberfil.sys`, `swapfile.sys` and `~$*` files. |
| `destination` | | `type`: `folder` (absolute `path`), `usb` (`path` relative to the disk, for example `Keepsake\\Fileserver`, plus `volumes`) or `sftp` (`host`, `port` (default 22), `user`, `auth` `password` or `key`, absolute remote `path`, `hostKeyFingerprint`). See [Destination](job-destination.md). |
| `shareCredentials` | empty | List of `{ "share": "\\\\server\\share", "user": "DOMAIN\\user" }`. |
| `schedules` | empty (manual only) | Entries with `kind` (`interval`, `daily`, `monthly`), `intervalMinutes`, `times` (`HH:mm`), `weekdays` (`monday` ... `sunday`, empty = every day), `daysOfMonth`, `mode` (`incremental` or `full`), `enabled`. See [Schedule](job-schedule.md). |
| `runMissed` | `true` | Run once after start-up if a scheduled run was missed. |
| `retryCount`, `retryDelayMinutes` | `2`, `15` | Retries after a failed run (0-10 retries, 1 minute to 24 hours apart). |
| `preCommand`, `postCommand`, `commandTimeoutMinutes` | none, none, `30` | Commands run through `cmd.exe` before and after the backup. See [Options](job-options.md). |
| `workers` | `0` | Parallel file workers, 0 = automatic. |
| `useVss` | `true` | Read from a Volume Shadow Copy so open files are backed up. |
| `retention` | none (keep all) | `keepLast`, `keepDaily`, `keepWeekly`, `keepMonthly`, `keepYearly`, `keepWithinDays`. See [Retention and checks](job-retention.md). |
| `pruneEveryDays`, `checkEveryDays` | `7`, `7` | Run cleanup or a check after a backup when the last one is older (0 = never). |
| `checkReadDataPercent` | `10` | Share of data read by scheduled checks (0 = structure only). |
| `notifyOnFailure`, `notifyOnWarning`, `notifyOnSuccess` | `true`, `true`, `false` | E-mail notifications, see [Monitoring and notifications](monitoring.md). |
| `alertNoSuccessHours` | `0` (off) | Alert when there was no successful backup for this many hours. |

`id`, `created` and `modified` appear in an export and can stay in the file. They are managed by the service.

!!! note
    `jobs import` transfers only the repository password. SFTP passwords, SFTP private keys and network share passwords cannot be set from the command line. Enter them in the job editor of the GUI after the import. Existing secrets of an updated job are kept.

## run

Starts a job now, the same as **Run now** in the GUI. By default it runs a backup. The run happens in the service, with the job's settings (VSS, retries, notifications, pre/post commands), and it appears in the run history.

```text
keepsake run <job> [--full | --check | --full-check | --prune] [--wait]
```

| Option | Meaning |
|---|---|
| `--full` | Full backup: re-read every file instead of trusting size and modification time. |
| `--check` | Verify the repository instead of backing up, reading the job's sample of the data (`checkReadDataPercent`). |
| `--full-check` | Verify the repository and read all stored data. |
| `--prune` | Apply the job's retention rules and free unused space. |
| `--wait` | Show progress until the run ends, print its result and return its exit code (0, 1, 2 or 6). Ctrl+C then cancels the run in the service. |

Without `--wait`, `run` returns 0 as soon as the service has accepted the run and prints the run ID. If the options are combined, `--prune` beats `--check`/`--full-check`, and `--full` only applies to backups.

```bash
keepsake run fileserver --wait
```

```bash
keepsake run fileserver --full --wait
```

```bash
keepsake run fileserver --full-check --wait --log D:\Logs\check.log
```

## cancel

Cancels a running job, or removes a job that is waiting for a free slot from the queue.

```text
keepsake cancel <job>
```

```bash
keepsake cancel fileserver
```

## runs

Shows the run history, newest first: run ID, job, start, duration, result, trigger and files/data. For runs that did not succeed, the message is shown below the line.

```text
keepsake runs [<job>] [--limit <n>]
```

| Option | Meaning |
|---|---|
| `<job>` | Only runs of this job. Default: all jobs. |
| `--limit <n>` | Number of runs, 1 to 1000. Default: 20. |

```bash
keepsake runs fileserver --limit 5
```

## log

Prints the full log of one run. Take the run ID from `runs`, from the output of `run`, or from [History and logs](history.md) in the GUI.

```text
keepsake log <run-id>
```

```bash
keepsake log 20261121T210000Z-3f9a1c
```

## init

Creates a new encrypted repository with a password and a recovery key. The recovery key is shown only once. Store it in KeePass right away: without the password or the recovery key, the backups cannot be restored.

```text
keepsake init --repo <location> [--password-file <file>] [--recovery-key-out <file>]
```

| Option | Meaning |
|---|---|
| `--password-file <file>` | Password for the new repository (or `KEEPSAKE_PASSWORD`). Without it you are prompted twice. Interactive entries need at least 8 characters. |
| `--recovery-key-out <file>` | Also write the recovery key to this file, together with location, repository ID and date. Move the file off the server afterwards. |

With `--json` the output includes the recovery key in the `recoveryKey` field. Do not let a script log it in plain text.

```bash
keepsake init --repo E:\Backups\fileserver
```

```bash
keepsake init --repo \\nas\backup\fileserver --password-file C:\Keepsake\repo-password.txt --recovery-key-out C:\Keepsake\fileserver-recovery.txt
```

## backup

Backs up folders and files into a new snapshot. The backup is incremental: unchanged files (same size and modification time as in the previous snapshot) are not read again, and only new data is stored.

```text
keepsake backup --repo <location> <folder-or-file>... [options]
```

| Option | Meaning |
|---|---|
| `<folder-or-file>...` | One or more folders or files to back up (required). |
| `--exclude <pattern>` | Exclude by name (`*.tmp`), folder (`cache\`) or path (`D:\Data\**\obj\`). Repeatable. See [What to back up and exclusions](job-sources.md). |
| `--exclude-file <file>` | Read exclusion patterns from a file: one per line, empty lines and lines starting with `#` are ignored. Repeatable. |
| `--no-default-excludes` | Also back up `$RECYCLE.BIN\`, `System Volume Information\`, `pagefile.sys`, `hiberfil.sys`, `swapfile.sys` and `~$*` files. |
| `--full` | Re-read every file instead of trusting size and modification time. |
| `--job <name>` | Job name stored in the snapshot. Incremental backups continue from the last snapshot of this job. |
| `--tag <tag>` | Tag the snapshot. Repeatable. |
| `--parent <snapshot>` | Use this snapshot as the base for change detection instead of the latest one of the same job, host and sources. |
| `--workers <n>` | Files processed in parallel, 1 to 32. Default: number of processors, at most 4. |
| `--report-excluded` | List every excluded item in the run report and `--log` file. |

!!! note
    The `backup` command reads the live files. It does not create a Volume Shadow Copy and does not use the backup privilege the service uses. Files that are open or locked are reported as not backed up (exit code 1). For servers, configure a job and use `keepsake run` instead.

```bash
keepsake backup --repo E:\Backups\fileserver --job fileserver D:\Data --exclude "*.tmp" --log D:\Logs\backup.log
```

```bash
keepsake backup --repo E:\Backups\fileserver --job fileserver D:\Data D:\Projects --exclude-file D:\Keepsake\excludes.txt --tag before-migration
```

## snapshots

Lists the snapshots in the repository, oldest first: short ID, time, job, mode, files, size and paths. Protected snapshots are marked with `*`.

```text
keepsake snapshots --repo <location> [--job <name>]
```

| Option | Meaning |
|---|---|
| `--job <name>` | Only snapshots of this job (not case-sensitive). |

```bash
keepsake snapshots --repo E:\Backups\fileserver
```

```bash
keepsake snapshots --repo E:\Backups\fileserver --job fileserver --json
```

### Selecting a snapshot

Wherever a command takes `<snapshot>`, you can give:

- `latest`: the newest snapshot. Together with `--job <name>` (`ls`, `restore`), the newest snapshot of that job.
- An ID or the start of an ID with at least 4 hexadecimal characters, for example `3f9a1c2e` from the `snapshots` list. It must match exactly one snapshot.

## ls

Lists the contents of a snapshot: type (`d` folder, `f` file), modification time, size and path.

```text
keepsake ls --repo <location> <snapshot|latest> [<path>] [--recursive] [--job <name>]
```

| Option | Meaning |
|---|---|
| `<path>` | Folder or file inside the snapshot, as a Windows path (`D:\Data\Finance`) or in snapshot form (`D/Data/Finance`). Default: the top level. |
| `--recursive` | Also list all subfolders. |
| `--job <name>` | With `latest`: the newest snapshot of this job. |

```bash
keepsake ls --repo E:\Backups\fileserver latest D:\Data\Finance
```

```bash
keepsake ls --repo E:\Backups\fileserver 3f9a1c2e D:\Data --recursive --json
```

## restore

Restores files from a snapshot, either into a folder or back to their original location. Without a snapshot argument, `latest` is used. NTFS permissions are not restored: restored files inherit the permissions of the folder they land in. More on restoring: [Restoring files](restore.md).

```text
keepsake restore --repo <location> [<snapshot|latest>] (--target <folder> | --original) [options]
```

Give exactly one of `--target` or `--original`.

| Option | Meaning |
|---|---|
| `--target <folder>` | Restore into this folder. By default the full path is kept: `D:\Data\Finance\x.xlsx` lands in `<folder>\D\Data\Finance\x.xlsx`. |
| `--original` | Restore to the original location (`D:\Data\...`). |
| `--include <path>` | Only restore this folder or file (Windows path or snapshot path). Repeatable. Default: everything. |
| `--flat` | With `--target`: place the selected folder directly in the target (`<folder>\Finance\...`) instead of keeping the full path. |
| `--conflict <policy>` | What to do when a different file already exists: `skip` (default, keep the existing file), `overwrite`, `newer` (replace only if the backed-up file is newer) or `rename` (restore next to it as `name (restored 1).ext`). Files identical in size and modification time are always left alone. |
| `--no-attributes` | Do not restore the read-only, hidden and system attributes. |
| `--dry-run` | Show what would be restored without writing anything. |
| `--job <name>` | With `latest`: the newest snapshot of this job. |

Files skipped because of a conflict are listed as problems and the restore finishes with warnings (exit code 1).

```bash
keepsake restore latest --repo E:\Backups\fileserver --target D:\Restore --include D:\Data\Finance --flat
```

```bash
keepsake restore 3f9a1c2e --repo E:\Backups\fileserver --original --include "D:\Data\Projects\Offer 2026.docx" --conflict rename
```

```bash
keepsake restore latest --repo sftp://backup@nas.example.org/backups/fileserver --sftp-password-file C:\Keepsake\sftp.txt --recovery-key-file C:\Keepsake\fileserver-recovery.txt --target D:\Restore --dry-run
```

## check

Verifies the repository. Without options it checks the structure: every snapshot, folder listing and data reference. With `--read-data` it also downloads and decrypts the stored data.

```text
keepsake check --repo <location> [--read-data] [--read-data-percent <1-100>]
```

| Option | Meaning |
|---|---|
| `--read-data` | Read and decrypt all stored data. |
| `--read-data-percent <n>` | Read and decrypt a random sample of `n` percent of the data (1 to 100). Implies `--read-data`. |

```bash
keepsake check --repo E:\Backups\fileserver --read-data-percent 10
```

```bash
keepsake check --repo E:\Backups\fileserver --read-data --log D:\Logs\check-full.log
```

If `check` reports missing or unreadable index files, run `keepsake repair`.

## prune

Removes old snapshots by retention rules and frees the space that no snapshot uses any more. The rules combine: a snapshot is kept if any rule keeps it. Protected snapshots are always kept. Without any `--keep-*` option no snapshot is deleted, and only unused data is removed. See [Retention and checks](job-retention.md).

```text
keepsake prune --repo <location> [--keep-last <n>] [--keep-daily <n>] [--keep-weekly <n>] [--keep-monthly <n>] [--keep-yearly <n>] [--keep-within <days>] [--job <name>] [--dry-run]
```

| Option | Meaning |
|---|---|
| `--keep-last <n>` | Keep the newest `n` snapshots. |
| `--keep-daily <n>` | Keep the newest snapshot of each of the last `n` days that have snapshots. |
| `--keep-weekly <n>` | The same per week. |
| `--keep-monthly <n>` | The same per month. |
| `--keep-yearly <n>` | The same per year. |
| `--keep-within <days>` | Keep all snapshots of the last `days` days. |
| `--job <name>` | Apply the rules only to snapshots of this job. Other snapshots stay. |
| `--dry-run` | Show which snapshots would be removed, without changing anything. |

```bash
keepsake prune --repo E:\Backups\fileserver --job fileserver --keep-daily 14 --keep-weekly 8 --keep-monthly 12 --dry-run
```

```bash
keepsake prune --repo E:\Backups\fileserver --job fileserver --keep-daily 14 --keep-weekly 8 --keep-monthly 12
```

## protect

Protects a snapshot from retention, so neither `prune` nor a job's cleanup ever removes it. `--remove` takes the protection away again. Protecting rewrites the snapshot record, so the snapshot gets a **new ID**, which is printed.

```text
keepsake protect <snapshot> --repo <location> [--remove]
```

| Option | Meaning |
|---|---|
| `--remove` | Remove the protection. |

```bash
keepsake protect 3f9a1c2e --repo E:\Backups\fileserver
```

## repair

Rebuilds the repository index from the pack files. Use it when `check` reports missing or unreadable index files. It reads only the pack headers, writes a new index and removes the old index files. Snapshots and data are not changed. Run `check` afterwards to see whether any snapshot still misses data.

```text
keepsake repair --repo <location>
```

```bash
keepsake repair --repo E:\Backups\fileserver --log D:\Logs\repair.log
```

## key

Manages the passwords and recovery keys of a repository. Every repository has at least one password and one recovery key (`KS1-...`), and each of them opens the repository on any computer. To unlock, use the repository credential options from Common options above. The application password of the service is not a repository key. Background: [Passwords and keys](../PASSWORDS-AND-KEYS.md).

```text
keepsake key list --repo <location>
keepsake key verify --repo <location>
keepsake key add-password --repo <location> [--new-password-file <file>] [--label <text>]
keepsake key add-recovery --repo <location> [--recovery-key-out <file>] [--label <text>]
keepsake key remove <id> --repo <location>
```

| Subcommand | What it does |
|---|---|
| `list` | Shows all keys: short ID, type, creation time, host and label. `*` marks the key you unlocked with. |
| `verify` | Checks whether the given password or recovery key opens the repository. It reads only the key files, takes no lock and works while a backup runs. Exit code 4 if the credential is not valid. |
| `add-password` | Adds another password. |
| `add-recovery` | Creates an additional recovery key and shows it once. |
| `remove <id>` | Removes a key, given by its ID or a unique start of it (from `key list`). The last key cannot be removed. |

| Option | Meaning |
|---|---|
| `--new-password-file <file>` | `add-password`: the new password (first line of the file). Without it you are prompted twice. `KEEPSAKE_PASSWORD` is not used for the new password. |
| `--recovery-key-out <file>` | `add-recovery`: also write the new recovery key to this file. |
| `--label <text>` | `add-password`, `add-recovery`: label shown in `key list`. Default: `Password` or `Recovery key`. |

!!! warning
    Removing a key does not revoke it. Anyone who kept a copy of the old repository key files and knows that password can still open the repository. After a leak, start a new repository and let the old one expire.

```bash
keepsake key verify --repo E:\Backups\fileserver --recovery-key-file C:\Keepsake\fileserver-recovery.txt
```

```bash
keepsake key add-password --repo E:\Backups\fileserver --label "Second admin"
```

```bash
keepsake key list --repo E:\Backups\fileserver
```

## unlock

Removes stale repository locks, for example after a crash or power loss. By default only locks older than 30 minutes are removed.

```text
keepsake unlock --repo <location> [--all]
```

| Option | Meaning |
|---|---|
| `--all` | Also remove current locks. Use it only when no other Keepsake process (service, GUI, another command prompt, another computer) works on the repository. |

```bash
keepsake unlock --repo E:\Backups\fileserver
```

## sftp-test

Tests which SSH algorithms work between this computer and an SFTP server. It connects once per key exchange, cipher, MAC and host key algorithm and reports which ones work. Nothing is read or written on the server. Use it when a connection fails with "MAC error" on one computer but works on others. The output starts with the .NET Framework release, the Windows version and the FIPS policy. A detailed log is always written: to the `--debug-log` file, or to `%TEMP%\keepsake-debug.log`. Send both to support.

```text
keepsake sftp-test --repo sftp://user@host[:port]/path [--sftp-password-file <file> | --sftp-key-file <file>] [--sftp-host-key SHA256:...]
```

Uses the SFTP options from Common options above. Without `--sftp-host-key` the host key is shown and you are asked to trust it for the test. With `--non-interactive` the test then fails. The exit code is 0 once the test has run, whatever the results are.

```bash
keepsake sftp-test --repo sftp://backup@nas.example.org/backups/fileserver --sftp-password-file C:\Keepsake\sftp.txt
```

## doctor

Checks this computer for everything Keepsake needs and prints one line per check (`OK`, warning, failure or info): Windows version and Service Pack, .NET Framework 4.8, elevation, the Keepsake service and whether its interface answers, TLS 1.2 and .NET strong crypto (relevant on Windows Server 2008 R2), Volume Shadow Copy, the Event Log source, protection of the data folder, the backup privilege and free space on the system drive. It needs no password.

```text
keepsake doctor [--json]
```

Exit code 2 if any check failed, 1 if there are warnings, 0 otherwise. Run it elevated, or some checks report only what a normal user can see. See also [Windows Server 2008 R2](../WINDOWS-2008R2.md) and [Troubleshooting](troubleshooting.md).

```bash
keepsake doctor
```

## version

Shows the program version and the repository format version.

```bash
keepsake version
```

With `--json`: `{"version": "...", "repositoryFormat": 1}`.

## help

Shows the overview of all commands, or the help of one command.

```bash
keepsake help backup
```

## Automation

### Unattended scripts

For scripts and scheduled tasks:

- Never let a command prompt. Pass `--non-interactive`, so a missing password fails at once with exit code 3 instead of hanging. Keepsake also switches to non-interactive mode by itself when input is redirected.
- Pass secrets as files (`--password-file`, `--recovery-key-file`, `--app-password-file`, `--sftp-password-file`) or through environment variables (`KEEPSAKE_PASSWORD`, `KEEPSAKE_RECOVERY_KEY`, `KEEPSAKE_APP_PASSWORD`, `KEEPSAKE_SFTP_PASSWORD`, `KEEPSAKE_SFTP_KEY_PASSPHRASE`). Restrict the files to the account that runs the task. `KEEPSAKE_REPOSITORY` saves repeating `--repo`.
- Use `--json` for output a script parses. Results go to stdout as JSON, progress is switched off, and errors are printed as `{"status": "error", "exitCode": n, "error": "..."}`. Exception: `jobs export` without `--out` always prints the job JSON.
- Evaluate the exit code (see Exit codes above), not the text output.
- Prefer service jobs over scripted `backup` commands. A job uses VSS, retries, notifications and the run history, and `keepsake run <job> --wait` returns the result of that run as its exit code.

### Example: start a job from a scheduled task

A batch file that a scheduled task runs elevated (for example as `SYSTEM` or an administrator, "Run with highest privileges"):

```bash
keepsake run fileserver --wait --non-interactive --app-password-file C:\Keepsake\app-password.txt
```

The task's last result is then `0` (success), `1` (warnings), `2` (failed) or `6` (cancelled), and the run shows up in [History and logs](history.md) like any other run.

### Example: monitoring check

A monitoring agent can read the job states as JSON. The `jobs` array contains each job's name, `enabled`, `lastRun` (with `state`, `start` and more) and `nextRun`:

```bash
keepsake status --json --non-interactive --app-password-file C:\Keepsake\app-password.txt
```

For a check that is independent of the service, verify the repository directly, read-only, with a 5 % data sample:

```bash
keepsake check --repo \\nas\backup\fileserver --password-file C:\Keepsake\repo-password.txt --read-data-percent 5 --json --non-interactive
```

Exit code 0 means healthy, 1 warnings, 2 problems found, 4 wrong password, 5 locked (for example a backup is running right now). Also see [Monitoring and notifications](monitoring.md) for e-mail alerts and Event Log IDs.

### SFTP repositories without a prompt

On the first connection to an SFTP server Keepsake shows the server's host key fingerprint and asks whether to trust it. Unattended runs cannot answer that question and fail instead. Compare the fingerprint once with the one your provider shows or with `ssh-keygen -lf <host key file>` on the server, then pin it with `--sftp-host-key` or `KEEPSAKE_SFTP_HOST_KEY`:

```bash
keepsake snapshots --repo sftp://backup@nas.example.org/backups/fileserver --sftp-password-file C:\Keepsake\sftp.txt --sftp-host-key SHA256:nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8 --password-file C:\Keepsake\repo-password.txt --non-interactive
```

If the server presents a different key than the pinned one, the connection is refused (exit code 2). If that happens unexpectedly, find out why before you pin the new key.
