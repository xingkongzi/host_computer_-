# SynJMD：只读预览回归；deployment 仅枚举 releases 和 server.json，不读取密钥。
$ErrorActionPreference='Stop'
$module=Split-Path $PSScriptRoot -Parent
$workspace=Split-Path $module -Parent
$monitor=Join-Path $workspace 'SynJMD_Monitor\SynJMD_Monitor'
$manager=Join-Path $workspace 'SynJMD_Manager\SynJMD_Manager'
. (Join-Path $module 'Delivery\OneClick-Versions.ps1')
function Assert($ok,$message) { if (-not $ok) { throw $message }; $script:count++ }
$count=0
function Snapshot {
    $result=@{}
    $directories=New-Object Collections.Stack
    foreach ($source in @($module,$monitor,$manager)) { $directories.Push($source) }
    $deployment=Join-Path $module 'deployment'
    while ($directories.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
            # IDE 缓存可能被 Visual Studio 独占；它不是发布输入，明确排除而不关闭用户程序。
            if ($entry.FullName -eq $deployment -or $entry.FullName -eq (Join-Path $monitor 'JmdUpdater') -or $entry.Name -in @('.git','.vs')) { continue }
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            if ($entry.PSIsContainer) { $directories.Push($entry.FullName) }
            else { $result[$entry.FullName]=(Get-FileHash -LiteralPath $entry.FullName).Hash }
        }
    }
    $allowed=@((Join-Path $deployment 'server.json'))
    $allowed+=@(Get-ChildItem -LiteralPath (Join-Path $deployment 'releases') -File -Recurse | Select-Object -ExpandProperty FullName)
    foreach ($path in $allowed) { if (Test-Path -LiteralPath $path) { $result[$path]=(Get-FileHash -LiteralPath $path).Hash } }
    return ($result.GetEnumerator() | Sort-Object Name | ForEach-Object { $_.Name+'='+$_.Value }) -join "`n"
}
$before=Snapshot
$config=Get-Content (Join-Path $module 'deployment\server.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$root=$config.RootDirectory
if (-not [IO.Path]::IsPathRooted($root)) { $root=Join-Path (Join-Path $module 'deployment') $root }
foreach ($channel in @('official','test')) {
    $raw=& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $module 'Delivery\Publish-OneClick.ps1') -PlanOnly -Channel $channel -NoPause
    Assert ($LASTEXITCODE -eq 0) 'Plan process failed'
    $plan=($raw -join "`n") | ConvertFrom-Json
    Assert ($plan.Channel -eq $channel -and $plan.Products.Count -eq 2) 'Plan shape invalid'
    foreach ($p in $plan.Products) {
        foreach ($field in @('Product','OfficialVersion','TestVersion','SourceVersion','SuggestedVersion','Commit','Dirty','Notes')) { Assert ($null -ne $p.PSObject.Properties[$field]) ('Missing '+$field) }
        Assert ($p.Dirty -is [bool] -and $p.Commit -match '^[a-f0-9]+$' -and $p.Notes.Length -gt 0) 'Git defaults missing'
        $app='SynJMD_'+$p.Product
        if ($channel -eq 'official') {
            $used=@(Get-UsedVersions $root $app)+@(Get-UsedVersions (Join-Path $root 'test') $app)
            $expected=Get-OfficialVersion $p.Product ([Version]$p.SourceVersion) $used
        } else { $expected=Get-TestVersion ([Version]$p.OfficialVersion) @(Get-UsedVersions (Join-Path $root 'test') $app) }
        Assert ($p.SuggestedVersion -eq [string]$expected) 'Suggestion differs from version rules'
    }
}
# 合成仓库验证完整中文正文和末尾 trailer，避免测试依赖当前 HEAD 的消息格式。
$fixture=Join-Path $env:TEMP ('JmdPlanGit-'+[Guid]::NewGuid().ToString('N'))
try {
    $info=Join-Path $fixture 'SynJMD_Manager\Properties'
    [void][IO.Directory]::CreateDirectory($info)
    [IO.File]::WriteAllText((Join-Path $info 'AssemblyInfo.cs'), '[assembly: AssemblyVersion("1.1.1.128")]')
    & git -C $fixture init -q
    $message="中文标题`n`n完整正文第一行`n说明：保留正文`n第二行`n`nCo-Authored-By: Test <test@example.com>`nSigned-off-by: Test <test@example.com>`n"
    $messagePath=Join-Path $fixture 'message.txt'
    [IO.File]::WriteAllText($messagePath,$message,[Text.UTF8Encoding]::new($false))
    & git -c safe.directory=$fixture -C $fixture add .
    & git -c safe.directory=$fixture -c user.name=Test -c user.email=test@example.com -C $fixture commit -q -F $messagePath
    $raw=& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $module 'Delivery\Publish-OneClick.ps1') -PlanOnly -Product Manager -ManagerRepo $fixture -NoPause
    $p=(($raw -join "`n") | ConvertFrom-Json).Products[0]
    Assert ($p.Notes -ceq "中文标题`n`n完整正文第一行`n说明：保留正文`n第二行") 'Full UTF8 notes or trailer removal failed'
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\') -or (Split-Path $resolved -Leaf) -notlike 'JmdPlanGit-*') { throw 'Unsafe fixture cleanup' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Assert ((Snapshot) -ceq $before) 'Plan changed workspace or deployment files'
Write-Output "OneClickPlanTests: PASS ($count assertions)"
