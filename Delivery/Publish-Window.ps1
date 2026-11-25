<# SynJMD：WinForms 发布入口。默认值仅由 PlanOnly 提供；真实发布严格串行。
   自测模式只接受内存钩子，绝不启动发布子进程。兼容 Windows PowerShell 5.1。 #>
param([switch]$SelfTest, [string]$MonitorRepo, [string]$ManagerRepo)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Publish-ProcessTree.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$script:publishScript = Join-Path $PSScriptRoot 'Publish-OneClick.ps1'
# Keep preview and publish requests on the same optional repository overrides.
$script:repoParameters = @{}
if ($MonitorRepo) { $script:repoParameters.MonitorRepo = $MonitorRepo }
if ($ManagerRepo) { $script:repoParameters.ManagerRepo = $ManagerRepo }
$script:busy = $false
$script:settingVersion=$false
$script:lastChannel=''
$script:testReplaceVersion=$false
$script:stopping=$false
$script:groups = @{}
$script:queue = New-Object Collections.Queue
$script:testCalls = @()
$script:testConfirmations = @()
$script:testError = ''
function New-Control($type, $text) {
    $c = New-Object ('Windows.Forms.' + $type)
    $c.Text = $text; $c.Dock = 'Fill'; $c.Margin = New-Object Windows.Forms.Padding(5)
    return $c
}
$form = New-Control Form 'SynJMD 上位机发布'
$form.Size = New-Object Drawing.Size(1020,850)
$form.MinimumSize = New-Object Drawing.Size(820,700)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object Drawing.Font('Microsoft YaHei UI',9)
$layout = New-Control TableLayoutPanel ''
$layout.RowCount=5; $layout.ColumnCount=1
foreach ($height in @(45,45,0,170,45)) {
    $style = New-Object Windows.Forms.RowStyle
    if ($height) { $style.SizeType='Absolute'; $style.Height=$height } else { $style.SizeType='Percent'; $style.Height=100 }
    [void]$layout.RowStyles.Add($style)
}
$form.Controls.Add($layout)
$products = New-Control FlowLayoutPanel ''
$products.Controls.Add((New-Control Label '发布产品：'))
$choices = @{}
foreach ($item in @(@('Monitor','Monitor'),@('Manager','Manager'),@('Both','两个都发布'))) {
    $r = New-Control RadioButton $item[1]; $r.AutoSize=$true; $r.Dock='None'
    $choices[$item[0]]=$r; $products.Controls.Add($r)
}
$choices.Monitor.Checked=$true
$layout.Controls.Add($products,0,0)
$channels = New-Control FlowLayoutPanel ''
$channels.Controls.Add((New-Control Label '发布通道：'))
$official = New-Control RadioButton '正式发布（所有人）'; $official.AutoSize=$true; $official.Dock='None'; $official.Checked=$true
$test = New-Control RadioButton '测试发布（只给测试通道）'; $test.AutoSize=$true; $test.Dock='None'
$channels.Controls.AddRange(@($official,$test)); $layout.Controls.Add($channels,0,1)
$body = New-Control TableLayoutPanel ''; $body.ColumnCount=2
[void]$body.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('Percent',50)))
[void]$body.ColumnStyles.Add((New-Object Windows.Forms.ColumnStyle('Percent',50)))
$layout.Controls.Add($body,0,2)
$index=0
foreach ($name in @('Monitor','Manager')) {
    $box=New-Control GroupBox $name
    $grid=New-Control TableLayoutPanel ''; $grid.ColumnCount=1; $grid.RowCount=8
    foreach ($h in @(25,30,25,25,30,40,25,0)) {
        $rs=New-Object Windows.Forms.RowStyle
        if ($h) { $rs.SizeType='Absolute'; $rs.Height=$h } else { $rs.SizeType='Percent'; $rs.Height=100 }
        [void]$grid.RowStyles.Add($rs)
    }
    $commit=New-Control TextBox ''; $version=New-Control TextBox ''; $notes=New-Control TextBox ''
    $notes.Multiline=$true; $notes.ScrollBars='Vertical'; $notes.AcceptsReturn=$true
    $dirty=New-Control Label ''; $dirty.ForeColor=[Drawing.Color]::Firebrick
    $live=New-Control Label ''
    $grid.Controls.Add((New-Control Label 'git 提交号'),0,0); $grid.Controls.Add($commit,0,1)
    $grid.Controls.Add($dirty,0,2); $grid.Controls.Add((New-Control Label '版本号'),0,3)
    $grid.Controls.Add($version,0,4); $grid.Controls.Add($live,0,5)
    $grid.Controls.Add((New-Control Label '更新说明'),0,6); $grid.Controls.Add($notes,0,7)
    $box.Controls.Add($grid); $body.Controls.Add($box,$index,0); $index++
    $groups[$name]=@{Box=$box;Commit=$commit;Version=$version;Notes=$notes;Dirty=$dirty;Live=$live;VersionEdited=$false}
    $version.Tag=$name
    $version.Add_TextChanged({ if (-not $script:settingVersion) { $groups[$this.Tag].VersionEdited=$true } })
}
$log=New-Control TextBox ''; $log.Multiline=$true; $log.ReadOnly=$true; $log.ScrollBars='Both'
$layout.Controls.Add($log,0,3)
$buttons=New-Control FlowLayoutPanel ''; $buttons.FlowDirection='RightToLeft'
$cancel=New-Control Button '取消/关闭'; $cancel.Dock='None'
$ok=New-Control Button '确定'; $ok.Dock='None'
$stop=New-Control Button '强制停止'; $stop.Dock='None'; $stop.Enabled=$false
$buttons.Controls.AddRange(@($cancel,$stop,$ok)); $layout.Controls.Add($buttons,0,4)
$form.AcceptButton=$ok
function Selected-Products {
    if ($choices.Both.Checked) { return @('Monitor','Manager') }
    if ($choices.Manager.Checked) { return @('Manager') }
    return @('Monitor')
}
function Selected-Channel { if ($test.Checked) { return 'test' }; return 'official' }
# 输入：产品/通道选择事件；输出：刷新建议版本，保留用户已编辑的提交和说明。
function Refresh-Defaults {
    $channel=Selected-Channel
    $json = & $publishScript -PlanOnly -Product Both -Channel $channel -NoPause @script:repoParameters
    $plan=($json -join "`n") | ConvertFrom-Json
    foreach ($p in $plan.Products) {
        $g=$groups[$p.Product]
        $replace=-not $g.VersionEdited
        if ($g.VersionEdited -and $script:lastChannel -and $script:lastChannel -ne $channel) {
            if ($SelfTest) { $replace=$script:testReplaceVersion }
            else { $replace=[Windows.Forms.MessageBox]::Show($form,($p.Product + '：是否将手动版本替换为新通道建议值 ' + $p.SuggestedVersion + '？'),'替换版本','YesNo','Question') -eq 'Yes' }
        }
        if ($replace) {
            $script:settingVersion=$true
            try { $g.Version.Text=$p.SuggestedVersion; $g.VersionEdited=$false } finally { $script:settingVersion=$false }
        }
        $g.Live.Text='线上正式版：' + $p.OfficialVersion + "`r`n线上测试版：" + $p.TestVersion
        $g.Dirty.Text=''; if ($p.Dirty) { $g.Dirty.Text='该仓库有未提交改动' }
        if (-not $g.ContainsKey('Initialized')) { $g.Commit.Text=$p.Commit; $g.Notes.Text=([string]$p.Notes -replace "`r?`n","`r`n"); $g.Initialized=$true }   # WinForms 文本框需要 CRLF 才换行
    }
    $script:lastChannel=$channel
    $selected=@(Selected-Products)
    foreach ($name in @('Monitor','Manager')) { $groups[$name].Box.Visible=$selected -contains $name; $groups[$name].Box.Enabled=$selected -contains $name }
    $both=$selected.Count -eq 2
    $body.ColumnStyles[0].Width=100; $body.ColumnStyles[1].Width=0
    if ($both) { $body.ColumnStyles[0].Width=50; $body.ColumnStyles[1].Width=50 }
    elseif ($selected[0] -eq 'Manager') { $body.SetColumn($groups.Manager.Box,0); $body.SetColumn($groups.Monitor.Box,1) }
    if ($both -or $selected[0] -eq 'Monitor') { $body.SetColumn($groups.Monitor.Box,0); $body.SetColumn($groups.Manager.Box,1) }
}
function Get-Requests {
    $requests=@()
    foreach ($name in @(Selected-Products)) {
        $g=$groups[$name]; $v=$g.Version.Text.Trim()
        if ($v -notmatch '^\d+\.\d+(?:\.\d+){0,2}$' -or -not [Version]::TryParse($v,[ref]([Version]$null))) { throw ($name + ' 版本号须为两到四段数字。') }
        if ([string]::IsNullOrWhiteSpace($g.Commit.Text) -or $g.Commit.Text -match '[\x00-\x1f\x7f]' -or $g.Commit.Text.Length -gt 64) { throw ($name + ' 提交号不能为空，最多 64 字符且不能含换行或控制字符。') }
        if ([string]::IsNullOrWhiteSpace($g.Notes.Text)) { throw ($name + ' 更新说明不能为空。') }
        $requests += @{ Product=$name;Channel=(Selected-Channel);Version=$v;Commit=$g.Commit.Text;Notes=$g.Notes.Text;Yes=$true;NoPause=$true }
    }
    foreach ($request in $requests) {
        foreach ($key in $script:repoParameters.Keys) { $request[$key] = $script:repoParameters[$key] }
    }
    return $requests
}
# Windows argv quoting for fixed -File invocation; never generate PowerShell source.
function Quote-Argument([string]$value) {
    return '"' + ([regex]::Replace(([regex]::Replace($value, '(\\*)"', '$1$1\"')), '(\\+)$', '$1$1')) + '"'
}
function Set-Busy($value) {
    $script:busy=$value; $products.Enabled=-not $value; $channels.Enabled=-not $value
    $body.Enabled=-not $value; $ok.Enabled=-not $value; $stop.Enabled=$value
}
$timer=New-Object Windows.Forms.Timer; $timer.Interval=100
function Add-Log([string]$line) {
    $log.AppendText($line + "`r`n")
    try { [IO.File]::AppendAllText($script:logPath,$line + "`r`n",[Text.Encoding]::UTF8) }
    catch {
        # Disk failures must never release ownership of a live publisher.
        try { [IO.File]::AppendAllText((Join-Path $script:work 'fallback.log'),$line + "`r`n",[Text.Encoding]::UTF8) } catch { }
    }
}
# 每个管道只保留一个异步读取任务，由 UI 定时器取已完成的结果，避免跨线程调用 PowerShell。
function Start-Next {
    $request=$queue.Dequeue()
    $script:requestPath=Join-Path $script:work ($request.Product + '-request.json')
    [IO.File]::WriteAllText($script:requestPath,($request | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $script:arguments=@('-RequestFile',$script:requestPath)
    if ($SelfTest) { $script:testCalls+=@{Request=$request;Arguments=$script:arguments;Notes=$request.Notes}; return }
    $psi=New-Object Diagnostics.ProcessStartInfo
    $psi.FileName=Join-Path $PSHOME 'powershell.exe'
    $psi.Arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ' + (Quote-Argument (Join-Path $PSScriptRoot 'Run-PublishRequest.ps1')) + ' -RequestFile ' + (Quote-Argument $script:requestPath)
    $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    $psi.StandardOutputEncoding=[Text.Encoding]::UTF8; $psi.StandardErrorEncoding=[Text.Encoding]::UTF8
    $script:child=New-Object Diagnostics.Process; $child.StartInfo=$psi
    [void]$child.Start()
    $script:stopping=$false
    Save-RunState 'Running' $request
    $script:streams=@(@{Reader=$child.StandardOutput;Task=$child.StandardOutput.ReadLineAsync();Done=$false},@{Reader=$child.StandardError;Task=$child.StandardError.ReadLineAsync();Done=$false})
    Add-Log ('正在发布 '+$request.Product+' '+$request.Version)
}
# Durable evidence remains after a crash; request files are removed only after exit.
function Save-RunState($status,$request) {
    try {
        $record=@{Status=$status;StartUtc=$script:startedUtc;Request=$request;Pid=$child.Id}
        [IO.File]::WriteAllText((Join-Path $script:work 'state.json'),($record | ConvertTo-Json -Depth 8),[Text.Encoding]::UTF8)
    } catch { Add-Log ('State write failed: '+$_.Exception.Message) }
}
function Stop-PublisherTree {
    if (-not $script:child -or $child.HasExited) { return }
    $script:stopping=$true; $queue.Clear()
    Add-Log ('Force stop requested, publisher PID='+$child.Id)
    # Detached LAN services are created by WMI and are outside this tree.
    foreach ($processId in @([PublishProcessTree]::Descendants($child.Id)) + @($child.Id)) {
        try {
            $owned=Get-Process -Id $processId -ErrorAction Stop
            if ($owned.StartTime -ge $child.StartTime) { $owned.Kill(); Add-Log ('Stopped PID='+$processId) }
        } catch { Add-Log ('Stop PID='+$processId+': '+$_.Exception.Message) }
    }
}
function Finish-Publish([string]$failure) {
    if ($script:child -and -not $child.HasExited) {
        Add-Log $failure
        $timer.Start()
        return
    }
    $timer.Stop()
    if ($script:child) { $script:child.Dispose(); $script:child=$null }
    try { foreach ($file in @(Get-ChildItem -LiteralPath $script:work -Filter '*-request.json')) { Remove-Item -LiteralPath $file.FullName } }
    catch { Add-Log ('Temporary file cleanup failed: '+$_.Exception.Message) }
    Set-Busy $false
    if ($failure) { [void][Windows.Forms.MessageBox]::Show($form,('发布失败：'+$failure+"`r`n日志位置："+$script:logPath),'发布失败','OK','Error') }
    else { [void][Windows.Forms.MessageBox]::Show($form,('发布成功。日志位置：'+$script:logPath),'发布完成','OK','Information') }
}
$timer.Add_Tick({
    try {
        foreach ($stream in $script:streams) {
            $limit=0
            while (-not $stream.Done -and $stream.Task.IsCompleted -and $limit -lt 100) {
                try { $line=$stream.Task.GetAwaiter().GetResult() }
                catch { $stream.Done=$true; Add-Log ('Output read failed: '+$_.Exception.Message); break }
                $limit++
                if ($null -eq $line) { $stream.Done=$true } else { Add-Log $line; $stream.Task=$stream.Reader.ReadLineAsync() }
            }
        }
        if ($child.HasExited -and @($script:streams | Where-Object { -not $_.Done }).Count -eq 0) {
            $code=$child.ExitCode
            try { Save-RunState ('Exited: '+$code) ([IO.File]::ReadAllText($script:requestPath) | ConvertFrom-Json) } catch { Add-Log $_.Exception.Message }
            $child.Dispose(); $script:child=$null
            if ($script:stopping) { Finish-Publish 'Stopped by user; inspect logs before retrying.' }
            elseif ($code -ne 0) { Finish-Publish ('退出码 '+$code+"`r`n"+((@($log.Lines | Select-Object -Last 8)) -join "`r`n")) }
            elseif ($queue.Count) { Start-Next } else { Finish-Publish '' }
        }
    } catch { Finish-Publish $_.Exception.Message }
})
$ok.Add_Click({
    try {
        $requests=@(Get-Requests)
        $summary='是否确定发布？'
        foreach ($r in $requests) {
            $channelText='正式发布（所有人）'; if ($r.Channel -eq 'test') { $channelText='测试发布（只给测试通道）' }
            $excerpt=$r.Notes -replace '\r?\n',' / '; if ($excerpt.Length -gt 180) { $excerpt=$excerpt.Substring(0,180)+'…' }
            $summary+="`r`n`r`n产品："+$r.Product+"`r`n通道："+$channelText+"`r`n版本号："+$r.Version+"`r`n提交号："+$r.Commit+"`r`n更新说明摘要："+$excerpt
        }
        if ($SelfTest) { $script:testConfirmations+=$summary }
        elseif ([Windows.Forms.MessageBox]::Show($form,$summary,'发布确认','YesNo','Question') -ne 'Yes') { return }
        $script:work=Join-Path $env:TEMP ('SynJMD-PublishWindow-'+[Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($script:work); $script:logPath=Join-Path $script:work 'publish.log'
        $queue.Clear(); foreach ($r in $requests) { $queue.Enqueue($r) }
        $script:startedUtc=[DateTime]::UtcNow.ToString('o')
        Set-Busy $true
        Start-Next
        if ($SelfTest) {
            while ($queue.Count) { Start-Next }
            foreach ($file in @(Get-ChildItem -LiteralPath $script:work -Filter '*-request.json')) { Remove-Item -LiteralPath $file.FullName }
            Set-Busy $false
        } else { $timer.Start() }
    } catch {
        if ($SelfTest) { $script:testError=$_.Exception.Message }
        else { if ($busy) { Finish-Publish $_.Exception.Message } else { [void][Windows.Forms.MessageBox]::Show($form,$_.Exception.Message,'无法发布','OK','Warning') } }
    }
})
$stop.Add_Click({
    if ([Windows.Forms.MessageBox]::Show($form,'强制停止可能留下未完成的发布。请按日志检查后再重试，版本号可能已被占用。是否继续？','强制停止','YesNo','Warning') -eq 'Yes') { Stop-PublisherTree }
})
$cancel.Add_Click({$form.Close()})
$form.Add_FormClosing({param($sender,$e) if ($script:busy) { $e.Cancel=$true; [void][Windows.Forms.MessageBox]::Show($form,'正在发布，请等待发布结束。','正在发布','OK','Information') }})
foreach ($r in @($choices.Values)+@($official,$test)) { $r.Add_CheckedChanged({if ($this.Checked) { try { Refresh-Defaults } catch { if ($SelfTest) { throw }; [void][Windows.Forms.MessageBox]::Show($form,$_.Exception.Message,'读取默认值失败') } }}) }
try { Refresh-Defaults } catch { if ($SelfTest) { throw }; [void][Windows.Forms.MessageBox]::Show($form,$_.Exception.Message,'读取默认值失败') }
if (-not $SelfTest) { try { [void]$form.ShowDialog() } finally { $timer.Dispose(); $form.Dispose() } }
