[CmdletBinding()]
param([switch]$CheckAppSymbols)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
Push-Location $workspacePath
try {
    if (-not (Test-Path -LiteralPath '.deps\WinDivert\x64\WinDivert.dll')) {
        & (Join-Path $PSScriptRoot 'prepare-dependencies.ps1')
    }
    foreach ($project in @('DownloadLimit.Tests', 'DownloadLimit.Windows.Tests')) {
        if ($project -eq 'DownloadLimit.Windows.Tests' -and $CheckAppSymbols) {
            dotnet run --project "tests\$project\$project.csproj" -c Release -- --check-assembly `
                'src/DownloadLimit.App/bin/Release/net10.0-windows/win-x64/DownloadLimit.dll'
        } else {
            dotnet run --project "tests\$project\$project.csproj" -c Release
        }
        if ($LASTEXITCODE -ne 0) { throw "$project failed." }
    }
    Write-Host 'All network-free checks passed. No driver handles, app launches or live traffic.'
} finally {
    Pop-Location
}
