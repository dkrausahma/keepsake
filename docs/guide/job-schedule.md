# Schedule

The **Schedule** tab decides when a job runs automatically. A job can have several schedule entries, each with its own times and its own backup type. A job without entries runs only when you start it.

## Add and edit entries

The list on the left shows all entries as text, for example `daily at 22:00 (incremental)` or `Sat at 23:00 (full)`. Select an entry to edit it on the right.

- **Add schedule** adds an entry: daily at 22:00, incremental.
- **Remove** deletes the selected entry.
- **Schedule active** switches a single entry off without deleting it. The list then shows `– disabled` after it.

A new job starts with one entry, daily at 22:00, incremental. The schedule only works while the job itself is enabled (**Enabled (scheduled runs active)** at the top of the editor).

## Schedule kinds

Choose the kind under **Repeat**.

| Kind | Fields | Runs |
|---|---|---|
| **Daily** | **At (HH:mm, several separated by commas)**, **On (none ticked = every day)** | At each time, on the ticked weekdays. No weekday ticked means every day. |
| **Interval** | **Every … minutes (at least 5)** | Repeatedly, every N minutes. Allowed: 5 minutes to 7 days (10080 minutes). |
| **Monthly** | **At (HH:mm, ...)**, **On days of the month (e.g. 1, 15; 31 = last day)** | At each time, on each listed day of the month. |

Times are local times in 24-hour format, for example `22:00` or `6:30, 12:30, 18:30`.

**Monthly and short months:** a day that does not exist in a month runs on the last day of that month. `31` runs on 30 April and on 28 or 29 February; `30` also runs on the last day of February.

**Interval timing:** the interval counts from the last scheduled start of this entry, not from the end of the run and not from manual runs. A new interval entry first runs one interval after you save it.

!!! note
    A new entry never starts a run immediately when you save. The service starts counting from the moment it first sees the entry. If you change the time of an existing entry to a time that already passed today, and the entry has not run since that time, the service treats it as a missed run.

## Backup type per entry

Each entry has a **Backup type**: **Incremental** or **Full**.

- **Incremental** reads only files whose size or modification time changed.
- **Full** re-reads every file. It is slower but catches files that changed without a new modification time.

Every backup is a complete snapshot either way, and only new data is stored.

If several entries are due at the same time, Keepsake starts one run. It is a full backup if at least one of the due entries is **Full**.

### Typical setup

| Entry | Repeat | Settings | Backup type |
|---|---|---|---|
| 1 | Daily | At `22:00`, Mon to Fri ticked | Incremental |
| 2 | Daily | At `22:00`, Sat ticked | Full |

This gives a fast incremental backup on working days and a full backup once a week. If you prefer a daily backup also on Saturday, leave all weekdays unticked in entry 1: on Saturday both entries are due and Keepsake runs one full backup.

## Missed runs after downtime

A scheduled run is "missed" when its time passed more than 5 minutes ago without a run, for example because the server was switched off or the service was stopped.

With **Run a missed backup once after the server starts (e.g. after it was switched off)** on the [Options](job-options.md) tab (default: on), the service starts the backup once shortly after it starts again. Several missed times are caught up with a single run, not one run per missed time. With the option off, the missed run is skipped and Keepsake writes a warning (event ID 1003) to the Windows Application event log.

## Retries after a failure

When a scheduled backup fails, Keepsake retries it. Set this on the [Options](job-options.md) tab under **When a scheduled backup fails**: **Retry** N **times, after** M **minutes** (default: 2 times after 15 minutes; 0 to 10 retries, 1 minute to 24 hours delay).

- Retries apply to scheduled and caught-up runs, not to backups you start with **Run now** or **Full backup**.
- A retry uses the same backup type as the failed run.
- Cancelled runs and runs with warnings are not retried.
- The card's **Next** line shows the time of a pending retry.

## A run is due while the job is still running

A job never runs twice at the same time. If an entry becomes due while the same job is still running or queued, that scheduled run is skipped. Keepsake logs it in the service log and writes a warning (event ID 1003) to the Windows Application event log. The next occurrence runs as usual.

## Manual-only jobs

Remove all entries to make a job manual-only (the tab says **No schedule = manual runs only.**). The job card then shows **Manual only** and **No schedule**. Start the backups with **Run now** or **Full backup** on the [Backup jobs](jobs.md) page, or from the [command line](command-line.md).

!!! tip
    A manual-only job can still alert you when nobody has run it for a while: set **Alert if no successful backup for … hours** on the [Retention & checks](job-retention.md) tab.
