# Bootstraps the optional updater into a product directory, keeping product data intact.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DestinationDirectory,
    [Parameter(Mandatory=$true)][string]$AppId,
    [Parameter(Mandatory=$true)][string]$DisplayName,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$ServerUrl='http://192.168.32.99:8787/',
    [string[]]$PreservePaths,
    [string[]]$RequiredPreservePaths,
    [string]$PublicKey,
    [string]$UpdaterPath,
    [switch]$ReplaceConfiguration,
    [switch]$AllowEmptyPreservePaths
)
$ErrorActionPreference = 'Stop'
$taskModuleRoot = Split-Path -Parent $PSScriptRoot
if (-not $UpdaterPath) { $UpdaterPath = Join-Path $taskModuleRoot 'bin\JmdUpdater.exe' }
if (-not $PublicKey) { $PublicKey = Join-Path $taskModuleRoot 'deployment\keys\public-key.xml' }
$DestinationDirectory = [System.IO.Path]::GetFullPath($DestinationDirectory)
if (-not (Test-Path -LiteralPath $DestinationDirectory -PathType Container)) { throw 'The existing product directory must be selected explicitly.' }
if ($AppId -notmatch '^[A-Za-z0-9_-]{1,80}$') { throw 'Invalid AppId.' }
$taskVersion = [Version]::Parse($Version)
$taskPreserved = @($PreservePaths | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($taskPreserved.Count -eq 0 -and -not $AllowEmptyPreservePaths) { throw 'Declare PreservePaths explicitly, or use AllowEmptyPreservePaths for a generic product with no local data.' }
$taskRequired = @($RequiredPreservePaths | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($taskRule in $taskRequired) {
    $taskProbe = $taskRule.Replace('\','/').TrimEnd('/')
    if ($taskRule.StartsWith('*.') -and $taskRule.IndexOf('/') -lt 0 -and $taskRule.IndexOf('\') -lt 0) {
        $taskProbe = '__jmd_preserve_probe_' + [Guid]::NewGuid().ToString('N') + $taskRule.Substring(1)
    }
    if ([System.IO.Path]::IsPathRooted($taskProbe) -or -not $taskProbe -or
        @($taskProbe.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or -not $_ -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 }).Count -ne 0) {
        throw ('Invalid required preservation declaration: ' + $taskRule)
    }
    $taskCovered = $false
    foreach ($taskPreserveRule in $taskPreserved) {
        $taskNormalizedRule = $taskPreserveRule.Replace('\','/').TrimEnd('/')
        if ($taskNormalizedRule.StartsWith('*.') -and $taskNormalizedRule.IndexOf('/') -lt 0) {
            if ($taskProbe.EndsWith($taskNormalizedRule.Substring(1), [StringComparison]::OrdinalIgnoreCase)) { $taskCovered = $true }
        } elseif ($taskProbe.Equals($taskNormalizedRule,[StringComparison]::OrdinalIgnoreCase) -or
            $taskProbe.StartsWith($taskNormalizedRule + '/', [StringComparison]::OrdinalIgnoreCase)) { $taskCovered = $true }
    }
    if (-not $taskCovered) { throw ('Required data declaration is not covered by PreservePaths: ' + $taskRule) }
}
$taskServer = [Uri]$ServerUrl
if (-not $taskServer.IsAbsoluteUri -or $taskServer.Scheme -notin @('http','https') -or $taskServer.UserInfo) { throw 'Invalid server URL.' }
$taskHelperRoot = Join-Path $DestinationDirectory 'JmdUpdater'
New-Item -ItemType Directory -Path $taskHelperRoot -Force | Out-Null
$taskConfigPath = Join-Path $DestinationDirectory 'jmd-update.json'
if ((Test-Path -LiteralPath $taskConfigPath) -and -not $ReplaceConfiguration) {
    $taskExisting = Get-Content -LiteralPath $taskConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskExisting.AppId -ne $AppId) { throw 'Existing updater configuration belongs to another product.' }
    throw 'Existing configuration was preserved. Use ReplaceConfiguration only after reviewing it.'
}
Copy-Item -LiteralPath $UpdaterPath -Destination (Join-Path $taskHelperRoot 'JmdUpdater.exe') -Force
if (Test-Path -LiteralPath ($UpdaterPath + '.config')) {
    Copy-Item -LiteralPath ($UpdaterPath + '.config') -Destination (Join-Path $taskHelperRoot 'JmdUpdater.exe.config') -Force
}
Copy-Item -LiteralPath $PublicKey -Destination (Join-Path $taskHelperRoot 'public-key.xml') -Force
$taskConfig = [ordered]@{
    Enabled=$true; AppId=$AppId; DisplayName=$DisplayName; ServerUrl=$taskServer.AbsoluteUri;
    PublicKeyFile='JmdUpdater/public-key.xml'; PollSeconds=60; RequestTimeoutSeconds=15; StageTimeoutSeconds=180;
    ExitTimeoutSeconds=30; StartupTimeoutSeconds=60; MaxPackageBytes=1073741824; PreservePaths=$taskPreserved; RequiredPreservePaths=$taskRequired
}
[System.IO.File]::WriteAllText($taskConfigPath, ($taskConfig | ConvertTo-Json -Depth 6), [System.Text.UTF8Encoding]::new($false))
$taskVersionPath = Join-Path $DestinationDirectory 'jmd-version.json'
if (-not (Test-Path -LiteralPath $taskVersionPath)) {
    [System.IO.File]::WriteAllText($taskVersionPath, ([ordered]@{AppId=$AppId;Version=$taskVersion.ToString()} | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
}
Write-Output ('Updater enabled in ' + $DestinationDirectory + '; server=' + $taskServer.AbsoluteUri)
