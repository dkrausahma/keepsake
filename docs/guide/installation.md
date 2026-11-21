# Installation and upgrade

Keepsake comes as an MSI installer for the computers you back up, and as a portable ZIP for restoring on any
computer without installing anything. This page covers both, upgrades and uninstalling.

## Requirements

- Windows Server 2008 R2 SP1 or Windows 10/11, 64-bit.
- .NET Framework 4.8 (included in current Windows 10/11; on 2008 R2 install it first).
- Administrator rights: the program, the service and most commands run elevated.

Windows Server 2008 R2 needs a few more prerequisites (Service Pack 1, .NET 4.8, TLS 1.2 for e-mail):
[Windows Server 2008 R2](../WINDOWS-2008R2.md).

## Download and check

Download `Keepsake-<version>.msi` or `Keepsake-<version>-portable.zip` from the release and compare its SHA-256 with
`SHA256SUMS.txt` of the same release:

```bash
powershell -Command "Get-FileHash .\Keepsake-0.9.14.msi -Algorithm SHA256"
```

!!! note "Unsigned releases"
    As long as the releases are not code-signed, Windows SmartScreen may warn when you start the MSI. Check the
    checksum, then choose **More info → Run anyway**. Signed releases show *Kraus-Ahma IT Consulting* as publisher.

## Install with the MSI

Run the MSI as administrator. The setup lets you choose the install folder (default
`C:\Program Files\dkait\keepsake`). It:

- installs the programs: `KeepsakeGui.exe` (the **Keepsake Backup** program), `keepsake.exe` (command line) and
  `keepsake-service.exe` (the service),
- registers and starts the *Keepsake Backup Service* (LocalSystem, starts automatically, restarts after a failure),
- adds the install folder to the `PATH`, so `keepsake` works in every new command prompt,
- adds **Keepsake Backup** and **Keepsake Recovery** to the Start menu,
- creates the data folder `%ProgramData%\Keepsake` and restricts it (and the install folder) to SYSTEM and
  Administrators.

The MSI refuses to install without .NET Framework 4.8, on 32-bit Windows, or on 2008 R2 without Service Pack 1.

Then start **Keepsake Backup** and continue with [Getting started](getting-started.md).

### Check the installation

In an elevated command prompt:

```bash
keepsake doctor
```

`doctor` checks Windows version, .NET, the service, the data folder permissions, VSS, the Event Log source, TLS 1.2,
the backup privilege and free space. Everything should be **OK** or **INFO**.

## Where Keepsake keeps its data

| Location | Contents |
|---|---|
| Install folder | Programs, this help (`help\`), `Enable-Tls12.ps1`, `Keepsake Recovery.cmd` |
| `%ProgramData%\Keepsake\config\` | Jobs and settings; passwords encrypted with Windows DPAPI for the service |
| `%ProgramData%\Keepsake\runs\` | Record, issue list and log of every run |
| `%ProgramData%\Keepsake\logs\` | Service log, optional debug log |
| The repositories | Wherever the jobs write them – Keepsake never stores backups in its own folders |

## Upgrade

Run the MSI of the new version. It replaces the programs and keeps the jobs, settings, run history and the
application password. The setup stops the service and starts it again: start the upgrade when no backup is running.
A backup interrupted by the upgrade is not lost – the next run reuses what was already stored.

An older MSI cannot be installed over a newer version ("A newer version of Keepsake is already installed").

Newer versions always read repositories written by older ones.

## Uninstall

Uninstall *Keepsake Backup* under **Settings → Apps** (or **Programs and Features**). This removes the programs, the
service and the Start menu entries. It keeps:

- `%ProgramData%\Keepsake` – jobs, settings and history; a later installation picks them up again,
- all repositories – your backups are never touched.

To remove everything, delete `%ProgramData%\Keepsake` after uninstalling.

## Portable version

The portable ZIP contains the same programs without the installer. Unpack it to a local folder (not to the backup
storage) on any Windows computer with .NET Framework 4.8. Use it to:

- restore without installing anything: double-click `Keepsake Recovery.cmd` – see [Recovery mode](recovery-mode.md),
- work with a repository on the command line (`keepsake snapshots`, `keepsake restore`, …) – see
  [Command line](command-line.md).

The portable version does not install the service, so it does not run scheduled backups.

Keep the portable ZIP of the version you use, with its checksum, in the KeePass entry of your backups. Every
repository also contains a copy of `keepsake.exe` in `tools\`, but that copy is not encrypted and could have been
replaced by whoever controls the storage – see the [disaster recovery runbook](../DISASTER-RECOVERY.md) for how to
check it.
