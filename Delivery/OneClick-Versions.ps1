# JmdUpdater: pure channel version discovery and validation; no deployment defaults or writes.
function Get-PublishedVersion([string]$oneRoot, [string]$oneAppId) {
    $oneLatest = Join-Path (Join-Path $oneRoot $oneAppId) 'latest.json'
    if (-not (Test-Path -LiteralPath $oneLatest)) { return $null }
    $oneEnvelope = Get-Content -LiteralPath $oneLatest -Raw -Encoding UTF8 | ConvertFrom-Json
    $onePayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($oneEnvelope.Payload)) | ConvertFrom-Json
    return ConvertTo-FourPart ([Version]$onePayload.Version)
}

function ConvertTo-FourPart([Version]$oneVersion) {
    # 与 ManifestIO.NormalizedVersion 一致：缺的段补 0，所以 1.17.50 和 1.17.50.0 是同一个版本。
    if ($null -eq $oneVersion) { return $null }
    return [Version]::new($oneVersion.Major, $oneVersion.Minor, [Math]::Max($oneVersion.Build, 0), [Math]::Max($oneVersion.Revision, 0))
}

function Get-UsedVersions([string]$oneRoot, [string]$oneAppId) {
    # 每次发布都会留下 <AppId>-<版本>.release.json；即使上次发布中途失败，这个版本号也已占用，不能再用。
    $oneDirectory = Join-Path $oneRoot $oneAppId
    if (-not (Test-Path -LiteralPath $oneDirectory)) { return @() }
    $onePrefix = $oneAppId + '-'
    $oneVersions = @(Get-ChildItem -LiteralPath $oneDirectory -Filter ($onePrefix + '*.release.json') | ForEach-Object {
        $oneParsed = $null
        if ([Version]::TryParse($_.Name.Substring($onePrefix.Length, $_.Name.Length - $onePrefix.Length - '.release.json'.Length), [ref]$oneParsed)) { ConvertTo-FourPart $oneParsed }
    })
    $oneLatestVersion = Get-PublishedVersion $oneRoot $oneAppId
    if ($oneLatestVersion) { $oneVersions += $oneLatestVersion }
    return @($oneVersions | Sort-Object -Unique)
}

function Get-NextVersion([string]$oneName, [Version]$oneBase) {
    # Monitor 习惯递增第三段（1.17.49.0 -> 1.17.50.0），Manager 递增第四段（1.1.1.128 -> 1.1.1.129）。
    if ($oneName -eq 'Monitor') { return [Version]::new($oneBase.Major, $oneBase.Minor, $oneBase.Build + 1, 0) }
    return [Version]::new($oneBase.Major, $oneBase.Minor, $oneBase.Build, $oneBase.Revision + 1)
}

# Test builds share the official three-part base; reject stale/future test branches.
function Get-TestVersion([Version]$oneOfficial, [Version[]]$oneUsed) {
    if (-not $oneOfficial) { throw 'An official base version is required.' }
    $oneOfficial = ConvertTo-FourPart $oneOfficial
    $oneRevision = $oneOfficial.Revision
    foreach ($oneItem in $oneUsed) {
        if ($oneItem.Major -eq $oneOfficial.Major -and $oneItem.Minor -eq $oneOfficial.Minor -and $oneItem.Build -eq $oneOfficial.Build) {
            $oneRevision = [Math]::Max($oneRevision, $oneItem.Revision)
        }
    }
    $oneNext = [Version]::new($oneOfficial.Major, $oneOfficial.Minor, $oneOfficial.Build, $oneRevision + 1)
    Assert-ChannelVersion $oneNext $oneUsed $null
    return $oneNext
}

# Inputs are channel-local history (including latest); no version may be reused or move backward.
function Assert-ChannelVersion([Version]$oneTarget, [Version[]]$oneUsed, [Version]$oneOfficial) {
    foreach ($oneItem in $oneUsed) {
        if ($oneTarget -le $oneItem) { throw ('Version must advance channel history: ' + $oneItem) }
    }
    if ($oneOfficial -and ($oneTarget.Major -ne $oneOfficial.Major -or $oneTarget.Minor -ne $oneOfficial.Minor -or
        $oneTarget.Build -ne $oneOfficial.Build -or $oneTarget -le $oneOfficial)) {
        throw 'Test version must advance official within the same three-part base.'
    }
}

# Preserve source-ahead defaults, but never propose a Monitor test revision as official.
function Get-OfficialVersion([string]$oneName, [Version]$oneSource, [Version[]]$oneUsed) {
    $oneHighest = $oneUsed | Sort-Object -Descending | Select-Object -First 1
    if (-not $oneHighest -or $oneSource -gt $oneHighest) { $oneNext = ConvertTo-FourPart $oneSource }
    else { $oneNext = Get-NextVersion $oneName $oneHighest }
    if ($oneName -eq 'Monitor' -and $oneNext.Revision -ne 0) { $oneNext = Get-NextVersion $oneName $oneNext }
    return $oneNext
}

function Get-OneClickSuggestedVersion([string]$Name, [string]$Channel, [Version]$Source, [Version]$Official, [Version[]]$Used) {
    if ($Channel -eq 'test') { return Get-TestVersion $Official $Used }
    return Get-OfficialVersion $Name $Source $Used
}
