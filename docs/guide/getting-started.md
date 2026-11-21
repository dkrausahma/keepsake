# Getting started

This page takes you from installation to the first finished backup, and shows how to check that you can get your
files back. Plan about ten minutes, plus the time the first backup takes.

## Before you start

You need:

- an administrator account on the computer you back up,
- a place for the backups that is **not** the disk you back up: a second disk, a network share, a USB disk or an SFTP
  server (for example a Hetzner Storage Box),
- a password manager such as KeePass for the passwords and the recovery key.

On Windows Server 2008 R2, install the prerequisites first: [Windows Server 2008 R2](../WINDOWS-2008R2.md).

## 1. Install

Run `Keepsake-<version>.msi` as administrator and follow the setup. It installs the programs, starts the
*Keepsake Backup Service* and adds **Keepsake Backup** and **Keepsake Recovery** to the Start menu. Details:
[Installation and upgrade](installation.md).

## 2. Set the application password

Start **Keepsake Backup** from the Start menu and confirm the administrator prompt. On the first start it asks for an
**Application password** (at least 8 characters). It protects access to Keepsake on this computer – the program and
the command line. It does **not** encrypt your backups; that is the repository password of each job.

!!! note "Replacing a lost computer?"
    Then you do not need to set anything up: **Restore from a backup location…** on the same screen opens
    [recovery mode](recovery-mode.md).

## 3. Create the first job

On the **Backup jobs** page, click **Create the first job** (later: **+ New job**). The job editor opens. Enter a
**Job name** at the top, then go through the tabs:

1. **What to back up** – tick the folders to back up. Untick a subfolder inside a ticked folder to leave it out.
   See [What to back up and exclusions](job-sources.md).
2. **Exclusions** – patterns such as `*.tmp` for files you never need. **Also exclude the standard system items**
   (recycle bin, `System Volume Information`, pagefile and similar) is on by default.
3. **Destination** – choose where the backups go, enter a **Repository password** twice and click
   **Check destination**. See [Destination](job-destination.md).
4. **Schedule** – a new job runs daily at 22:00. Change the time or add entries, for example a weekly full backup.
   See [Schedule](job-schedule.md).
5. **Retention & checks** and **Options** – the defaults are a good start: see
   [Retention and checks](job-retention.md) and [Options](job-options.md).

Click **Save**. Keepsake creates the encrypted repository and shows its **recovery key** once.

## 4. Store the recovery key

The recovery key (`KS1-…`) opens your backups when the password is lost – on any computer. Click
**Copy KeePass entry** and paste it into a new KeePass entry: it contains the location, the host key, the recovery key
and how to restore. Add the repository password (and the SFTP or share password, if any) to the same entry. Then tick
**I have stored the recovery key in a safe place outside this server** and click **Done**.

Keep a copy of the KeePass database **off** this computer. Which password or key you need when:
[Passwords and keys](../PASSWORDS-AND-KEYS.md).

## 5. Run the first backup

Click **Run now** on the job. The card shows the progress; the first backup reads and stores everything, later runs
only store what changed. When it has finished, the card shows **Last backup OK** – or a warning if some files could
not be read; **History** shows which ones and why. See [History and logs](history.md).

## 6. Check that you can get your files back

A backup you have never restored from is a hope, not a backup:

1. On the job, click **Check keys** and test the password and the recovery key from KeePass.
2. On the **Restore** page, restore a folder into a new folder and open some files. See [Restoring files](restore.md).
3. Once, before you rely on Keepsake: do the recovery test at the end of the
   [disaster recovery runbook](../DISASTER-RECOVERY.md) on a second computer, using only the KeePass entry.

## Next steps

- Get an e-mail when a backup fails: enter the SMTP server under [Settings](settings.md) and switch on the
  notifications on the job's [Retention & checks](job-retention.md) tab.
- Feed the Windows Event Log into your monitoring: [Monitoring and notifications](monitoring.md).
- Back up to a second place as well (another job with another destination), for example USB disks you take
  off site.
