<#
.SYNOPSIS
  把已整理好运行环境的程序目录封装成单个 exe（自解压启动器）。

.EXAMPLE
  .\Build-Portable.ps1 -SourceDir "D:\SynJMD_Monitor_xxx" -EntryExe SynJMD_Monitor.exe `
      -Title "SynJMD Monitor" -Icon ..\..\SynJMD_Monitor\SynJMD_Monitor\resources\logo.ico `
      -Preserve default.txt,parms.db,parms_en.db,*.csv,*.log,*.cuv,data,Logs -OutDir D:\out
#>
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][string]$EntryExe,
    [Parameter(Mandatory = $true)][string]$Title,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Icon,
    [string[]]$Preserve = @(),
    [string[]]$Exclude = @()   # 不打进包的相对路径（个人日志等）
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
if (-not (Test-Path -LiteralPath (Join-Path $SourceDir $EntryExe))) { throw "源目录里没有 $EntryExe" }
$verJson = Get-Content -LiteralPath (Join-Path $SourceDir 'jmd-version.json') -Raw | ConvertFrom-Json
$appId = $verJson.AppId; $version = $verJson.Version
if (-not ($appId -match '^[A-Za-z0-9_.-]+$') -or $appId -match '^\.+$') { throw "jmd-version.json 中 AppId 无效：$appId" }
if (-not ($version -match '^\d+(\.\d+){1,3}$')) { throw "jmd-version.json 中 Version 无效：$version" }
if ($EntryExe -match '[\\/:]|\.\.' -or -not $EntryExe.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) { throw "EntryExe 必须是源目录根下的 exe 文件名" }

# 保留规则 = 源目录 jmd-update.json 的 PreservePaths + 更新器信任配置 + 命令行额外指定
$updCfg = Join-Path $SourceDir 'jmd-update.json'
if (Test-Path -LiteralPath $updCfg) {
    $upd = Get-Content -LiteralPath $updCfg -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($upd.PreservePaths) { $Preserve = @($upd.PreservePaths) + $Preserve }
}
$Preserve = @(@($Preserve) + @('jmd-update.json', 'JmdUpdater/public-key.xml') | Where-Object { $_ } | Select-Object -Unique)
Write-Host ("保留规则：" + ($Preserve -join ', '))

$Exclude = @($Exclude | ForEach-Object { $_.Replace('\', '/').Trim('/') } | Where-Object { $_ })
foreach ($must in @($EntryExe, 'jmd-version.json')) {
    if ($Exclude | Where-Object { $must -ieq $_ }) { throw "不能排除 $must" }
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$fw = Split-Path $csc

$work = Join-Path ([IO.Path]::GetTempPath()) ("jmd-portable-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    # 1) 打包程序目录
    $zipPath = Join-Path $work 'payload.zip'
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try {
        $count = 0
        foreach ($f in Get-ChildItem -LiteralPath $SourceDir -Recurse -File -Force) {
            $rel = $f.FullName.Substring($SourceDir.Length + 1).Replace('\', '/')
            if ($rel -like '*.jmdtmp' -or ($Exclude | Where-Object { $rel -ieq $_ -or $rel.StartsWith($_ + '/', [StringComparison]::OrdinalIgnoreCase) })) { Write-Host "  排除 $rel"; continue }
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel, 'Optimal')
            $count++
        }
    } finally { $zip.Dispose() }
    Write-Host "已打包 $count 个文件"

    # 2) 生成配置并编译
    function Esc([string]$s) { '"' + $s.Replace('\', '\\').Replace('"', '\"') + '"' }
    $patterns = ($Preserve | ForEach-Object { Esc $_ }) -join ', '
    $cfg = @"
internal static class PortableConfig
{
    public const string AppId = $(Esc $appId);
    public const string EntryExe = $(Esc $EntryExe);
    public const string Version = $(Esc $version);
    public const string Title = $(Esc $Title);
    public static readonly string[] PreservePatterns = new string[] { $patterns };
}
"@
    $cfgPath = Join-Path $work 'PortableConfig.cs'
    [IO.File]::WriteAllText($cfgPath, $cfg, (New-Object Text.UTF8Encoding $true))

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $out = Join-Path (Resolve-Path -LiteralPath $OutDir).Path ("{0}_{1}.exe" -f $appId, $version)
    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/codepage:65001',
        "/out:$out", "/resource:$zipPath,payload.zip",
        "/reference:$fw\System.IO.Compression.dll", "/reference:$fw\System.IO.Compression.FileSystem.dll",
        '/reference:System.Windows.Forms.dll')
    if ($Icon) { $cscArgs += "/win32icon:$((Resolve-Path -LiteralPath $Icon).Path)" }
    $cscArgs += @((Join-Path $PSScriptRoot 'PortableLauncher.cs'), $cfgPath)
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc 编译失败" }
    Write-Host ("生成 {0}  ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
    $out
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
