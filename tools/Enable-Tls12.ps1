<#
.SYNOPSIS
    Enables TLS 1.2 for SChannel clients and .NET on Windows Server 2008 R2 / Windows 7.

.DESCRIPTION
    Windows Server 2008 R2 supports TLS 1.2 but has it switched off for client connections.
    Keepsake needs it for e-mail notifications over STARTTLS/SSL (SFTP does not use TLS).
    This script sets the documented registry values and must run elevated; a reboot is required.
    On Windows Server 2012 and newer nothing needs to be changed.

    Values set:
      HKLM\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client
          Enabled = 1, DisabledByDefault = 0
      HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319            (and WOW6432Node)
          SchUseStrongCrypto = 1, SystemDefaultTlsVersions = 1
#>
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (Run as administrator).'
}

$os = [Environment]::OSVersion.Version
if ($os.Major -gt 6 -or ($os.Major -eq 6 -and $os.Minor -ge 2)) {
    Write-Host 'This Windows version enables TLS 1.2 by default. Only the .NET values are set.'
} else {
    $key = 'HKLM:\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\TLS 1.2\Client'
    New-Item -Path $key -Force | Out-Null
    New-ItemProperty -Path $key -Name 'Enabled' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $key -Name 'DisabledByDefault' -Value 0 -PropertyType DWord -Force | Out-Null
    Write-Host "SChannel: TLS 1.2 enabled for clients."
}

foreach ($net in 'HKLM:\SOFTWARE\Microsoft\.NETFramework\v4.0.30319', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\.NETFramework\v4.0.30319') {
    if (Test-Path $net) {
        New-ItemProperty -Path $net -Name 'SchUseStrongCrypto' -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $net -Name 'SystemDefaultTlsVersions' -Value 1 -PropertyType DWord -Force | Out-Null
        Write-Host ".NET: strong crypto enabled ($net)."
    }
}
Write-Host ''
Write-Host 'Done. Restart the computer, then check with: keepsake doctor' -ForegroundColor Green
