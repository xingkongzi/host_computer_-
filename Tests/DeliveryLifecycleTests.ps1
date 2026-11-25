# JmdUpdater: isolated mirror and real-process restart/rollback regression checks.
$ErrorActionPreference = 'Stop'
$delivery = Join-Path (Split-Path $PSScriptRoot -Parent) 'Delivery'
. (Join-Path $delivery 'Server-Lifecycle.ps1')
. (Join-Path $delivery 'Mirror-Release.ps1')
$testRoot = Join-Path $env:TEMP ('JmdLifecycle-' + [Guid]::NewGuid().ToString('N'))
$configPath = Join-Path $testRoot 'server.json'
$updater = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\JmdUpdater.exe'
$count = 0
function Assert($value, $message) { if (-not $value) { throw $message }; $script:count++ }
function Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $value = $listener.LocalEndpoint.Port; $listener.Stop(); return $value
}
function Write-Config($value) { [IO.File]::WriteAllText($configPath, ($value | ConvertTo-Json -Depth 10)) }
function Record { Get-Content -LiteralPath (Join-Path $testRoot 'server.pid') -Raw | ConvertFrom-Json }
function Latest($directory, $version) {
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(('{"Version":"' + $version + '"}')))
    [IO.File]::WriteAllText((Join-Path $directory 'latest.json'), ('{"Payload":"' + $payload + '"}'))
}
function Refused([scriptblock]$action, $message) {
    $failed = $false
    try { & $action } catch { $failed = $true }
    Assert $failed $message
}
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    $config = @{ RootDirectory='empty-releases'; BindAddress='127.0.0.1'; Port=(Port); AllowedCidrs=@('127.0.0.0/8') }
    Write-Config $config
    & $updater serve --config $configPath --check
    Assert ($LASTEXITCODE -ne 0 -and -not (Test-Path (Join-Path $testRoot 'empty-releases'))) '--check created missing root'
    # The temporary service must survive destruction of its publishing parent tree.
    . (Join-Path $delivery 'Publish-ProcessTree.ps1')
    $parentScript=Join-Path $testRoot 'parent.ps1'
    $parentBody="& '"+(Join-Path $delivery 'Start-LanServer.ps1').Replace("'","''")+"' -ConfigPath '"+$configPath.Replace("'","''")+"' -UpdaterPath '"+$updater.Replace("'","''")+"'`r`nStart-Sleep -Seconds 60"
    [IO.File]::WriteAllText($parentScript,$parentBody,[Text.Encoding]::UTF8)
    $parent=Start-Process (Join-Path $PSHOME 'powershell.exe') -ArgumentList ('-NoProfile -File "'+$parentScript+'"') -WindowStyle Hidden -PassThru
    try {
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path (Join-Path $testRoot 'server.pid')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Assert (Test-Path (Join-Path $testRoot 'server.pid')) 'Detached startup did not record PID'
        $independent=Record
        $descendants=@([PublishProcessTree]::Descendants($parent.Id))
        Assert ($descendants -notcontains $independent.Pid) 'Service remains in publishing tree'
        foreach ($ownedId in $descendants+@($parent.Id)) { Stop-Process -Id $ownedId -Force -ErrorAction SilentlyContinue }
        Test-LanHealth $configPath
        Assert ((Get-Process -Id $independent.Pid).StartTime.ToUniversalTime().Ticks -eq $independent.StartUtcTicks) 'Service died with parent'
        Assert ($independent.ConfigSha256 -eq (Get-FileHash $configPath).Hash -and $independent.ExecutablePath -eq $updater) 'Detached identity mismatch'
    } finally { if (-not $parent.HasExited) { $parent.Kill() }; $parent.Dispose() }
    & (Join-Path $delivery 'Stop-LanServer.ps1') -ConfigPath $configPath
    & (Join-Path $delivery 'Start-LanServer.ps1') -ConfigPath $configPath -UpdaterPath $updater
    Assert (Test-Path (Join-Path $testRoot 'empty-releases')) 'First Start did not create release root'
    Test-LanHealth $configPath
    $external = Record
    # Even identical staged bytes must migrate a process running from a different path.
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'server-bin') | Out-Null
    Copy-Item $updater (Join-Path $testRoot 'server-bin\JmdUpdater.exe')
    Restart-ChangedServer $configPath $updater
    Assert ((Record).Pid -ne $external.Pid -and (Record).ExecutablePath -eq (Join-Path $testRoot 'server-bin\JmdUpdater.exe')) 'External running path was treated as unchanged'
    & (Join-Path $delivery 'Stop-LanServer.ps1') -ConfigPath $configPath
    # No process and no snapshot: Restart is also a valid first-deployment entry point.
    $pointer = Join-Path $testRoot 'last-good.json'
    Remove-Item -LiteralPath $pointer
    $config.RootDirectory = 'restart-empty'
    Write-Config $config
    Restart-ChangedServer $configPath $updater
    Assert (Test-Path (Join-Path $testRoot 'restart-empty')) 'First Restart did not create root'
    Test-LanHealth $configPath
    $root = Join-Path $testRoot 'releases'
    $app = Join-Path $root 'App'
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    foreach ($name in @('App-1.0.0.0.zip', 'App-1.0.0.0.release.json', 'latest.json')) {
        [IO.File]::WriteAllBytes((Join-Path $app $name), [Text.Encoding]::UTF8.GetBytes('signed-original-' + $name))
    }
    Latest $app '1.0.0.0'
    $mirror = Join-Path $root 'test\App'
    New-Item -ItemType Directory -Path $mirror -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $app 'App-1.0.0.0.zip') -Destination $mirror
    Copy-OfficialReleaseToTest $root 'App' ([Version]'1.0.0.0')
    Copy-OfficialReleaseToTest $root 'App' ([Version]'1.0.0.0')
    foreach ($name in @('App-1.0.0.0.zip', 'App-1.0.0.0.release.json', 'latest.json')) {
        Assert ((Get-FileHash -LiteralPath (Join-Path $app $name)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $mirror $name)).Hash) 'Mirror bytes differ'
    }
    Latest $mirror '1.0.0.1'
    $newer = (Get-FileHash (Join-Path $mirror 'latest.json')).Hash
    Copy-OfficialReleaseToTest $root 'App' ([Version]'1.0.0.0')
    Assert ((Get-FileHash (Join-Path $mirror 'latest.json')).Hash -eq $newer) 'Stale retry downgraded latest'
    Latest $mirror '1.0.0.0'
    [IO.File]::AppendAllText((Join-Path $mirror 'latest.json'), ' ')
    Refused { Copy-OfficialReleaseToTest $root 'App' ([Version]'1.0.0.0') } 'Equal-version different bytes accepted'
    Copy-Item (Join-Path $app 'latest.json') (Join-Path $mirror 'latest.json') -Force
    foreach ($product in @($app, $mirror)) {
        $lock = [IO.File]::Open((Join-Path $product '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        try { Refused { Copy-OfficialReleaseToTest $root 'App' ([Version]'1.0.0.0') } 'Publisher lock ignored' } finally { $lock.Dispose() }
    }
    $config = @{ RootDirectory=$root; BindAddress='127.0.0.1'; Port=(Port); AllowedCidrs=@('127.0.0.0/8') }
    Write-Config $config
    Restart-ChangedServer $configPath $updater
    $old = Record
    Restart-ChangedServer $configPath $updater
    Assert ((Record).Pid -eq $old.Pid) 'Unchanged server restarted'
    $config.ClientChannels = @{ '127.0.0.1'='official' }
    Write-Config $config
    $rejected = $false
    try { Restart-ChangedServer $configPath $updater } catch { $rejected = $true }
    Assert $rejected 'Invalid candidate was accepted'
    Assert ((Record).Pid -eq $old.Pid) 'Invalid candidate stopped old server'
    Test-LanHealth (Get-LanLastGood $testRoot).Config
    $config.Remove('ClientChannels')
    # Valid configuration but rejects local HTTP: startup must fail health and restore old config/binary.
    $config.Port = Port
    $config.AllowedCidrs = @('192.0.2.0/24')
    Write-Config $config
    $rejected = $false
    try { Restart-ChangedServer $configPath $updater } catch { $rejected = $true; Write-Host $_.Exception.Message }
    Assert $rejected 'Health failure not reported'
    Assert ((Record).Port -eq $old.Port) 'Old endpoint not restored'
    Assert ((Get-FileHash -LiteralPath (Record).ExecutablePath).Hash -eq (Get-FileHash -LiteralPath $updater).Hash) 'Old binary not restored'
    Test-LanHealth $configPath
    # Fail after the new EXE was copied but before its config; committed bytes must survive.
    $before = Get-LanLastGood $testRoot
    $pointerHash = (Get-FileHash $pointer).Hash
    $oldExeHash = (Get-FileHash $before.Exe).Hash
    $oldConfigHash = (Get-FileHash $before.Config).Hash
    $fixture = Join-Path $testRoot 'candidate.exe'
    Copy-Item $updater $fixture
    [IO.File]::AppendAllText($fixture, 'different bytes')
    [IO.File]::WriteAllText(($fixture + '.config'), '<configuration/>')
    function Copy-Item {
        param($LiteralPath, $Destination)
        if ($LiteralPath -eq ($fixture + '.config')) { throw 'Injected snapshot config write failure' }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    try { Refused { Save-LanLastGood $configPath $fixture } 'Snapshot failure not raised' }
    finally { Remove-Item Function:\Copy-Item }
    Assert ((Get-FileHash $pointer).Hash -eq $pointerHash) 'Failed save replaced pointer'
    Assert ((Get-FileHash $before.Exe).Hash -eq $oldExeHash -and (Get-FileHash $before.Config).Hash -eq $oldConfigHash) 'Failed save damaged committed generation'
    # A candidate that passes check but crashes on serve exercises startup rollback independently of HTTP denial.
    $brokenSource = Join-Path $testRoot 'Broken.cs'
    $brokenExe = Join-Path $testRoot 'Broken.exe'
    [IO.File]::WriteAllText($brokenSource, 'class Broken { static int Main(string[] args) { return System.Array.IndexOf(args, "--check") >= 0 ? 0 : 7; } }')
    & (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe') /nologo /target:exe /out:$brokenExe $brokenSource
    if ($LASTEXITCODE -ne 0) { throw 'Broken candidate fixture compilation failed' }
    $rejected = $false
    try { Restart-ChangedServer $configPath $brokenExe } catch { $rejected = $true }
    Assert $rejected 'Crashing binary not reported'
    Assert ((Get-FileHash -LiteralPath (Record).ExecutablePath).Hash -eq (Get-FileHash -LiteralPath $updater).Hash) 'Crash rollback binary mismatch'
    Test-LanHealth $configPath
    Assert (Test-Path -LiteralPath $before.Exe) 'Previous generation was removed'
    # With no new pointer, the original snapshot layout must still restore a failed startup.
    $good = Get-LanLastGood $testRoot
    $legacy = Join-Path $testRoot 'server-bin.last-good'
    New-Item -ItemType Directory -Path $legacy | Out-Null
    Copy-Item $good.Exe (Join-Path $legacy 'JmdUpdater.exe')
    Copy-Item $good.Config (Join-Path $testRoot 'server.last-good.json')
    Remove-Item -LiteralPath $pointer
    Refused { Restart-ChangedServer $configPath $brokenExe } 'Legacy rollback failure not reported'
    Test-LanHealth $configPath
    Assert ((Get-FileHash (Record).ExecutablePath).Hash -eq $oldExeHash) 'Legacy rollback bytes differ'
    # Compare the running .config too, including presence changes and changed bytes.
    $configured = Join-Path $testRoot 'configured.exe'
    Copy-Item $updater $configured
    [IO.File]::WriteAllText(($configured + '.config'), '<configuration/>')
    $prior = Record
    Restart-ChangedServer $configPath $configured
    Assert ((Record).Pid -ne $prior.Pid) 'Added runtime config did not restart'
    $prior = Record
    [IO.File]::WriteAllText(($configured + '.config'), '<configuration> </configuration>')
    Restart-ChangedServer $configPath $configured
    Assert ((Record).Pid -ne $prior.Pid) 'Changed runtime config did not restart'
    $prior = Record
    Restart-ChangedServer $configPath $updater
    Assert ((Record).Pid -ne $prior.Pid -and -not (Test-Path ((Record).ExecutablePath + '.config'))) 'Removed runtime config did not restart'
    # Running process without any snapshot must be retained.
    Remove-Item -LiteralPath $pointer
    Remove-Item -LiteralPath (Join-Path $testRoot 'server.last-good.json')
    $retained = Record
    Refused { Restart-ChangedServer $configPath $brokenExe } 'Running service without snapshot was stopped'
    Assert ((Record).Pid -eq $retained.Pid) 'Snapshot refusal replaced process'
    Save-LanLastGood $configPath (Record).ExecutablePath
    # Changed port/root must take effect before publication; root path is intentionally relative.
    $config.AllowedCidrs = @('127.0.0.0/8'); $config.RootDirectory = 'other-releases'; $config.Port = Port
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'other-releases') | Out-Null
    Write-Config $config
    Restart-ChangedServer $configPath $updater
    Assert ((Record).Port -eq $config.Port) 'Changed port not active'
    Test-LanHealth $configPath
    Write-Host "PASS: $count mirror/lifecycle assertions"
} finally {
    if (Test-Path -LiteralPath (Join-Path $testRoot 'server.pid')) { & (Join-Path $delivery 'Stop-LanServer.ps1') -ConfigPath $configPath }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
