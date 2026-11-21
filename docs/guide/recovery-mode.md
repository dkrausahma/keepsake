# Recovery mode

Recovery mode restores straight from a backup location. It does not need the Keepsake service, an application
password or a backup job – only the location of the repository and its repository password or recovery key. Use it on
a replacement computer after losing a server, on a computer where Keepsake is not installed (portable ZIP), or when
the service is not reachable. Once a backup is open, you work on the same restore page as with the service.

## Start recovery mode

| From | How |
|---|---|
| Start menu | **Keepsake Recovery** (installed with the MSI) |
| Keepsake GUI | **Restore from a backup location…** on the first-start page, the login page and the Restore page (when there are no jobs) |
| Service not reachable | **Restore without the service…** on the error screen |
| Command line | `KeepsakeGui.exe --recovery` |
| Portable ZIP | `Keepsake Recovery.cmd` |

Recovery mode runs with the rights of the logged-on administrator. The header always shows **Keepsake Backup ·
Recovery** so you can tell it apart from normal operation.

## Open a backup

The **Open a backup** screen asks for:

| Field | What to enter |
|---|---|
| **Where is the backup?** | The folder that contains `keepsake.json`. Use **Browse…** for a local folder. |
| **SFTP password** | Only for SFTP locations: the password of the storage account. |
| **or private key file** | Only for SFTP: an OpenSSH or PuTTY key file (`.ppk`, `.pem`, `.key`, `id_*`). If set, the password field is ignored. |
| **Key passphrase (if the key has one)** | Only for SFTP with an encrypted key file. |
| **Unlock with** | **Repository password** or **Recovery key (KS1-…)**. |
| Password / key box | The repository password, or the recovery key text. **Load from file…** reads the key from a text file. |
| **Write a debug log (SFTP connection details for support, no passwords)** | Writes `%TEMP%\keepsake-debug.log`; the path is shown below the checkbox. |

Location formats:

| Storage | Example |
|---|---|
| USB disk or local folder | `E:\Keepsake\Fileserver` |
| Network share | `\\server\backup\fileserver` |
| SFTP | `sftp://user@host/path` or `sftp://user@host:port/path` (for example `sftp://u123456@u123456.your-storagebox.de:23/backups/fileserver`) |

!!! note
    A network share that needs a login is not connected by recovery mode. Connect it in Explorer first (or with
    `net use`), then enter the UNC path.

Click **Open backup**. On success, the restore page opens with the backups found in the repository. Errors appear in red
above the button, for example `The password is not valid for this repository.` or
`No Keepsake repository found at <location>.` See [Troubleshooting](troubleshooting.md).

Where to find the location, passwords and the recovery key: [Passwords and keys](../PASSWORDS-AND-KEYS.md).

## Confirm the SFTP host key

On the first connection to an SFTP server, Keepsake shows the dialog **Unknown host key** with the server's fingerprint
(`SHA256:…`). Compare it with the fingerprint you recorded, for example in the job's KeePass entry. Click **Yes** to
trust it for this session; **No** stops with `Host key not trusted – not connected.`

If the server presents a different key than expected, the connection is refused with a message that the host key has
**CHANGED**. Verify with the server administrator before you continue.

## Restore

After opening, the page works like [Restoring files](restore.md), with these differences:

- **Job** lists the job names found in the backups. Backups without a job name appear as `(backups without job name)`.
- **Protect this backup** is not available; protection needs the service.
- The restore runs in the GUI process, not in the service. It does not appear in [History and logs](history.md);
  use **Show log** to see the result and the full log before you close the backup.

## The lock dialog

Before a restore, Keepsake checks the repository for current exclusive locks. A computer that crashed during a cleanup leaves an
exclusive lock that blocks restores. In that case the dialog **Backup locked** shows whose lock it is (purpose,
computer, process, user, since when). The lock expires by itself 30 minutes after that computer stopped working on the
backup.

- Click **Yes** to remove only the locks that block the restore and start it. Do this when that computer is lost or no
  longer runs Keepsake.
- Click **No** to cancel: `Restore not started: the backup is locked. Try again in 30 minutes, or remove the lock.`

## What recovery mode changes in the repository

Opening and browsing only read the repository. A restore writes a temporary lock file into the repository's `locks`
folder and removes it at the end. Removing another computer's lock (see above) also deletes that lock file. Backup
data, snapshots and keys are never changed.

!!! warning
    The storage account needs **write** access, even for a restore. A read-only share or SFTP account cannot be used
    for restores.

## Leave recovery mode

- **Open another backup** closes the current backup and returns to **Open a backup**. It is disabled while a restore runs.
- **Back to the service** closes the backup and connects to the Keepsake service on this computer (login page).

## Related

- [Disaster recovery](../DISASTER-RECOVERY.md) – the complete procedure after losing a server, including the command line.
- [Passwords and keys](../PASSWORDS-AND-KEYS.md) – what you need to keep, and where.
