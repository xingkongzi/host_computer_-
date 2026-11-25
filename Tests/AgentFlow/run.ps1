$ErrorActionPreference = 'Stop'
$module = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = Join-Path $PSScriptRoot 'output'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
foreach ($folder in @($output, (Join-Path $output 'host-old'), (Join-Path $output 'host-new'))) { New-Item -ItemType Directory -Path $folder -Force | Out-Null }
$references = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.IO.Compression.dll', '/r:System.IO.Compression.FileSystem.dll')
$contracts = Join-Path $module 'Shared\Contracts.cs'
foreach ($generation in @('old', 'new')) {
    $arguments = @('/nologo', '/utf8output', '/target:winexe', ('/out:' + (Join-Path $output ('host-' + $generation + '\FlowHost.exe'))))
    if ($generation -eq 'new') { $arguments += '/define:NEW_HOST' }
    & $compiler @arguments @references $contracts (Join-Path $PSScriptRoot 'HostFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic host build failed.' }
}
$sources = @((Join-Path $PSScriptRoot 'AgentFlowTests.cs'))
foreach ($folder in @('Shared', 'Agent', 'Installer', 'Publisher')) { $sources += @(Get-ChildItem -LiteralPath (Join-Path $module $folder) -Filter '*.cs' | Select-Object -ExpandProperty FullName) }
$test = Join-Path $output 'AgentFlowTests.exe'
$fingerprints = @{}
foreach ($source in $sources) { $fingerprints[$source] = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
& $compiler /nologo /utf8output /target:exe /out:$test @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Real Agent flow test build failed.' }
foreach ($source in $sources) {
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $fingerprints[$source]) { throw 'Source changed while compiling Agent flow tests; rerun against a stable snapshot.' }
}
$fingerprints | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source-snapshot.json') -Encoding UTF8
& $test
if ($LASTEXITCODE -ne 0) { throw 'Real Agent flow tests failed.' }
