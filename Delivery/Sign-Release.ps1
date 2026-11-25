<#
JMD reusable Windows Authenticode signing step for a clean deployment directory.
Signs only explicitly listed product files. Private keys stay in the Windows certificate store.
Run before generating ZIPs, SHA256SUMS or signed update manifests. Never re-sign published files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$SigningConfig,
    [string[]]$RequiredFiles,
    [string]$SignToolPath,
    [switch]$VerifyOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $taskRoot -PathType Container)) { throw 'Select an existing clean deployment directory.' }
$taskSettings = Get-Content -LiteralPath $SigningConfig -Raw -Encoding UTF8 | ConvertFrom-Json
$taskThumbprint = ([string]$taskSettings.CertificateThumbprint -replace '\s','').ToUpperInvariant()
if ($taskThumbprint -notmatch '^[A-F0-9]{40}$') { throw 'Configure the exact code-signing certificate thumbprint.' }
$taskLocation = [string]$taskSettings.StoreLocation
if ($taskLocation -notin @('CurrentUser','LocalMachine')) { throw 'StoreLocation must be CurrentUser or LocalMachine.' }
if ($taskSettings.RequireTimestamp -isnot [bool]) { throw 'RequireTimestamp must explicitly be true or false.' }
$taskRequireTimestamp = $taskSettings.RequireTimestamp
$taskTimestamp = $null
if ($taskRequireTimestamp) {
    $taskTimestamp = [Uri]([string]$taskSettings.TimestampUrl)
    if (-not $taskTimestamp.IsAbsoluteUri -or $taskTimestamp.Scheme -notin @('http','https') -or $taskTimestamp.UserInfo) { throw 'Configure an approved RFC3161 timestamp URL.' }
} elseif (-not [string]::IsNullOrWhiteSpace([string]$taskSettings.TimestampUrl)) { throw 'Local-only signing requires an empty TimestampUrl.' }

# Resolve every path and its existing ancestors before any file is modified.
$taskFiles = @()
foreach ($taskName in @($taskSettings.Files)) {
    $taskRelative = ([string]$taskName).Replace('\','/')
    if ([IO.Path]::IsPathRooted($taskRelative) -or -not $taskRelative -or
        @($taskRelative.Split('/') | Where-Object { -not $_ -or $_ -eq '.' -or $_ -eq '..' -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 }).Count -ne 0) { throw ('Unsafe signing path: ' + $taskRelative) }
    $taskFull = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRelative))
    if (-not $taskFull.StartsWith($taskRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($taskFull) -notin @('.exe','.dll') -or
        -not (Test-Path -LiteralPath $taskFull -PathType Leaf)) { throw ('Signing target is not an existing EXE/DLL inside the deployment: ' + $taskRelative) }
    $taskAncestor = $taskFull
    while ($taskAncestor) {
        if ((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Signing paths cannot contain reparse points.' }
        $taskAncestor = [IO.Path]::GetDirectoryName($taskAncestor)
    }
    if ($taskFiles -contains $taskFull) { throw ('Duplicate signing target: ' + $taskRelative) }
    $taskFiles += $taskFull
}
if ($taskFiles.Count -eq 0) { throw 'Declare the product files to sign.' }
# RequiredFiles from Publish-Release includes JmdUpdater/JmdUpdater.exe, now part of the signed network inventory.
foreach ($taskRequired in $RequiredFiles) {
    if ($taskFiles -notcontains [IO.Path]::GetFullPath((Join-Path $taskRoot $taskRequired))) { throw ('Missing required signing target: ' + $taskRequired) }
}
if (-not $SignToolPath) {
    $taskCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($taskCommand) { $SignToolPath = $taskCommand.Source }
    else {
        $taskSdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $taskCandidates = @(Get-ChildItem -Path (Join-Path $taskSdk '*\x64\signtool.exe') -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)
        if ($taskCandidates.Count -ne 0) { $SignToolPath = $taskCandidates[0].FullName }
    }
}
if (-not $SignToolPath -or -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'Install the Windows SDK signing tools or provide SignToolPath.' }

# A freshly copied or freshly signed executable is often opened for a moment by Windows (indexing or
# security scanning), which makes SignTool fail with "file is being used" or a file read error. Retry
# briefly; signing the same file again and verifying are both repeatable. Real failures still stop.
function Invoke-SignToolWithRetry([string[]]$taskToolArguments, [string]$taskFailure) {
    for ($taskAttempt = 1; ; $taskAttempt++) {
        $taskPrevious = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'    # SignTool stderr must not abort before its exit code is checked
        try { & $SignToolPath @taskToolArguments } finally { $ErrorActionPreference = $taskPrevious }
        if ($LASTEXITCODE -eq 0) { return }
        if ($taskAttempt -ge 5) { throw $taskFailure }
        Write-Output ('SignTool did not succeed (attempt ' + $taskAttempt + ' of 5); retrying in 2 seconds.')
        Start-Sleep -Seconds 2
    }
}
if (-not $VerifyOnly) {
    $taskCertificate = Get-Item -LiteralPath ('Cert:\' + $taskLocation + '\My\' + $taskThumbprint)
    if (-not $taskCertificate.HasPrivateKey -or $taskCertificate.NotBefore -gt (Get-Date) -or $taskCertificate.NotAfter -le (Get-Date) -or
        @($taskCertificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) { throw 'The selected certificate needs an accessible private key, a current validity period and Code Signing usage.' }
    foreach ($taskFile in $taskFiles) {
        $taskArgs = @('sign','/fd','SHA256','/sha1',$taskThumbprint,'/s','My')
        if ($taskRequireTimestamp) { $taskArgs += @('/td','SHA256','/tr',$taskTimestamp.AbsoluteUri) }
        if ($taskLocation -eq 'LocalMachine') { $taskArgs += '/sm' }
        $taskArgs += $taskFile
        Invoke-SignToolWithRetry $taskArgs ('Signing or timestamping failed: ' + $taskFile)
    }
}
foreach ($taskFile in $taskFiles) {
    $taskVerifyArgs = @('verify','/pa','/all')
    if ($taskRequireTimestamp) { $taskVerifyArgs += '/tw' }
    $taskVerifyArgs += $taskFile
    Invoke-SignToolWithRetry $taskVerifyArgs ('Authenticode verification failed. Internal certificates require IT-approved trust on the build computer: ' + $taskFile)
    $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskFile
    for ($taskAttempt = 1; $taskSignature.Status -eq 'UnknownError' -and $taskAttempt -lt 5; $taskAttempt++) {
        Start-Sleep -Seconds 2    # same transient file access as above
        $taskSignature = Get-AuthenticodeSignature -LiteralPath $taskFile
    }
    if ($taskSignature.Status -ne 'Valid' -or -not $taskSignature.SignerCertificate -or
        $taskSignature.SignerCertificate.Thumbprint -ne $taskThumbprint -or
        ($taskRequireTimestamp -and -not $taskSignature.TimeStamperCertificate)) { throw ('Signature, publisher or timestamp does not match policy: ' + $taskFile) }
    Write-Output ('Verified Authenticode: ' + $taskFile)
}
