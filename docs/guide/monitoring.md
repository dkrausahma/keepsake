# Monitoring and notifications

Keepsake reports the result of every run in three ways: e-mail notifications, entries in the Windows Event Log and the
`keepsake status` command for scripts. A separate alert fires when a job has had no successful backup for a set number
of hours, which also catches jobs that silently stopped running. Use whichever fits your existing monitoring.

## E-mail notifications

E-mail needs an SMTP server in [Settings](settings.md). Which events send a mail is set per job, in the job editor on the
**Retention & checks** tab under **Notifications (e-mail, see Settings)** (see [Retention and checks](job-retention.md)):

| Switch | Default | Sends a mail when |
|---|---|---|
| **When a run fails** | on | Any run of the job (backup, restore, check, cleanup) fails. |
| **When a run has warnings (files not backed up)** | on | Any run of the job finishes with warnings. |
| **After every successful backup** | off | A backup finishes successfully (not checks, cleanups or restores). |
| **Alert if no successful backup for … hours (0 = off)** | 0 | See below. Allowed: 0–2160 hours (90 days). |

Cancelled runs send no mail.

The subject looks like `[Keepsake] FAILED: fileserver – backup on SRV01` (`FAILED`, `WARNING` or `OK`). The body lists
job, server, run type and trigger, run ID, start time and duration, the result with its message, the file counts and
the first 30 problems from the issue list. It ends with where to find the full log
(**History & logs**, or `keepsake log <run-id>`).

If a mail cannot be sent, the service writes the reason to its log and to the Event Log (ID 1005). Use
**Send test e-mail** in [Settings](settings.md) to check the configuration.

## "No successful backup" alert

Set **Alert if no successful backup for … hours** to a value above 0 to be warned when a job has not backed up
successfully for that long. A backup with warnings counts as successful. For a new job, the time counts from when the
job was created.

- The alert is checked only for enabled jobs and not while the job is running.
- It writes Event Log ID 1004 and the service log, and sends the mail
  `[Keepsake] NO BACKUP: <job> on <computer> – since <time>` if e-mail is configured.
- While the condition lasts, it repeats once per alert period (for example every 26 hours with a value of 26).

!!! tip
    Choose a value somewhat above the schedule interval: for a daily backup, 26 or 30 hours leaves room for a slow run
    without false alarms.

## Windows Event Log

The service writes important events to the **Application** log with the source **Keepsake**:

| ID | Level | Event |
|---|---|---|
| 1000 | Information | A run (backup, restore, check, cleanup) succeeded. |
| 1001 | Error | A run failed. The message contains the reason. |
| 1002 | Warning | A run finished with warnings. |
| 1003 | Warning | A scheduled backup was skipped because the previous run was still running, or was missed while the service was not running and catch-up is off for the job. |
| 1004 | Error | No successful backup for longer than the job's alert period. |
| 1005 | Warning | An e-mail notification could not be sent. |

Events 1000–1002 end with the run ID, the job ID and the start time. Cancelled runs write no event.

The service registers the source at start-up, which needs administrator rights once (the service runs as LocalSystem,
so this normally happens automatically). `keepsake doctor` shows whether the source is registered.

### Connect your monitoring tool

Any tool that reads the Windows Event Log can alert on Keepsake (PRTG, Zabbix, Nagios/Icinga, SCOM, Checkmk and others).
A typical setup:

- Alert on source `Keepsake`, IDs **1001** and **1004** (errors).
- Warn on IDs **1002**, **1003** and **1005**.
- Optionally use **1000** as a heartbeat: no 1000 event within the expected time means the backup did not run.

To test a filter, query the log in PowerShell:

```powershell
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Keepsake' } -MaxEvents 20
```

## Status for scripts

`keepsake status` shows the service and the state, last run, last result and next run of every job. Add `--json` for
machine-readable output: an object with `service` (version, host, start time, data folder, running runs) and `jobs`
(one entry per job, including the last run and its state). With `--json` the command exits with code 0 whenever it could
query the service; evaluate the job states in the JSON.

Commands that run something (`keepsake run <job> --wait`, `backup`, `check` …) return exit codes your scripts can use:
0 success, 1 warnings, 2 failed, 3 usage error, 4 wrong password or key, 5 repository locked, 6 cancelled.

Service commands need the application password; in scripts pass it with `--app-password-file <file>`. See
[Command line](command-line.md).
