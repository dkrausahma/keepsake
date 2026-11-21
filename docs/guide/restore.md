# Restoring files

The **Restore** page brings back folders and files from a backup of one of your jobs. You pick a job and a point in
time, tick what you need in the folder tree, choose where it goes and what happens to files that already exist. The
Keepsake service does the work and logs the restore like any other run. To restore without the service, without a
login or without a job – for example on a replacement computer – use [recovery mode](recovery-mode.md) instead.

## Pick the job and the point in time

1. Open **Restore** in the sidebar.
2. Select the job under **Job**.
3. Select the point in time under **Backup from**. The newest backup is selected first.

Each entry in **Backup from** shows:

| Part | Example | Meaning |
|---|---|---|
| Local time | `2026-11-20 22:00` | When the backup started. |
| `protected` | `· protected` | The backup is never removed by the retention rules. |
| Mode | `· incremental` / `· full` | How the backup read the files. Both kinds contain the complete file set. |
| Size | `· 48,210 files, 112 GB` | Number of files and total size in this backup. |

!!! note
    The list shows every backup in the job's repository. If several jobs or computers write to the same repository,
    their backups appear here as well. The label does not show the job or computer name.

## Select folders and files

The tree starts at the level that contains the backed-up folders. The line above it (`In D:\Data`) shows where you are.
Folders are shown in bold with their modification time; files show size and modification time.

- Tick a folder to restore it with everything below it. Items inside a ticked folder are ticked and greyed out.
- Tick single files to restore only those.
- The line below the tree shows how many items are selected, or `Nothing selected – tick folders or files on the left.`

## Choose the target

Under **Restore to**:

- **A different folder** (default) – enter a folder or pick one with **…**. It is created if it does not exist.
    - Without **Keep the full original path (D\Data\…)**, each ticked item lands directly in the target folder.
      Ticking `D:\Data\Finance` and restoring to `E:\Restore` gives `E:\Restore\Finance`.
    - With **Keep the full original path (D\Data\…)**, the drive and the full path are recreated below the target:
      `E:\Restore\D\Data\Finance`.
- **The original location** – files go back to where they were backed up from (`D:\Data\Finance`).

Restores never follow a junction or symbolic link below the target folder (or below the backed-up folder when you
restore to the original location).

## Decide what happens to existing files

**If a file already exists** offers four policies. A file that is identical to the backed-up version (same size and
modification time) is always left alone and counted as "already up to date".

| Policy | Effect |
|---|---|
| **Keep the existing file** (default) | The existing file stays; the backed-up version is not written. |
| **Replace if the backup is newer** | Replaces the file only if the backed-up version has a newer modification time. |
| **Always replace** | Replaces the existing file. |
| **Restore as a copy (name (restored 1).ext)** | Writes the backed-up version next to the existing file as `name (restored 1).ext`, `name (restored 2).ext`, … |

Kept files appear in the run's issue list as *skipped* with the reason.

!!! warning
    **The original location** together with **Always replace** overwrites the current versions. Keepsake asks for
    confirmation before it starts.

## Dry run

Tick **Dry run – only show what would happen** to run the restore without writing anything. The result line and the
log show how many files would be restored, kept or left alone. Use it before a large restore to the original location.

## Start the restore and follow it

1. Click **Start restore**. The button is available once a backup is selected, at least one item is ticked and a
   target folder is set (or **The original location** is chosen).
2. A progress bar and a status line appear: `Restoring: <files> files, <size> written · <current path>`.
   `Waiting…` means the service has queued the restore because the maximum number of parallel jobs is reached
   (see [Settings](settings.md)).
3. When it ends, the result line shows the state and the counts, for example
   `SUCCESS: 1,204 restored, 3 already up to date, 0 kept, 0 failed`. A dry run adds `(dry run)`.
4. Click **Show log** to open the run detail window: summary, issue list, phases and the full log.
   See [History and logs](history.md).

A restore is a run of the job. You cannot start it while the job is running or waiting to run
(`'<job>' is already running or waiting to run.`). The Restore page has no cancel button; **Cancel** on the
[Backup jobs](jobs.md) page or `keepsake cancel <job>` stops the job's current run, including a restore.

## Protect a backup

Click **Protect this backup** next to **Backup from** to keep the selected backup forever: retention rules never
remove a protected backup. The label then shows `protected` and the button changes to **Remove protection**.
You cannot change protection while the job is running. See [Retention and checks](job-retention.md).

## What is restored and what is not

| Restored | Not restored |
|---|---|
| File contents | NTFS permissions (ACLs) and owner |
| Creation and last-write time of files and folders | Files that were not backed up (see the backup's issue list) |
| File attributes (read-only, hidden, system, archive …); for folders hidden and system | |

NTFS permissions are not part of the backup. Restored files and folders inherit the permissions of the target folder.

!!! warning
    Restore confidential data only into folders whose permissions are suitable. A restore of the payroll folder into
    `C:\Temp` makes it readable for everyone who can read `C:\Temp`.

The service runs as LocalSystem and writes the files with its rights. For a network target folder, the computer
account needs write access to the share.

## Restore from another computer's backups

- **On a computer without Keepsake jobs, or without the service** – use [recovery mode](recovery-mode.md). You only need
  the backup location and its password or recovery key.
- **Through the service** – a job whose destination points to the existing repository shows all its backups, including
  those of other computers. Prefer recovery mode if you do not want this computer to back up into that repository.

If the computer has no jobs yet, the Restore page says so and offers **Restore from a backup location…**, which starts
recovery mode. For the full procedure after losing a server, see [Disaster recovery](../DISASTER-RECOVERY.md).
