# JmdUpdater: copy signed official bytes; publish latest last so interrupted copies are retryable.
# Inputs are an explicit release root, product id and version. No deployment defaults.
. (Join-Path $PSScriptRoot 'OneClick-Versions.ps1')
function Copy-OfficialReleaseToTest([string]$ReleaseRoot, [string]$AppId, [Version]$Version) {
    if ($AppId -notmatch '\A[A-Za-z0-9_-]+\z') { throw 'Invalid AppId' }
    $source = Join-Path $ReleaseRoot $AppId
    $target = Join-Path (Join-Path $ReleaseRoot 'test') $AppId
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    $sourceLock = $null; $targetLock = $null
    try {
        # Same product lock and sharing mode as Publisher; hold both while reading/copying.
        $sourceLock = [IO.File]::Open((Join-Path $source '.publish.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $targetLock = [IO.File]::Open((Join-Path $target '.publish.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $normalized = ConvertTo-FourPart $Version
        $current = Get-PublishedVersion (Join-Path $ReleaseRoot 'test') $AppId
        if ($current -and $current -gt $normalized) {
            Write-Output ('Mirror skipped: test latest ' + $current + ' is newer than ' + $Version)
            return
        }
        if ((Get-PublishedVersion $ReleaseRoot $AppId) -ne $normalized) { throw 'Official latest does not match mirror version' }
        if ($current -eq $normalized -and (Get-FileHash -LiteralPath (Join-Path $target 'latest.json')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $source 'latest.json')).Hash) {
            throw 'Equal-version mirror latest bytes differ'
        }
        foreach ($name in @(($AppId + '-' + $Version + '.zip'), ($AppId + '-' + $Version + '.release.json'), 'latest.json')) {
            $from = Join-Path $source $name
            $to = Join-Path $target $name
            $temporary = $to + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            try {
                Copy-Item -LiteralPath $from -Destination $temporary
                if ((Get-FileHash -LiteralPath $temporary).Hash -ne (Get-FileHash -LiteralPath $from).Hash) { throw 'Mirror copy hash mismatch' }
                if ($name -ne 'latest.json' -and (Test-Path -LiteralPath $to)) {
                    if ((Get-FileHash -LiteralPath $to).Hash -ne (Get-FileHash -LiteralPath $temporary).Hash) { throw ('Immutable mirror conflict: ' + $to) }
                } elseif (Test-Path -LiteralPath $to) { [IO.File]::Replace($temporary, $to, [NullString]::Value) }
                else { [IO.File]::Move($temporary, $to) }
            } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        }
    } finally {
        if ($targetLock) { $targetLock.Dispose() }
        if ($sourceLock) { $sourceLock.Dispose() }
    }
}
