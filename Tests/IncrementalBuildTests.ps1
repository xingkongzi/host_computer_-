# SynJMD：临时源码副本验证指纹缓存，不改真实源码或 deployment。
$ErrorActionPreference='Stop'
$module=Split-Path $PSScriptRoot -Parent
$copy=Join-Path $env:TEMP ('JmdIncremental-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($copy)
try {
    foreach ($name in @('build.ps1','Program.cs','Shared','Agent','Installer','Publisher')) { Copy-Item -LiteralPath (Join-Path $module $name) -Destination $copy -Recurse }
    $delivery=Join-Path $copy 'Delivery'; [void][IO.Directory]::CreateDirectory($delivery)
    Get-ChildItem -LiteralPath (Join-Path $module 'Delivery') -Filter '*.cs' | Copy-Item -Destination $delivery
    $build=Join-Path $copy 'build.ps1'; $exe=Join-Path $copy 'bin\JmdUpdater.exe'
    & $build
    $hash=(Get-FileHash $exe).Hash; $stamp=(Get-Item $exe).LastWriteTimeUtc
    $second=@(& $build)
    if (($second -join '') -notmatch '源码未变化，沿用现有 JmdUpdater.exe' -or (Get-FileHash $exe).Hash -ne $hash -or (Get-Item $exe).LastWriteTimeUtc -ne $stamp) { throw 'Unchanged build was not reused' }
    Add-Content -LiteralPath (Join-Path $copy 'Program.cs') -Value '// Incremental build test change' -Encoding UTF8
    $third=@(& $build)
    if (($third -join '') -match '源码未变化' -or (Get-Item $exe).LastWriteTimeUtc -eq $stamp) { throw 'Changed source did not rebuild' }
    $forced=@(& $build -Force)
    if (($forced -join '') -match '源码未变化') { throw 'Force skipped compilation' }
    [IO.File]::AppendAllText($exe, 'tampered artifact')
    $repaired=@(& $build)
    if (($repaired -join '') -match '源码未变化' -or (Get-FileHash $exe).Hash -eq '') { throw 'Tampered EXE reused' }
    $cache=Get-Content (Join-Path $copy 'bin\JmdUpdater.build.sha256') -Raw | ConvertFrom-Json
    if ($cache.OutputSha256 -ne (Get-FileHash $exe).Hash) { throw 'Artifact hash not saved' }
    # Use private compiler/reference copies to change actual input bytes without touching Framework.
    $framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $privateFramework=Join-Path $copy 'framework'
    [void][IO.Directory]::CreateDirectory($privateFramework)
    foreach ($locale in @('1033','2052')) { Copy-Item (Join-Path $framework $locale) $privateFramework -Recurse }
    foreach ($name in @('csc.exe','csc.exe.config','mscorlib.dll','System.dll','System.Core.dll','System.Web.Extensions.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')) {
        Copy-Item (Join-Path $framework $name) $privateFramework
    }
    $buildText=[IO.File]::ReadAllText($build)
    $buildText=$buildText.Replace("Join-Path `$env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'",("'"+(Join-Path $privateFramework 'csc.exe')+"'"))
    [IO.File]::WriteAllText($build,$buildText,[Text.Encoding]::UTF8)
    [IO.File]::WriteAllText((Join-Path $privateFramework 'csc.rsp'), '/this-option-must-not-be-read')
    & $build
    foreach ($inputName in @('csc.exe','System.dll')) {
        [IO.File]::AppendAllText((Join-Path $privateFramework $inputName),'fingerprint test')
        $changed=@(& $build)
        if (($changed -join '') -match '源码未变化') { throw ('Changed input reused: '+$inputName) }
    }
    if ($buildText -notmatch '/noconfig' -or $buildText -notmatch '/nostdlib\+') { throw 'Implicit compiler references enabled' }
    [IO.File]::WriteAllText($build,$buildText.Replace('/warn:4','/warn:3'),[Text.Encoding]::UTF8)
    $changedOptions=@(& $build)
    if (($changedOptions -join '') -match '源码未变化') { throw 'Changed compiler options reused' }
    Write-Output 'IncrementalBuildTests: PASS (reuse, artifact tamper, source, compiler path/bytes, reference bytes, noconfig, options, Force)'
} finally {
    $resolved=[IO.Path]::GetFullPath($copy)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\') -or (Split-Path $resolved -Leaf) -notlike 'JmdIncremental-*') { throw 'Unsafe cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

