# SynJMD：将桌面入口设置为隐藏控制台的 WinForms 窗口；不启动发布。
$ErrorActionPreference='Stop'
$shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'SynJMD 一键发布.lnk'
$shell=New-Object -ComObject WScript.Shell
$link=$shell.CreateShortcut($shortcutPath)
$link.TargetPath=Join-Path $PSHOME 'powershell.exe'
$link.Arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "'+(Join-Path $PSScriptRoot 'Publish-Window.ps1')+'"'
$link.WorkingDirectory=$PSScriptRoot
$link.WindowStyle=7
$link.Description='SynJMD 上位机图形化发布：选择产品、通道并确认后发布'
if (-not $link.IconLocation) { $link.IconLocation=(Join-Path $env:WINDIR 'System32\shell32.dll')+',167' }
$link.Save()
Write-Output ('已更新快捷方式：'+$shortcutPath)
