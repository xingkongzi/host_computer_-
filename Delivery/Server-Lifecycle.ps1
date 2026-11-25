# JmdUpdater: transactional server restart. Explicit paths also support isolated loopback tests.
# The last-good configuration uses an absolute root, independent of snapshot filename.
function Test-LanHealth([string]$ConfigPath) {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $address = $config.BindAddress
    if ($address -eq '0.0.0.0') { $address = '127.0.0.1' }
    # A known missing path exercises HTTP parsing, routing and CIDR checks without publishing a probe file.
    $url = 'http://' + $address + ':' + $config.Port + '/official/__jmd_health__/missing.json'
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $request = [Net.HttpWebRequest]::Create($url); $request.Proxy = $null; $request.Timeout = 500
        try { $response = $request.GetResponse(); $response.Close() }
        catch [Net.WebException] {
            $response = $_.Exception.Response
            if ($response) {
                $status = [int]$response.StatusCode; $response.Close()
                if ($status -eq 404) { return }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw ('Server HTTP health check failed: ' + $url)
}

# Resolve only a committed generation; legacy snapshots are used only without a pointer.
# Input: configuration directory. Output: validated snapshot paths, or null when none exists.
function Get-LanLastGood([string]$Directory) {
    $pointer = Join-Path $Directory 'last-good.json'
    if (Test-Path -LiteralPath $pointer) {
        $current = Get-Content -LiteralPath $pointer -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($current.Generation -notmatch '\A[0-9]{17}-[a-f0-9]{32}\z') { throw 'Invalid last-good generation' }
        $generation = Join-Path (Join-Path $Directory 'last-good') $current.Generation
        $hashes = Get-Content -LiteralPath (Join-Path $generation 'hashes.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($name in @('JmdUpdater.exe', 'server.json')) {
            if (-not $hashes.PSObject.Properties[$name]) { throw 'Incomplete last-good hashes' }
        }
        foreach ($entry in $hashes.PSObject.Properties) {
            if ($entry.Name -notin @('JmdUpdater.exe','JmdUpdater.exe.config','server.json')) { throw 'Invalid snapshot file' }
            if ((Get-FileHash -LiteralPath (Join-Path $generation $entry.Name)).Hash -ne $entry.Value) { throw 'Last-good hash mismatch' }
        }
        if ((Test-Path -LiteralPath (Join-Path $generation 'JmdUpdater.exe.config')) -ne [bool]$hashes.PSObject.Properties['JmdUpdater.exe.config']) { throw 'Unexpected snapshot config' }
        return @{ Config=(Join-Path $generation 'server.json'); Exe=(Join-Path $generation 'JmdUpdater.exe') }
    }
    $config = Join-Path $Directory 'server.last-good.json'
    $exe = Join-Path $Directory 'server-bin.last-good\JmdUpdater.exe'
    if ((Test-Path -LiteralPath $config) -and (Test-Path -LiteralPath $exe)) { return @{ Config=$config; Exe=$exe } }
    return $null
}

# Snapshot after HTTP health succeeds. Publish the pointer only after every byte is verified.
# Old generations are deliberately retained, including the previous committed generation.
function Save-LanLastGood([string]$ConfigPath, [string]$UpdaterPath) {
    $directory = Split-Path -Parent $ConfigPath
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not [IO.Path]::IsPathRooted($config.RootDirectory)) { $config.RootDirectory = [IO.Path]::GetFullPath((Join-Path $directory $config.RootDirectory)) }
    $name = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N')
    $generation = Join-Path (Join-Path $directory 'last-good') $name
    $pointer = Join-Path $directory 'last-good.json'
    $temporary = $pointer + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    New-Item -ItemType Directory -Path $generation -Force | Out-Null
    try {
        $hashes = [ordered]@{}
        foreach ($suffix in @('', '.config')) {
            $source = $UpdaterPath + $suffix
            if ($suffix -and -not (Test-Path -LiteralPath $source)) { continue }
            $file = 'JmdUpdater.exe' + $suffix
            $expected = (Get-FileHash -LiteralPath $source).Hash
            Copy-Item -LiteralPath $source -Destination (Join-Path $generation $file)
            if ((Get-FileHash -LiteralPath (Join-Path $generation $file)).Hash -ne $expected) { throw 'Snapshot copy hash mismatch' }
            $hashes[$file] = $expected
        }
        $text = $config | ConvertTo-Json -Depth 20
        $encoding = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText((Join-Path $generation 'server.json'), $text, $encoding)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $expected = [BitConverter]::ToString($sha.ComputeHash($encoding.GetBytes($text))).Replace('-', '') } finally { $sha.Dispose() }
        if ((Get-FileHash -LiteralPath (Join-Path $generation 'server.json')).Hash -ne $expected) { throw 'Snapshot JSON hash mismatch' }
        $hashes['server.json'] = $expected
        [IO.File]::WriteAllText((Join-Path $generation 'hashes.json'), ($hashes | ConvertTo-Json), $encoding)
        [IO.File]::WriteAllText($temporary, (@{ Generation=$name } | ConvertTo-Json), $encoding)
        if (Test-Path -LiteralPath $pointer) { [IO.File]::Replace($temporary, $pointer, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $pointer) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}

# Explicit startup may create the release root; --check itself remains read-only.
function Initialize-LanRoot([string]$ConfigPath) {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $root = $config.RootDirectory
    if (-not [IO.Path]::IsPathRooted($root)) { $root = Join-Path (Split-Path -Parent $ConfigPath) $root }
    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root -Force | Out-Null }
}

# Validate candidate before stopping; restore the last healthy binary/config on startup failure and throw.
function Restart-ChangedServer([string]$ConfigPath, [string]$CandidateUpdater) {
    $ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    $directory = Split-Path -Parent $ConfigPath
    $pidFile = Join-Path $directory 'server.pid'
    $record = $null; $running = $false
    if (Test-Path -LiteralPath $pidFile) {
        $record = Get-Content -LiteralPath $pidFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $process = Get-Process -Id $record.Pid -ErrorAction SilentlyContinue
        $running = $process -and $process.StartTime.ToUniversalTime().Ticks -eq $record.StartUtcTicks -and $process.Path -eq $record.ExecutablePath
    }
    $active = Join-Path $directory 'server-bin\JmdUpdater.exe'
    $same = $running -and $record.ConfigSha256 -eq (Get-FileHash -LiteralPath $ConfigPath).Hash -and
        $record.ExecutablePath -eq $active -and (Get-FileHash -LiteralPath $record.ExecutablePath).Hash -eq (Get-FileHash -LiteralPath $CandidateUpdater).Hash
    $candidateConfig = $CandidateUpdater + '.config'; $activeConfig = $active + '.config'
    $runningConfig = if ($running) { $record.ExecutablePath + '.config' } else { $activeConfig }
    if ((Test-Path -LiteralPath $candidateConfig) -ne (Test-Path -LiteralPath $runningConfig)) { $same = $false }
    elseif ((Test-Path -LiteralPath $candidateConfig) -and (Get-FileHash -LiteralPath $candidateConfig).Hash -ne (Get-FileHash -LiteralPath $runningConfig).Hash) { $same = $false }
    if ($same) { return }
    if (-not $running) { Initialize-LanRoot $ConfigPath }
    & $CandidateUpdater serve --config $ConfigPath --check
    if ($LASTEXITCODE -ne 0) { throw 'Candidate configuration rejected; existing server retained.' }
    $snapshot = Get-LanLastGood $directory
    if ($running -and -not $snapshot) {
        throw 'No last-good snapshot; restart refused before stopping the running server.'
    }
    try {
        & (Join-Path $PSScriptRoot 'Stop-LanServer.ps1') -ConfigPath $ConfigPath
        New-Item -ItemType Directory -Path (Split-Path -Parent $active) -Force | Out-Null
        Copy-Item -LiteralPath $CandidateUpdater -Destination $active -Force
        if (Test-Path -LiteralPath $candidateConfig) { Copy-Item -LiteralPath $candidateConfig -Destination $activeConfig -Force }
        elseif (Test-Path -LiteralPath $activeConfig) { Remove-Item -LiteralPath $activeConfig }
        & (Join-Path $PSScriptRoot 'Start-LanServer.ps1') -ConfigPath $ConfigPath -UpdaterPath $active
    } catch {
        $failure = $_
        & (Join-Path $PSScriptRoot 'Stop-LanServer.ps1') -ConfigPath $ConfigPath
        if ($snapshot) {
            $goodConfig = $snapshot.Config; $goodExe = $snapshot.Exe
            Copy-Item -LiteralPath $ConfigPath -Destination (Join-Path $directory 'server.failed.json') -Force
            Copy-Item -LiteralPath $goodConfig -Destination $ConfigPath -Force
            Copy-Item -LiteralPath $goodExe -Destination $active -Force
            if (Test-Path -LiteralPath ($goodExe + '.config')) { Copy-Item -LiteralPath ($goodExe + '.config') -Destination $activeConfig -Force }
            elseif (Test-Path -LiteralPath $activeConfig) { Remove-Item -LiteralPath $activeConfig }
            & (Join-Path $PSScriptRoot 'Start-LanServer.ps1') -ConfigPath $ConfigPath -UpdaterPath $active
            throw ('Candidate startup failed; last-good service restored. ' + $failure.Exception.Message)
        }
        throw $failure
    }
}
