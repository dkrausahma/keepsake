---
name: release
description: Build a Keepsake Backup release - bump the version, close the CHANGELOG section, build MSI + portable ZIP + checksums, verify them, fast-forward main and tag. Use only when Dominique runs /release.
argument-hint: "[version, e.g. 0.10.0] [cert thumbprint for signing]"
disable-model-invocation: true
---

# /release — Keepsake Backup

Running `/release` is Dominique's go-ahead for the bump commit, the release build (which runs the
tests), the fast-forward of `main` and the local tag. It is **not** a go-ahead to push: pushing and
publishing stay with him. Report after each numbered step.

## 1. Check the starting point
- Branch must be `dev`. The working tree must be clean except for changes Dominique wants in this
  release; if anything else is modified, list it and stop.
- `CHANGELOG.md` must have a `## Unreleased` section with at least one entry. If it is missing or
  empty, stop and ask what goes into the release.
- Current version: `<Version>` in `Directory.Build.props`. Last release: `git describe --tags --abbrev=0`.

## 2. Decide the version
- `$ARGUMENTS` may give it. Otherwise bump the third field (0.9.15 → 0.9.16).
- It must be higher than the current one in the first three fields; a same-version MSI does not upgrade.
- Say the version you will use before changing anything.

## 3. Bump
- `Directory.Build.props`: `<Version>`.
- `installer/Keepsake.Installer.wixproj`: the fallback `<ProductVersion Condition=...>`.
- `CHANGELOG.md`: rename `## Unreleased` to `## <version> – <YYYY-MM-DD>` (en dash, today's date).
  Don't add a new empty `Unreleased` section.
- Help: run `python tools/help-docs/build.py`. If it changes files under `src/Keepsake.Gui/help/`,
  the help was stale; include them in this commit and say so.
- Show the file list, then commit: `Bump to <version>`.

## 4. Build
```powershell
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1
```
- With a code-signing thumbprint in `$ARGUMENTS`, add `-CertificateThumbprint <thumbprint>`.
  Without one the build is unsigned; say so in the report (releases for others should be signed, SEC-008).
- Before reporting test results: the SFTP tests (server on `localhost:2222`) and the mail test (Mailpit
  on `localhost:1025`) pass without doing anything when those services aren't running, and the
  data-folder ownership tests only really run elevated. Check whether they ran and say which didn't.
- If the build or tests fail, stop. Don't tag, don't touch `main`.

## 5. Verify the artifacts
- `artifacts\Keepsake-<version>.msi` and `artifacts\Keepsake-<version>-portable.zip` exist.
- The MSI's ProductVersion equals the version:
  ```powershell
  $i = New-Object -ComObject WindowsInstaller.Installer
  $db = $i.GetType().InvokeMember('OpenDatabase','InvokeMethod',$null,$i,@((Resolve-Path "artifacts\Keepsake-<version>.msi").Path,0))
  $v = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@("SELECT Value FROM Property WHERE Property='ProductVersion'"))
  $v.GetType().InvokeMember('Execute','InvokeMethod',$null,$v,$null); $r = $v.GetType().InvokeMember('Fetch','InvokeMethod',$null,$v,$null)
  $r.GetType().InvokeMember('StringData','GetProperty',$null,$r,1)
  ```
- `artifacts\SHA256SUMS.txt` lists both files.

## 6. Release branch and tag (local only)
- Fast-forward `main` without switching branches: `git fetch . dev:main`. If it refuses (not a
  fast-forward), stop and ask.
- Tag the bump commit: `git tag -a v<version> -m "Keepsake <version>"`.

## 7. Hand over
Report version, test summary (including what didn't really run), artifact sizes and hashes, signed or
not. Then give Dominique the commands to run himself, each in its own block:
```powershell
git push origin dev main v<version>
```
```powershell
gh release create v<version> "artifacts\Keepsake-<version>.msi" "artifacts\Keepsake-<version>-portable.zip" "artifacts\SHA256SUMS.txt" --title "Keepsake <version>" --notes "<this version's CHANGELOG section>"
```
(`gh release create` with a fine-grained token needs Contents read/write; see the vault note
`refs/github-fine-grained-pat-release-403`.)
