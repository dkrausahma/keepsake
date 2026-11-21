# Destination

The **Destination** tab decides where a job stores its backups and with which password they are encrypted. The backups go into an encrypted repository in a folder, on a network share, on one or more USB disks or on an SFTP server. Keepsake creates the repository when you save the job, or uses an existing one.

Under **Where are the backups stored?** choose **Folder or network share**, **USB disk** or **SFTP server**.

## Folder or network share

Enter the path in **Folder (local disk or \\server\share\folder)** or pick it with **Browse…**. The path must be absolute, for example `E:\Backups\Fileserver` or `\\nas\backup\fileserver`.

For a network path, Keepsake connects with the computer account (the service runs as LocalSystem) unless you enter an account of its own:

| Field | Content |
|---|---|
| **User (DOMAIN\user)** | Account for the share, `DOMAIN\user` or `server\user`. |
| **Password** | Its password. If a password is already stored, the field stays empty; leave it empty to keep the stored one. |

The credentials are used only for the connection to this share.

## USB disk

Keepsake recognises USB disks by their volume ID, not by drive letter, so the disk is found whatever letter Windows gives it.

1. Connect the disk.
2. Select it in the list and click **Add disk**. Click **Refresh** if a disk you just connected is missing.
3. Enter **Folder on the disk (without drive letter)**, for example `Keepsake\Fileserver`. The default is `Keepsake`.

**Disks of this job** lists the registered disks with label, size and **connected as E:** or **not connected**. **Remove** unregisters a disk; the data on it stays.

### Several disks (rotation)

Register several disks to rotate them, for example one on site and one off site. Each disk holds its own, independent repository with the same repository password. A backup goes to the first registered disk that is connected. If none is connected, the run fails with *None of the USB disks of this job is connected*.

When you save, Keepsake creates the repository on the connected disk. For every other disk, connect it, click **Refresh** and then **Prepare this disk** next to it. This creates the repository on that disk and shows its own recovery key. If no disk is connected when you save, the job is saved anyway; prepare each disk later.

!!! warning
    Every disk gets a different recovery key. Store each one in KeePass.

## SFTP server

| Field | Content |
|---|---|
| **Server** | Host name or IP address. |
| **Port** | Default 22. |
| **User** | SFTP user name. |
| **Password** / **Private key** | How Keepsake logs in. |
| **SFTP password** | For password login. Leave empty to keep a stored password. |
| **Load key file…** | For key login: an OpenSSH or PuTTY (`.ppk`) private key file. The key is stored on this server; load a new file to replace it. |
| **Key passphrase (if the key is protected)** | Passphrase of the private key. |
| **Remote folder** | Absolute path on the server, for example `/backups/fileserver`. |

### Host key

On the first connection, Keepsake shows the server's host key (`SHA256:...`) and asks whether to trust it. Compare it with the fingerprint on the server (`ssh-keygen -lf` on the server's key file) before you confirm. The trusted key is then shown as **Trusted host key: ...**. **Forget** removes it; the next check asks again.

If the server later presents a different key, Keepsake warns that the host key has **CHANGED** and shows the new and the trusted key. This can mean the server was reinstalled, or that the connection is intercepted. Only trust the new key if the server administrator confirms it. Scheduled runs do not connect to a server whose key changed.

### Hetzner Storage Box

For a host ending in `.your-storagebox.de`, a new job starts on port 23. Port 23 is OpenSSH and offers authenticated encryption (AES-GCM); port 22 is ProFTPD and only offers AES-CTR. If a job uses port 22, a hint with the button **Use port 23** appears. It switches the port, asks you to trust the host key of port 23 and checks the destination. If port 23 does not work, Keepsake switches back to port 22. Both ports reach the same storage, so an existing repository is found on either port.

## Repository password

| Field | When to fill it |
|---|---|
| **Repository password (encrypts the backups)** | Always for a new job. For an existing job only if you change the destination or want to enter the password again. |
| **Repeat password (only needed for a new repository)** | Only when Keepsake creates a new repository. |

- **New repository**: the password needs at least 8 characters, and both fields must match.
- **Existing repository**: enter the password that repository already has. The **Repeat password** field is not needed. If the password is wrong, the job is not saved.

The repository password is stored on this server, protected by Windows, so scheduled backups can run. Keepsake never shows stored passwords again. What the password and the recovery key are for is explained in [Passwords and keys](../PASSWORDS-AND-KEYS.md).

## Check destination

**Check destination** tests the destination with the values on the tab, without saving. The result appears in the box below the buttons:

| Line | Meaning |
|---|---|
| Location | The resolved location, for example the USB path with its current drive letter. |
| **Repository found, password correct – N snapshot(s).** | An existing repository opens with the entered or stored password. |
| **Repository found.** | A repository exists, but no password was tested. |
| **No repository yet – a new encrypted repository is created when you save.** | The location is empty. |
| **Writable, deletable · N GB free** | Keepsake can write and delete there. Free space is shown where the destination reports it. |
| **Writable, but files cannot be deleted** | Backups work, but cleanup of old backups will not. |
| **NOT writable** | Backups will fail. |
| **Encryption: ... – strong / good / weak** | SFTP only: the negotiated encryption. See below. |
| Further lines | Problems, for example a wrong password or a share that cannot be reached. |

The encryption rating describes the SSH connection, not the backups, which are always encrypted by Keepsake:

- **strong**: authenticated encryption (AES-GCM or ChaCha20-Poly1305) with a modern key exchange.
- **good**: AES-CTR with HMAC-SHA2. A note explains that the server offers no authenticated encryption.
- **weak**: CBC, 3DES or SHA-1. Ask the server administrator to enable stronger algorithms.

For USB destinations, the check uses the first connected disk.

## Copy for KeePass

**Copy for KeePass** copies a text block for the job's KeePass entry: job name, location, SFTP host key, which logins belong in the entry, the repository ID (after a check), this computer's name and how to restore. It contains no passwords and no recovery key. Paste it into KeePass and add the repository password, the recovery key and the storage login there.

## Create the repository and store the recovery key

When you save a job whose destination holds no repository, Keepsake asks *Create a new encrypted repository on ... ?*. After you confirm, the window **Store your recovery key** shows the recovery key once.

| Button | What it does |
|---|---|
| **Copy KeePass entry** | Copies the same text block as **Copy for KeePass**, plus the recovery key on a line of its own. |
| **Copy key only** | Copies only the key. |
| **Save as file…** | Saves the KeePass text block, including the key, as a text file readable only by you. Move it off this server, for example as an attachment of the KeePass entry. |

Keepsake clears the clipboard two minutes after copying. **Done** becomes available once you tick **I have stored the recovery key in a safe place outside this server.** If you close the window without ticking it, Keepsake asks for confirmation.

!!! warning
    The repository password and the recovery key are the only ways to open the backups. Without them, the backups cannot be restored. Test them later with **Check keys** on the job card.

If you cancel the confirmation, the job is not saved.
