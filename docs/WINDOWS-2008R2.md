# Keepsake Backup on Windows Server 2008 R2

Keepsake targets Windows Server 2008 R2 SP1 and Windows 10/11. Everything it uses exists on
2008 R2, but a few prerequisites are not there by default. `keepsake doctor` checks all of them.

## Prerequisites

| Item | Why | How |
|---|---|---|
| Service Pack 1 (build 7601) | .NET Framework 4.8 requires it | Windows Update / KB976932 |
| .NET Framework 4.8 | runtime of all Keepsake programs | offline installer `ndp48-x86-x64-allos-enu.exe` (KB4503548) |
| 64-bit Windows | the MSI is x64 | 2008 R2 is 64-bit only |
| TLS 1.2 for clients | e-mail notifications (STARTTLS/SSL); not needed for SFTP or file shares | `Enable-Tls12.ps1` in the install folder (elevated) + reboot |
| Volume Shadow Copy service not disabled | backing up open files | `services.msc` → Volume Shadow Copy → Manual |
| Up-to-date root certificates | SMTP servers with public certificates | root certificate update / Windows Update |

Notes:

- 2008 R2 is out of support. Keep the server off the internet as far as possible and store the
  backups **off** this server (share, USB rotation, SFTP).
- The GUI needs the "Desktop Experience"-free WPF of .NET 4.8 – nothing else to install.
- Paths longer than 260 characters work (Keepsake uses `\\?\` paths internally).
- Internet Explorer is not used.
- **SFTP and AES-CTR.** On 2008 R2 with .NET Framework 4.8, SSH.NET's AES-CTR (Windows AES provider) failed with
  "MAC error" – e.g. a Hetzner Storage Box on port 22 (ProFTPD mod_sftp, AES-CTR only), which works from
  Windows 10/11. AES-GCM (no HMAC) worked. Since 0.9.8 Keepsake computes AES-CTR and all HMACs with BouncyCastle
  (`ManagedAesCtrCipher`, `ManagedHmac`), like AES-GCM and ChaCha20, and prefers AEAD ciphers anyway. `keepsake sftp-test --repo sftp://…` shows which algorithms work from
  this computer. (Hetzner Storage Box: port 23 is OpenSSH with AES-GCM, port 22 is mod_sftp; the job editor
  offers "Use port 23", which confirms port 23's own host key and checks that the repository is found there.)
  For support, switch on *Settings → Write a debug log* (`%ProgramData%\Keepsake\logs\debug.log`), or in the
  portable recovery mode the checkbox under *Open backup* (`%TEMP%\keepsake-debug.log`).

## Installation

1. Install the prerequisites, reboot.
2. Run `Keepsake-<version>.msi` (as administrator). It installs to `C:\Program Files\dkait\keepsake` (or a folder you choose in the setup),
   registers and starts the *Keepsake Backup Service* (LocalSystem, automatic, restarts on
   failure), adds `keepsake` to the PATH and creates the Start menu entry *Keepsake Backup*.
3. Open *Keepsake Backup*, set the application password, create the first job.
4. Run `keepsake doctor` in an elevated prompt – everything should be OK or INFO.

Configuration and history live in `%ProgramData%\Keepsake` and survive upgrades and uninstalls.

## Test plan (run on a 2008 R2 SP1 VM before production)

Tick each item; note deviations.

**Installation**
- [ ] MSI refuses to install without .NET 4.8 (clear message).
- [ ] MSI installs; service is running and set to automatic; `keepsake` works in a new prompt.
- [ ] `keepsake doctor` (elevated) shows no FAIL; WARN only for TLS 1.2 before running the script.
- [ ] `Enable-Tls12.ps1` (install folder) + reboot → doctor shows TLS 1.2 OK.

**GUI**
- [ ] Start menu entry starts the GUI with a UAC prompt; first-time setup and login work.
- [ ] Folder tree shows all drives; tick/untick works; long folder names display.
- [ ] Job with a folder destination: repository created, recovery key dialog requires confirmation.
- [ ] Job with a USB disk: disk listed with label; after re-plugging under another letter the backup still finds it.
- [ ] Job with a network share and its own credentials: check destination OK; backup works while the service account has no access to the share.
- [ ] Job with SFTP: host key prompt; backup works.

**Backup**
- [ ] Open Outlook PST / locked Office file is backed up (VSS); log shows no "locked" errors.
- [ ] Folder whose ACL excludes Administrators is backed up (backup privilege).
- [ ] Path longer than 260 characters is backed up and restored.
- [ ] Incremental run after no changes takes seconds for the real data set (~50,000 files).
- [ ] Initial ~200 GB backup completes; cancel in the middle → next run continues without re-uploading.
- [ ] Scheduled run at the configured time; machine off at that time → catch-up after start.

**Maintenance and alerts**
- [ ] Cleanup after a backup removes snapshots according to the rules; protected snapshot survives.
- [ ] Scheduled check runs; `--read-data` check reports 0 errors.
- [ ] Test e-mail arrives; a failed run sends a mail; "no successful backup" alert after the set hours.
- [ ] Event Log: entries 1000/1001/1002 appear under source *Keepsake*.

**Restore and disaster recovery**
- [ ] Restore single files to another folder and to the original location (each conflict policy once).
- [ ] Follow `DISASTER-RECOVERY.md` scenario 1 on a second computer using only the recovery key.
- [ ] Uninstall: service removed, `%ProgramData%\Keepsake` kept; reinstall picks the jobs up again.
