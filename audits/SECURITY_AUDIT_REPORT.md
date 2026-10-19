# Security Audit Report – Keepsake Backup

| | |
|---|---|
| **Scope** | Repository `P:\dk@\work\clas\keepsake`, branch `master`, commit `7fc7d50` plus uncommitted working-tree changes (product rename "Keepsake Backup" in GUI texts and docs – no security relevance). All source (`src/`), tests, installer (`installer/`), build scripts (`tools/`), docs, full git history (9 commits). |
| **Date** | 2026-10-18 |
| **Method** | Read-only review in five parallel areas (authentication and pipe API; local privilege boundaries; injection/parsing/untrusted repository; configuration, secrets, installer, supply chain; cryptography and data protection), followed by a manual re-check of every High finding against the code. NuGet advisory check (`dotnet list package --vulnerable / --deprecated --include-transitive`) – the only external request. Nothing was built, started or changed except this report. |
| **Assumed deployment** | On-premises Windows Server 2008 R2 SP1 … 2022 and Windows 10/11. The service runs as **LocalSystem** with SeBackupPrivilege on file servers that many domain users may log on to (RDP/terminal servers, other service accounts). GUI (elevated) and CLI talk to the service over a local named pipe. Backups go to SFTP servers, SMB shares, USB disks or local volumes. **The backup destination is treated as untrusted** (it may read, delete, replay or substitute files). There is no internet-facing listener; "internet-facing" in the audit template is mapped to: remote attackers controlling a destination, and unprivileged local users. |
| **Not covered** | Runtime behaviour on real servers (no test VM), the product website (`kraus-ahma.com`, separate folder), WiX 5.0.2 advisories (not covered by the NuGet check), third-party library internals (BouncyCastle, SSH.NET, MailKit) beyond how they are called. |

---

## Remediation status (2026-10-18, version 0.9.2, not committed)

Fixed in the working tree unless noted. 101 tests pass (Debug and Release); new regression tests are in
`tests/Keepsake.Core.Tests/LinkSafetyTests.cs` and `tests/Keepsake.Service.Tests/SecurityTests.cs`.

| ID | Status | What changed |
|---|---|---|
| SEC-001 | Fixed | Service refuses links in the data folder, takes ownership (Administrators) and resets ACLs of all children at every start; console mode with the default folder is restricted too; DPAPI now uses the service account's scope (`dpapi-u:`), old values are converted on load; MSI creates and locks `%ProgramData%\Keepsake` (permanent); `doctor` checks owner and extra ACEs. The ownership test runs only elevated – verify once with an elevated test run or on a VM. |
| SEC-002 | Fixed | MSI locks `INSTALLFOLDER` (SYSTEM/Admins full, Users read & execute); `cmd.exe`, `taskkill.exe`, `sc.exe` started from System32 by full path. PATH entry kept (needed for the `keepsake` command). |
| SEC-003 | Fixed | Restore refuses junctions/links below the anchor (target folder, or backed-up source folder for original location) and verifies the final path of every new file. Residual: a race between that check and the final rename remains theoretically possible. |
| SEC-004 | Fixed (partly) | Folder storage never lists, writes or deletes through links; listing depth limited; repository-relative paths validated. Not done: restrictive ACL on new repository folders (could lock out access from other computers / moved USB disks). |
| SEC-005 | Fixed | Clients connect with Identification level and verify the pipe server runs as LocalSystem or as the client's own account; the server claims the name with `FILE_FLAG_FIRST_PIPE_INSTANCE` when no instance of its own exists and rejects remote clients. Not done: separate console-mode pipe name (would change CLI usage; the client check covers it). |
| SEC-006 | Mitigated | The tool copy is compared with the installed files after every backup and repaired; README in new repositories and the DR runbook point to the signed release first and explain how to check the copy. Full fix needs signed releases (SEC-008). |
| SEC-007 | Fixed | Argon2 memory ≤ 1 GiB, iterations ≤ 20, parallelism ≤ 16, salt validated; key files > 64 KiB, with invalid names or beyond 64 slots are skipped; malformed slots no longer abort the open. |
| SEC-008 | Ready, needs a certificate | `build-release.ps1 -CertificateThumbprint` signs exes, DLLs, script and MSI (timestamped); `SHA256SUMS.txt` is written. A code-signing certificate must be obtained by the owner. |
| SEC-009 | Fixed | Repository listings keep only valid object names in the right place and of plausible size; pack trailer length bounded; short IDs rejected; SFTP listing depth and entry count limited. |
| SEC-010 | Fixed | Stored SFTP secrets are only used for the same host, port and user. |
| SEC-011 | Fixed | New job IDs must be 32 lowercase hex characters, otherwise the service generates one. |
| SEC-012 | Fixed | Shadow-copy IDs must parse as GUID before reaching the WQL query. |
| SEC-013 | Fixed | One password check at a time; every attempt (login and password change) counts toward the lockout. |
| SEC-014 | Fixed (partly) | Password change ends all other sessions; CLI logs out; session count capped. Not done: absolute session lifetime and binding to the connection (would log GUI users out during long sessions). |
| SEC-015 | Fixed (partly) | Requests capped at 8 MiB, at most 16 connections, Argon2 serialised, `runs.list` capped at 10,000. Not done: read timeout (the GUI keeps idle connections). |
| SEC-016 | Fixed | GUI `--pipe` is Debug-only. |
| SEC-017 | Fixed | Restore rejects invalid names (`.`, `..`, separators, `:`, wildcards, control characters) and verifies containment in the target folder; invalid timestamps no longer abort a restore. |
| SEC-018 | Open | Needs a pack listing at every backup or per-job state – performance and behaviour change; left for a separate change. |
| SEC-019 | Documented | `keepsake help key` and README explain that removing a key does not revoke it. |
| SEC-020 | Mitigated | Logging in to SMTP without TLS is still possible (internal relays) but logs a warning every time; STARTTLS errors are reported cleanly. |
| SEC-021 | Fixed | Recovery-key files (CLI and GUI) are written owner-only; the GUI clipboard copy is excluded from clipboard history/cloud and cleared after two minutes. |
| SEC-022 | Fixed | Minimum repository-password length (8) enforced in Core for `init` and `key add-password`. |
| SEC-023 | Fixed | Exclusion regexes have a 1-second timeout; on timeout the file is backed up, not skipped. |
| SEC-024 | Open | Service SID / required-privileges list could break VSS; needs a test on 2008 R2 first. |
| SEC-025 | Fixed | Release builds use `ContinuousIntegrationBuild`; no local paths in the binaries (checked). |
| SEC-026, SEC-027 | Owner decision | Public contact data, commit identity, licence and third-party notices. |
| SEC-028 | Fixed | `nuget.config` (nuget.org only, source mapping) and `packages.lock.json` for every project. |
| SEC-029 | Partly | CSV formula cells neutralised; `.gitignore` covers key/certificate/password files; README security notes; FORMAT.md ID note; README command typo fixed. Open: System.Text.Json upgrade before Nov 2026, CI, `SECURITY.md`. |

Also found while fixing (not a security issue, present in all versions): a restore into a target folder that is
itself a junction fails ("part of the path not found") with the long-path settings of the programs.

---

## 1. Executive summary

**Overall risk rating: High.**

No Critical finding: there is no remote, unauthenticated path into the service, and the cryptography is sound. But there are **several independent ways for an unprivileged local user – or whoever controls a backup destination – to reach SYSTEM, delete files as SYSTEM, or obtain the keys to all backups**. On a file server that is exactly the account that matters.

**Top 5 risks**

1. A non-admin can pre-create or own `%ProgramData%\Keepsake`; with machine-scope DPAPI this exposes every stored password and lets pre/post commands run as SYSTEM (**SEC-001**).
2. A restore into a folder the requesting user controls follows junctions, so SYSTEM writes or deletes files wherever the user points it (**SEC-003**).
3. The folder destination follows junctions when listing, and automatic prune, `repair` and `unlock` then delete files outside the repository as SYSTEM (**SEC-004**).
4. GUI and CLI never check that the pipe belongs to the real service; a squatter during any service downtime receives the application password and repository/SFTP/share secrets (**SEC-005**).
5. The disaster-recovery runbook tells admins to run `keepsake.exe` copied from the untrusted destination, which nothing authenticates (**SEC-006**); release binaries are unsigned (**SEC-008**).

**Posture.** The cryptographic core is well done: AES-256-GCM with random nonces from a CSPRNG, keyed HMAC blob IDs verified after every decryption, content-addressed objects checked against their hash, key slots bound to the repository ID, Argon2id above OWASP minimums, decompression only after authentication, strict SFTP host-key pinning, a pipe DACL that denies network logons, no secrets returned by the API, no unsafe deserializers and no secrets anywhere in git history. The danger concentrates at the **boundary between the LocalSystem service and the Windows file system**: folders and paths it trusts without checking ownership or reparse points (data folder, install folder, restore targets, folder destinations), the pipe client trusting whoever owns the pipe name, and executables or parameters taken from the untrusted destination. The custom install-folder option added in this session (commit `c00c946`) introduced one of these issues (SEC-002).

---

## 2. Findings

| ID | Severity | Title | Effort | Confidence |
|---|---|---|---|---|
| SEC-001 | High | Data folder can be pre-created/owned by a non-admin; machine-scope DPAPI then exposes all secrets and allows SYSTEM command execution | Medium | Likely |
| SEC-002 | High | Custom install folder gets no ACL, holds the LocalSystem service and is added to the system PATH; child processes started by bare name | Small–Medium | Confirmed (conditional) |
| SEC-003 | High | Restore follows junctions/reparse points in the target tree (writes and deletes as SYSTEM) | Medium | Likely |
| SEC-004 | High | Folder destination listing follows junctions; prune, repair and unlock delete (and backups write) outside the repository as SYSTEM | Medium | Likely |
| SEC-005 | High | Pipe-name squatting: clients do not verify the server, no first-instance protection | Medium | Confirmed / Likely |
| SEC-006 | High | Disaster recovery runs an unauthenticated `keepsake.exe` copied from the untrusted destination | Medium | Confirmed |
| SEC-007 | Medium | Argon2id parameters and key-slot count from the untrusted repository are unbounded (DoS as SYSTEM) | Small | Confirmed |
| SEC-008 | Medium | Release binaries and MSI are not Authenticode-signed; no published checksum file | Medium | Confirmed |
| SEC-009 | Low | Unbounded reads and unvalidated names from the destination (pre-auth allocations, crashes, SFTP listing loops) | Small | Confirmed |
| SEC-010 | Low | `repo.test` / `repo.init` reuse a job's stored secrets against a caller-chosen destination | Small | Confirmed |
| SEC-011 | Low | Client-supplied job ID used unvalidated in file paths, including a recursive delete | Small | Confirmed |
| SEC-012 | Low | WQL query built by concatenation from `state\vss.json` | Small | Confirmed |
| SEC-013 | Low | Login brute-force limiter bypassable by parallel requests; `changePassword` not counted; in-memory only | Small | Confirmed |
| SEC-014 | Low | Sessions survive a password change, no absolute lifetime, not bound to the connection; CLI never logs out | Small | Confirmed |
| SEC-015 | Low | Pre-authentication DoS on the pipe (64 MiB frames, unlimited threads, no read timeout, parallel Argon2) | Small | Confirmed |
| SEC-016 | Low | GUI accepts `--pipe` in Release builds | Small | Confirmed |
| SEC-017 | Low | Restore does not validate tree node names (needs the repository key) | Small | Confirmed |
| SEC-018 | Low | No rollback/freshness detection; stale index entries win and can make new backups silently unrestorable | Small–Medium | Likely |
| SEC-019 | Low | Removing a password or recovery key does not revoke it against old slot copies | Small (docs) / Large (rekey) | Confirmed |
| SEC-020 | Low | SMTP mode "none" sends credentials and report contents in clear | Small | Confirmed |
| SEC-021 | Low | Recovery-key files written with inherited ACLs; clipboard not cleared | Small | Confirmed |
| SEC-022 | Low | No minimum repository-password length on non-interactive CLI paths | Small | Confirmed |
| SEC-023 | Low | ReDoS in the exclusion-pattern matcher (no regex timeout) | Small | Likely |
| SEC-024 | Low | Service runs with the full LocalSystem token (no service SID, no required-privileges list) | Small | Confirmed |
| SEC-025 | Low | Internal build path (`P:\dk@\work\clas\…`) embedded in every shipped binary | Small | Confirmed |
| SEC-026 | Low | Personal contact data and an employer e-mail address in code and commit metadata become public on publication | Small | Confirmed |
| SEC-027 | Low | Licence text says "internal only"; third-party notices incomplete | Small | Confirmed |
| SEC-028 | Low | No NuGet lock files or source mapping | Small | Confirmed |
| SEC-029 | Informational | Collected informational items (see section) | – | – |

> **Re-checked by hand:** SEC-001, SEC-002, SEC-003, SEC-004, SEC-005, SEC-006, SEC-007, SEC-011, SEC-012 – cited lines read and data flow confirmed. Line numbers of the remaining Low/Informational items come from the area reviews.

---

### SEC-001 – High – Data folder can be pre-created/owned by a non-admin; machine-scope DPAPI then exposes all secrets and allows SYSTEM command execution

**Affected:** `src/Keepsake.Service/DataPaths.cs:37-55`; `src/Keepsake.Service/Secrets.cs:14,20,32`; `src/Keepsake.Service/Stores.cs` (loads `config\jobs.json`, `config\settings.json`); `src/Keepsake.Service/CommandRunner.cs:14`; `src/Keepsake.Service/Vss.cs` (`state\vss.json`); `installer/Package.wxs` (does not create or secure the data folder); `src/Keepsake.Cli/Doctor.cs` (checks only root allow-ACEs).

```csharp
foreach (string dir in new[] { Root, Config, State, Runs, Logs })
    Directory.CreateDirectory(dir);
...
security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
... // SYSTEM + Administrators FullControl
Directory.SetAccessControl(Root, security);          // owner never set, children untouched

ProtectedData.Protect(..., Entropy /* constant "Keep…" */, DataProtectionScope.LocalMachine);
```

**Attack scenario.** By default, standard users may create folders under `C:\ProgramData`. A local user (terminal-server user, compromised service account) creates `C:\ProgramData\Keepsake` and/or `config\` before Keepsake is installed or first started. The service later replaces only the root DACL. The user stays owner – owners keep the right to change the DACL – and explicit or protected ACEs on child folders survive. The user can then read `jobs.json`/`settings.json`. Because DPAPI uses **machine scope** with a constant, public entropy, any local process can decrypt the stored repository passwords and SFTP, share and SMTP credentials. The user can also write a job definition whose pre/post command the service runs through `cmd.exe` as SYSTEM, or edit `state\vss.json` (see SEC-012). Running `keepsake-service console` once with the default data folder (no restriction in console mode, `ServiceHost.cs:44`) has a similar effect through inherited ACLs.

**Business impact.** Local privilege escalation to SYSTEM on the file server. Disclosure of the keys to every backup and of all destination credentials. Ability to tamper with or destroy backups.

**Recommended fix.**
- Create the folder in the MSI with an explicit owner (Administrators) and protected DACL (`CreateFolder` + `util:PermissionEx`).
- At every service start, verify that the owner of Root and all children is SYSTEM or Administrators and that no reparse points exist. Otherwise take ownership and reset ACLs recursively, or refuse to start.
- Create directories with the security descriptor atomically (`Directory.CreateDirectory(path, DirectorySecurity)`).
- Consider DPAPI CurrentUser scope under LocalSystem, or per-install random entropy kept in an admin-only location.
- Never default console mode to `%ProgramData%`.
- Extend `keepsake doctor` to check owner and children.

**Effort:** Medium. **Confidence:** Likely. Verify on a 2008 R2 and a 2019/Win11 VM that a standard user can create `ProgramData\Keepsake\config`, and that owner and child ACEs survive the service's first start.

---

### SEC-002 – High (only when an admin chooses a folder outside Program Files) – Custom install folder gets no ACL, holds the LocalSystem service and is added to the system PATH

**Affected:** `installer/Package.wxs:33-43` (folder choice, `RegistrySearch`), `:60-79` (`ServiceInstall` as LocalSystem from `INSTALLFOLDER`), `:82-84` (`<Environment Name="PATH" Value="[INSTALLFOLDER]" … System="yes">`), `:95` (`WixUI_InstallDir`) – introduced in commit `c00c946` (this session). `src/Keepsake.Service/CommandRunner.cs:14` (`"cmd.exe"`) and `:68` (`"taskkill.exe"`), `src/Keepsake.Service/Program.cs` (`"sc.exe"`): bare executable names with `UseShellExecute = false`.

```xml
<ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />
<Environment Id="KeepsakePath" Name="PATH" Value="[INSTALLFOLDER]" Part="last" Action="set" System="yes" ... />
<!-- no PermissionEx / LockPermissions anywhere -->
```

**Attack scenario.** If the admin installs to, for example, `C:\Keepsake` or `D:\Apps\Keepsake`, the folder inherits the parent's ACL. Folders under a drive root commonly let Users create files, and on newer Windows Authenticated Users get Modify. A local user can then:
- replace or plant service binaries or DLLs that load into the SYSTEM service;
- place an executable named like `cmd.exe` in the application folder, which `CreateProcess` searches before System32 when a job runs a pre/post command;
- plant DLLs that other SYSTEM services look for along the machine-wide PATH;
- alter `Enable-Tls12.ps1` or the GUI, which admins run elevated.

**Business impact.** Local privilege escalation to SYSTEM on the file server.

**Recommended fix.**
- Lock `INSTALLFOLDER` in the MSI (`util:PermissionEx`: SYSTEM and Administrators Full, Users Read & Execute, inheritance protected), or reject folders outside `ProgramFiles64Folder`.
- Start child processes by full path (`Path.Combine(Environment.SystemDirectory, "cmd.exe")`, likewise `taskkill.exe`, `sc.exe`).
- Reconsider the system-PATH entry.
- Have `keepsake doctor` check the install folder's ACL.

**Effort:** Small–Medium. **Confidence:** Confirmed that no ACL is set and PATH is appended; exploitability depends on the chosen folder. Check with `icacls` on a custom folder on each target OS.

---

### SEC-003 – High – Restore follows junctions/reparse points in the target tree

**Affected:** `src/Keepsake.Core/Restore/RestoreEngine.cs:376` (`Directory.CreateDirectory(Path.GetDirectoryName(extended))`), `:379` (temp file), `:394-399` (delete existing file, then move), `:404` and `:438-446` (attributes). There is no reparse-point or final-path check anywhere in the restore path.

```csharp
Directory.CreateDirectory(Path.GetDirectoryName(extended));
temp = extended + TempSuffix;
using (var output = new FileStream(temp, FileMode.Create, ...)) { ... }
if (File.Exists(extended)) { File.SetAttributes(extended, FileAttributes.Normal); File.Delete(extended); }
File.Move(temp, extended);
```

**Attack scenario.** The typical restore is the helpdesk putting a user's files back into the user's own share folder, either at the original location or into a folder there. That user has Modify rights on the folder, knows when and where the restore will happen, and earlier backed up files whose content they chose. Before or during the restore, the user replaces a folder along the restore path with a junction to a protected location. The service, running as SYSTEM, then creates and overwrites files there. With the overwrite policy it also deletes the existing file first.

**Business impact.** Code execution as SYSTEM (by writing into system or program folders) or deletion of protected files, triggered through a routine helpdesk restore.

**Recommended fix.**
- Refuse reparse points under the restore root: open directories with `FILE_FLAG_OPEN_REPARSE_POINT` and check attributes along the path.
- After creating each directory, verify that the final path (`GetFinalPathNameByHandle`) is still under the intended root.
- Create files relative to a verified directory handle.
- Optionally perform restores with the requesting admin's token, or warn when the target is writable by non-admins.

**Effort:** Medium. **Confidence:** Likely. The code has no checks (confirmed). Verify on a VM that a share user can place a mount-point junction in their folder and that a SYSTEM restore writes through it.

---

### SEC-004 – High – Folder destination listing follows junctions; prune, repair and unlock delete outside the repository as SYSTEM

**Affected:** `src/Keepsake.Core/Storage/FileSystemStorage.cs:77-97` (`EnumerateFiles("*", SearchOption.AllDirectories)`, no reparse check, relative path taken from the enumerated full path), `:69` (Delete); `src/Keepsake.Core/Maintenance/Pruner.cs:104` (list of `data/`) and `:208` (`Storage.Delete(Layout.PackPath(packId))` for packs not in any index); `src/Keepsake.Core/Repo/Layout.cs:15`; `src/Keepsake.Core/Maintenance/IndexRebuilder.cs:77` (repair deletes listed index files); `src/Keepsake.Core/Repo/RepoLock.cs:56,92` (`unlock` deletes unreadable "locks"); `IndexRebuilder.cs:106-121` (ToolCopy writes into the repository).

```csharp
files = new DirectoryInfo(local).EnumerateFiles("*", SearchOption.AllDirectories);
...
string relative = file.FullName.Substring(_root.Length + 1).Replace('\\', '/');
// Pruner: unknown pack names → Storage.Delete(Layout.PackPath(packId))
```

**Attack scenario.** Someone who can write into the repository folder plants directory junctions inside `data\`, `index\` or `locks\` that point to other folders on the server. That can be a local user when the repository sits on a local or data volume whose inherited ACL lets Users create folders (not restricted by Keepsake), or anyone who swaps or tampers with a USB disk. The listing descends into the junction targets. The automatic prune after backups then deletes matching "unknown pack" files there as SYSTEM, and `keepsake repair` / `keepsake unlock` (both recommended by error messages and the DR runbook) delete everything they list. Backups and the tool copy also write through such junctions. A junction cycle makes listings practically endless. SMB destinations are mostly unaffected, because junctions on a share resolve on the server.

**Business impact.** Arbitrary file deletion as SYSTEM on the file server (configuration, secrets, system files, user data). Backups can stall. Without a restrictive repository ACL, local users can also simply delete backups on local or USB destinations.

**Recommended fix.**
- In `FileSystemStorage.List`, skip and report reparse points and recurse only to the layout's expected depth.
- Accept only valid object names (64 lowercase hex for packs, index and snapshots; 32 hex for locks and keys) before reading or deleting.
- Before deleting, verify that the final path is under the repository root.
- Set an explicit restrictive ACL on the repository root when it is created (`init`).

**Effort:** Medium. **Confidence:** Likely. The code path is confirmed. Verify on a VM that .NET Framework 4.8 `EnumerateFiles(AllDirectories)` descends into junctions, and check the effective ACL of repository folders on non-system volumes.

---

### SEC-005 – High – Pipe-name squatting: clients do not verify the server

**Affected:** `src/Keepsake.Service/PipeServer.cs:74-75` (`MaxAllowedServerInstances`, no first-instance flag), `:94` (the only listening instance is disposed on error), `:149` (SYSTEM FullControl, also in console mode); `src/Keepsake.Protocol/KeepsakeClient.cs:83` (no impersonation level, no server identity check); default pipe name `keepsake-service` (`KeepsakeClient.cs:13`). There is no `GetNamedPipeServerProcessId` and no `FILE_FLAG_FIRST_PIPE_INSTANCE` anywhere in `src/`.

```csharp
server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, ...);
var pipe = new NamedPipeClientStream(".", pipeName ?? Wire.DefaultPipeName, PipeDirection.InOut, PipeOptions.None);
```

**Attack scenario.** Whenever the service is not running, any local account may create the pipe name `keepsake-service`: during an MSI upgrade or repair, during the 60-second restart delay after a crash, after a manual stop, or while the service is disabled. The shipped console mode is enough to do it. The elevated GUI and the CLI connect to whoever owns the name, without checking it. The squatter receives the application password from `auth.login`. By reporting "no password set", it can also make the GUI show the setup screen. It receives repository, SFTP and share passwords sent with `jobs.save` / `repo.test`. Because the client sets no impersonation level, a squatter that holds SeImpersonatePrivilege (IIS application pools, SQL Server, NETWORK SERVICE) can likely act with the admin's token.

**Business impact.** Theft of the repository password (decrypts every backup) and of destination credentials; possibly full compromise of the server.

**Recommended fix.**
- Client: pass `TokenImpersonationLevel.Identification`. After connecting, check that the server process (`GetNamedPipeServerProcessId`) runs as SYSTEM and is the installed service.
- Server: create the first instance with `FILE_FLAG_FIRST_PIPE_INSTANCE` and fail hard if the name already exists. Keep one listening instance at all times.
- Give console mode a different default pipe name and drop the SYSTEM FullControl entry there.

**Effort:** Medium. **Confidence:** Confirmed that no server check or first-instance flag exists. The impersonation default is Likely: check in the .NET Framework 4.8 reference source that impersonation level `None` omits `SECURITY_SQOS_PRESENT`.

---

### SEC-006 – High – Disaster recovery runs an unauthenticated `keepsake.exe` from the untrusted destination

**Affected:** `src/Keepsake.Core/Maintenance/IndexRebuilder.cs:106-121` (`ToolCopy.Ensure`: plain copy of `keepsake.exe` and DLLs into `tools/keepsake-<ver>/`; skipped when the exe already exists, so a planted copy is kept); `src/Keepsake.Core/Repo/Repository.cs:358-375` (unauthenticated README.txt points to it); `docs/DISASTER-RECOVERY.md:14,23,143` (runbook says to copy and run it); `tools/build-release.ps1` (no signing).

```csharp
string target = Folder + "/keepsake-" + version;
if (storage.Exists(target + "/keepsake.exe"))
    return false;          // never re-verified
foreach (string f in files) storage.Write(target + "/" + Path.GetFileName(f), File.ReadAllBytes(f));
```

**Attack scenario.** Whoever controls the SFTP server, share or USB disk (the threat the encryption is designed for) replaces the copied executable or a DLL, plants a "newer" version folder, or edits README.txt. On disaster day an administrator follows the runbook, runs that program on a recovery machine (often elevated) and types in the repository password or recovery key.

**Business impact.** Code execution on the recovery machine and theft of the key that decrypts every backup. This defeats the core promise: the destination "can delete but cannot read".

**Recommended fix.**
- Authenticode-sign all binaries (SEC-008) and have the runbook verify the signature.
- Store a hash manifest of the tool files inside the authenticated repository data and verify it before use. Always rewrite the tool folder rather than trusting an existing one.
- Change the runbook and README to prefer the signed MSI or portable ZIP from a trusted source.

**Effort:** Medium. **Confidence:** Confirmed.

---

### SEC-007 – Medium – Argon2id parameters and key-slot count from the untrusted repository are unbounded

**Affected:** `src/Keepsake.Core/Crypto/PasswordKdf.cs:35-53` (only algorithm and version are checked; memory, iterations and parallelism go straight to BouncyCastle); `src/Keepsake.Core/Repo/Repository.cs:97-127` (every file in `keys/` is parsed and tried); reached from every job run and from pipe handlers (save, test, browse – `ApiHandler.cs`).

```csharp
.WithMemoryAsKB(p.MemoryKiB).WithIterations(p.Iterations).WithParallelism(p.Parallelism)
.WithSalt(Convert.FromBase64String(p.Salt))
```

**Attack scenario.** The destination plants a key slot with an enormous memory or iteration value, or hundreds of slots. Every repository open (scheduled backup, check, prune, restore, GUI browse) then exhausts memory on the file server or runs an Argon2 computation that cannot be cancelled. A bad Base64 salt throws an exception that is not mapped to a Keepsake error. A *downgrade* is not exploitable: new slots always use the default parameters.

**Business impact.** Denial of service on the production file server, and backups that silently stay "running".

**Recommended fix.** Enforce bounds before deriving (for example memory 19 MiB–1 GiB, iterations 1–10, parallelism 1–16, salt 16–64 bytes). Cap the key-file size and the number of slots tried. Accept only 32-hex slot names. Treat any parse error as "skip slot".

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-008 – Medium – Release binaries and MSI are not Authenticode-signed; no published checksum file

**Affected:** `tools/build-release.ps1` (no signtool step; lines ~79-82 print SHA-256 only to the console); artifacts `Keepsake-0.9.1.msi`, `keepsake*.exe`, `KeepsakeGui.exe`, DLLs, `Enable-Tls12.ps1` – all `NotSigned` (checked by the configuration review).

**Attack scenario.** Users download the MSI or ZIP from GitHub Releases or a mirror. A tampered build cannot be told apart from the real one, and SmartScreen, AppLocker and WDAC cannot trust the publisher. This also leaves SEC-006 without a verification anchor.

**Business impact.** A supply-chain compromise installs a SYSTEM service on customers' file servers.

**Recommended fix.** Sign exes, DLLs, the MSI and the PowerShell script with a timestamped Authenticode certificate (or Azure Trusted Signing). Publish a `SHA256SUMS` file. Later add GitHub build-provenance attestations.

**Effort:** Medium. **Confidence:** Confirmed.

---

### SEC-009 – Low – Unbounded reads and unvalidated names from the destination

**Affected:**
- `src/Keepsake.Core/Storage/FileSystemStorage.cs` (`ReadAllBytes`);
- `src/Keepsake.Core/Storage/SftpStorage.cs:79-86` (unbounded MemoryStream) and `:142-156` (recursive listing without depth or loop guard);
- `src/Keepsake.Core/Repo/Repository.cs:241` (whole object read before the hash check);
- `src/Keepsake.Core/Repo/Pack.cs:81-85` (unauthenticated `headerLength` drives the read size; the `(int)` cast can overflow);
- `src/Keepsake.Core/Repo/Layout.cs:15` (`Substring(0, 2)` on short names);
- `Checker.cs` and `Pruner.cs:104` (`ToDictionary` throws on duplicate names).

**Attack scenario.** The destination plants oversized files, a crafted pack trailer, one-character or duplicate names, or (over SFTP) endless directory listings. Check, prune and repair then crash or hang, so retention never runs and the disk fills up.

**Business impact.** Availability of backups and maintenance; possible instability of the service process.

**Recommended fix.** Reject files above expected sizes before reading. Bound `headerLength` against the file size. Filter listings to valid hex names. Limit SFTP listing depth (2) and entry counts.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-010 – Low – `repo.test` / `repo.init` reuse stored secrets against a caller-chosen destination

**Affected:** `src/Keepsake.Service/ApiHandler.cs:331-353` (`GetSecrets(p.JobId).Merge(p.Secrets)` with an arbitrary `p.Destination`); `src/Keepsake.Service/Destinations.cs:162-181` (SFTP password sent to `d.Host`; host-key fingerprint supplied by the caller).

**Attack scenario.** An authenticated caller – today an admin; together with SEC-005, anyone holding a phished session – points an existing job ID at their own SFTP host with its fingerprint pinned. The service sends the stored SFTP password or authenticates there with the stored private key.

**Business impact.** Destination credentials leave the machine without ever being displayed.

**Recommended fix.** Reuse stored secrets only when host, port, user and path match the stored destination exactly. Otherwise require them to be entered again.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-011 – Low – Client-supplied job ID used unvalidated in file paths

**Affected:** `src/Keepsake.Service/Stores.cs:186` (`copy.Id = string.IsNullOrEmpty(copy.Id) ? Guid.NewGuid().ToString("N") : copy.Id`); `src/Keepsake.Service/RunHistory.cs:56-58` (`Path.Combine(_root, record.JobId)`) and the recursive delete of a job's run folder (`RunHistory.cs` ~179-187); `src/Keepsake.Protocol/Jobs.cs:175` (validation does not check `Id`).

**Attack scenario.** A pipe client creating a new job may supply any ID, including an absolute or `..` path. Run records are then written outside the data folder, and deleting the job deletes that directory recursively as SYSTEM. This is reachable only by authenticated admins today. It would become severe together with any weakness in pipe access (SEC-005).

**Business impact.** Defence in depth: arbitrary directory deletion as SYSTEM.

**Recommended fix.** Always generate IDs on the server for new jobs, or validate them against `^[0-9a-f]{32}$`.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-012 – Low – WQL query built by concatenation from `state\vss.json`

**Affected:** `src/Keepsake.Service/Vss.cs:57` (IDs loaded from `state\vss.json`), `:160` (`"SELECT * FROM Win32_ShadowCopy WHERE ID='" + id + "'"` followed by `Delete()` on every match), `:143`.

**Attack scenario.** Shadow-copy IDs left over after a crash are read from the state file and inserted into a WMI query whose results are deleted. Whoever can write that file (see SEC-001) can widen the query and delete all shadow copies on the server, removing the previous-versions safety net – a common step before ransomware.

**Business impact.** Loss of all volume shadow copies.

**Recommended fix.** Accept only IDs matching a GUID pattern (`{xxxxxxxx-…}`) before building the query. Fix SEC-001.

**Effort:** Small. **Confidence:** Confirmed (exploitable only together with SEC-001).

---

### SEC-013 – Low – Login brute-force limiter bypassable; `changePassword` not counted

**Affected:** `src/Keepsake.Service/AuthService.cs:57-80` (lockout checked before Argon2, counted afterwards, so parallel connections each get a guess), `:83-91` (`ChangePassword` failures not counted); counters in memory only.

**Attack scenario.** Only principals that can open the pipe (elevated admins, SYSTEM) can guess. The limiter is weaker than intended.

**Business impact.** Low; the app password is not a boundary against local admins (see SEC-029).

**Recommended fix.** Count the attempt before running Argon2. Serialise logins with a semaphore. Count `changePassword` failures. Optionally persist the counter.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-014 – Low – Sessions survive a password change; no absolute lifetime; not bound to the connection

**Affected:** `src/Keepsake.Service/AuthService.cs:83-91` (other sessions not revoked), `:94-107` (30-minute idle timeout only), `:118-126` (unbounded session count); `src/Keepsake.Cli/ServiceCommands.cs:87-103` (no `auth.logout`).

**Attack scenario.** After an admin changes the app password because of a suspected compromise, an attacker's existing session stays valid as long as it keeps being used.

**Business impact.** A password change does not end an attacker's access.

**Recommended fix.** Revoke all sessions on password change. Add an absolute lifetime (for example 8 h). Bind tokens to the connection. Have the CLI log out in `finally`.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-015 – Low – Pre-authentication DoS on the pipe

**Affected:** `src/Keepsake.Protocol/KeepsakeClient.cs:14,30-33` (allocates up to 64 MiB per frame from the length prefix), `src/Keepsake.Service/PipeServer.cs:74,105,116` (one thread per connection, unlimited instances, blocking read without timeout), `AuthService.cs` with `PasswordKdf.cs` (64 MiB per login, no concurrency limit), `ApiHandler.cs` (`runs.list` limit not clamped).

**Attack scenario.** A local admin script or process (or a buggy one) opens many connections or sends large length prefixes, exhausting service memory and threads.

**Business impact.** Service unavailability. Reachable by privileged principals only.

**Recommended fix.** Cap request frames at about 1 MiB and allocate incrementally. Limit concurrent connections. Add read timeouts. Serialise Argon2. Clamp `limit`.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-016 – Low – GUI accepts `--pipe` in Release builds

**Affected:** `src/Keepsake.Gui/App.xaml.cs:20-23` (outside `#if DEBUG`; only `--app-password-file` at 24-28 is Debug-only – confirmed absent from the Release binary). The CLI `--pipe` option is in `src/Keepsake.Cli/ServiceCommands.cs`.

**Attack scenario.** Through a doctored shortcut or instruction, the elevated GUI is pointed at an arbitrary pipe name owned by an attacker. This enables the phishing in SEC-005 without waiting for service downtime.

**Business impact.** As SEC-005, but it needs social engineering.

**Recommended fix.** Put `--pipe` under `#if DEBUG`, or fix SEC-005 (server identity check), which makes it harmless.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-017 – Low – Restore does not validate tree node names

**Affected:** `src/Keepsake.Core/Restore/RestoreEngine.cs:313-319`; `src/Keepsake.Core/Backup/RepoPaths.cs:48-59,87-88` (`components.Aggregate(folder, Path.Combine)`); `src/Keepsake.Core/Repo/Model.cs:117-118` (unchecked timestamps).

**Attack scenario.** Tree blobs are authenticated, so only someone holding the repository key can craft names such as `..`, absolute paths, device names or alternate data streams. Examples are a second server sharing the repository, or a compromised host. On restore such names escape the target folder, and invalid values abort the restore.

**Business impact.** Defence in depth: SYSTEM file writes outside the target.

**Recommended fix.** Reject empty, `.` and `..` names, names containing `\ / :` or control characters, and reserved device names. Verify that combined paths stay under the target root. Mask attributes as backup does.

**Effort:** Small. **Confidence:** Confirmed (requires the key).

---

### SEC-018 – Low – No rollback/freshness detection; stale index entries win

**Affected:** `src/Keepsake.Core/Repo/MasterIndex.cs:42-43` (first index listed wins), `src/Keepsake.Core/Backup/BlobWriter.cs:61-73` (dedup without checking that the pack exists), `src/Keepsake.Core/Repo/Repository.cs:339-344` ("latest" = newest surviving snapshot), `src/Keepsake.Core/Repo/RepoLock.cs:73-82` (unreadable locks treated as stale).

**Attack scenario.** The destination hides newer snapshots, so "latest" restores old data. Or it replays an index from before a prune, so new backups dedup against packs that no longer exist and report Success until the next check. Deleting is inherent to the threat model, but nothing detects it.

**Business impact.** False assurance; silently incomplete backups until `check` runs.

**Recommended fix.** Drop index entries whose pack is missing from the listing. Keep the last snapshot ID and time per job in service state and warn on regression. Run checks automatically.

**Effort:** Small–Medium. **Confidence:** Likely.

---

### SEC-019 – Low – Removing a password or recovery key does not revoke it

**Affected:** `src/Keepsake.Core/Repo/Repository.cs:179-187` (deletes the slot file only); the master key never changes (`docs/FORMAT.md` §3.1).

**Attack scenario.** A former admin who knows an old password and kept a copy of the old `keys/` file (or a destination operator who kept it) can still unwrap the master key and read past and future backups.

**Business impact.** "Remove key" gives a false sense of revocation.

**Recommended fix.** Document this clearly in the UI and docs. Offer a rekey or a migration to a new repository for compromise cases.

**Effort:** Small (docs) / Large (rekey). **Confidence:** Confirmed.

---

### SEC-020 – Low – SMTP mode "none" sends credentials and report contents in clear

**Affected:** `src/Keepsake.Service/Notifier.cs:53-58` (`SecureSocketOptions.None`, then `Authenticate`), `:96-103` (mail body with up to 30 file paths and error messages); `ApiHandler.cs:140` (mode "none" allowed).

**Attack scenario.** A network observer between the server and the mail relay captures the SMTP credentials and the file paths in the reports.

**Business impact.** Mail account takeover; disclosure of file names.

**Recommended fix.** Refuse authentication without TLS, or require an explicit acknowledgement, and warn in the UI.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-021 – Low – Recovery-key files written with inherited ACLs; clipboard not cleared

**Affected:** `src/Keepsake.Cli/Commands.cs:203-210` (`File.WriteAllText` for `--recovery-key-out`, also used by `key add-recovery`); `src/Keepsake.Gui/Views/RecoveryKeyWindow.xaml.cs:31` (`Clipboard.SetText`, no clearing), `:40` ("Save as").

**Attack scenario.** The key file lands in a folder that other users can read, or stays in Windows clipboard history or cloud clipboard sync.

**Business impact.** Recovery-key disclosure, which decrypts all backups.

**Recommended fix.** Write with an owner-only DACL. Clear the clipboard after 30–60 s and exclude the content from clipboard history.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-022 – Low – No minimum repository-password length on non-interactive CLI paths

**Affected:** `src/Keepsake.Cli/CommandContext.cs:143-150` (file or `KEEPSAKE_PASSWORD` accepted if non-empty; the 8-character check at :155 only applies to prompts); `src/Keepsake.Core/Repo/Repository.cs:58,165`. The GUI and API enforce 8 characters (`ApiHandler.cs:346`).

**Attack scenario.** A scripted `init` with a weak password makes the readable key slots easy to brute-force offline.

**Business impact.** Weakens encryption of the affected repository.

**Recommended fix.** Enforce the policy in `Repository.Init` and `AddPasswordKey`.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-023 – Low – ReDoS in the exclusion-pattern matcher

**Affected:** `src/Keepsake.Core/Backup/ExcludeMatcher.cs:73,77-109` (globs translated to `.*` / `(?:.*\\)?`, no match timeout).

**Attack scenario.** Patterns are set by admins, but the file names they are matched against are created by users. Certain pattern shapes combined with crafted long names cause heavy backtracking and stall backups.

**Business impact.** Backups stall.

**Recommended fix.** Add a `matchTimeout`, collapse consecutive wildcards, or use a non-backtracking glob matcher.

**Effort:** Small. **Confidence:** Likely.

---

### SEC-024 – Low – Service runs with the full LocalSystem token

**Affected:** `installer/Package.wxs:60-79` (no `ServiceConfig` with a service SID or required privileges); `src/Keepsake.Service/ServiceHost.cs:53` (enables SeBackupPrivilege only).

**Attack scenario.** Any code-execution bug in the service (or one of SEC-001 to SEC-004) gets every LocalSystem privilege, not just backup and restore.

**Business impact.** A larger blast radius.

**Recommended fix.** Configure the service SID type "restricted"/"unrestricted" and a required-privileges list (SeBackupPrivilege, SeRestorePrivilege, SeChangeNotifyPrivilege, plus whatever VSS needs). Test on 2008 R2.

**Effort:** Small. **Confidence:** Confirmed (configuration absent; the effect needs testing).

---

### SEC-025 – Low – Internal build path embedded in every shipped binary

**Affected:** all release binaries contain `P:\dk@\work\clas\keepsake\…\*.pdb` (PDB path); `Directory.Build.props` sets `Deterministic` but not `ContinuousIntegrationBuild` or `PathMap`.

**Attack scenario.** This is information exposure, not an exploit: every recipient sees the developer's drive layout and the folder name `clas`.

**Business impact.** Leaks internal naming; builds are not reproducible across machines.

**Recommended fix.** Set `ContinuousIntegrationBuild=true` for release builds (or a `PathMap`), and add SourceLink when the repository is published.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-026 – Low – Personal contact data and an employer e-mail address become public on publication

**Affected:** `installer/Package.wxs:29-31` (address, mobile number in ARP properties), `src/Keepsake.Gui/Views/AboutWindow.xaml` (phone, LinkedIn), `installer/License.rtf:3`; all 9 commits are authored with an e-mail address at a different company domain than the copyright holder in `Directory.Build.props`.

**Attack scenario.** Not a vulnerability. Publishing makes the data harvestable for spam and phishing, and the commit identity may raise questions about who owns the code.

**Business impact.** Privacy and IP clarity.

**Recommended fix.** Decide deliberately which contact data ships. Use a business or GitHub noreply address, and rewrite author metadata before the first push if wanted.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-027 – Low – Licence text says "internal only"; third-party notices incomplete

**Affected:** `installer/License.rtf` ("Internal software. For use within the organisation only."); there is no `LICENSE` file; the shipped transitive packages (`Microsoft.Bcl.*`, `System.*`, `Microsoft.Extensions.*`) are not listed, and no MIT notice texts are included.

**Attack scenario.** Not a vulnerability. It creates legal ambiguity and possible non-compliance with MIT attribution requirements in the MSI and ZIP.

**Business impact.** Legal risk on publication.

**Recommended fix.** Add a `LICENSE` file, update `License.rtf`, and ship a `THIRD-PARTY-NOTICES` file with the full licence texts.

**Effort:** Small. **Confidence:** Confirmed.

---

### SEC-028 – Low – No NuGet lock files or source mapping

**Affected:** no `packages.lock.json`, no `RestorePackagesWithLockFile`, no `nuget.config` (restore uses machine-level sources: nuget.org plus a local SDK folder).

**Attack scenario.** Today there is no private feed, so the dependency-confusion risk is low. Restore results can still drift between machines and future CI.

**Business impact.** Weaker supply-chain reproducibility.

**Recommended fix.** Add a `nuget.config` with `<clear/>`, nuget.org and `packageSourceMapping`. Enable lock files and locked-mode restore in CI.

**Effort:** Small. **Confidence:** Confirmed. (Positive: all versions are pinned exactly.)

---

### SEC-029 – Informational – Collected items

- **The app password is not a boundary against local administrators:** they can edit `settings.json` directly, and the API exposes SYSTEM-level capabilities (pre/post commands, restore to any path). Document this and log changes to commands and restore targets in the Event Log (`DataPaths.cs`, `ApiHandler.cs`).
- **`auth.setup` is unauthenticated** until a password exists. It is check-and-set under a lock and cannot be repeated afterwards (`AuthService.cs:36-47`). Optionally require a one-time code from the protected data folder.
- **Pre/post commands run as SYSTEM through `cmd.exe`** by design. The job name is passed in `KEEPSAKE_JOB`, so an unquoted `%KEEPSAKE_JOB%` in a command expands metacharacters (`JobRunner.cs:275-278`). Document quoting, or restrict job names. The full command line is written to the run log, so passwords placed in it end up in history files (`CommandRunner.cs:26`).
- **Error and log detail:**
  - `"Internal error: " + ex.Message` is returned to clients (`ApiHandler.cs:58`).
  - Stack traces are logged.
  - Event Log entries contain paths and `user@host`, and the Application log is readable by interactive users.
  - `service.log` rotates by size only.
  - `--log` files inherit ACLs.
  - Run history is kept 90 days / 200 runs.
- **Secrets in environment variables** (`KEEPSAKE_PASSWORD`, `KEEPSAKE_RECOVERY_KEY`, `KEEPSAKE_SFTP_PASSWORD`, `KEEPSAKE_APP_PASSWORD`) are visible to same-user processes. `init --json` prints the recovery key to stdout. Recommend ACL'd files.
- **Restored files inherit the target folder's permissions** (NTFS ACLs are not backed up, by design). Confidential files restored into a widely readable folder become readable. Document this for helpdesk restores.
- **Hard links in user folders:** backup reads with backup semantics, so a hard link inside a user folder could pull a file into the backup that the user cannot read. A later restore into the user's folder would expose it. Needs manual verification (Windows has required write access to the target for hard-link creation since 2015).
- **Share connections:** `WNetAddConnection2(..., flags 0)` as LocalSystem creates the connection in SYSTEM's logon session, which other SYSTEM services share (`Destinations.cs:31-43`). The connection is cancelled on dispose; verify that nothing persists.
- **USB destinations are recognised by `Volume{GUID}`,** which can be cloned (`LocalVolumes.cs`). This is consistent with the threat model.
- **SFTP error text and e-mail:** exception text from an attacker-controlled SFTP server flows into logs, the Event Log and e-mails. E-mail is plain text and the subject is MIME-encoded, so there is no header or HTML injection.
- **CSV export does not neutralise leading `= + - @`** (`HistoryViewModel.cs:218-224`). It is not currently reachable, because cells start with fixed text or drive letters. Prefix `'` anyway.
- **Cryptography notes:**
  - One encryption key per repository with random 96-bit nonces (about 2^32 seals is the NIST limit; not reachable in practice).
  - GCM is not key-committing (no practical impact here).
  - Blob AAD is only the kind, mitigated by verifying the blob ID after decryption.
  - Unauthenticated `keepsake.json` only allows denial of service.
  - Compression before encryption and per-pack counts leak sizes. The keyed chunker and HMAC IDs prevent content confirmation.
  - Keys and passwords are not zeroed in memory, and opened repositories are cached for 5 minutes.
  - `FORMAT.md` says IDs are 64 hex, but repository, slot and lock IDs are 32 hex.
- **STARTTLS errors:** if the server lacks STARTTLS, MailKit's `NotSupportedException` is not caught (`Notifier.cs:63-65`). Sending fails safely, but the error is unclear.
- **Dependencies:**
  - The NuGet advisory check found **no known vulnerabilities** in any project, including transitive packages.
  - Deprecated packages are test-only (legacy `xunit` 2.9.2 and its transitive dependencies).
  - `System.Text.Json` 8.0.5 follows .NET 8, whose support ends in November 2026: plan the move to 9.x/10.x.
  - WiX 5.0.2 was not checked online.
- **`Enable-Tls12.ps1`** only enables TLS 1.2 client settings and strong-crypto flags machine-wide, never disables anything, and requires elevation. Acceptable. The script is unsigned (SEC-008).
- **Installer details:**
  - Suppressing ICE43/ICE57 is justified.
  - No `AllowSameVersionUpgrades`, so rebuilding the same version can install side by side.
  - On upgrade, the `RegistrySearch` overrides an `INSTALLFOLDER` passed on the command line.
  - `LaunchGui` runs the GUI elevated from the install folder (acceptable once SEC-002 is fixed).
  - The stage-folder wildcard ships whatever lands in `artifacts\stage`.
- **Before publishing on GitHub:** there is no CI, no Dependabot, no CodeQL, no `SECURITY.md` and no branch protection. `.gitignore` lacks `*.pfx *.snk *.pem *.ppk *.key *.env *password*.txt`, although the README suggests password files. `claudeinput/` (licensed fonts) is ignored and was never committed, but it sits in the working tree.

---

## 3. Positive findings

| Control | Evidence |
|---|---|
| Pipe DACL denies NETWORK logons and allows only SYSTEM, Administrators and the process owner; non-elevated UAC tokens are refused | `src/Keepsake.Service/PipeServer.cs:144-158` |
| Authentication enforced in one place; only `auth.status/setup/login` skip it; unknown methods rejected | `src/Keepsake.Service/ApiHandler.cs:66-74, 221` |
| App password: Argon2id (64 MiB, t=3, p=2, 16-byte random salt, NFC), constant-time compare, stored separately from repository keys | `AuthService.cs:134-141`, `PasswordKdf.cs:22-33`, `Util/Bytes.cs:23-31` |
| Session tokens: 256 bits from a CSPRNG, 30-minute idle expiry, logout removes the session | `AuthService.cs:123`, `Util/Bytes.cs:35-47` |
| API never returns secrets (`Has*` flags only, SMTP password blanked, job export without secrets) | `ApiHandler.cs:127-134, 252-261`, `ServiceCommands.cs` (export) |
| `--app-password-file` compiled out of Release; debug manifest used only in Debug; Release GUI requires elevation | `App.xaml.cs:24-28`, `Keepsake.Gui.csproj:11`, `app.manifest:8` |
| AES-256-GCM with a fresh random nonce per seal; generic failure messages | `Crypto/Aead.cs:28-38, 60-63` |
| Keyed HMAC blob IDs verified after decryption; content-addressed objects verified before decryption; pack headers authenticated and size-checked | `Repo/Repository.cs:239-246, 276-290`, `Repo/Pack.cs:63-123` |
| Key slots bound to the repository ID (AAD); HKDF with repository-ID salt and separate labels; RFC 5869 implementation | `Repository.cs:133, 202`, `RepoKeys.cs:15-22`, `Hkdf.cs` |
| Recovery key: 256 bits from a CSPRNG, 16-bit checksum, strict Base32 parsing | `Crypto/RecoveryKey.cs:19, 67-69` |
| Decompression only after authentication (no pre-auth zstd bomb) | `Repository.cs:226, 281` |
| CSPRNG everywhere; `System.Random` only for check sampling | `Util/Bytes.cs:35-48`, `Check/Checker.cs:207` |
| No BinaryFormatter, XmlSerializer, DataContract, TypeNameHandling or polymorphic JSON; System.Text.Json with concrete types | `Util/Json.cs:16-48` |
| No archive extraction in the code | grep for `ZipFile`/`ZipArchive`/`GZip` found nothing |
| SFTP host keys strictly pinned during key exchange, before authentication; unknown or changed keys refused unless explicitly confirmed; passwords in URLs refused | `Storage/SftpStorage.cs:29, 296-315`, `StorageFactory.cs:222`, `CommandContext.cs:93-108` |
| SMTP enforces TLS 1.2+ with no certificate-validation override; STARTTLS mode is mandatory, not opportunistic | `Service/Notifier.cs:52` |
| Backup does not follow directory junctions or symlinks | `Backup/BackupEngine.cs:394-397` |
| Run-log lookups validate the run ID against history before building paths | `Service/RunHistory.cs:113-116` |
| Pre/post commands: `UseShellExecute=false`, System32 working directory, timeout, process-tree kill, no secrets in the environment | `Service/CommandRunner.cs:14-21`, `JobRunner.cs:275-278` |
| Service image path quoted (WiX and `sc create`) – no unquoted-service-path issue | `installer/Package.wxs`, `Service/Program.cs:113-127` |
| No secrets anywhere in git history (all revisions searched); test fixtures obviously fake; no large or licensed binaries committed | git history review |
| All NuGet versions pinned exactly; no build script downloads anything; the release build checks DLL version conflicts and binding redirects | `*.csproj`, `tools/build-release.ps1:42-45, 66` |
| NuGet advisory check: no known vulnerabilities | `dotnet list package --vulnerable --include-transitive` |

---

## 4. Manual verification checklist

| # | Check | Finding |
|---|---|---|
| 1 | On 2008 R2 and 2019/Win11 VMs: can a standard user create `C:\ProgramData\Keepsake\config` before installation, and do owner and child ACEs survive the service's first start? Can that user read `jobs.json` afterwards? | SEC-001 |
| 2 | Install to a custom folder under `C:\` and `D:\` on each target OS; check `icacls` and whether a standard user can create files there. | SEC-002 |
| 3 | Can a share user replace a folder in their share with a mount-point junction, and does a SYSTEM restore (original location, overwrite) write through it? | SEC-003 |
| 4 | Does .NET Framework 4.8 `EnumerateFiles(AllDirectories)` descend into junctions? Do prune, repair or unlock delete through a junction in a test repository? What is the effective ACL of repository folders created on non-system volumes and USB disks? | SEC-004 |
| 5 | Can a standard user create `\\.\pipe\keepsake-service` while the service is stopped? Does `NamedPipeClientStream` without an impersonation level allow server-side impersonation? Which accounts on the target servers hold SeImpersonatePrivilege? How long is the service down during an MSI upgrade? | SEC-005 |
| 6 | Are release binaries signed outside the repository process? Is the portable ZIP hash published anywhere? | SEC-006, SEC-008 |
| 7 | BouncyCastle 2.7.0 Argon2: allocation and run time with very large memory or iteration values; does the service survive, and does the job hang? | SEC-007 |
| 8 | Does SSH.NET 2026.0.0 build `FullName` from server-supplied names without sanitising them? | SEC-009 |
| 9 | `sc sdshow KeepsakeService` after an MSI install: non-admins must not have START/STOP/CHANGE_CONFIG. | SEC-005, SEC-024 |
| 10 | Required-privileges and service-SID configuration: does VSS still work with a restricted privilege set on 2008 R2? | SEC-024 |
| 11 | Event Log ACLs on the target servers, and whether monitoring forwards message text off the machine. | SEC-029 |
| 12 | Online advisory check for WiX 5.0.2 (not covered by `dotnet list package`). | SEC-029 |
| 13 | Before publishing: run gitleaks or trufflehog over the full history as a second opinion (the manual grep found nothing). | SEC-026 |
| 14 | Does any persistent share connection or credential remain after a job (`net use` as SYSTEM, Credential Manager)? | SEC-029 |

---

## 5. Prioritised remediation plan

**High**
1. **SEC-001** – Create and lock `%ProgramData%\Keepsake` in the MSI. Verify the owner and reset ACLs (including children, no reparse points) at service start. Move away from machine-scope DPAPI with constant entropy.
2. **SEC-002** – Lock `INSTALLFOLDER` with `PermissionEx` (or allow only Program Files). Start `cmd.exe`, `taskkill.exe` and `sc.exe` by full System32 path. Reconsider the PATH entry.
3. **SEC-003** – Refuse reparse points along restore targets and verify final paths stay under the restore root.
4. **SEC-004** – Skip reparse points in `FileSystemStorage.List`, validate object names, and verify final paths before deleting. Set a restrictive ACL on new repository folders.
5. **SEC-005** – Client: Identification impersonation level plus a server process/SID check. Server: first-instance flag. Use a separate console-mode pipe name.
6. **SEC-006** – Verify the tool copy (authenticated hash manifest or signatures), always rewrite it, and change the runbook to prefer trusted downloads.

**Medium**
7. **SEC-007** – Bound the Argon2 parameters, key-file size and slot count; skip malformed slots.
8. **SEC-008** – Authenticode-sign all binaries, the MSI and scripts; publish `SHA256SUMS`.

**Low**
9. **SEC-009** – Add size limits and name validation for destination listings and reads; add SFTP depth and loop guards.
10. **SEC-010** – Reuse stored secrets only for an unchanged destination.
11. **SEC-011** – Generate job IDs on the server and validate their format.
12. **SEC-012** – Validate shadow-copy IDs as GUIDs before building the WQL query.
13. **SEC-013** – Count login attempts before Argon2, serialise logins, count `changePassword` failures.
14. **SEC-014** – Revoke sessions on password change; add an absolute lifetime and connection binding; CLI logout.
15. **SEC-015** – Add a frame-size cap, connection limit, read timeout and Argon2 semaphore; clamp the `runs.list` limit.
16. **SEC-016** – Make GUI `--pipe` Debug-only.
17. **SEC-017** – Validate tree node names and enforce target containment on restore.
18. **SEC-018** – Drop index entries for missing packs; detect snapshot regression per job.
19. **SEC-019** – Document that removing a key does not revoke it; plan a rekey option.
20. **SEC-020** – Refuse SMTP authentication without TLS.
21. **SEC-021** – Write key files with an owner-only DACL; clear the clipboard.
22. **SEC-022** – Enforce the minimum repository-password length in Core.
23. **SEC-023** – Add a regex match timeout and collapse wildcards.
24. **SEC-024** – Configure a service SID and required privileges.
25. **SEC-025** – Use `ContinuousIntegrationBuild`/`PathMap` for releases.
26. **SEC-026** – Decide on public contact data and the commit identity before publishing.
27. **SEC-027** – Add a `LICENSE` file and third-party notices; update `License.rtf`.
28. **SEC-028** – Add `nuget.config` with source mapping and lock files.

**Informational**
29. **SEC-029** – Document the app-password boundary, the quoting of `KEEPSAKE_JOB` and ACL inheritance on restore. Plan the System.Text.Json upgrade before November 2026. Add `.gitignore` patterns, CI, `SECURITY.md` and Dependabot before publishing.
