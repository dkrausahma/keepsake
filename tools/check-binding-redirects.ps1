<#
.SYNOPSIS
    Verifies that the binding redirects of every program in a folder match the assembly versions of the
    DLLs in that folder (service, CLI and GUI share one install folder).
#>
param([string] $Folder = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\stage'))
$problems = 0
foreach ($config in Get-ChildItem $Folder -Filter '*.exe.config') {
    [xml] $xml = Get-Content $config.FullName
    $ns = @{ a = 'urn:schemas-microsoft-com:asm.v1' }
    foreach ($dep in (Select-Xml -Xml $xml -XPath '//a:dependentAssembly' -Namespace $ns)) {
        $name = $dep.Node.assemblyIdentity.name
        $target = $dep.Node.bindingRedirect.newVersion
        $dll = Join-Path $Folder ($name + '.dll')
        if (-not (Test-Path $dll)) { continue } # framework assembly
        $actual = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
        if ($actual -ne $target) {
            '{0}: {1} redirects to {2}, but the folder has {3}' -f $config.Name, $name, $target, $actual
            $problems++
        }
    }
}
if ($problems -eq 0) { 'All binding redirects match.' } else { exit 1 }
