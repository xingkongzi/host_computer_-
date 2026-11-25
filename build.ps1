# Build the reusable updater without NuGet or a machine-wide SDK installation.
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'), [switch]$Force)
$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskSources = @((Join-Path $PSScriptRoot 'Program.cs'))
foreach ($taskSourceFolder in @('Shared', 'Agent', 'Installer', 'Publisher', 'Delivery')) {
    $taskFolder = Join-Path $PSScriptRoot $taskSourceFolder
    if (Test-Path -LiteralPath $taskFolder) { $taskSources += @(Get-ChildItem -LiteralPath $taskFolder -Filter '*.cs' -Recurse | Select-Object -ExpandProperty FullName) }
}
$taskOutput = Join-Path $OutputDirectory 'JmdUpdater.exe'
# 指纹覆盖编译器、源码和参数；仅成功构建后保存。
$taskOptions = @('/noconfig','/nostdlib+','/nologo','/target:exe','/optimize+','/utf8output','/warn:4',('/out:' + $taskOutput))
$taskFramework = Split-Path -Parent $taskCompiler
$taskReferences = @('mscorlib.dll','System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll',
    'System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll') | ForEach-Object { Join-Path $taskFramework $_ }
$taskOptions += @($taskReferences | ForEach-Object { '/r:' + $_ })
$taskSources = @($taskSources | Sort-Object)
$taskFingerprintText = ($taskOptions -join "`n") + "`n" + $taskCompiler + ":" + (Get-FileHash -LiteralPath $taskCompiler).Hash
foreach ($taskReference in $taskReferences) { $taskFingerprintText += "`n" + $taskReference + ":" + (Get-FileHash -LiteralPath $taskReference).Hash }
foreach ($taskSource in $taskSources) { $taskFingerprintText += "`n" + $taskSource + ':' + (Get-FileHash -LiteralPath $taskSource).Hash }
$taskSha = [Security.Cryptography.SHA256]::Create()
try { $taskFingerprint = [BitConverter]::ToString($taskSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskFingerprintText))).Replace('-', '') }
finally { $taskSha.Dispose() }
$taskStamp = Join-Path $OutputDirectory 'JmdUpdater.build.sha256'
$taskCached = $null
try { $taskCached = [IO.File]::ReadAllText($taskStamp) | ConvertFrom-Json } catch { }
if (-not $Force -and (Test-Path -LiteralPath $taskOutput) -and $taskCached -and
    $taskCached.InputSha256 -eq $taskFingerprint -and $taskCached.OutputSha256 -eq (Get-FileHash -LiteralPath $taskOutput).Hash) {
    Write-Output '源码未变化，沿用现有 JmdUpdater.exe'
    Write-Output $taskOutput
    return
}
if (Test-Path -LiteralPath $taskStamp) { Remove-Item -LiteralPath $taskStamp }
& $taskCompiler @taskOptions @taskSources
if ($LASTEXITCODE -ne 0) { throw "Updater build failed ($LASTEXITCODE)." }
Write-Output $taskOutput

[IO.File]::WriteAllText($taskStamp, (@{InputSha256=$taskFingerprint;OutputSha256=(Get-FileHash -LiteralPath $taskOutput).Hash} | ConvertTo-Json), [Text.Encoding]::ASCII)
