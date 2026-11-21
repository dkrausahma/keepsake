# Troubleshooting

This page lists common problems by the symptom or message you see, with the cause and the fix. Messages are quoted as
Keepsake shows them; parts in angle brackets vary. If an entry does not solve the problem, run `keepsake doctor` and
collect the logs listed at the end of this page.

## Service and login

### "The Keepsake service is not reachable."

**Cause:** The *Keepsake Backup Service* is stopped, not installed, or still starting.
**Fix:** Open `services.msc`, start *Keepsake Backup Service* and set it to *Automatic*; then click **Try again**.
`keepsake doctor` (elevated) shows the service state. On a computer without Keepsake installed, click
**Restore without the service…** to use [recovery mode](recovery-mode.md).

### "Wrong application password." / "Too many wrong passwords. Try again in <n> seconds."

**Cause:** The application password is wrong. After 5 wrong attempts in a row, every further wrong attempt blocks logins
for an increasing time (2 s, 4 s, 8 s … up to 5 minutes). Attempts from the GUI and the command line count together.
**Fix:** Wait the time shown and enter the correct password. A correct login resets the counter. The application password
is not the repository password; see [Passwords and keys](../PASSWORDS-AND-KEYS.md).

### "The session expired. Please log in again."

**Cause:** No activity for 30 minutes.
**Fix:** Log in again.

## Passwords and recovery keys

### "The password is not valid for this repository." / "The recovery key is not valid for this repository."

**Cause:** The password or key belongs to another repository, or the password has a typo.
**Fix:** Check that the location is the right repository (each USB disk and each job has its own). Try the recovery key
instead of the password, or the other way round. **Check keys** on the [Backup jobs](jobs.md) page and
`keepsake key verify` test a password or key without restoring.

### "The recovery key contains a typo (checksum mismatch)."

**Cause:** The recovery key was mistyped. The key carries a checksum, so Keepsake detects typos before trying it.
**Fix:** Copy the key again from KeePass or the key file, or use **Load from file…**. Dashes and spaces do not matter.
`This is not a Keepsake recovery key (it must start with 'KS1-').` means the text is not a recovery key at all.

## Repository

### "No Keepsake repository found at <location>."

**Cause:** The path does not point to the repository folder, or the disk or share is not connected.
**Fix:** Enter the folder that contains `keepsake.json` – not its parent or a subfolder. For USB disks, check the drive
letter; for SFTP, check the path after the host name. For a job, the message adds `Create it in the job settings first.`

### "The repository is in use by '<purpose>' on <computer> …"

**Cause:** Another Keepsake process holds a lock on the repository – a running backup, check or cleanup, or a process that
crashed. A crashed process leaves a lock that expires after 30 minutes without refresh.
**Fix:** If the named process is still running, wait for it to finish. If not, wait 30 minutes or remove stale locks with
`keepsake unlock --repo <location>`. `--all` also removes current locks; use it only when you are sure no other Keepsake
process works on the repository. [Recovery mode](recovery-mode.md) offers to remove a blocking lock itself.

### `keepsake check` reports errors

**Cause:** Missing or damaged files in the repository (disk errors, interrupted copies, manual changes).
**Fix:** If the check reports missing or unreadable index files, run `keepsake repair --repo <location>`. It rebuilds the
index from the pack files; backups and data are not changed. Run `keepsake check --repo <location> --read-data` afterwards.
See [Command line](command-line.md).

## Backup warnings in the issue list

The run detail window lists every item that was not backed up (see [History and logs](history.md)).

| Reason | Cause | Fix |
|---|---|---|
| `Access denied.` | The service account cannot read the file, even with the backup privilege. | Check the file's permissions; `keepsake doctor` shows whether the backup privilege is available. |
| `File is locked by another program. Enable VSS for the job to back up open files.` | The file was open and VSS was off or unavailable. | Tick **Use VSS snapshots so open and locked files are backed up (recommended)** on the job's **Options** tab ([Options](job-options.md)). |
| `Path is too long.` | The path exceeds what Windows allows for this file. | Shorten the path, or exclude it. |
| `File was deleted during the backup.` | The file vanished between listing and reading. | Usually harmless (temporary files). Exclude such files to avoid the warning. |
| `File changed while it was being backed up; the stored copy may be inconsistent.` | The file was written during the backup without VSS. | Enable VSS. |
| `Source folder or file not found – it was not backed up.` | A selected source no longer exists. | Fix the job's sources ([What to back up](job-sources.md)). |
| `Junction or symbolic link – the linked folder is not followed.` | Links are never followed. | Add the link target as its own source if you need it. |

### "VSS snapshot could not be created (<reason>). Files are read directly; open or locked files may be skipped."

**Cause:** The Volume Shadow Copy service is disabled or failed, or there is not enough space for the shadow copy.
**Fix:** Set the *Volume Shadow Copy* service to *Manual* (not *Disabled*); `keepsake doctor` checks this. Check the
System and Application event logs for VSS errors and free space on the volume. `No VSS snapshot for this <type> drive`
is normal for drives that do not support shadow copies (for example network drives).

## Destinations

### "None of the USB disks of this job is connected (<disks>)."

**Cause:** No registered disk of the job is attached, or Windows has not mounted it.
**Fix:** Connect one of the listed disks and check that it appears in Explorer. Keepsake recognises disks by volume ID,
not drive letter; a new disk must be added to the job first ([Destination](job-destination.md)).

### "Cannot connect to \\server\share as '<user>': <reason>"

**Cause:** The stored share credentials are wrong or the account has no access. Without credentials, the service connects
as the computer account (LocalSystem).
**Fix:** Enter user (`DOMAIN\user`) and password for the share on the job's **Destination** tab, or for source shares under
**Network shares used as sources** → **Add share** on the **Options** tab. Give the account (or the computer account) write access to the destination share.
In recovery mode, connect the share in Explorer first.

## SFTP

### Unknown host key

**Cause:** First connection to this server; the host key is not trusted yet (`The host key of <host> is not trusted yet: SHA256:…`).
**Fix:** Click **Check destination** on the job's **Destination** tab and compare the fingerprint with the one from your
provider (for example Hetzner Robot) or `ssh-keygen -lf` on the server before you confirm. On the command line, pass
`--sftp-host-key SHA256:…`.

### "WARNING: the host key of <host> has CHANGED …"

**Cause:** The server was reinstalled, the port changed (a different SSH server answers), or someone intercepts the
connection.
**Fix:** Verify the new fingerprint with the server administrator. Only then trust the new key in the job editor
(**Check destination**).

### "SFTP login to <host> as '<user>' failed: …"

**Cause:** Wrong user, password or key, or the key is not authorised on the server.
**Fix:** Check the credentials in the job's **Destination** tab. `The SFTP private key cannot be read: … (does it need a passphrase?)`
means the key file needs its passphrase or is not a supported format (OpenSSH or PuTTY).

### "MAC error" / "MAC error with every algorithm combination the server supports"

**Cause:** The SSH algorithms do not work between this computer and the server – seen on Windows Server 2008 R2 against
servers that only offer AES-CTR.
**Fix:** Run `keepsake sftp-test --repo sftp://user@host:port/path` on the affected computer; it shows which algorithms
work and writes a detailed log. For a Hetzner Storage Box, use port 23 (OpenSSH, offers AES-GCM) instead of port 22 – the
job editor offers **Use port 23**. Port 23 has its own host key. See [Windows Server 2008 R2](../WINDOWS-2008R2.md).

### Collecting SFTP details for support

Switch on **Write a debug log (SFTP connection details for support, no passwords)** in [Settings](settings.md)
(`%ProgramData%\Keepsake\logs\debug.log`) or, in recovery mode, on the **Open a backup** screen (`%TEMP%\keepsake-debug.log`).
Reproduce the problem and send the file together with the `keepsake sftp-test` output.

## E-mail

### Test e-mail or notifications do not arrive

**Cause:** Wrong server, port or security setting; the server rejects the login or the sender; or TLS 1.2 is not
enabled (Windows Server 2008 R2).
**Fix:**

1. Click **Send test e-mail** in [Settings](settings.md) and read the message (`E-mail could not be sent via <host>:<port>: …`).
2. Match port and security: 587 with `starttls`, 465 with `ssl`.
3. On Windows Server 2008 R2, run `Enable-Tls12.ps1` from the install folder as administrator and reboot.
   `keepsake doctor` shows `TLS 1.2 (SChannel)` as OK afterwards.
4. Check the spam folder and whether the server accepts the **From** address.

Failed notifications are logged in the service log and as Event Log ID 1005 ([Monitoring](monitoring.md)).

## Check this computer: `keepsake doctor`

Run `keepsake doctor` in an elevated prompt. It checks the Windows version, .NET Framework 4.8, elevation, the Keepsake
service and its interface, TLS 1.2 and .NET strong crypto, the Volume Shadow Copy service, the Event Log source, the
permissions of the data folder, the backup privilege and free space on the system drive. Each line is `OK`, `INFO`,
`WARN` or `FAIL` with a hint. Add `--json` for machine-readable output.

## Logs for support

| What | Where |
|---|---|
| Full log of a run | **History & logs** → run → **Full log**, or `keepsake log <run-id>`; on disk `%ProgramData%\Keepsake\runs\<job ID>\<run ID>.log` |
| Issue list of a run | Run detail window → **Export all as CSV…** |
| Service log | `%ProgramData%\Keepsake\logs\service.log` (plus `.1` … `.5`) |
| SFTP debug log | `%ProgramData%\Keepsake\logs\debug.log`, or `%TEMP%\keepsake-debug.log` in recovery mode and for `sftp-test` |
| Computer check | Output of `keepsake doctor` |
| Event Log | Application log, source `Keepsake` |
