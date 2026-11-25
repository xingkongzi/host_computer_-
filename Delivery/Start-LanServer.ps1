<#
JmdUpdater LAN publishing endpoint. Starts one hidden, read-only server process.
Firewall installation is explicit and restricted to the configured profiles and LAN CIDRs.
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$UpdaterPath,
    [switch]$InstallFirewall,
    [switch]$UpdateExistingFirewall
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Server-Lifecycle.ps1')
$taskModuleRoot = Split-Path -Parent $PSScriptRoot
if (-not $ConfigPath) { $ConfigPath = Join-Path $taskModuleRoot 'deployment\server.json' }
$taskUseStagedBinary = -not $UpdaterPath
$taskBuiltUpdater = Join-Path $taskModuleRoot 'bin\JmdUpdater.exe'
$ConfigPath = [System.IO.Path]::GetFullPath($ConfigPath)
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) { throw 'Create server.json before starting the LAN server.' }
$taskConfigHash = (Get-FileHash -LiteralPath $ConfigPath -Algorithm SHA256).Hash
$taskConfig = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskDeploymentRoot = Split-Path -Parent $ConfigPath
if ($taskUseStagedBinary) { $UpdaterPath = Join-Path $taskDeploymentRoot 'server-bin\JmdUpdater.exe' }
$UpdaterPath = [System.IO.Path]::GetFullPath($UpdaterPath)
if ($taskUseStagedBinary -and -not (Test-Path -LiteralPath $taskBuiltUpdater -PathType Leaf)) { throw 'Build JmdUpdater before starting the LAN server.' }
if (-not $taskUseStagedBinary -and -not (Test-Path -LiteralPath $UpdaterPath -PathType Leaf)) { throw 'Updater executable was not found.' }
$taskPidPath = Join-Path $taskDeploymentRoot 'server.pid'
$taskLogs = Join-Path $taskDeploymentRoot 'logs'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
$taskAddresses = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | ForEach-Object {
    if ($_.OperationalStatus -eq [System.Net.NetworkInformation.OperationalStatus]::Up) {
        $_.GetIPProperties().UnicastAddresses | ForEach-Object { $_.Address.ToString() }
    }
}
if ($taskConfig.BindAddress -ne '0.0.0.0' -and $taskAddresses -notcontains $taskConfig.BindAddress) {
    throw ('Configured bind address is not active on this computer: ' + $taskConfig.BindAddress)
}
$taskAlreadyRunning = $null
if (Test-Path -LiteralPath $taskPidPath) {
    $taskRecord = Get-Content -LiteralPath $taskPidPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $taskExisting = Get-Process -Id $taskRecord.Pid -ErrorAction SilentlyContinue
    if ($taskExisting -and $taskExisting.StartTime.ToUniversalTime().Ticks -eq $taskRecord.StartUtcTicks -and
        $taskExisting.Path -eq $taskRecord.ExecutablePath) {
        $taskAlreadyRunning = $taskExisting
    }
}
if (-not $taskAlreadyRunning) {
    $taskCheckUpdater = $UpdaterPath
    if ($taskUseStagedBinary) { $taskCheckUpdater = $taskBuiltUpdater }
    Initialize-LanRoot $ConfigPath
    & $taskCheckUpdater serve --config $ConfigPath --check
    if ($LASTEXITCODE -ne 0) { throw 'Server configuration validation failed.' }
}
if (-not $taskAlreadyRunning -and $taskUseStagedBinary) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $UpdaterPath) -Force | Out-Null
    Copy-Item -LiteralPath $taskBuiltUpdater -Destination $UpdaterPath -Force
    if (Test-Path -LiteralPath ($taskBuiltUpdater + '.config')) { Copy-Item -LiteralPath ($taskBuiltUpdater + '.config') -Destination ($UpdaterPath + '.config') -Force }
}
if ($InstallFirewall) {
    $taskIdentity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $taskIdentity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'InstallFirewall requires an elevated PowerShell session.'
    }
    $taskRemoteRanges = @($taskConfig.AllowedCidrs | Where-Object { $_ -notlike '127.*' })
    if ($taskRemoteRanges.Count -eq 0 -or $taskConfig.BindAddress -eq '0.0.0.0') { throw 'An explicit local address and LAN CIDR are required for firewall installation.' }
    foreach ($taskCidr in $taskRemoteRanges) {
        $taskCidrParts = $taskCidr -split '/'
        $taskNetworkAddress = $null
        $taskPrefixLength = 0
        if ($taskCidrParts.Count -ne 2 -or -not [System.Net.IPAddress]::TryParse($taskCidrParts[0], [ref]$taskNetworkAddress) -or
            $taskNetworkAddress.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork -or
            -not [int]::TryParse($taskCidrParts[1], [ref]$taskPrefixLength) -or $taskPrefixLength -lt 1 -or $taskPrefixLength -gt 32) {
            throw ('Firewall remote range must be an explicit IPv4 CIDR without Any: ' + $taskCidr)
        }
    }
    $taskProfiles = @('Private','Domain')
    if ($taskConfig.PSObject.Properties.Name -contains 'FirewallProfiles') { $taskProfiles = @($taskConfig.FirewallProfiles) }
    if ($taskProfiles.Count -eq 0 -or @($taskProfiles | Where-Object { $_ -notin @('Private','Domain','Public') }).Count -ne 0) {
        throw 'FirewallProfiles must explicitly contain Private, Domain and/or Public; Any is not accepted.'
    }
    $taskRuleName = 'JmdUpdater-LAN-' + $taskConfig.Port + '-' + $taskConfig.BindAddress
    $taskExistingRule = Get-NetFirewallRule -Name $taskRuleName -ErrorAction SilentlyContinue
    if ($taskExistingRule) {
        if (-not $UpdateExistingFirewall) { throw ('Firewall rule already exists; inspect it before using UpdateExistingFirewall: ' + $taskRuleName) }
        $taskApplicationFilter = $taskExistingRule | Get-NetFirewallApplicationFilter
        if ($taskApplicationFilter.Program -ne $UpdaterPath) { throw 'Existing rule targets another program and was not changed.' }
        Set-NetFirewallRule -Name $taskRuleName -Enabled True -Profile $taskProfiles -Direction Inbound -Action Allow | Out-Null
        $taskExistingRule | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -LocalAddress $taskConfig.BindAddress -RemoteAddress $taskRemoteRanges | Out-Null
        $taskExistingRule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $taskConfig.Port | Out-Null
    } else {
        New-NetFirewallRule -Name $taskRuleName -DisplayName ('JMD LAN updater ' + $taskConfig.Port) -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $taskConfig.Port -LocalAddress $taskConfig.BindAddress -RemoteAddress $taskRemoteRanges `
            -Program $UpdaterPath -Profile $taskProfiles | Out-Null
    }
}
if ($taskAlreadyRunning) {
    Write-Output ('Server already running. PID=' + $taskAlreadyRunning.Id)
    exit 0
}
# WMI provider creates the service outside the caller's process tree/job.
$taskCommand = '"' + $UpdaterPath + '" serve --config "' + $ConfigPath + '" --stdout "' + (Join-Path $taskLogs 'server.stdout.log') + '" --stderr "' + (Join-Path $taskLogs 'server.stderr.log') + '"'
$taskStartup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ShowWindow=[uint16]0}
$taskCreated = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine=$taskCommand; CurrentDirectory=$taskModuleRoot; ProcessStartupInformation=$taskStartup
}
if ($taskCreated.ReturnValue -ne 0) { throw ('Detached server creation failed: ' + $taskCreated.ReturnValue) }
$taskServer = Get-Process -Id $taskCreated.ProcessId -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 750
if (-not $taskServer -or $taskServer.HasExited) {
    throw ('LAN server exited: ' + (Get-Content -LiteralPath (Join-Path $taskLogs 'server.stderr.log') -Raw -ErrorAction SilentlyContinue))
}
$taskRecord = [ordered]@{ Pid=$taskServer.Id; StartUtcTicks=$taskServer.StartTime.ToUniversalTime().Ticks;
    ExecutablePath=$UpdaterPath; ConfigPath=$ConfigPath; ConfigSha256=$taskConfigHash; BindAddress=$taskConfig.BindAddress; Port=$taskConfig.Port }
[System.IO.File]::WriteAllText($taskPidPath, ($taskRecord | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
try {
    Test-LanHealth $ConfigPath
    Save-LanLastGood $ConfigPath $UpdaterPath
} catch {
    & (Join-Path $PSScriptRoot 'Stop-LanServer.ps1') -ConfigPath $ConfigPath | Out-Null
    throw
}
Write-Output ('Server started. PID=' + $taskServer.Id + ' URL=http://' + $taskConfig.BindAddress + ':' + $taskConfig.Port + '/')
