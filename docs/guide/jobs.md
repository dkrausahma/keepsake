# The Backup jobs page

The **Backup jobs** page is the start page of Keepsake. It shows one card per job with its current state, the last run, the schedule and the next run, and it holds the buttons to run, check, clean up, restore and edit each job. Each job backs up its selected folders into its own encrypted repository.

## Create a job

1. Click **+  New job** (top right). If no job exists yet, the page shows **Create the first job** instead.
2. Enter a **Job name**.
3. Work through the tabs of the job editor:
    - [What to back up and Exclusions](job-sources.md)
    - [Destination](job-destination.md)
    - [Schedule](job-schedule.md)
    - [Retention & checks](job-retention.md)
    - [Options](job-options.md)
4. Click **Save**.

When you save, Keepsake checks the settings. Problems are listed in red next to the **Save** button and the job is not saved. If the destination holds no repository yet, Keepsake asks you to confirm creating one and then shows the recovery key (see [Destination](job-destination.md)).

A new job starts with these defaults: one schedule, daily at 22:00, incremental; keep one backup per day for 14 days, one per week for 8 weeks and one per month for 12 months; clean up and check every 7 days; VSS on; 2 retries after 15 minutes.

!!! note
    There is no "clone" or "copy job" function. To create a similar job, create a new one and repeat the settings.

## What a job card shows

| Element | Meaning |
|---|---|
| Coloured dot and state text | State of the job (see below). The colour follows the state of the last run. |
| `→ ...` | The destination: a path, `USB disk <label> \<folder>` or `sftp://user@host/path`. |
| **Last run** | Start time, duration, number of files and the amount of new data of the last run. `—` if the job never ran. |
| **Schedule** | All schedule entries, for example `daily at 22:00 (incremental) · Sat at 23:00 (full)`. **Manual only** if the job has no schedule. |
| **Next** | Time of the next automatic run (or a pending retry), followed by how long ago the last successful backup was. Shows **Disabled** or **No schedule** when nothing is planned. |
| Message line | For runs that did not succeed: the error or warning summary of the last run. |

While a run is active, the card shows a progress bar, the number of files, the bytes read and stored, and the file currently being processed.

### State texts

| Text | Meaning |
|---|---|
| **Last backup OK** | The last run finished without problems. |
| **Last backup with warnings** | The run finished, but some items were not backed up (for example locked files). Open **History** for the list. |
| **Last backup FAILED** | The run did not create a backup. The message line shows why. |
| **Last backup cancelled** | Someone cancelled the last run. |
| **Never run** | The job has no run yet. |
| **Disabled** | Scheduled runs are switched off (see below). |
| **Waiting for a free slot** | The run is queued because the maximum number of parallel jobs is already running. |
| Phase name | While running: **Starting**, **Pre-command**, **Creating VSS snapshot**, **Opening repository**, **Backing up**, **Checking**, **Cleaning up**, **Post-command** and so on. |

The state text also covers checks and cleanups: these appear as runs of the job, too.

## Actions on a card

| Button | What it does |
|---|---|
| **Run now** | Starts an incremental backup immediately. |
| **Full backup** | Starts a full backup immediately. |
| **Cancel** | Stops the running or queued run of this job after a confirmation. Data already stored is kept and reused by the next run. A queued run is simply removed. |
| **Edit** | Opens the job editor. Not available while the job runs. |
| **Check** | Verifies the repository now (integrity check). It reads the share of data set on the [Retention & checks](job-retention.md) tab. |
| **Clean up** | Applies the retention rules now and frees space. Only works if the job has retention rules; otherwise Keepsake reports that there is nothing to clean up. |
| **History** | Opens [History and logs](history.md) filtered to this job. |
| **Restore** | Opens [Restoring files](restore.md) for this job. |
| **Check keys** | Opens **Check password or recovery key**: test the repository password or the recovery key (`KS1-...`) from your KeePass entry without restoring. Only the repository's key files are read; nothing is changed, stored or logged. See [Passwords and keys](../PASSWORDS-AND-KEYS.md). |
| **Delete** | Deletes the job and its run history after a confirmation. |

**Run now**, **Full backup**, **Edit**, **Check**, **Clean up** and **Delete** are greyed out while the job is running or queued. **Run now** and **Full backup** are also greyed out for a disabled job.

### Full and incremental backups

Every backup is a complete snapshot of the selected files; you can restore any snapshot on its own. The two modes differ only in how files are read:

- **Incremental** reads only files whose size or modification time changed since the last backup. Unchanged files are taken over from the previous snapshot. This is fast.
- **Full** re-reads every file instead of trusting size and modification time. It is slower but catches changes that did not update the modification time.

In both modes only new data is stored, so a full backup does not double the repository size.

### Delete a job

Deleting removes the job definition and its run history from this server. The backups in the destination are **not** deleted. You can still restore them with Keepsake Recovery, using the location and the repository password or recovery key from your KeePass entry (see [Disaster recovery](../DISASTER-RECOVERY.md)). A running job cannot be deleted; cancel it first.

## Disabled jobs

Clear **Enabled (scheduled runs active)** at the top of the job editor to disable a job. A disabled job:

- starts no scheduled runs, no catch-up runs and no retries,
- sends no "no successful backup" alert,
- cannot be started with **Run now** or **Full backup**.

**Check**, **Clean up**, **Restore** and **Check keys** still work.

## Parallel jobs

Keepsake runs at most as many jobs at the same time as set in **Jobs that may run at the same time** on the [Settings](settings.md) page (1 to 8, default 1). Further runs wait in a queue and show **Waiting for a free slot**. One job never runs twice at the same time: a scheduled run that becomes due while the same job is still running is skipped (see [Schedule](job-schedule.md)). Checks and cleanups that follow a backup wait behind the backup of the same job.
