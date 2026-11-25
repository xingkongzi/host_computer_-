# Optional publishing-machine startup only. Clients do not get a startup item.
[CmdletBinding()]
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$taskStartup = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
$taskShortcut = Join-Path $taskStartup 'JMD LAN Update Server.lnk'
if ($Remove) {
    if (Test-Path -LiteralPath $taskShortcut) { Remove-Item -LiteralPath $taskShortcut }
    Write-Output 'Publishing server login startup removed.'
    exit 0
}
$taskStartScript = Join-Path $PSScriptRoot 'Start-LanServer.ps1'
$taskConfig = Join-Path (Split-Path $PSScriptRoot -Parent) 'deployment\server.json'
if (!(Test-Path -LiteralPath $taskConfig)) { throw 'Configure and test the publishing server before installing login startup.' }
if (Test-Path -LiteralPath $taskShortcut) { throw 'Publishing startup item already exists; inspect it before replacing.' }
$taskShell = New-Object -ComObject WScript.Shell
$taskLink = $taskShell.CreateShortcut($taskShortcut)
$taskLink.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$taskLink.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $taskStartScript + '"'
$taskLink.WorkingDirectory = Split-Path $PSScriptRoot -Parent
$taskLink.WindowStyle = 7
$taskLink.Description = 'Starts the configured, subnet-restricted JMD LAN publishing endpoint after this user logs in.'
$taskLink.Save()
Write-Output $taskShortcut
