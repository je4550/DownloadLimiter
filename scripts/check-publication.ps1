[CmdletBinding()]
param([string]$ReleaseDirectory)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
Push-Location $workspacePath
try {
    $sourceFiles = @(git -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0 -or $sourceFiles.Count -eq 0) {
        throw 'Stage the intended source files before checking publication.'
    }
    $patterns = @{
        'private home path' = '(?i)[a-z]:[\\/]Users[\\/](?!Public[\\/]|Default[\\/])[^\\/\s<>"'']+'
        'private Unix home path' = '/(?:home|Users)/[A-Za-z0-9._-]+/'
        'private key' = '-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----'
        'GitHub credential' = '(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})'
        'cloud access key' = '(?:AKIA|ASIA)[A-Z0-9]{16}'
        'literal credential' = '(?i)(?:api[_-]?key|access[_-]?token|auth[_-]?token|client[_-]?secret|password)\s*[:=]\s*["''][^"''\s]{8,}["'']'
        'machine account SID' = 'S-1-5-21(?:-\d{6,}){3}-\d+'
        'email address' = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
    }
    $privateMarkers = @([Environment]::GetFolderPath('UserProfile'), $workspacePath, [Environment]::UserName) |
        Where-Object { $_.Length -gt 3 }
    $issues = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $sourceFiles) {
        if ($relativePath -match '(?i)(^|/)(artifacts|\.deps|\.vs|bin|obj|logs|TestResults)/|(^|/)(\.env[^/]*|settings\.json)$|\.(exe|dll|sys|zip|pdb|pfx|p12|pem|key|snk|log)$') {
            $issues.Add("$relativePath : private/generated file")
            continue
        }
        $content = (git show ":$relativePath") -join "`n"
        if ($LASTEXITCODE -ne 0) { throw "Could not inspect staged file: $relativePath" }
        foreach ($rule in $patterns.GetEnumerator()) {
            if ($content -match $rule.Value) { $issues.Add("$relativePath : $($rule.Key)") }
        }
        foreach ($marker in $privateMarkers) {
            if ($content.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $issues.Add("$relativePath : local environment value")
                break
            }
        }
    }
    if ($ReleaseDirectory) {
        $releasePath = [IO.Path]::GetFullPath((Join-Path $workspacePath $ReleaseDirectory))
        $allowed = @('DownloadLimit.exe', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt', 'SHA256SUMS.txt', 'BUILD-STATUS.txt',
            'docs/DESIGN.md', 'docs/WINDOWS-VALIDATION.md', 'Licenses/WinDivert-LICENSE.txt',
            'Licenses/WinDivert-2.2.2-Source.zip', 'Licenses/DotNet-LICENSE.txt',
            'Licenses/DotNet-NOTICES.txt', 'Licenses/WindowsDesktop-LICENSE.txt')
        foreach ($item in Get-ChildItem -LiteralPath $releasePath -Recurse -File) {
            $relativePath = $item.FullName.Substring($releasePath.TrimEnd('\').Length + 1).Replace('\', '/')
            if ($allowed -notcontains $relativePath) { $issues.Add("release/$relativePath : unexpected release file") }
            if ($item.Extension -in @('.md', '.txt')) {
                $content = [IO.File]::ReadAllText($item.FullName)
                foreach ($marker in $privateMarkers) {
                    if ($content.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                        $issues.Add("release/$relativePath : local environment value")
                    }
                }
            }
        }
    }
    if ($issues.Count -gt 0) {
        $issues | Select-Object -Unique | ForEach-Object { Write-Host $_ }
        throw 'Publication check failed. Matched values are intentionally not printed.'
    }
    Write-Host "Publication check passed: $($sourceFiles.Count) source files; no matching credentials or personal paths."
    Write-Host 'This is a targeted check, not a guarantee that arbitrary secrets are detectable.'
} finally {
    Pop-Location
}
