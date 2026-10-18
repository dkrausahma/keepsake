# Keepsake Backup – Disaster Recovery Runbook

This runbook is for the day something is gone. Print it and keep it with the KeePass export of
the recovery keys. Every scenario works on **any** Windows computer with the .NET Framework 4.8 –
the original server, its configuration and its Windows domain are not needed.

## What you need

| Item | Where it is |
|---|---|
| The backup repository | the folder / network share / USB disk / SFTP server of the job |
| The repository password **or** the recovery key (`KS1-…`) | KeePass |
| For SFTP: user, password or private key, host key fingerprint | KeePass |
| `keepsake.exe` | **preferred:** the portable ZIP or the MSI from the official release (keep a copy and its SHA-256 from `SHA256SUMS.txt` with the KeePass entry). Fallback: `tools\keepsake-<version>\` inside every repository – see the warning below |

> Test this runbook once on a second computer before you rely on Keepsake (see the end of this document).

---

## Scenario 1 – The server is gone, the backups are on a share, USB disk or folder

1. On any Windows computer, make the repository reachable (plug in the USB disk, open the share).
2. Unpack the portable ZIP (or install the MSI) on the local disk, e.g. `C:\Keepsake`, and open a command
   prompt there.

   > **Copy in the repository:** `tools\keepsake-<version>` works too, but it is **not encrypted** – whoever
   > controls the backup storage could have replaced it. Before you type a password into it, check it:
   > `Get-AuthenticodeSignature .\keepsake.exe` must show a valid signature of Kraus-Ahma IT Consulting
   > (signed releases), or `Get-FileHash` of the files must match the release you recorded. The service
   > re-checks this copy after every backup and repairs it, but on disaster day that service is gone.
3. **With the GUI** (easiest): double-click `Keepsake Recovery.cmd` (or run `KeepsakeGui.exe --recovery`,
   confirm the administrator prompt). Enter the repository folder (e.g. `E:\Keepsake\Fileserver`) and the
   password or the recovery key, click *Open backup*, pick the point in time, tick what you need, choose a target
   folder and *Start restore*. *Show log* lists anything that could not be restored. No service, no configuration
   and no installation are needed. On a computer with Keepsake installed whose service is not running, the GUI
   offers the same as *Restore without the service…*.

   **With the command line** – list the backups:

   ```bash
   keepsake snapshots --repo E:\Keepsake\Fileserver
   ```

   Enter the repository password when asked. With the recovery key instead:

   ```bash
   keepsake snapshots --repo E:\Keepsake\Fileserver --use-recovery-key
   ```

4. Restore everything from the newest backup into a new folder:

   ```bash
   keepsake restore latest --repo E:\Keepsake\Fileserver --target D:\Restore --log D:\Restore\restore.log
   ```

   The files end up under `D:\Restore\<drive>\<original path>`. They get the permissions of
   `D:\Restore` – NTFS permissions are not part of the backup, so the files are always readable.

5. Check `restore.log` for files that could not be restored.

## Scenario 2 – The backups are on an SFTP server

Same as scenario 1, with the SFTP location (the `tools` folder can be downloaded with any SFTP client,
e.g. WinSCP):

```bash
keepsake snapshots --repo sftp://backup@nas.example.org/backups/fileserver
```

Keepsake shows the server's host key fingerprint and asks whether to trust it. Compare it with the
fingerprint stored in KeePass. For scripts, pass it directly and use files for the secrets:

```bash
keepsake restore latest --repo sftp://backup@nas.example.org/backups/fileserver --sftp-host-key SHA256:... --sftp-password-file sftp.txt --recovery-key-file key.txt --target D:\Restore
```

With a private key: `--sftp-key-file id_ed25519` (and `--sftp-key-passphrase-file`).

## Scenario 3 – Only some files or folders are needed

Browse the snapshot, then restore just that part:

```bash
keepsake ls latest --repo E:\Keepsake\Fileserver D:\Data\Finance
```

```bash
keepsake restore latest --repo E:\Keepsake\Fileserver --include D:\Data\Finance\2026 --target D:\Restore --flat
```

`--flat` puts `2026` directly into `D:\Restore` instead of recreating the full path.
To restore an older state, use a snapshot ID from `keepsake snapshots` instead of `latest`.

If the Keepsake service still runs on the server, the GUI (Restore page) does the same with a
folder tree.

## Scenario 4 – Restore into the original location on a rebuilt server

```bash
keepsake restore latest --repo E:\Keepsake\Fileserver --original --conflict newer
```

`--conflict` decides what happens with existing files: `skip` (default), `newer`, `overwrite`,
`rename`. Run with `--dry-run` first to see what would happen.

## Scenario 5 – The password is lost

Use the recovery key from KeePass (`--use-recovery-key` or `--recovery-key-file`). Then set a new
password so the service can continue:

```bash
keepsake key add-password --repo E:\Keepsake\Fileserver --use-recovery-key
```

Update the job in the GUI with the new password. Remove the old password key with
`keepsake key list` / `keepsake key remove <id>` if it may have leaked.

If both the password and the recovery key are lost, **the backups cannot be decrypted** – by design.

## Scenario 6 – The repository reports errors

1. Find out what is wrong:

   ```bash
   keepsake check --repo E:\Keepsake\Fileserver --read-data --log check.log
   ```

2. If index files are missing or damaged ("index file cannot be read", "pack is not referenced"):

   ```bash
   keepsake repair --repo E:\Keepsake\Fileserver
   ```

3. Check again. Remaining errors name the affected files ("data block … is missing – the file cannot
   be restored"). Everything else is still restorable; restore it now, then investigate the
   storage (disk errors, interrupted copies).

4. If the repository is locked by a crashed process ("in use by … since …"):

   ```bash
   keepsake unlock --repo E:\Keepsake\Fileserver
   ```

## Scenario 7 – Set up Keepsake again on a new server

1. Install Keepsake (MSI), open the GUI, set the application password.
2. Create a job with the same destination and the repository password. Keepsake recognises the
   existing repository ("Repository found, password correct – N snapshots") and continues it;
   the first backup reads all files again but only stores what changed.

---

## Recovery test (do this before going live, then once a year)

- [ ] On a computer that is **not** the backup server, with only the KeePass entry at hand:
- [ ] Unpack the portable ZIP from the KeePass attachment / release (not the copy in the repository), check its SHA-256.
- [ ] `Keepsake Recovery.cmd` → open the backup with the **recovery key** (not the password) – the backups are listed.
- [ ] `keepsake snapshots` with the recovery key – same list on the command line.
- [ ] Restore one folder with `--include … --flat` into a temp folder; open some files.
- [ ] `keepsake check --read-data-percent 10` – no errors.
- [ ] Note the date and result in the IT documentation.
