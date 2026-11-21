# Keepsake Backup

Keepsake Backup backs up folders of Windows servers and PCs into encrypted, compressed and deduplicated
repositories – on a local disk, a network share, rotating USB disks or an SFTP server – and restores them, also on
another computer without the original server, its configuration or its Windows domain.

It runs on Windows Server 2008 R2 SP1 and Windows 10/11 (x64). A Windows service runs the backups on their schedules;
the **Keepsake Backup** program and the `keepsake` command line manage it.

!!! tip "Help for the page you are on"
    Press ++f1++ in Keepsake Backup to open the help page of the current page, job editor tab or dialog.
    **Help** in the sidebar opens this overview.

## Where to start

| You want to … | Read |
|---|---|
| Install Keepsake and back up the first folder | [Getting started](guide/getting-started.md) |
| Install, upgrade or uninstall, use the portable version | [Installation and upgrade](guide/installation.md) |
| Set up or change a backup job | [The Backup jobs page](guide/jobs.md) and the job editor tabs: [What to back up](guide/job-sources.md), [Destination](guide/job-destination.md), [Schedule](guide/job-schedule.md), [Retention and checks](guide/job-retention.md), [Options](guide/job-options.md) |
| Get files back | [Restoring files](guide/restore.md) |
| Restore on a replacement computer | [Recovery mode](guide/recovery-mode.md) and the [disaster recovery runbook](DISASTER-RECOVERY.md) |
| Know which password or key you need | [Passwords and keys](PASSWORDS-AND-KEYS.md) |
| See what a run did, or what was not backed up | [History and logs](guide/history.md) |
| Get e-mails or Event Log entries for monitoring | [Monitoring and notifications](guide/monitoring.md) |
| Script Keepsake | [Command line](guide/command-line.md) |
| Fix a problem | [Troubleshooting](guide/troubleshooting.md) |

## How Keepsake works

- **Repository.** Each backup job writes into its own repository: a folder (or SFTP path) with encrypted pack files.
  Files are split into chunks; a chunk that is already stored – from an earlier backup or another file – is not stored
  again. That makes every backup small after the first one.
- **Snapshots.** Every backup run creates a snapshot: a complete point-in-time image you can restore on its own.
  *Incremental* and *full* only describe how changes are found, not what a restore needs.
- **Encryption.** Everything in the repository – contents, file names, folder structure – is encrypted with
  AES-256-GCM. The storage operator only sees opaque files. The repository password or the recovery key opens it.
- **Self-describing.** The location plus the password or recovery key are all a restore needs. No database, no
  configuration of the old server.
- **Service.** The *Keepsake Backup Service* runs as LocalSystem, independent of a logged-on user. Closing the program
  never stops a backup.
