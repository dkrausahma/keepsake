# Keepsake Backup – Passwords and keys

Keepsake uses one application password per computer and, per backup repository, a password and a recovery key.
They protect different things. This page says which one you need when.

## The short version

To restore on **any** Windows computer you need:

1. access to the repository (folder, network share, USB disk, or for SFTP: server, user and password/private key), and
2. **either** the repository password **or** the recovery key (`KS1-…`) – one of the two is enough.

If both the repository password and the recovery key are lost, the backups cannot be decrypted by anyone. Keep
both in KeePass, and keep a copy of the KeePass database **off** the server that is backed up.

## Overview

| | Application password | Repository password | Recovery key (`KS1-…`) | Destination credentials |
|---|---|---|---|---|
| **Protects** | Access to the Keepsake service on this computer (GUI and CLI) | The backups of one repository (encryption) | The backups of one repository (encryption) | Access to the storage: share, SFTP server |
| **Set** | First start of the GUI, or `keepsake setup` | When the repository is created (job editor, or `keepsake init`) | Generated automatically when the repository is created; shown **once** | In the job editor (share credentials, SFTP login, host key) |
| **How many** | One per computer | One per repository; more can be added | One per repository; more can be added | Per job |
| **Stored on the server** | Only as a hash (`config\settings.json`) | Encrypted for the service (`config\jobs.json`), so scheduled backups run unattended | **Never** – only you have it | Encrypted for the service (`config\jobs.json`) |
| **Needed to restore on another computer** | No | Yes – this **or** the recovery key | Yes – this **or** the password | Yes |
| **If lost** | Not critical: the backups are unaffected; an administrator can reset the service configuration | Use the recovery key and add a new password | Create a new one with the password (`keepsake key add-recovery`) | Ask the storage administrator / provider |

### Application password

Only guards the Keepsake service on this computer against casual use. It cannot decrypt anything, and it is useless
on another computer. Local administrators can get around it (they can edit the configuration). Change it under
*Settings → Application password*; that does not affect any repository.

### Repository password

Encrypts the backups of a repository. Keepsake stores a copy on the server (protected with Windows DPAPI for the
service account), which is why scheduled backups and restores on the *Restore* page never ask for it. That copy only
works on this server – on another computer you type the password (or the recovery key) yourself.

### Recovery key

A second, independent way into the same repository. It is 256 bits of random data, so it cannot be guessed, and
it keeps working when passwords are added, changed or removed. Use it when the password is forgotten or was stored
wrongly. Keepsake does not keep a copy: if it was not stored when it was shown, create a new one with
`keepsake key add-recovery` (which needs the password).

**"Key file"**: there is no separate key file. *Save as file…* (GUI) or `--recovery-key-out` (CLI) writes the
same `KS1-…` text into a text file (`keepsake-recovery-key.txt`, readable only by you). *Load from file…* and
`--recovery-key-file` read it back; pasting the text works just as well. Case, spaces and dashes do not matter.

The password and the recovery key are equally powerful: whoever has either one plus access to the repository can
read all backups.

## Where to keep what (KeePass)

One entry per backup job:

- repository location (folder, share, USB disk label, or `sftp://user@host:port/path`)
- repository password
- recovery key (`KS1-…`) – as text or the saved key file as attachment
- for SFTP: user, password or private key (+ passphrase), host key fingerprint (`SHA256:…`). The account needs
  write access even for a restore: Keepsake writes a temporary lock file into the repository
- for shares: user and password, if the job uses its own share credentials

Plus, once: the application password of each server, and the portable ZIP of the installed version with its
SHA-256 from `SHA256SUMS.txt`.

## Checking that the stored keys work

Test the password and the recovery key from KeePass right after creating a job and from time to time:

- GUI: *Backup jobs → Check keys* on the job – only the repository's key files are read, nothing is restored or
  changed.
- Command line (also without the service): `keepsake key verify --repo <location> --recovery-key-file key.txt`
  or `--password-file pw.txt`; exit code 0 = valid, 4 = not valid.

Once a year, do the full recovery test from `DISASTER-RECOVERY.md` on a second computer.

## Managing keys

All on the command line, with the current password or recovery key (`keepsake help key`):

| Task | Command |
|---|---|
| List the keys of a repository | `keepsake key list --repo <location>` |
| Add a password (e.g. the old one is forgotten) | `keepsake key add-password --repo <location> --use-recovery-key` |
| Create an additional recovery key | `keepsake key add-recovery --repo <location> --recovery-key-out key.txt` |
| Remove a key | `keepsake key remove <id> --repo <location>` (the last key cannot be removed) |

After adding a new repository password, enter it in the job editor as well, so the service uses it.

**Removing a key does not revoke it** for someone who kept a copy of the repository's key files and knows that
password or key. After a leak, create a new repository and let the old one expire.
