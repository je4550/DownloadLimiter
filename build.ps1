[CmdletBinding()]
param(
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]*$')]
    [string]$OutputName = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$workspacePath = $PSScriptRoot
$artifactsPath = Join-Path $workspacePath 'artifacts'
$releasePath = Join-Path $artifactsPath $OutputName
$stagePath = Join-Path $artifactsPath ('.publish-' + [Guid]::NewGuid().ToString('N'))

function Assert-ArtifactPath([string]$Candidate) {
    $resolvedPath = [IO.Path]::GetFullPath($Candidate)
    $rootPath = [IO.Path]::GetFullPath($artifactsPath).TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Build output must remain inside the artifacts directory.'
    }
}

Push-Location $workspacePath
try {
    & (Join-Path $workspacePath 'scripts\prepare-dependencies.ps1')
    dotnet restore 'src\DownloadLimit.App\DownloadLimit.App.csproj' -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'App dependency restore failed.' }
    $restoredAssets = Get-Content -LiteralPath 'src\DownloadLimit.App\obj\project.assets.json' -Raw | ConvertFrom-Json
    $packageCache = $restoredAssets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $runtimePackage = Join-Path $packageCache 'microsoft.netcore.app.runtime.win-x64\10.0.12'
    $desktopPackage = Join-Path $packageCache 'microsoft.windowsdesktop.app.runtime.win-x64\10.0.12'
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination '.deps\DotNet-LICENSE.txt' -Force
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination '.deps\DotNet-NOTICES.txt' -Force
    Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination '.deps\WindowsDesktop-LICENSE.txt' -Force

    foreach ($project in @('DownloadLimit.Tests', 'DownloadLimit.Windows.Tests', 'DownloadLimit.LiveSmoke')) {
        dotnet build "tests\$project\$project.csproj" -c Release
        if ($LASTEXITCODE -ne 0) { throw "$project compilation failed. Tests were not executed." }
    }
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    dotnet publish 'src\DownloadLimit.App\DownloadLimit.App.csproj' -c Release -r win-x64 --self-contained true --no-restore -o $stagePath
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    foreach ($document in @('README.md', 'THIRD-PARTY-NOTICES.txt', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $workspacePath $document) -Destination $stagePath -Force
    }
    $docsDirectory = Join-Path $stagePath 'docs'
    New-Item -ItemType Directory -Path $docsDirectory -Force | Out-Null
    foreach ($document in Get-ChildItem -LiteralPath (Join-Path $workspacePath 'docs') -Filter '*.md' -File) {
        Copy-Item -LiteralPath $document.FullName -Destination $docsDirectory -Force
    }
    $licenseDirectory = Join-Path $stagePath 'Licenses'
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    Copy-Item -LiteralPath '.deps\WinDivert\LICENSE' -Destination (Join-Path $licenseDirectory 'WinDivert-LICENSE.txt') -Force
    Copy-Item -LiteralPath '.deps\WinDivert-2.2.2-Source.zip' -Destination $licenseDirectory -Force
    foreach ($licenseName in @('DotNet-LICENSE.txt', 'DotNet-NOTICES.txt', 'WindowsDesktop-LICENSE.txt')) {
        Copy-Item -LiteralPath (Join-Path $workspacePath ".deps\$licenseName") -Destination $licenseDirectory -Force
    }
    $digest = (Get-FileHash -LiteralPath (Join-Path $stagePath 'DownloadLimit.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    "$digest  DownloadLimit.exe" | Set-Content -LiteralPath (Join-Path $stagePath 'SHA256SUMS.txt') -Encoding ascii
    @'
Target: self-contained Windows x64, .NET 10 / WinForms
Compilation: PASS (app and all test projects)
Tests executed by this build script: NONE
App launched by this build script: NO
Driver loaded/installed by this build script: NO
Startup task registered by this build script: NO
Live Windows/network validation by this build script: NOT PERFORMED
Historical validation scope: README.md; manual checks: docs/WINDOWS-VALIDATION.md
This clean package excludes local logs, settings and raw measurements.
'@ | Set-Content -LiteralPath (Join-Path $stagePath 'BUILD-STATUS.txt') -Encoding utf8

    # Build from a fresh directory: previous local reports must never enter a ZIP.
    if (Test-Path -LiteralPath $releasePath) {
        $archivePath = Join-Path $artifactsPath ('archive-' + $OutputName + '-' + [Guid]::NewGuid().ToString('N'))
        Assert-ArtifactPath $releasePath
        Assert-ArtifactPath $archivePath
        Move-Item -LiteralPath $releasePath -Destination $archivePath
    }
    Assert-ArtifactPath $stagePath
    Assert-ArtifactPath $releasePath
    Move-Item -LiteralPath $stagePath -Destination $releasePath
    $zipPath = Join-Path $artifactsPath "DownloadLimit-$OutputName.zip"
    Compress-Archive -Path (Join-Path $releasePath '*') -DestinationPath $zipPath -Force
    Write-Host "Published artifacts/$OutputName/DownloadLimit.exe"
    Write-Host "SHA-256: $digest"
    Write-Host 'Compilation/package only. No tests or application execution were performed.'
} finally {
    if (Test-Path -LiteralPath $stagePath) {
        Assert-ArtifactPath $stagePath
        Remove-Item -LiteralPath $stagePath -Recurse -Force
    }
    Pop-Location
}
