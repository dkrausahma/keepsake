<#
.SYNOPSIS
    Builds a Keepsake release: Release binaries, the MSI installer and a portable ZIP.

.DESCRIPTION
    Output in artifacts\:
      Keepsake-<version>.msi            installer (service, GUI, CLI)
      Keepsake-<version>-portable.zip   all binaries; keepsake.exe runs without installation
                                        (disaster recovery on another computer)
    Run from any folder:  powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 [-SkipTests]
#>
param(
    [switch] $SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

[xml] $props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Keepsake $version" -ForegroundColor Cyan

Write-Host '== Build (Release)'
dotnet build Keepsake.slnx -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not $SkipTests) {
    Write-Host '== Tests'
    dotnet test Keepsake.slnx -c Release -nologo --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host '== Stage'
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($project in 'Keepsake.Service', 'Keepsake.Cli', 'Keepsake.Gui') {
    $bin = Join-Path $root "src\$project\bin\Release\net48"
    Get-ChildItem $bin -File | Where-Object { $_.Extension -notin '.pdb', '.xml' } | ForEach-Object {
        $target = Join-Path $stage $_.Name
        if ((Test-Path $target) -and $_.Extension -eq '.dll') {
            # Shared libraries must be identical across the three programs.
            $a = (Get-Item $target).VersionInfo.FileVersion; $b = $_.VersionInfo.FileVersion
            if ($a -ne $b) { throw "Version conflict for $($_.Name): $a vs $b" }
        }
        Copy-Item $_.FullName $target -Force
    }
}
Copy-Item (Join-Path $root 'tools\Enable-Tls12.ps1') $stage
Copy-Item (Join-Path $root 'docs\DISASTER-RECOVERY.md') $stage
@"
Keepsake $version

  KeepsakeGui.exe      management program (run as administrator)
  keepsake.exe         command line; 'keepsake help' lists all commands
  keepsake-service.exe Windows service (installed by the MSI)

Restore on any computer (no installation needed, .NET Framework 4.8 required):
  keepsake snapshots --repo <backup folder or sftp://user@host/path>
  keepsake restore latest --repo <...> --target D:\Restore

See DISASTER-RECOVERY.md for the full procedure.
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding UTF8

& (Join-Path $PSScriptRoot 'check-binding-redirects.ps1') -Folder $stage
if ($LASTEXITCODE -ne 0) { throw 'Binding redirects do not match the staged libraries.' }

Write-Host '== MSI'
dotnet build installer\Keepsake.Installer.wixproj -c Release -nologo -v q -p:StageDir=$stage -p:ProductVersion=$version -o $artifacts
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

Write-Host '== Portable ZIP'
$zip = Join-Path $artifacts "Keepsake-$version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

Get-ChildItem $artifacts -File | Where-Object { $_.Extension -in '.msi', '.zip' } | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    '{0,-36} {1,10:N0} KB  SHA256 {2}' -f $_.Name, ($_.Length / 1KB), $hash
}
Write-Host 'Done.' -ForegroundColor Green
