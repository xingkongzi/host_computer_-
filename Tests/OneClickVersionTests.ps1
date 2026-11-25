# JmdUpdater channel version tests: pure functions, synthetic histories, no deployment access.
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'Delivery\OneClick-Versions.ps1')
$testRoot = Join-Path $env:TEMP ('OneClickVersions-' + [Guid]::NewGuid().ToString('N'))
$script:assertions = 0
function Equal($actual, $expected) {
    if ([string]$actual -ne [string]$expected) { throw "Expected $expected; got $actual" }
    $script:assertions++
}
function Refused([scriptblock]$action) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected version refusal' }
    $script:assertions++
}
function Latest([string]$directory, [string]$version) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(('{"Version":"' + $version + '"}')))
    [IO.File]::WriteAllText((Join-Path $directory 'latest.json'), ('{"Payload":"' + $payload + '"}'))
}
try {
    $official = Join-Path $testRoot 'App'
    $testing = Join-Path $testRoot 'test\App'
    Latest $official '1.17.50'
    Latest $testing '1.17.50.2'
    foreach ($version in @('1.17.49.0','1.17.51.0')) { [IO.File]::WriteAllText((Join-Path $official ('App-' + $version + '.release.json')), '{}') }
    foreach ($version in @('1.17.49.9','1.17.50.4')) { [IO.File]::WriteAllText((Join-Path $testing ('App-' + $version + '.release.json')), '{}') }
    Equal (ConvertTo-FourPart ([Version]'1.17')) '1.17.0.0'
    Equal (ConvertTo-FourPart ([Version]'1.17.50')) '1.17.50.0'
    $usedOfficial = @(Get-UsedVersions $testRoot 'App')
    $usedTest = @(Get-UsedVersions (Join-Path $testRoot 'test') 'App')
    Equal $usedOfficial.Count 3
    Equal $usedTest.Count 3
    Equal (Get-UsedVersions $testRoot 'Missing').Count 0
    Equal (Get-OfficialVersion 'Monitor' ([Version]'1.17.50.0') $usedOfficial) '1.17.52.0'
    Equal (Get-OfficialVersion 'Monitor' ([Version]'1.17.53.4') $usedOfficial) '1.17.54.0'
    Equal (Get-OfficialVersion 'Manager' ([Version]'1.1.1.128') @([Version]'1.1.1.128')) '1.1.1.129'
    $combined = @([Version]'1.1.1.128', [Version]'1.1.1.132')
    Equal (Get-OfficialVersion 'Manager' ([Version]'1.1.1.128') $combined) '1.1.1.133'
    Refused { Assert-ChannelVersion ([Version]'1.1.1.132') $combined $null }
    Refused { Assert-ChannelVersion ([Version]'1.1.1.129') $combined $null }
    $base = Get-PublishedVersion $testRoot 'App'
    Equal (Get-TestVersion $base $usedTest) '1.17.50.5'
    Equal (Get-TestVersion $base @()) '1.17.50.1'
    Equal ((Get-NextVersion 'Monitor' $base) -gt (Get-TestVersion $base $usedTest)) True
    Refused { Get-TestVersion $null @() }
    Refused { Get-TestVersion $base @([Version]'1.17.51.1') }
    Refused { Assert-ChannelVersion ([Version]'1.17.50.4') $usedTest $base }
    Refused { Assert-ChannelVersion ([Version]'1.17.50.3') $usedTest $base }
    Refused { Assert-ChannelVersion ([Version]'1.17.51.1') $usedTest $base }
    Refused { Assert-ChannelVersion $base @() $base }
    Refused { Assert-ChannelVersion ([Version]'1.17.51.0') $usedOfficial $null }
    Equal (Get-TestVersion ([Version]'1.1.1.128') @()) '1.1.1.129'
    Write-Host "PASS: $script:assertions channel version assertions"
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
