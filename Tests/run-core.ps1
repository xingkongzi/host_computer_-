$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskOutput = Join-Path $taskRoot 'test-output'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$taskSources = @((Join-Path $taskRoot 'Shared\Contracts.cs'), (Join-Path $taskRoot 'Shared\Support.cs'), (Join-Path $taskRoot 'Agent\HostClient.cs'), (Join-Path $taskRoot 'Agent\Agent.cs'), (Join-Path $taskRoot 'Agent\TransactionCleanup.cs'), (Join-Path $PSScriptRoot 'CoreTests.cs'))
$taskExe = Join-Path $taskOutput 'CoreTests.exe'
& $taskCompiler /nologo /utf8output /target:exe /out:$taskExe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll @taskSources
if ($LASTEXITCODE -ne 0) { throw 'Core tests did not compile.' }
& $taskExe
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
