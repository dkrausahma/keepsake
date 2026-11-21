# Options

The **Options** tab holds the settings that change how a job runs: catch-up after downtime, VSS shadow copies, retries, commands before and after the backup, and credentials for network shares the job reads from. Two related settings are elsewhere: **Enabled (scheduled runs active)** at the top of the job editor, and the e-mail notifications on the [Retention & checks](job-retention.md) tab.

## Overview

| Option | Default | Allowed |
|---|---|---|
| **Run a missed backup once after the server starts (e.g. after it was switched off)** | on | |
| **Use VSS snapshots so open and locked files are backed up (recommended)** | on | |
| **Retry** … **times, after** … **minutes** | 2 times, 15 minutes | 0 to 10 times, 1 minute to 24 hours |
| **Command before the backup** | empty | |
| **Command after the backup** | empty | |
| **Command timeout** | 30 minutes | 1 minute to 24 hours |
| **Network shares used as sources** | none | |

## Enable or disable the job

**Enabled (scheduled runs active)** sits next to the job name at the top of the editor. Clear it to stop all scheduled runs, catch-up runs, retries and "no successful backup" alerts of the job, for example while a server is being migrated. See [The Backup jobs page](jobs.md) for what a disabled job can still do.

## Missed backups

With **Run a missed backup once after the server starts (e.g. after it was switched off)** on, a scheduled run that was missed while the server or the service was down starts once after the service starts again. Turn it off if a backup during working hours would disturb users. Details: [Schedule](job-schedule.md).

## VSS shadow copies

With **Use VSS snapshots so open and locked files are backed up (recommended)** on, Keepsake creates a Volume Shadow Copy of each local drive that holds a source and reads the files from it. Open and locked files, such as Outlook data files or databases, are backed up in the state they had when the shadow copy was taken. The shadow copies are deleted after the run.

- VSS is used for local fixed drives only. Sources on network shares are read directly. For a removable drive, Keepsake reads the files directly and adds a warning.
- If a shadow copy cannot be created, the backup does not fail. Keepsake reads the files directly and adds the warning *VSS snapshot could not be created (...). Files are read directly; open or locked files may be skipped.* The run then ends with warnings.
- With VSS off, locked files are skipped and listed in the run log.

Leave VSS on unless a specific application has problems with shadow copies.

## Retries

**When a scheduled backup fails**, Keepsake waits the given number of minutes and tries again, up to the given number of times. `0` turns retries off. Retries apply to scheduled and caught-up backups only; a backup you start manually is not retried. See [Schedule](job-schedule.md).

## Commands before and after the backup

Use the commands to prepare the data, for example to dump a database into a folder the job backs up, or to stop and restart a service.

| Field | When it runs |
|---|---|
| **Command before the backup (cmd.exe; a non-zero exit code cancels the backup)** | Before the VSS shadow copy is taken and before the repository is opened. |
| **Command after the backup (always runs; KEEPSAKE_STATUS holds success / warning / failed)** | After the backup, whatever the result: also after a failed or cancelled backup and after a failed pre-command. |

How the commands run:

- Through `cmd.exe /c`, as the service account **LocalSystem** (SYSTEM), with `C:\Windows\System32` as working directory. Use full paths for scripts and programs.
- Mapped drive letters are not available. Use UNC paths, and remember that SYSTEM reaches network shares with the computer account.
- If a command runs longer than **Command timeout**, Keepsake ends it together with all processes it started. A pre-command that times out fails the run.
- A **pre-command** that ends with an exit code other than `0` fails the run: *The pre-command failed with exit code N. The backup was not started.* A failed scheduled run is retried as usual.
- A **post-command** that ends with an exit code other than `0` turns a successful run into a run with warnings. It does not change a failed run.
- The output of both commands and their exit codes appear in the run log in [History and logs](history.md).

### Environment variables

Keepsake passes these variables to both commands:

| Variable | Content |
|---|---|
| `KEEPSAKE_JOB` | The job name. |
| `KEEPSAKE_RUN_ID` | The ID of the run, as shown in the history. |
| `KEEPSAKE_MODE` | `incremental` or `full`. |
| `KEEPSAKE_REPOSITORY` | The destination as text, for example `E:\Backups\Fileserver`, `USB disk Backup1 \Keepsake` or `sftp://backup@host/backups/fileserver`. |

The post-command also gets:

| Variable | Content |
|---|---|
| `KEEPSAKE_STATUS` | `success`, `warning` or `failed`; `cancelled` if the backup was cancelled. |
| `KEEPSAKE_SNAPSHOT` | The ID of the new snapshot, or empty if no snapshot was created. |

Job names and paths can contain spaces, so always quote the variables:

```bat
C:\Scripts\after-backup.cmd "%KEEPSAKE_JOB%" "%KEEPSAKE_STATUS%"
```

```bat
"C:\Program Files\PostgreSQL\16\bin\pg_dump.exe" -U backup -f "D:\Dumps\app.sql" app
```

!!! warning
    The commands run with full SYSTEM rights. Only enter commands and scripts that only administrators can change.

## Network shares used as sources

For each network share the job reads from, you can store an account of its own. Click **Add share** and fill in:

| Column | Content |
|---|---|
| **Share (\\server\share)** | The share itself, `\\server\share`, without subfolders. |
| **User (DOMAIN\user)** | `DOMAIN\user` or `server\user`. |
| **Password** | The password. For a stored password the field stays empty; leave it empty to keep it. |

Without an entry, the service connects with the computer account (`DOMAIN\SERVER$`). The credentials are used only for the connection to that share. Credentials for a destination share are entered on the [Destination](job-destination.md) tab instead. **Remove** deletes an entry.
