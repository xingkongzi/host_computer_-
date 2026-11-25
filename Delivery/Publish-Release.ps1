# Packages a clean product deployment directory; private material is never placed in releases.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$ClientConfig,
    [Parameter(Mandatory=$true)][string]$EntryPoint,
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$NotesFile,
    [string]$PrivateKey,
    [string]$ReleaseRoot,
    [string]$UpdaterPath,
    [string]$SigningConfig,
    [switch]$UseExistingSignatures,
    [switch]$IsolatedDryRun,
    [switch]$AllowEmptyPreservePaths,
    # 主程序改名时，本版本替换的旧入口程序名（例如 DriveManager.exe）；写入签名清单。
    [string[]]$PreviousEntryPoints
)
$ErrorActionPreference = 'Stop'
$taskModuleRoot = Split-Path -Parent $PSScriptRoot
if (-not $UpdaterPath) { $UpdaterPath = Join-Path $taskModuleRoot 'bin\JmdUpdater.exe' }
if (-not $PrivateKey) { $PrivateKey = Join-Path $taskModuleRoot 'deployment\keys\private-key.xml' }
if (-not $ReleaseRoot) { $ReleaseRoot = Join-Path $taskModuleRoot 'deployment\releases' }
# Sign the updater in the clean source directory before hashing; never substitute the CLI binary afterward.
# Once a product signing policy exists, ordinary publishing cannot silently skip signing.
if ($IsolatedDryRun) {
    # Rehearsals require temporary inputs; never consult deployment signing defaults.
    $taskTempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    foreach ($taskDryPath in @($PrivateKey, $ReleaseRoot, $SourceDirectory, $ClientConfig)) {
        if (-not $taskDryPath -or -not [IO.Path]::GetFullPath($taskDryPath).StartsWith($taskTempPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'IsolatedDryRun requires all publishing inputs under TEMP.'
        }
    }
}
if (-not $SigningConfig -and -not $IsolatedDryRun) {
    $taskProductConfig = Get-Content -LiteralPath $ClientConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskProductConfig.AppId -notmatch '^[A-Za-z0-9_-]{1,80}$') { throw 'Invalid product identity.' }
    $taskDefaultSigning = Join-Path $taskModuleRoot ('deployment\signing.' + $taskProductConfig.AppId + '.json')
    if (Test-Path -LiteralPath $taskDefaultSigning) { $SigningConfig = $taskDefaultSigning }
}
if ($SigningConfig) {
    & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -SourceDirectory $SourceDirectory -SigningConfig $SigningConfig `
        -RequiredFiles @($EntryPoint,'JmdUpdater/JmdUpdater.exe') -VerifyOnly:$UseExistingSignatures
} elseif ($UseExistingSignatures) { throw 'UseExistingSignatures requires an explicit or product-default signing policy.' }
$taskArguments = @('publish','--source',([System.IO.Path]::GetFullPath($SourceDirectory)),
    '--config',([System.IO.Path]::GetFullPath($ClientConfig)),'--entry',$EntryPoint,'--version',$Version,
    '--root',([System.IO.Path]::GetFullPath($ReleaseRoot)),'--key',([System.IO.Path]::GetFullPath($PrivateKey)))
if ($NotesFile) { $taskArguments += @('--notes-file',([System.IO.Path]::GetFullPath($NotesFile))) }
if ($AllowEmptyPreservePaths) { $taskArguments += @('--allow-empty-preserve','true') }
if ($PreviousEntryPoints) { $taskArguments += @('--previous-entry', (@($PreviousEntryPoints) -join ';')) }
& $UpdaterPath @taskArguments
if ($LASTEXITCODE -ne 0) { throw ('Publishing failed with exit code ' + $LASTEXITCODE) }
