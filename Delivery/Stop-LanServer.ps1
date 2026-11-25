# Stops only the exact server process recorded by Start-LanServer.ps1.
[CmdletBinding()]
param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
if (-not $ConfigPath) { $ConfigPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'deployment\server.json' }
$taskPidPath = Join-Path (Split-Path -Parent ([System.IO.Path]::GetFullPath($ConfigPath))) 'server.pid'
if (-not (Test-Path -LiteralPath $taskPidPath)) { Write-Output 'No recorded LAN server.'; exit 0 }
$taskRecord = Get-Content -LiteralPath $taskPidPath -Raw -Encoding UTF8 | ConvertFrom-Json
$taskServer = Get-Process -Id $taskRecord.Pid -ErrorAction SilentlyContinue
if ($taskServer) {
    if ($taskServer.StartTime.ToUniversalTime().Ticks -ne $taskRecord.StartUtcTicks -or $taskServer.Path -ne $taskRecord.ExecutablePath) {
        throw 'Recorded PID now belongs to another process. It was not stopped.'
    }
    # Process.Kill on the identity-checked object: Stop-Process -Id has thrown NullReferenceException on
    # this hidden, redirected server process under Windows PowerShell 5.1.
    $taskServer.Kill()
    if (-not $taskServer.WaitForExit(10000)) { throw 'LAN server did not stop within 10 seconds.' }
}
Remove-Item -LiteralPath $taskPidPath
Write-Output 'LAN server stopped.'
