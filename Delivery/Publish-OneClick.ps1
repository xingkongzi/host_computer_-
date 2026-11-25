<#
一键发布：编译 -> 整理干净目录 -> 部署更新器 -> 签名 -> 发布到局域网更新服务 -> 线上核验。
双击桌面快捷方式即可；也可带参数无人值守运行。
-DryRun 走完整流程，但发布到临时目录，不改源码版本号，不影响线上。
#>
[CmdletBinding()]
param(
    [ValidateSet('Monitor','Manager','Both')][string]$Product,
    [ValidateSet('official','test')][string]$Channel = 'official',
    [string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$PlanOnly,
    [string]$Commit,
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$NoPause,
    [string]$ManagerRepo,
    [string]$MonitorRepo,
    [string]$QtDir = 'D:\Qt\5.12.2\msvc2017_64',
    [string]$MSBuildPath = 'D:\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe',
    [string]$CrtDir = 'D:\Microsoft Visual Studio\2017\Community\VC\Redist\MSVC\14.16.27012\x64\Microsoft.VC141.CRT'
)
$ErrorActionPreference = 'Stop'
$oneModuleRoot = Split-Path -Parent $PSScriptRoot                      # ...\JmdUpdater
$oneWorkspaceRoot = Split-Path -Parent $oneModuleRoot                  # ...\JMD_host_computer
if (-not $MonitorRepo) { $MonitorRepo = Join-Path $oneWorkspaceRoot 'SynJMD_Monitor\SynJMD_Monitor' }
$oneMonitorRepo = $MonitorRepo
if (-not $ManagerRepo) {
    $ManagerRepo = Join-Path $oneWorkspaceRoot 'SynJMD_Manager\SynJMD_Manager'
}
$oneDeployment = Join-Path $oneModuleRoot 'deployment'
# 与服务端相同的规则解析发布根目录（server.json 的 RootDirectory，相对路径以 server.json 所在目录为基准）。
$oneLiveRoot = Join-Path $oneDeployment 'releases'
if (-not $DryRun -and (Test-Path -LiteralPath (Join-Path $oneDeployment 'server.json'))) {
    $oneServerRoot = [string](Get-Content -LiteralPath (Join-Path $oneDeployment 'server.json') -Raw -Encoding UTF8 | ConvertFrom-Json).RootDirectory
    if (-not $oneServerRoot) { $oneServerRoot = 'releases' }
    if ([IO.Path]::IsPathRooted($oneServerRoot)) { $oneLiveRoot = [IO.Path]::GetFullPath($oneServerRoot) }
    else { $oneLiveRoot = [IO.Path]::GetFullPath((Join-Path $oneDeployment $oneServerRoot)) }
}
$oneServerConfig = Join-Path $oneDeployment 'server.json'
$oneDryServerStarted = $false
$oneLatin1 = [Text.Encoding]::GetEncoding(28591)                        # 按字节无损读写源码，不动原编码
$oneStamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$oneScratch = Join-Path $env:TEMP ('JmdOneClick-' + $oneStamp)

# Manager 主程序名取自工程文件的 AssemblyName（改名只改工程文件一处）。名字不再是 DriveManager 时，
# 发布的签名清单会声明替换旧入口 DriveManager.exe，已装旧版的电脑可通过局域网更新自动改名。
$oneManagerExe = 'DriveManager.exe'
$oneManagerProject = Join-Path $ManagerRepo 'ManagerLog\ManagerLog.csproj'
if (Test-Path -LiteralPath $oneManagerProject) {
    $oneAssemblyMatch = [regex]::Match([IO.File]::ReadAllText($oneManagerProject), '<AssemblyName>\s*([A-Za-z0-9_.-]+)\s*</AssemblyName>')
    if ($oneAssemblyMatch.Success) { $oneManagerExe = $oneAssemblyMatch.Groups[1].Value + '.exe' }
}
$oneManagerPrevious = @()
if ($oneManagerExe -ne 'DriveManager.exe') { $oneManagerPrevious = @('DriveManager.exe') }

$oneProducts = @{
    Monitor = @{
        AppId = 'SynJMD_Monitor'; DisplayName = 'SynJMD Monitor'; EntryPoint = 'SynJMD_Monitor.exe'; PreviousEntryPoints = @()
        Repo = $oneMonitorRepo; Example = (Join-Path $oneModuleRoot 'config.monitor.example.json')
    }
    Manager = @{
        AppId = 'SynJMD_Manager'; DisplayName = 'SynJMD Manager'; EntryPoint = $oneManagerExe; PreviousEntryPoints = $oneManagerPrevious
        Repo = $ManagerRepo; Example = (Join-Path $oneModuleRoot 'config.manager.example.json')
    }
}

function Write-Step([string]$oneText) { Write-Host ''; Write-Host ('==> ' + $oneText) -ForegroundColor Cyan }

function Invoke-Logged([string]$oneFile, [string]$oneArguments, [string]$oneLog, [string]$oneWhat) {
    # 不用 Start-Process -Wait：它会等整个子进程树退出，而 MSBuild 拉起的 VBCSCompiler / 复用节点
    # 会在后台常驻约 10 分钟，导致编译早已结束、窗口却一直卡在“编译”这一步。只等主进程本身。
    $oneProcess = Start-Process -FilePath $oneFile -ArgumentList $oneArguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $oneLog -RedirectStandardError ($oneLog + '.err')
    $null = $oneProcess.Handle  # Windows PowerShell 5.1 需先取句柄，退出后 ExitCode 才可靠
    $oneProcess.WaitForExit()
    if ($oneProcess.ExitCode -ne 0) {
        Get-Content -LiteralPath $oneLog -Tail 25 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
        throw ($oneWhat + ' 失败（退出码 ' + $oneProcess.ExitCode + '），完整日志：' + $oneLog)
    }
}

. (Join-Path $PSScriptRoot 'OneClick-Versions.ps1')
. (Join-Path $PSScriptRoot 'Server-Lifecycle.ps1')
. (Join-Path $PSScriptRoot 'Mirror-Release.ps1')

function Get-SourceVersion([string]$oneName) {
    if ($oneName -eq 'Monitor') {
        $oneText = $oneLatin1.GetString([IO.File]::ReadAllBytes((Join-Path $oneMonitorRepo 'mainwindow.cpp')))
        $oneMatch = [regex]::Match($oneText, 'QStringLiteral\("SynJMD_Monitor"\),\s*QStringLiteral\("(\d+\.\d+\.\d+\.\d+)"\)')
        if (-not $oneMatch.Success) { throw '在 mainwindow.cpp 里找不到 Monitor 的更新版本号。' }
        return [Version]$oneMatch.Groups[1].Value
    }
    $oneText = $oneLatin1.GetString([IO.File]::ReadAllBytes((Join-Path $ManagerRepo 'SynJMD_Manager\Properties\AssemblyInfo.cs')))
    $oneMatch = [regex]::Match($oneText, '(?m)^\[assembly: AssemblyVersion\("(\d+\.\d+\.\d+\.\d+)"\)\]')
    if (-not $oneMatch.Success) { throw '在 AssemblyInfo.cs 里找不到 Manager 的版本号。' }
    return [Version]$oneMatch.Groups[1].Value
}

function Get-MonitorLabel([Version]$oneVersion) { return ('V{0:D2}.{1:D2}.{2:D2}' -f $oneVersion.Major, $oneVersion.Minor, $oneVersion.Build) }

function New-SourceEdit([string]$onePath, [string]$onePattern, [string]$oneReplacement, [int]$oneExpected) {
    # 只计算，不写盘；按 Latin-1 逐字节读写，只替换 ASCII 版本号，其余字节（含 BOM 和中文）原样保留。
    $oneBytes = [IO.File]::ReadAllBytes($onePath)
    $oneText = $oneLatin1.GetString($oneBytes)
    $oneCount = [regex]::Matches($oneText, $onePattern).Count
    if ($oneCount -ne $oneExpected) { throw ('版本号位置数量不符（期望 ' + $oneExpected + '，实际 ' + $oneCount + '）：' + $onePath) }
    return @{ Path = $onePath; Old = $oneBytes; New = $oneLatin1.GetBytes([regex]::Replace($oneText, $onePattern, $oneReplacement)) }
}

function Set-SourceVersion([string]$oneName, [Version]$oneVersion) {
    # 每次发布都把所有版本位置同步一遍（已经是目标值的文件不重写）；先全部校验再写，写失败就恢复已写的文件。
    $oneEdits = @()
    if ($oneName -eq 'Monitor') {
        if (($Channel -eq 'official' -and $oneVersion.Revision -ne 0) -or $oneVersion.Major -gt 99 -or $oneVersion.Minor -gt 99 -or $oneVersion.Build -gt 99) {
            throw 'Monitor 正式版第四位需为 0；前三段不得超过 99（关于页显示为 Vaa.bb.cc）。'
        }
        $oneEdits += New-SourceEdit (Join-Path $oneMonitorRepo 'mainwindow.cpp') `
            '(QStringLiteral\("SynJMD_Monitor"\),\s*QStringLiteral\(")\d+\.\d+\.\d+\.\d+("\))' ('${1}' + $oneVersion.ToString() + '${2}') 1
        $oneEdits += New-SourceEdit (Join-Path $oneMonitorRepo 'dialogs\dlgabout.ui') 'V\d{2}\.\d{2}\.\d{2}(?!\d)' (Get-MonitorLabel $oneVersion) 1
    } else {
        foreach ($oneInfo in @('SynJMD_Manager\Properties\AssemblyInfo.cs', 'ManagerLog\Properties\AssemblyInfo.cs')) {
            $oneEdits += New-SourceEdit (Join-Path $ManagerRepo $oneInfo) `
                '(?m)^(\[assembly: Assembly(?:File)?Version\(")\d+\.\d+\.\d+\.\d+("\)\])' ('${1}' + $oneVersion.ToString() + '${2}') 2
        }
    }
    $oneEdits = @($oneEdits | Where-Object { [Convert]::ToBase64String($_.Old) -ne [Convert]::ToBase64String($_.New) })
    $oneWritten = @()
    try {
        foreach ($oneEdit in $oneEdits) {
            if ((Get-Item -LiteralPath $oneEdit.Path).IsReadOnly) { throw ('文件是只读的，无法改版本号：' + $oneEdit.Path) }
        }
        foreach ($oneEdit in $oneEdits) { [IO.File]::WriteAllBytes($oneEdit.Path, $oneEdit.New); $oneWritten += $oneEdit }
    } catch {
        foreach ($oneEdit in $oneWritten) { try { [IO.File]::WriteAllBytes($oneEdit.Path, $oneEdit.Old) } catch { } }
        throw
    }
    if ($oneEdits.Count -gt 0) { Write-Host ('已把 ' + $oneEdits.Count + ' 个文件里的版本号改为 ' + $oneVersion) }
    if ((Get-SourceVersion $oneName) -ne $oneVersion) { throw '改写后源码版本号不对，已停止。' }
}

function Get-GitState([string]$oneRepo) {
    # 打开脚本时读取一次；只用于给出默认值，可以手动修改。
    $oneState = @{ Commit = ''; Subject = ''; Dirty = $false }
    $oneOldEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)   # git 输出 UTF-8，否则中文提交说明会乱码
        $oneHash = & git --no-optional-locks -c safe.directory=$oneRepo -C $oneRepo rev-parse --short=8 HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $oneHash) { $oneState.Commit = ([string]$oneHash).Trim() }
        $oneSubject = & git --no-optional-locks -c safe.directory=$oneRepo -c i18n.logOutputEncoding=utf-8 -C $oneRepo log -1 --format=%B 2>$null
        if ($LASTEXITCODE -eq 0 -and $oneSubject) { $oneState.Subject = ((@($oneSubject) -join "`n") -replace '(?im)(?:\r?\n){2}(?:[A-Za-z][A-Za-z0-9-]*:[^\r\n]*(?:\r?\n|$)(?:[ \t]+[^\r\n]*(?:\r?\n|$))*)+\s*\z', '').Trim() }
        $oneChanges = & git --no-optional-locks -c safe.directory=$oneRepo -C $oneRepo status --porcelain 2>$null
        $oneState.Dirty = [bool]$oneChanges
    } catch { } finally { [Console]::OutputEncoding = $oneOldEncoding }
    return $oneState
}

function Test-CommitText([string]$oneText) {
    if ($oneText.Length -gt 64 -or $oneText -match '[\x00-\x1f\x7f]') { throw '源码提交号最多 64 个字符，且不能含换行等控制字符。' }
    return $oneText
}

function Read-Notes([string]$oneTitle) {
    Write-Host ('请输入【' + $oneTitle + '】的更新说明（会显示在使用者的更新提示里）。可多行，输入空行结束：') -ForegroundColor Yellow
    $oneLines = @()
    while ($true) {
        $oneLine = Read-Host
        if ([string]::IsNullOrWhiteSpace($oneLine)) { break }
        $oneLines += $oneLine
    }
    return ($oneLines -join "`r`n")
}

function Build-Monitor([Version]$oneVersion, [string]$oneWork) {
    if (-not $env:QtMsBuild) { $env:QtMsBuild = Join-Path $env:LOCALAPPDATA 'QtMsBuild' }
    # 输出到独立目录，不覆盖可能正在运行的 release\SynJMD_Monitor.exe。
    $oneOut = Join-Path $oneMonitorRepo ('build\oneclick-' + $oneVersion + '-' + $oneStamp)
    $oneLog = Join-Path $oneWork 'monitor-build.log'
    $oneArguments = '"' + (Join-Path $oneMonitorRepo 'SynJMD_Monitor.vcxproj') + '" /m /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x64 "/p:OutDir=' + $oneOut + '\\"'
    Invoke-Logged $MSBuildPath $oneArguments $oneLog 'Monitor 编译'
    $oneExe = Join-Path $oneOut 'SynJMD_Monitor.exe'
    if (-not (Test-Path -LiteralPath $oneExe)) { throw ('编译后没有找到 ' + $oneExe) }
    # 版本号在 exe 里是 UTF-16 字符串；确认编出来的确实是这个版本。
    $oneNeedle = $oneLatin1.GetString([Text.Encoding]::Unicode.GetBytes($oneVersion.ToString()))
    if ($oneLatin1.GetString([IO.File]::ReadAllBytes($oneExe)).IndexOf($oneNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw ('编译出的 exe 里没有版本号 ' + $oneVersion + '，请检查 mainwindow.cpp。')
    }
    return $oneExe
}

function New-MonitorStage([string]$oneExe, [string]$oneStage, [string]$oneWork) {
    Copy-Item -LiteralPath $oneExe -Destination $oneStage
    $oneOldPath = $env:PATH
    try {
        $env:PATH = (Join-Path $QtDir 'bin') + ';' + $env:PATH
        $oneArguments = '--release --no-compiler-runtime --no-translations --dir "' + $oneStage + '" "' + (Join-Path $oneStage 'SynJMD_Monitor.exe') + '"'
        Invoke-Logged (Join-Path $QtDir 'bin\windeployqt.exe') $oneArguments (Join-Path $oneWork 'windeployqt.log') 'windeployqt'
    } finally { $env:PATH = $oneOldPath }
    # 程序只用 SQLite，其他数据库插件依赖无关的客户端库。
    Get-ChildItem -LiteralPath (Join-Path $oneStage 'sqldrivers') -Filter '*.dll' | Where-Object { $_.Name -ne 'qsqlite.dll' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Get-ChildItem -LiteralPath $CrtDir -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $oneStage }
    # 参数库用 monitor_parms*.db：现场的 parms.db / parms_en.db 在保留名单里，更新不会覆盖；
    # 新名字不在名单里，每次更新都换成最新库。不要把新名字加进 PreservePaths。
    foreach ($oneName in @('monitor_parms.db', 'monitor_parms_en.db', 'Translation_en.qm')) {
        Copy-Item -LiteralPath (Join-Path $oneMonitorRepo $oneName) -Destination $oneStage
    }
    [IO.File]::WriteAllText((Join-Path $oneStage 'default.txt'), "group`t`t`tgroup`r`n", [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $oneStage 'qt.conf'), "[Paths]`r`nPrefix=.`r`nPlugins=.`r`n", [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $oneStage 'Start_SynJMD_Monitor.cmd'), "@echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"%~dp0SynJMD_Monitor.exe`"`r`n", [Text.Encoding]::ASCII)
    foreach ($oneName in @('SynJMD_Monitor.exe','Qt5Core.dll','Qt5Gui.dll','Qt5Widgets.dll','Qt5Sql.dll','Qt5SerialPort.dll','Qt5Network.dll',
            'platforms\qwindows.dll','sqldrivers\qsqlite.dll','msvcp140.dll','vcruntime140.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $oneStage $oneName))) { throw ('干净目录缺少运行文件：' + $oneName) }
    }
}

function Build-Manager([Version]$oneVersion, [string]$oneWork) {
    $oneLog = Join-Path $oneWork 'manager-build.log'
    $oneArguments = '"' + (Join-Path $ManagerRepo 'ManagerLog\ManagerLog.csproj') + '" /t:Build /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=AnyCPU'
    Invoke-Logged $MSBuildPath $oneArguments $oneLog ('Manager 编译（如果 Release 版 ' + $oneManagerExe + ' 正在运行，请先关闭）')
    $oneBin = Join-Path $ManagerRepo 'ManagerLog\bin\Release'
    foreach ($oneName in @($oneManagerExe, 'DriveSysView.dll')) {
        $oneBuilt = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $oneBin $oneName)).FileVersion
        if ($oneBuilt -ne $oneVersion) { throw ($oneName + ' 编译出的版本是 ' + $oneBuilt + '，不是 ' + $oneVersion) }
    }
    return $oneBin
}

function New-ManagerStage([string]$oneBin, [string]$oneStage) {
    # 只拷程序文件：不带调试符号、ClickOnce 清单、文档 xml，更不带本机的现场参数表和日志。
    $oneSkipExtensions = @('.pdb', '.xml', '.application', '.manifest', '.xlsx', '.xls', '.csv', '.log', '.txt', '.json', '.tmp', '.bak')
    $oneSkipDirectories = @('app.publish', 'JmdUpdater', '.jmd-update', 'Logs', 'data')
    $oneBinRoot = [IO.Path]::GetFullPath($oneBin).TrimEnd('\')
    foreach ($oneFile in Get-ChildItem -LiteralPath $oneBinRoot -File -Recurse) {
        $oneRelative = $oneFile.FullName.Substring($oneBinRoot.Length + 1)
        if ($oneSkipExtensions -contains $oneFile.Extension.ToLowerInvariant()) { continue }
        if ($oneFile.Name -like '*.vshost.*') { continue }
        # 编译目录里可能留着另一个名字的主程序（改名前后编译的产物），只打包当前名字的 exe/config。
        if (@('DriveManager.exe', 'SynJMD_Manager.exe') -contains $oneRelative -and $oneRelative -ne $oneManagerExe) { continue }
        if (@('DriveManager.exe.config', 'SynJMD_Manager.exe.config') -contains $oneRelative -and $oneRelative -ne ($oneManagerExe + '.config')) { continue }
        if ($oneSkipDirectories -contains $oneRelative.Split('\')[0]) { continue }
        $oneTarget = Join-Path $oneStage $oneRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $oneTarget) -Force | Out-Null
        Copy-Item -LiteralPath $oneFile.FullName -Destination $oneTarget
    }
    foreach ($oneName in @($oneManagerExe,($oneManagerExe + '.config'),'DriveSysView.dll','NPOI.dll','Modbus.dll','MVC.dll','ZedGraph.dll','AGauge.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $oneStage $oneName))) { throw ('干净目录缺少运行文件：' + $oneName) }
    }
}

function Assert-StageVersion([string]$oneName, [string]$oneStage, [Version]$oneVersion) {
    # 签名前再核对一次：干净目录里的程序就是这个版本。
    if ($oneName -eq 'Manager') {
        foreach ($oneFile in @($oneManagerExe, 'DriveSysView.dll')) {
            $oneFound = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $oneStage $oneFile)).FileVersion
            if ($oneFound -ne $oneVersion) { throw ('干净目录里的 ' + $oneFile + ' 版本是 ' + $oneFound + '，不是 ' + $oneVersion) }
        }
        return
    }
    $oneNeedle = $oneLatin1.GetString([Text.Encoding]::Unicode.GetBytes($oneVersion.ToString()))
    if ($oneLatin1.GetString([IO.File]::ReadAllBytes((Join-Path $oneStage 'SynJMD_Monitor.exe'))).IndexOf($oneNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw ('干净目录里的 SynJMD_Monitor.exe 不含版本号 ' + $oneVersion)
    }
}

function Test-LiveRelease($oneInfo, [Version]$oneVersion, [string]$oneWork, [string]$oneLocalPackage, [string]$oneUrlChannel) {
    $oneServer = Get-Content -LiteralPath $oneServerConfig -Raw -Encoding UTF8 | ConvertFrom-Json
    $oneBase = 'http://' + $oneServer.BindAddress + ':' + $oneServer.Port + '/' + $oneUrlChannel + $oneInfo.AppId + '/'
    $oneClient = [Net.WebClient]::new(); $oneClient.Proxy = $null; $oneClient.Encoding = [Text.Encoding]::UTF8
    try {
        $oneEnvelope = $oneClient.DownloadString($oneBase + 'latest.json') | ConvertFrom-Json
        $onePayload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($oneEnvelope.Payload)) | ConvertFrom-Json
        if ((ConvertTo-FourPart ([Version]$onePayload.Version)) -ne $oneVersion) { throw ('线上 latest 是 ' + $onePayload.Version + '，不是刚发布的 ' + $oneVersion) }
        $onePackage = Join-Path $oneWork ($oneInfo.AppId + '-live.zip')
        $oneClient.DownloadFile($oneBase + $onePayload.PackageName, $onePackage)
    } finally { $oneClient.Dispose() }
    $oneHash = (Get-FileHash -LiteralPath $onePackage -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($oneHash -ne $onePayload.PackageSha256 -or (Get-Item -LiteralPath $onePackage).Length -ne $onePayload.PackageSize) {
        throw '从线上下载的更新包与清单里的哈希/大小不一致。'
    }
    if ($oneHash -ne (Get-FileHash -LiteralPath $oneLocalPackage -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw '线上提供的更新包不是本次生成的那个（服务可能指向了别的发布目录）。'
    }
    Write-Host ('线上核验通过：' + $oneBase + 'latest.json -> ' + $onePayload.Version + '，更新包 ' + $onePayload.Files.Count + ' 个文件，哈希一致。') -ForegroundColor Green
}

function Publish-Product([string]$oneName, [Version]$oneVersion, [string]$oneNotes, [string]$oneCommit, [string]$oneReleaseRoot) {
    $oneHistoryNotes = $oneNotes.TrimEnd()   # 发布历史只记使用者写的说明，不含下面追加的提交号和通道标记
    # 提交号同时写进更新说明末尾，“可更新版本”提示里也能看到。
    if ($oneCommit) { $oneNotes = $oneNotes.TrimEnd() + "`r`n`r`n源码：" + $oneCommit }
    if ($Channel -eq 'test') { $oneNotes = $oneNotes.TrimEnd() + "`r`n（测试通道版本）" }
    $oneInfo = $oneProducts[$oneName]
    $oneWork = Join-Path $oneScratch $oneName
    New-Item -ItemType Directory -Path $oneWork -Force | Out-Null
    $oneDay = Get-Date -Format 'yyyyMMdd'
    if ($oneName -eq 'Monitor') { $oneStageName = 'SynJMD_Monitor_' + (Get-MonitorLabel $oneVersion) + '_win64_' + $oneDay }
    else { $oneStageName = 'SynJMD_Manager_' + $oneVersion + '_win_' + $oneDay }
    if ($DryRun) { $oneStage = Join-Path $oneWork $oneStageName; $oneNotesFile = Join-Path $oneWork 'release-notes.txt' }
    else {
        $oneDist = Join-Path $oneInfo.Repo 'dist'
        New-Item -ItemType Directory -Path $oneDist -Force | Out-Null
        $oneStage = Join-Path $oneDist $oneStageName
        if (Test-Path -LiteralPath $oneStage) { $oneStage = $oneStage + '_' + (Get-Date -Format 'HHmmss') }
        $oneNotesFile = Join-Path $oneDist ('release-notes-' + $oneVersion + '.txt')
    }
    [IO.File]::WriteAllText($oneNotesFile, $oneNotes, [Text.UTF8Encoding]::new($true))

    if (-not $DryRun) {
        Write-Step ($oneName + '：同步源码里的版本号为 ' + $oneVersion)
        Set-SourceVersion $oneName $oneVersion
    }
    Write-Step ($oneName + '：Release 编译（约 1 分钟）')
    if ($oneName -eq 'Monitor') { $oneBuilt = Build-Monitor $(if ($DryRun) { Get-SourceVersion $oneName } else { $oneVersion }) $oneWork } else { $oneBuilt = Build-Manager $(if ($DryRun) { Get-SourceVersion $oneName } else { $oneVersion }) $oneWork }

    Write-Step ($oneName + '：整理干净的程序目录')
    New-Item -ItemType Directory -Path $oneStage | Out-Null
    if ($oneName -eq 'Monitor') {
        New-MonitorStage $oneBuilt $oneStage $oneWork
        $oneBuildDirectory = [IO.Path]::GetFullPath((Split-Path -Parent $oneBuilt))
        $oneBuildPrefix = [IO.Path]::GetFullPath((Join-Path $oneMonitorRepo 'build')).TrimEnd('\') + '\'
        if (-not $oneBuildDirectory.StartsWith($oneBuildPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build cleanup path escaped workspace build directory.' }
        Remove-Item -LiteralPath $oneBuildDirectory -Recurse -Force -ErrorAction SilentlyContinue   # 本次专用的编译输出目录
    } else { New-ManagerStage $oneBuilt $oneStage }
    Assert-StageVersion $oneName $oneStage $(if ($DryRun) { Get-SourceVersion $oneName } else { $oneVersion })
    $oneExample = Get-Content -LiteralPath $oneInfo.Example -Raw -Encoding UTF8 | ConvertFrom-Json
    $oneDeployOptions = @{}
    if ($DryRun) { $oneDeployOptions.PublicKey = Join-Path $oneScratch 'public-key.xml' }
    & (Join-Path $PSScriptRoot 'Deploy-Client.ps1') @oneDeployOptions -DestinationDirectory $oneStage -AppId $oneInfo.AppId -DisplayName $oneInfo.DisplayName `
        -Version $oneVersion.ToString() -ServerUrl $oneExample.ServerUrl -PreservePaths @($oneExample.PreservePaths) `
        -RequiredPreservePaths @($oneExample.RequiredPreservePaths)
    # Deploy-Client places the fresh updater in the stage; Publish-Release signs it before packaging.
    # 上位机“关于”页和更新窗口从这个文件读取源码提交号（UTF-8 无 BOM，Qt 的 JSON 解析不接受 BOM）。
    $oneBuildInfo = [ordered]@{
        AppId = $oneInfo.AppId; Version = $oneVersion.ToString(); SourceCommit = $oneCommit
        PublishedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    }
    [IO.File]::WriteAllText((Join-Path $oneStage 'build-info.json'), ($oneBuildInfo | ConvertTo-Json), [Text.UTF8Encoding]::new($false))

    Write-Step ($oneName + '：签名并发布 ' + $oneVersion)
    $onePublishOptions = @{}
    if ($DryRun) { $onePublishOptions = @{ PrivateKey = (Join-Path $oneScratch 'private-key.xml'); IsolatedDryRun = $true } }
    & (Join-Path $PSScriptRoot 'Publish-Release.ps1') @onePublishOptions -SourceDirectory $oneStage -ClientConfig (Join-Path $oneStage 'jmd-update.json') `
        -EntryPoint $oneInfo.EntryPoint -PreviousEntryPoints @($oneInfo.PreviousEntryPoints) -Version $oneVersion.ToString() -NotesFile $oneNotesFile -ReleaseRoot $oneReleaseRoot
    if ((Get-PublishedVersion $oneReleaseRoot $oneInfo.AppId) -ne $oneVersion) { throw '发布命令结束了，但 latest.json 没有变成新版本。' }

    $oneUrlChannel = 'official/'; if ($Channel -eq 'test') { $oneUrlChannel = 'test/' }
    Test-LiveRelease $oneInfo $oneVersion $oneWork (Join-Path (Join-Path $oneReleaseRoot $oneInfo.AppId) ($oneInfo.AppId + '-' + $oneVersion + '.zip')) $oneUrlChannel
    if ($Channel -eq 'official') {
        $oneTestRoot = Join-Path $oneReleaseRoot 'test'
        $oneTestHighest = Get-UsedVersions $oneTestRoot $oneInfo.AppId | Sort-Object -Descending | Select-Object -First 1
        if ($oneTestHighest -and $oneTestHighest -gt $oneVersion) { Write-Host ('Skipping test mirror: existing version ' + $oneTestHighest) }
        else {
            Copy-OfficialReleaseToTest $oneReleaseRoot $oneInfo.AppId $oneVersion
            if ((Get-PublishedVersion $oneTestRoot $oneInfo.AppId) -ne $oneVersion) { throw 'Test mirror latest mismatch.' }
            Test-LiveRelease $oneInfo $oneVersion $oneWork (Join-Path (Join-Path $oneTestRoot $oneInfo.AppId) ($oneInfo.AppId + '-' + $oneVersion + '.zip')) 'test/'
        }
    } else { Write-Host '仅测试通道可见' -ForegroundColor Yellow }
    if ($DryRun) { Write-Host ('DryRun completed; embedded product version unchanged: ' + $oneReleaseRoot) -ForegroundColor Green }
    Write-Host ($oneInfo.DisplayName + ' ' + $oneVersion + ' 已发布。干净目录：' + $oneStage) -ForegroundColor Green
    # 已经发布成功，记历史失败只提示，不算发布失败。
    $oneHistoryCsv = Join-Path $oneDeployment 'publish-history.csv'; if ($DryRun) { $oneHistoryCsv = Join-Path $oneScratch 'publish-history.csv' }
    $oneHistoryLog = ''; if ($oneTranscript -and (Test-Path -LiteralPath $oneTranscript)) { $oneHistoryLog = Split-Path -Leaf $oneTranscript }
    try {
        & (Join-Path $PSScriptRoot 'Publish-History.ps1') -ReleaseRoot $oneLiveRoot -CsvPath $oneHistoryCsv -AppId $oneInfo.AppId `
            -Channel $Channel -Version $oneVersion.ToString() -Commit $oneCommit -Notes $oneHistoryNotes -LogFile $oneHistoryLog
    } catch { Write-Host ('注意：发布已成功，但发布历史没记上：' + $_.Exception.Message) -ForegroundColor Yellow }
}

# 预览在任何写入和构建之前返回。
if ($PlanOnly) {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $oneDeployment 'releases')).TrimEnd('\')
    if ($oneLiveRoot.TrimEnd('\') -ne $allowedRoot) { throw '预览只允许读取 deployment/releases。' }
    $preview = @()
    $selected = @('Monitor', 'Manager')
    if ($Product -and $Product -ne 'Both') { $selected = @($Product) }
    foreach ($name in $selected) {
        $app = $oneProducts[$name]
        $live = Get-PublishedVersion $oneLiveRoot $app.AppId
        $testLive = Get-PublishedVersion (Join-Path $oneLiveRoot 'test') $app.AppId
        $source = Get-SourceVersion $name
        $git = Get-GitState $app.Repo
        $root = $oneLiveRoot
        if ($Channel -eq 'test') { $root = Join-Path $root 'test' }
        $used = @(Get-UsedVersions $root $app.AppId)
        if ($Channel -eq 'official') { $used += @(Get-UsedVersions (Join-Path $oneLiveRoot 'test') $app.AppId) }
        $suggested = Get-OneClickSuggestedVersion $name $Channel $source $live $used
        $preview += [ordered]@{ Product=$name; OfficialVersion=[string]$live; TestVersion=[string]$testLive
            SourceVersion=[string]$source; SuggestedVersion=[string]$suggested; Commit=$git.Commit
            Dirty=$git.Dirty; Notes=$git.Subject }
    }
    [ordered]@{ Channel=$Channel; Products=@($preview) } | ConvertTo-Json -Depth 5
    return
}
if ($NotesFile) { $Notes = [IO.File]::ReadAllText([IO.Path]::GetFullPath($NotesFile), [Text.Encoding]::UTF8) }

$oneExit = 0
# 同一时间只允许一个一键发布：改版本号、编译、复制、签名、发布全程独占，避免两次发布互相覆盖编译输出。
$oneMutex = New-Object Threading.Mutex($false, 'Local\SynJMD.OneClickPublish')
$oneOwnsMutex = $false
try {
    try { $oneOwnsMutex = $oneMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $oneOwnsMutex = $true }
    if (-not $oneOwnsMutex) { throw '已经有一个一键发布窗口在运行，请等它结束后再试。' }
    New-Item -ItemType Directory -Path $oneScratch -Force | Out-Null
    $oneLogs = Join-Path $oneDeployment 'logs'; if ($DryRun) { $oneLogs = Join-Path $oneScratch 'logs' }
    New-Item -ItemType Directory -Path $oneLogs -Force | Out-Null
    $oneTranscript = Join-Path $oneLogs ('oneclick-' + $oneStamp + '.log')
    try { Start-Transcript -LiteralPath $oneTranscript | Out-Null } catch { }

    $oneRequired = @($MSBuildPath, (Join-Path $QtDir 'bin\windeployqt.exe'), $CrtDir)
    if (-not $DryRun) { $oneRequired += @((Join-Path $oneDeployment 'keys\private-key.xml'), (Join-Path $oneDeployment 'server.json')) }
    foreach ($onePath in $oneRequired) {
        if (-not (Test-Path -LiteralPath $onePath)) { throw ('找不到发布所需的路径：' + $onePath) }
    }
    # 每次都重新构建，保证包里的更新器与当前源码一致（几秒钟）。
    Write-Step '构建更新器 JmdUpdater.exe'
    & (Join-Path $oneModuleRoot 'build.ps1') | Out-Null
    if ($DryRun) {
        $oneLiveRoot = Join-Path $oneScratch 'releases'
        New-Item -ItemType Directory -Path $oneLiveRoot | Out-Null
        & (Join-Path $oneModuleRoot 'bin\JmdUpdater.exe') keygen --private (Join-Path $oneScratch 'private-key.xml') --public (Join-Path $oneScratch 'public-key.xml')
        if ($LASTEXITCODE -ne 0) { throw 'DryRun key generation failed.' }
        $oneReleaseRoot = $oneLiveRoot
        # Reserve an ephemeral loopback port for isolated HTTP/hash verification.
        $oneProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $oneProbe.Start(); $oneDryPort = $oneProbe.LocalEndpoint.Port; $oneProbe.Stop()
        $oneServerConfig = Join-Path $oneScratch 'server.json'
        $oneDryConfig = @{ RootDirectory = $oneLiveRoot; BindAddress = '127.0.0.1'; Port = $oneDryPort; AllowedCidrs = @('127.0.0.0/8') }
        [IO.File]::WriteAllText($oneServerConfig, ($oneDryConfig | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        & (Join-Path $PSScriptRoot 'Start-LanServer.ps1') -ConfigPath $oneServerConfig -UpdaterPath (Join-Path $oneModuleRoot 'bin\JmdUpdater.exe') | ForEach-Object { Write-Host $_ }
        $oneDryServerStarted = $true
    }
    else { $oneReleaseRoot = $oneLiveRoot }

    Write-Host '================ SynJMD 上位机 一键发布 ================' -ForegroundColor Cyan
    if ($DryRun) { Write-Host '【演练模式】发布到临时目录，不改源码版本号，不影响线上。' -ForegroundColor Yellow }
    $oneStatus = @{}
    foreach ($oneName in @('Monitor', 'Manager')) {
        $oneLive = Get-PublishedVersion $oneLiveRoot $oneProducts[$oneName].AppId
        $oneTestLive = Get-PublishedVersion (Join-Path $oneLiveRoot 'test') $oneProducts[$oneName].AppId
        Write-Host ('  ' + $oneName + ' test channel latest: ' + $oneTestLive)
        $oneSource = Get-SourceVersion $oneName
        $oneGit = Get-GitState $oneProducts[$oneName].Repo
        $oneStatus[$oneName] = @{ Live = $oneLive; Source = $oneSource; Git = $oneGit }
        $oneLiveText = '（尚未发布）'; if ($oneLive) { $oneLiveText = $oneLive.ToString() }
        Write-Host ('  {0,-8} 线上版本 {1,-12} 源码版本 {2}' -f $oneName, $oneLiveText, $oneSource)
        $oneGitText = $oneGit.Commit; if (-not $oneGitText) { $oneGitText = '（读取失败）' }
        Write-Host ('           git 最新提交 {0}  {1}' -f $oneGitText, $oneGit.Subject)
        if ($oneGit.Dirty) { Write-Host '           注意：该仓库还有未提交的改动，本次编译会包含它们。' -ForegroundColor Yellow }
    }
    if (-not $Product) {
        Write-Host ''
        Write-Host '  1 = 发布 Monitor    2 = 发布 Manager    3 = 两个都发布    其他 = 退出'
        switch ((Read-Host '请选择').Trim()) { '1' { $Product = 'Monitor' } '2' { $Product = 'Manager' } '3' { $Product = 'Both' } default { throw '已取消，没有发布任何东西。' } }
    }
    if (-not $Yes -and -not $DryRun -and -not $PSBoundParameters.ContainsKey('Channel')) {
        switch ((Read-Host '1 = 正式发布（所有人）  2 = 测试发布（只给测试通道）').Trim()) {
            '1' { $Channel = 'official' } '2' { $Channel = 'test' } default { throw 'Cancelled.' }
        }
    }
    if ($Channel -eq 'test') { $oneReleaseRoot = Join-Path $oneLiveRoot 'test' }
    if ($Product -eq 'Both') { $oneSelected = @('Monitor', 'Manager') } else { $oneSelected = @($Product) }
    if ($Version -and $oneSelected.Count -ne 1) { throw '-Version 只能在发布单个产品时指定。' }
    if ($PSBoundParameters.ContainsKey('Commit') -and $oneSelected.Count -ne 1) { throw '-Commit 只能在发布单个产品时指定。' }

    $onePlan = @()
    foreach ($oneName in $oneSelected) {
        $oneLive = $oneStatus[$oneName].Live; $oneSource = $oneStatus[$oneName].Source
        # 默认 = 线上和历史上用过的最高版本再加一；源码版本更高且没用过时直接用源码版本。
        $oneUsed = @(Get-UsedVersions $oneReleaseRoot $oneProducts[$oneName].AppId)
        if ($Channel -eq 'official') { $oneUsed += @(Get-UsedVersions (Join-Path $oneLiveRoot 'test') $oneProducts[$oneName].AppId) }

        $oneBase = $oneLive
        if ($DryRun -and -not $oneBase) { $oneBase = $oneSource }
        $oneSuggested = Get-OneClickSuggestedVersion $oneName $Channel $oneSource $oneBase $oneUsed
        if ($Version) { $oneTarget = [Version]$Version }
        elseif ($Yes -or $DryRun) { $oneTarget = $oneSuggested }
        else {
            $oneTyped = (Read-Host ($oneName + ' 要发布的版本号（直接回车 = ' + $oneSuggested + '）')).Trim()
            if ($oneTyped) { $oneTarget = [Version]$oneTyped } else { $oneTarget = $oneSuggested }
        }
        $oneTarget = ConvertTo-FourPart $oneTarget
        if ($oneTarget.Revision -lt 0) { $oneTarget = [Version]::new($oneTarget.Major, $oneTarget.Minor, $oneTarget.Build, 0) }
        $oneOfficialBase = $null; if ($Channel -eq 'test') { $oneOfficialBase = $oneBase }
        Assert-ChannelVersion $oneTarget $oneUsed $oneOfficialBase
        if ($Channel -eq 'official' -and $oneName -eq 'Monitor' -and $oneTarget.Revision -ne 0) { throw 'Monitor official revision must be zero.' }
        if ($Notes) { $oneText = $Notes } elseif ($DryRun) { $oneText = '演练，不对外发布。' } else { $oneText = Read-Notes ($oneName + ' ' + $oneTarget) }
        if ([string]::IsNullOrWhiteSpace($oneText)) { throw '更新说明不能为空。已取消，没有发布任何东西。' }
        $oneDefaultCommit = $oneStatus[$oneName].Git.Commit
        if ($PSBoundParameters.ContainsKey('Commit')) { $oneCommitText = $Commit.Trim() }
        elseif ($Yes -or $DryRun) { $oneCommitText = $oneDefaultCommit }
        else {
            $oneTyped = (Read-Host ($oneName + ' 源码提交号（直接回车 = ' + $oneDefaultCommit + '，可改成任意文字）')).Trim()
            if ($oneTyped) { $oneCommitText = $oneTyped } else { $oneCommitText = $oneDefaultCommit }
        }
        $oneCommitText = Test-CommitText $oneCommitText
        $onePlan += @{ Name = $oneName; Version = $oneTarget; Notes = $oneText; Commit = $oneCommitText }
    }

    Write-Host ''
    Write-Host '即将执行：' -ForegroundColor Yellow
    foreach ($oneItem in $onePlan) {
        $oneCommitShown = $oneItem.Commit; if (-not $oneCommitShown) { $oneCommitShown = '（不记录）' }
        Write-Host ('  ' + $oneItem.Name + ' -> ' + $oneItem.Version + '    源码：' + $oneCommitShown)
        Write-Host ('      说明：' + ($oneItem.Notes -replace '\r?\n', ' / '))
    }
    if (@($onePlan | Where-Object { $_.Name -eq 'Manager' }).Count -gt 0 -and @($oneProducts.Manager.PreviousEntryPoints).Count -gt 0) {
        Write-Host ''
        Write-Host ('注意：这是 Manager 主程序改名版（' + (@($oneProducts.Manager.PreviousEntryPoints) -join '、') + ' -> ' + $oneProducts.Manager.EntryPoint + '）。') -ForegroundColor Red
        Write-Host '只有已经装上“支持改名的更新器”（改名前最后一版）的电脑才能自动升级；还没升上去的电脑会收不到更新，' -ForegroundColor Red
        Write-Host '只能手动安装新包。请确认所有使用者都已更新到改名前的版本后再发布。' -ForegroundColor Red
    }
    if (-not $DryRun) {
        Write-Host '发布后，开启了局域网更新的电脑会在约 1 分钟内收到更新提示；已发布的版本号不能撤回或覆盖。' -ForegroundColor Yellow
        if (-not $Yes -and (Read-Host '确认发布请输入 Y').Trim() -notin @('Y', 'y')) { throw '已取消，没有发布任何东西。' }
    }
    if (-not $DryRun) {
        Restart-ChangedServer -ConfigPath $oneServerConfig -CandidateUpdater (Join-Path $oneModuleRoot 'bin\JmdUpdater.exe')
    }
    foreach ($oneItem in $onePlan) { Publish-Product $oneItem.Name $oneItem.Version $oneItem.Notes $oneItem.Commit $oneReleaseRoot }
    Write-Host ''
    Write-Host '全部完成。' -ForegroundColor Green
    if (-not $DryRun) { Write-Host '提醒：源码里的版本号已更新，记得把它和本次代码一起提交到 git。' }
} catch {
    $oneExit = 1
    Write-Host ''
    Write-Host ('失败：' + $_.Exception.Message) -ForegroundColor Red
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    Write-Host ('中间文件和编译日志在：' + $oneScratch) -ForegroundColor DarkGray
} finally {
    if ($oneDryServerStarted) { & (Join-Path $PSScriptRoot 'Stop-LanServer.ps1') -ConfigPath $oneServerConfig | ForEach-Object { Write-Host $_ } }
    try { Stop-Transcript | Out-Null } catch { }
    if ($oneOwnsMutex) { $oneMutex.ReleaseMutex() }
    $oneMutex.Dispose()
}
if (-not $NoPause) { Read-Host '按回车键关闭窗口' | Out-Null }
exit $oneExit
