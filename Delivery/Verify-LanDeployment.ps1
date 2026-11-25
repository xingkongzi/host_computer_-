# Reads the actual HTTP release, verifies its signature/inventory and checks bootstrap downloads.
# Never starts or replaces a product; all downloaded/extracted files stay in a unique temporary folder.
[CmdletBinding()]
param([string]$ArtifactManifest, [switch]$CompareWithLocalBinaries)
$ErrorActionPreference = 'Stop'
$taskModuleRoot = Split-Path $PSScriptRoot -Parent
if (!$ArtifactManifest) { $ArtifactManifest = Join-Path $taskModuleRoot 'deployment\artifacts.json' }
[Reflection.Assembly]::LoadFrom((Join-Path $taskModuleRoot 'bin\JmdUpdater.exe')) | Out-Null
$taskTempRoot = Join-Path $env:TEMP ('JmdLiveVerify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskTempRoot | Out-Null
$taskArtifacts = Get-Content -LiteralPath $ArtifactManifest -Raw -Encoding UTF8 | ConvertFrom-Json
function Get-LocalHttpText([string]$taskUrl) {
    $taskRequest = [Net.HttpWebRequest]::Create($taskUrl)
    $taskRequest.Proxy = $null
    $taskRequest.Timeout = 15000
    $taskResponse = $taskRequest.GetResponse()
    try {
        $taskReader = [IO.StreamReader]::new($taskResponse.GetResponseStream(), [Text.Encoding]::UTF8)
        try { $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
    } finally { $taskResponse.Dispose() }
}
$taskResults = @()
foreach ($taskArtifact in $taskArtifacts) {
    if (!$taskArtifact.AppId) { continue }
    $taskArchiveName = [IO.Path]::GetFileName($taskArtifact.Bootstrap)
    if (!$taskArchiveName.EndsWith('_bootstrap.zip')) { throw 'Unexpected bootstrap artifact name.' }
    $taskDirectory = Join-Path ([IO.Path]::GetDirectoryName($taskArtifact.Bootstrap)) $taskArchiveName.Substring(0, $taskArchiveName.Length - '_bootstrap.zip'.Length)
    $taskConfig = [JmdUpdater.ManifestIO]::LoadConfig((Join-Path $taskDirectory 'jmd-update.json'))
    $taskBase = $taskConfig.ServerUrl.TrimEnd('/') + '/' + $taskArtifact.AppId + '/'
    $taskEnvelope = Get-LocalHttpText ($taskBase + 'latest.json')
    $taskRelease = [JmdUpdater.ManifestIO]::Verify($taskEnvelope, $taskConfig.PublicKeyXml)
    if ($taskRelease.AppId -ne $taskArtifact.AppId) { throw 'Live product identity mismatch.' }
    $taskPackage = Join-Path $taskTempRoot ($taskArtifact.AppId + '.zip')
    $taskClient = [Net.WebClient]::new()
    $taskClient.Proxy = $null
    try { $taskClient.DownloadFile(($taskBase + $taskRelease.PackageName), $taskPackage) } finally { $taskClient.Dispose() }
    if ((Get-Item -LiteralPath $taskPackage).Length -ne $taskRelease.PackageSize -or
        [JmdUpdater.ManifestIO]::Sha256File($taskPackage) -ne $taskRelease.PackageSha256) { throw 'Live update package hash/size mismatch.' }
    $taskStage = Join-Path $taskTempRoot ($taskArtifact.AppId + '-stage')
    [JmdUpdater.InstallerService]::ValidatePackage($taskRelease, $taskPackage, $taskStage, $taskConfig)
    if ($CompareWithLocalBinaries) {
        foreach ($taskFile in $taskRelease.Files) {
            # Publisher regenerates version JSON canonically, so compare its identity semantically.
            if ($taskFile.Path -eq 'jmd-version.json') { continue }
            $taskOriginal = [JmdUpdater.UpdatePaths]::UnderRoot($taskDirectory, $taskFile.Path)
            $taskExtracted = [JmdUpdater.UpdatePaths]::UnderRoot($taskStage, $taskFile.Path)
            if ([JmdUpdater.ManifestIO]::Sha256File($taskOriginal) -ne [JmdUpdater.ManifestIO]::Sha256File($taskExtracted)) {
                throw ('Published program file differs from final dist: ' + $taskFile.Path)
            }
        }
        $taskLocalVersion = Get-Content -LiteralPath (Join-Path $taskDirectory 'jmd-version.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($taskLocalVersion.AppId -ne $taskRelease.AppId -or $taskLocalVersion.Version -ne $taskRelease.Version) { throw 'Published version differs from final dist.' }
    }
    $taskMetadata = Get-LocalHttpText ($taskArtifact.Url + '.sha256.json') | ConvertFrom-Json
    $taskBootstrap = Join-Path $taskTempRoot $taskArchiveName
    $taskClient = [Net.WebClient]::new()
    $taskClient.Proxy = $null
    try { $taskClient.DownloadFile($taskArtifact.Url, $taskBootstrap) } finally { $taskClient.Dispose() }
    if ([JmdUpdater.ManifestIO]::Sha256File($taskBootstrap) -ne $taskArtifact.Sha256 -or
        $taskMetadata.Sha256 -ne $taskArtifact.Sha256) { throw 'Bootstrap HTTP hash mismatch.' }
    $taskResults += [ordered]@{
        AppId = $taskRelease.AppId; Version = $taskRelease.Version; Files = $taskRelease.Files.Count
        Signature = 'PASS'; UpdateHash = 'PASS'; InventoryExtraction = 'PASS'; BootstrapHttpHash = 'PASS'
        FinalBinaryCompared = [bool]$CompareWithLocalBinaries
    }
}
$taskResults | ConvertTo-Json -Depth 4
Write-Output ('Verification fixtures: ' + $taskTempRoot)
