[CmdletBinding()]
param([string] $DotNetPath = 'dotnet')

$ErrorActionPreference = 'Stop'

function Invoke-DotNet([string[]] $Arguments) {
    & $DotNetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

Push-Location $PSScriptRoot
try {
    Invoke-DotNet @('build', 'src/Acp.Transport.csproj', '-c', 'Release', '--warnaserror')
    Invoke-DotNet @('run', '--project', 'tests/Acp.Transport.Tests.csproj', '-c', 'Release')
    Invoke-DotNet @('run', '--project', 'examples/Loopback/Acp.Transport.Example.csproj', '-c', 'Release')
    Invoke-DotNet @('build', 'examples/AgentSmoke/Acp.Transport.AgentSmoke.csproj', '-c', 'Release', '--warnaserror')
    Write-Host 'Verification complete.'
}
finally { Pop-Location }
