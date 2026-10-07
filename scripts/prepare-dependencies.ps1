[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$workspacePath = Split-Path -Parent $PSScriptRoot
$dependenciesPath = Join-Path $workspacePath '.deps'
New-Item -ItemType Directory -Force -Path $dependenciesPath | Out-Null

function Get-PinnedArchive([string]$Name, [string]$Url, [string]$ExpectedHash) {
    $archivePath = Join-Path $dependenciesPath $Name
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri $Url -OutFile $archivePath -UseBasicParsing
    }
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualHash -ne $ExpectedHash) {
        throw "Dependency hash mismatch for $Name. Expected $ExpectedHash; got $actualHash."
    }
    return $archivePath
}

$binaryArchive = Get-PinnedArchive 'WinDivert-2.2.2-A.zip' `
    'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip' `
    '63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15'
$null = Get-PinnedArchive 'WinDivert-2.2.2-Source.zip' `
    'https://reqrypt.org/download/WinDivert-2.2.2-Source.zip' `
    '8E904FBEEF2A6180FFC533E9B636AFD14282AF7510E421A9CB381D978302ACD9'

$unpackPath = Join-Path $dependenciesPath 'binary-unpack'
Expand-Archive -LiteralPath $binaryArchive -DestinationPath $unpackPath -Force
$binaryDirectory = Join-Path $unpackPath 'WinDivert-2.2.2-A'
$nativeDirectory = Join-Path $dependenciesPath 'WinDivert\x64'
New-Item -ItemType Directory -Force -Path $nativeDirectory | Out-Null
foreach ($fileName in @('WinDivert.dll', 'WinDivert64.sys')) {
    Copy-Item -LiteralPath (Join-Path $binaryDirectory "x64\$fileName") -Destination (Join-Path $nativeDirectory $fileName) -Force
}
Copy-Item -LiteralPath (Join-Path $binaryDirectory 'LICENSE') -Destination (Join-Path $dependenciesPath 'WinDivert\LICENSE') -Force

# Read signing metadata only. Never execute a WinDivert sample or load its driver here.
$driverPath = Join-Path $nativeDirectory 'WinDivert64.sys'
$signature = Get-AuthenticodeSignature -LiteralPath $driverPath
if ($signature.Status -ne 'Valid') {
    throw "Windows could not validate the official driver's Authenticode signature: $($signature.Status)."
}
Write-Host "Prepared WinDivert 2.2.2; signed driver publisher: $($signature.SignerCertificate.Subject)"
