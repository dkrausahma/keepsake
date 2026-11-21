# Retention and checks

The **Retention & checks** tab decides which backups a job keeps, how often old backups are removed to free space, how often the repository is verified, and when Keepsake sends e-mail about the job.

## Which backups to keep

Each rule keeps some snapshots. A snapshot is kept if **any** rule keeps it. `0` turns a rule off. If all rules are `0`, Keepsake keeps every snapshot and never removes anything.

| Rule | Keeps |
|---|---|
| **Last backups** | The N newest snapshots. |
| **One per day, for … days** | The newest snapshot of each of the last N days that have a snapshot. |
| **One per week, for … weeks** | The newest snapshot of each of the last N weeks (Monday to Sunday) that have a snapshot. |
| **One per month, for … months** | The newest snapshot of each of the last N calendar months that have a snapshot. |
| **One per year, for … years** | The newest snapshot of each of the last N calendar years that have a snapshot. |
| **Everything from the last … days** | Every snapshot younger than N days, counted from the time of the cleanup. |

Days, weeks, months and years are counted in local time and only where a snapshot exists. A day without a backup (for example a weekend) does not use up one of the N days. Allowed values are 0 to 10000.

A new job starts with 14 days, 8 weeks and 12 months; the other rules are `0`.

### Example

A job backs up every evening at 22:00 and keeps **One per day, for 14 days**, **One per week, for 8 weeks** and **One per month, for 12 months**.

- The daily rule keeps the snapshots of the 14 most recent days.
- The weekly rule keeps the newest snapshot of each of the 8 most recent weeks. For the last two weeks these are snapshots the daily rule keeps anyway; for the six weeks before, it keeps the Sunday snapshot.
- The monthly rule keeps the newest snapshot of each of the 12 most recent months, that is the snapshot of the last day of each month.

Together the job keeps about 30 snapshots: every day for two weeks, then one per week for about two months, then one per month for a year. All other snapshots are removed at the next cleanup.

### Protected backups

Protected snapshots are never removed by the retention rules. You protect a snapshot on the [Restoring files](restore.md) page with **Protect this backup** (and remove it with **Remove protection**).

### Rules apply to this job's snapshots only

Every snapshot records the name the job had when it was created. Retention, the cleanup preview and **Clean up** only consider snapshots with the job's current name.

!!! warning "Renaming a job"
    If you rename a job, snapshots created under the old name are no longer covered by retention. They stay in the repository until you remove them. Avoid renaming jobs that already have backups, and never give two jobs that share a repository the same name.

## Preview the cleanup

Click **Preview cleanup** to see what the next cleanup would remove with the values currently on the tab, before you save. The list shows each snapshot with `keep` or `REMOVE`, its time and the rules that keep it (for example `daily 2026-11-20`, `weekly 2026-11-16`, `protected`). The preview works for saved jobs that have backups; for a new job the button is not available.

## Cleanup

**Clean up every … days (after a backup; 0 = never)** controls the automatic cleanup. Default: 7 days.

After every backup that ends with **OK** or with warnings, Keepsake checks whether the last cleanup is older than this interval. If so, it queues a cleanup right after the backup. A cleanup only runs if the job has at least one retention rule.

A cleanup:

1. removes the snapshots that no rule keeps,
2. deletes stored data that no remaining snapshot needs, including data left over by interrupted backups,
3. repacks files in the repository that are at least 25 % unused, so the space is actually freed.

The run's message shows how many snapshots were removed and how much space was freed. If the repository has missing data, the cleanup stops without changing anything and asks you to run a check first.

To clean up immediately, click **Clean up** on the job card on the [Backup jobs](jobs.md) page.

!!! note
    Cleanup needs permission to delete files at the destination. **Check destination** on the [Destination](job-destination.md) tab tells you if deleting is not possible.

## Integrity checks

**Check every … days, reading … % of the data** controls the automatic check. Defaults: every 7 days, 10 %.

- Every check verifies the structure of the repository: index, snapshots and that all referenced data exists.
- In addition, the given share of stored data is read, decrypted and compared with its checksum. Each check picks a random sample, so over time all data gets verified. `0 %` checks the structure only; `100 %` reads everything.

After every backup that ends with **OK** or with warnings, Keepsake checks whether the last check is older than the interval and, if so, queues a check after the backup (and after a cleanup, if one is due). `0` days turns automatic checks off. Since a new job has no check yet, the first check runs after its first backup.

To check immediately, click **Check** on the job card. A manual check reads the same percentage.

## Notifications

E-mail notifications are configured per job on this tab. They need a working mail server on the [Settings](settings.md) page.

| Option | Sends a mail when |
|---|---|
| **When a run fails** | A backup, check or cleanup fails. Default: on. |
| **When a run has warnings (files not backed up)** | A run ends with warnings. Default: on. |
| **After every successful backup** | A backup ends without problems. Default: off. |
| **Alert if no successful backup for … hours (0 = off)** | No backup ended with **OK** or with warnings for this many hours. Default for a new job: 48. |

The "no successful backup" alert also catches a stopped schedule or a job that keeps failing. It is repeated once per alert period while the situation lasts, and it is not sent while the job is running. A disabled job sends no alert. See [Monitoring and notifications](monitoring.md) for the mail content.
