<# SynJMD: JSON is data, never PowerShell source. EchoOnly performs no publication. #>
param([Parameter(Mandatory=$true)][string]$RequestFile, [switch]$EchoOnly)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
try {
    $request=[IO.File]::ReadAllText($RequestFile,[Text.Encoding]::UTF8) | ConvertFrom-Json
    $parameters=@{}
    foreach ($name in @('Product','Channel','Version','Commit','Notes','MonitorRepo','ManagerRepo')) {
        $parameters[$name]=[string]$request.$name
    }
    if ($EchoOnly) { $parameters | ConvertTo-Json -Compress; exit 0 }
    $parameters.Yes=$true; $parameters.NoPause=$true
    & (Join-Path $PSScriptRoot 'Publish-OneClick.ps1') @parameters
    exit $LASTEXITCODE
} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
