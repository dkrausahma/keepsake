<#
.SYNOPSIS
    Lists libraries that differ between the service, CLI and GUI builds. They are installed into one
    folder, so every shared DLL must have the same version everywhere.
#>
param([string] $Configuration = 'Release')
$root = Split-Path $PSScriptRoot -Parent
$all = foreach ($p in 'Keepsake.Service', 'Keepsake.Cli', 'Keepsake.Gui') {
    Get-ChildItem (Join-Path $root "src\$p\bin\$Configuration\net48\*.dll") | ForEach-Object {
        [pscustomobject]@{ Project = $p; Name = $_.Name; Version = $_.VersionInfo.FileVersion }
    }
}
$conflicts = $all | Group-Object Name | Where-Object { ($_.Group.Version | Select-Object -Unique).Count -gt 1 }
if (-not $conflicts) { 'No version conflicts.'; exit 0 }
foreach ($c in $conflicts) {
    $c.Name + ': ' + (($c.Group | ForEach-Object { $_.Project + '=' + $_.Version }) -join ', ')
}
exit 1
