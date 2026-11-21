# Keepsake Backup

Encrypted, deduplicated file backup for **Windows Server 2008 R2 SP1 and Windows 10/11** – Windows only, no
Linux target. Overview, layout, CLI and service usage: `README.md`. Requirements with IDs (JOB-01, RST-07, …):
`docs/REQUIREMENTS.md`. Repository format: `docs/FORMAT.md`. Passwords and keys: `docs/PASSWORDS-AND-KEYS.md`
(keep it, the README table, `keepsake help` and the GUI texts consistent).

## Environment

- Develop natively on Windows (Developer PowerShell / VS terminal), not WSL: WPF, the service and the WiX MSI
  need the Windows toolchain.
- Everything ships on **.NET Framework 4.8** (Core and Protocol are .NET Standard 2.0) because .NET 8+ does not
  run on 2008 R2. Do not use APIs or packages that need a newer runtime.
- C# 12, `Nullable` and `ImplicitUsings` off (`Directory.Build.props`).

## Build and test

```bash
dotnet build Keepsake.slnx -c Release
dotnet test Keepsake.slnx -c Release --no-build
```

- SFTP tests need a server on `localhost:2222`, the mail test Mailpit on `localhost:1025` (Docker commands in
  `README.md`); without them those tests pass without doing anything.
- Some tests (data-folder ownership) only really run elevated.
- Release: `powershell -ExecutionPolicy Bypass -File tools\build-release.ps1` → MSI, portable ZIP and
  `SHA256SUMS.txt` in `artifacts\`.

## Conventions

- **Version bump before every release build:** `<Version>` in `Directory.Build.props` (at least the third
  field – a same-version MSI does not upgrade) and the fallback `ProductVersion` in
  `installer/Keepsake.Installer.wixproj`. Add an entry to `CHANGELOG.md` in the same change.
- **Format compatibility (REP-07):** newer versions must read every older repository. Any format change
  updates `docs/FORMAT.md`.
- **Dependencies:** `packages.lock.json` is committed for every project (restore in locked mode). After a
  package change run `tools\check-dll-versions.ps1`; `build-release.ps1` checks the binding redirects of the
  staged files (`tools\check-binding-redirects.ps1`).
- **Security:** the service runs as LocalSystem and the backup destination is untrusted – never follow
  links, validate everything read from a repository. Fixes reference audit IDs (SEC-0xx) from
  `audits/SECURITY_AUDIT_REPORT.md`; update its remediation table when one changes.
- English only in GUI, CLI, logs and docs (NFR-10).
- Behaviour or command changes also update `README.md`, and where relevant `docs/DISASTER-RECOVERY.md`
  and `docs/WINDOWS-2008R2.md`.
