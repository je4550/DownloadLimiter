[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    # Explicit opt-in only. The tests reference Core, not the Windows transport.
    dotnet run --project 'tests\DownloadLimit.Tests\DownloadLimit.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Offline tests failed.' }
} finally { Pop-Location }
