# History and logs

The **History & logs** page lists every run of the Keepsake service: backups, restores, integrity checks and
cleanups, with their result. Double-click a run to open its detail window with the summary, the list of everything that
was not backed up and why, the phase timings and the full log. The service keeps run logs for a configurable time and
stores them as files under `%ProgramData%\Keepsake`.

## The run list

The list shows the newest 200 runs, newest first, and refreshes by itself while the page is open. Use the drop-down
at the top right to show **All jobs** or a single job.

| Column | Content |
|---|---|
| **Result** | State of the run (see below), with a coloured dot. |
| **Started** | Local start time. |
| **Job** | Job name. |
| **Type** | `Backup`, `Full backup`, `Restore`, `Check` or `Cleanup`. |
| **Trigger** | `Manual`, `Schedule`, `Missed` (catch-up after the service was not running) or `Retry <n>`. |
| **Duration** | Run time; `…` while the run is queued or running. |
| **Files** | Backups: number of files, plus `(<n> not backed up)` if any were skipped. Restores: `<n> restored`. |
| **Data** | Backups: `<new> new / <stored> stored`. Restores: bytes written. |
| **Message** | Short reason for a failure or warning. |

To open a run, double-click it, or select it and click **Show details**.

!!! note
    Restores in [recovery mode](recovery-mode.md) do not run in the service and do not appear here.

## Run types

| Type | What it does |
|---|---|
| Backup / Full backup | Backs up the job's sources. A full backup reads every file again. |
| Restore | Restores files from a backup (see [Restoring files](restore.md)). |
| Check | Verifies the repository structure and decrypts a share of the data (see [Retention and checks](job-retention.md)). |
| Cleanup | Applies the retention rules and frees the space of removed backups. |

## Run states

| State | Meaning |
|---|---|
| **Queued** | Waiting to start, for example because the maximum number of parallel jobs is reached. |
| **Running** | In progress. |
| **Success** | Finished without problems. |
| **Warning** | Finished, but with problems: some items were not backed up or not restored. A backup with warnings still created a usable backup of everything else. |
| **Failed** | The run did not complete. A failed backup creates no backup. |
| **Cancelled** | Stopped by a user, or still queued when the service stopped unexpectedly. |

Items that are excluded by a pattern are listed as *skipped* but do not cause a warning. Locked or unreadable files,
missing source folders, files deleted or changed during the backup, links that are not followed and VSS problems do.

If the service stops unexpectedly (crash, power loss), runs that were still running are marked **Failed** with
`Interrupted: the service stopped unexpectedly.` at the next start; queued runs are marked **Cancelled**.

## The run detail window

The window title shows job and start time. At the top you see the state with the message, then the summary:

- run ID, type and trigger
- start, end and duration
- backups: new, changed, unchanged and not backed-up files, number of folders; total size, bytes read, new data and
  stored size after compression
- restores: restored, already up to date, kept and failed files, bytes written
- the short ID of the backup (snapshot) that was written or restored

### Not backed up / problems

This tab lists every item that was not processed or needs attention, with **Kind**, **Path** and **Reason**.

| Kind | Meaning |
|---|---|
| Error | The item was not backed up or restored (for example `Access denied.`, `Path is too long.`). Counted as "not processed". |
| Warning | Processed with a problem (for example `File changed while it was being backed up; the stored copy may be inconsistent.`). |
| Skipped | Left out on purpose (excluded by a pattern, or an existing file kept during a restore). |

- The filter drop-down shows **Problems** (errors and warnings, default), **Errors**, **Warnings**, **Skipped** or
  **All**.
- The search box filters by path or reason.
- The counter next to it shows `<n> not processed · <n> warnings · <n> skipped`.
- The list shows at most 5,000 entries. **Export all as CSV…** saves the complete list (all kinds, unfiltered) as a
  semicolon-separated UTF-8 file with the columns `Kind;Path;Message;Time`.

[Troubleshooting](troubleshooting.md) explains the common reasons.

### Phases

Each phase of the run with its start time (UTC) and duration in seconds. Use it to see where a slow run spends its time.

### Full log

The complete readable log of the run, as it is stored on disk. Copy it from here for support.

## Log retention

The service deletes old run logs once a day. A run is removed when it is older than **Keep run logs for (days)** or
when the job has more than **… and at most (runs per job)** newer runs. The defaults are 90 days and 200 runs per job.
Change them in [Settings](settings.md).

## Where the logs live on disk

All files are below the service's data folder, by default `%ProgramData%\Keepsake` (the **Settings** page shows the
actual **Data folder**). Only SYSTEM and Administrators can read it.

| File | Content |
|---|---|
| `runs\<job ID>\<run ID>.record.json` | Run record: state, times, counts, message. |
| `runs\<job ID>\<run ID>.detail.json` | Phases and issue list. |
| `runs\<job ID>\<run ID>.log` | Readable full log. |
| `logs\service.log` | Diagnostic log of the service itself (start, stop, schedules, settings changes, errors). Rotated at 10 MB, 5 files kept (`service.log.1` … `.5`). |
| `logs\debug.log` | Optional SFTP debug log, only when switched on in [Settings](settings.md). |

The command line shows the same information: `keepsake runs [<job>]` lists runs and `keepsake log <run-id>` prints a
run's full log. See [Command line](command-line.md).
