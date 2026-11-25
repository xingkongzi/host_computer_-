<#
发布历史总表。一键发布每成功发布一个产品，就调用本脚本记一条。
- <发布根目录>\publish-history.json：程序读的总览，局域网更新服务会把它提供给客户端（Monitor“帮助 -> 发布总览”）。
- <CsvPath>：给人看的总表，Excel 直接打开；每次都由 json 整体重新生成，所以被 Excel 占用导致这一次没写成也没关系，下次会补齐。
每次运行都会对照已发布的 *.release.json，把总览里缺的版本补进去（发布时间取文件修改时间），
所以第一次运行、或某次发布后没记上的版本，都会在下一次运行时补齐。同一产品、同一通道、同一版本只保留一条。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseRoot,    # 发布根目录（releases，不是 releases\test）
    [Parameter(Mandatory = $true)][string]$CsvPath,
    [switch]$Rebuild,                                      # 只补历史、不追加
    [string]$AppId,
    [ValidateSet('', 'official', 'test')][string]$Channel = '',
    [string]$Version,
    [string]$Commit,
    [string]$Notes,
    [string]$LogFile,
    [string]$PublishedAt
)
$ErrorActionPreference = 'Stop'
# 相对路径按 PowerShell 当前目录解析成绝对路径；[IO.File] 用的是进程工作目录，两者可能不同。
$ReleaseRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseRoot)
$CsvPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CsvPath)
$Channel = $Channel.ToLowerInvariant()   # ValidateSet 不区分大小写，客户端按小写比较
$historyProducts = [ordered]@{ SynJMD_Monitor = 'Monitor'; SynJMD_Manager = 'Manager' }
$historyJson = Join-Path $ReleaseRoot 'publish-history.json'

function ConvertFrom-ReleaseFile([string]$historyPath, [string]$historyAppId, [string]$historyChannel) {
    $historyRaw = [IO.File]::ReadAllText($historyPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $historyPayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([string]$historyRaw.Payload)) | ConvertFrom-Json
    if ([string]$historyPayload.AppId -cne $historyAppId) { throw ('清单里的产品是 ' + $historyPayload.AppId + '，与所在目录不符') }
    $historyNotes = [string]$historyPayload.ReleaseNotes
    $historyCommit = ''
    # 发布脚本追加的固定格式是“空行 + 源码：xxx”，只认这种单独成段的写法，正文里出现“源码：”不算。
    $historyMatch = [regex]::Match($historyNotes, '\r?\n\r?\n源码：([^\r\n]*)(?:\r?\n（测试通道版本）)?\s*\z')
    if ($historyMatch.Success) {
        $historyCommit = $historyMatch.Groups[1].Value.Trim()
        $historyNotes = $historyNotes.Substring(0, $historyMatch.Index)
    }
    # “（测试通道版本）”是发布脚本给测试版单独一行追加的；正式版正文里的同样字样不动。
    if ($historyChannel -eq 'test') { $historyNotes = $historyNotes -replace '\r?\n（测试通道版本）\s*\z', '' }
    $historyNotes = $historyNotes.TrimEnd()
    return New-Entry $historyAppId $historyChannel ([string]$historyPayload.Version) $historyCommit $historyNotes '' `
        ((Get-Item -LiteralPath $historyPath).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))
}

function New-Entry([string]$entryAppId, [string]$entryChannel, [string]$entryVersion, [string]$entryCommit,
                   [string]$entryNotes, [string]$entryLog, [string]$entryTime) {
    $entryProduct = $historyProducts[$entryAppId]; if (-not $entryProduct) { $entryProduct = $entryAppId }
    return [pscustomobject][ordered]@{
        PublishedAt = $entryTime; Product = $entryProduct; AppId = $entryAppId; Channel = $entryChannel
        Version = $entryVersion; Commit = $entryCommit; Notes = $entryNotes; LogFile = $entryLog
    }
}

function Get-BackfillEntries {
    $backfill = @()
    foreach ($backAppId in $historyProducts.Keys) {
        $officialVersions = @{}
        $officialDirectory = Join-Path $ReleaseRoot $backAppId
        if (Test-Path -LiteralPath $officialDirectory) {
            foreach ($file in @(Get-ChildItem -LiteralPath $officialDirectory -Filter ($backAppId + '-*.release.json'))) {
                try { $entry = ConvertFrom-ReleaseFile $file.FullName $backAppId 'official'; $officialVersions[$entry.Version] = $true; $backfill += $entry }
                catch { Write-Warning ('跳过无法读取的发布记录：' + $file.FullName + '：' + $_.Exception.Message) }
            }
        }
        # 正式版会镜像到 test 目录；只有 test 目录里才有的版本才算测试发布。
        $testDirectory = Join-Path (Join-Path $ReleaseRoot 'test') $backAppId
        if (Test-Path -LiteralPath $testDirectory) {
            foreach ($file in @(Get-ChildItem -LiteralPath $testDirectory -Filter ($backAppId + '-*.release.json'))) {
                try { $entry = ConvertFrom-ReleaseFile $file.FullName $backAppId 'test'; if (-not $officialVersions.ContainsKey($entry.Version)) { $backfill += $entry } }
                catch { Write-Warning ('跳过无法读取的发布记录：' + $file.FullName + '：' + $_.Exception.Message) }
            }
        }
    }
    return $backfill
}

function Get-EntryKey($keyEntry) { return ([string]$keyEntry.AppId + '|' + [string]$keyEntry.Channel + '|' + [string]$keyEntry.Version).ToLowerInvariant() }

function Read-History {
    $known = @()
    if (Test-Path -LiteralPath $historyJson) {
        $document = [IO.File]::ReadAllText($historyJson, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($null -eq $document.Entries) { throw ('发布历史文件格式不对：' + $historyJson) }
        $known = @($document.Entries)
    }
    # 补上总览里还没有的已发布版本（首次运行，或之前某次发布后没记上）。
    $keys = @{}; foreach ($entry in $known) { $keys[(Get-EntryKey $entry)] = $true }
    foreach ($entry in @(Get-BackfillEntries)) {
        $key = Get-EntryKey $entry
        if (-not $keys.ContainsKey($key)) { $known += $entry; $keys[$key] = $true }
    }
    return $known
}

function Protect-CsvCell([string]$cellText) {
    # 以 = + - @ 开头的单元格 Excel 会当公式算，前面加 ' 让它按文字显示。
    if ($cellText -match '^[=+\-@\t\r\n]') { return "'" + $cellText }
    return $cellText
}

function Write-TextAtomic([string]$targetPath, [string]$targetText, [Text.Encoding]$targetEncoding) {
    $targetDirectory = Split-Path -Parent $targetPath
    New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
    $temporary = Join-Path $targetDirectory ('.' + (Split-Path -Leaf $targetPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, $targetText, $targetEncoding)
        if (Test-Path -LiteralPath $targetPath) { [IO.File]::Replace($temporary, $targetPath, [NullString]::Value) }   # $null 会被转成空字符串
        else { [IO.File]::Move($temporary, $targetPath) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue } }
}

function Get-SortKey($sortEntry) {
    $sortTime = [DateTime]::MinValue
    [void][DateTime]::TryParseExact([string]$sortEntry.PublishedAt, 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None, [ref]$sortTime)
    return $sortTime
}

if (-not $Rebuild) {
    if (-not $AppId -or -not $Channel -or -not $Version) { throw '追加发布记录需要 -AppId、-Channel、-Version。' }
    if (-not $PublishedAt) { $PublishedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }
}
# 读-改-写全程独占（Global：不同登录会话之间也互斥），避免两次同时写时一方的记录被覆盖。
$historyMutex = New-Object Threading.Mutex($false, 'Global\SynJMD.PublishHistory')
$historyOwnsMutex = $false
try {
    try { $historyOwnsMutex = $historyMutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $historyOwnsMutex = $true }
    if (-not $historyOwnsMutex) { throw '另一个进程正在写发布历史，等了 60 秒仍未结束。' }

    $entries = @(Read-History)
    if (-not $Rebuild) {
        $newEntry = New-Entry $AppId $Channel $Version $Commit $Notes $LogFile $PublishedAt
        $newKey = Get-EntryKey $newEntry
        $entries = @($entries | Where-Object { (Get-EntryKey $_) -ne $newKey })
        $entries += $newEntry
    }
    # 新的在前；时间相同时按版本号从高到低。
    $entries = @($entries | Sort-Object -Property @{ Expression = { Get-SortKey $_ }; Descending = $true },
        @{ Expression = { try { [Version][string]$_.Version } catch { [Version]'0.0' } }; Descending = $true })

    $document = [ordered]@{ Format = 1; UpdatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); Entries = $entries }
    # UTF-8 无 BOM：Qt 的 JSON 解析不接受 BOM。
    Write-TextAtomic $historyJson ($document | ConvertTo-Json -Depth 5) ([Text.UTF8Encoding]::new($false))

    $rows = foreach ($entry in $entries) {
        $channelText = '正式'; if ($entry.Channel -eq 'test') { $channelText = '测试' }
        $firstLine = (([string]$entry.Notes) -split '\r?\n' | Where-Object { $_.Trim() } | Select-Object -First 1)
        [pscustomobject][ordered]@{
            '发布时间' = (Protect-CsvCell $entry.PublishedAt); '产品' = (Protect-CsvCell $entry.Product); '通道' = $channelText
            '版本号' = (Protect-CsvCell $entry.Version); '源码提交' = (Protect-CsvCell $entry.Commit)
            '更新说明（首行）' = (Protect-CsvCell $firstLine); '完整更新说明' = (Protect-CsvCell $entry.Notes)
            '发布日志' = (Protect-CsvCell $entry.LogFile)
        }
    }
    $csvText = ''
    if ($rows) { $csvText = (@($rows | ConvertTo-Csv -NoTypeInformation) -join "`r`n") + "`r`n" }
    Write-Host ('发布总览已更新：' + $historyJson + '（共 ' + $entries.Count + ' 条）')
    try {
        Write-TextAtomic $CsvPath $csvText ([Text.UTF8Encoding]::new($true))   # 带 BOM，Excel 打开中文不乱码
        Write-Host ('发布历史总表：' + $CsvPath)
    } catch { Write-Warning ('发布历史总表 ' + $CsvPath + ' 没写成（可能正被 Excel 打开），下次发布会自动补齐：' + $_.Exception.Message) }
} finally {
    if ($historyOwnsMutex) { $historyMutex.ReleaseMutex() }
    $historyMutex.Dispose()
}
