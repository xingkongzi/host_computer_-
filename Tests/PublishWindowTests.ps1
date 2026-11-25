# SynJMD：加载真实控件并驱动确定按钮；SelfTest 永不启动发布子进程。
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'Delivery\Publish-Window.ps1') -SelfTest
$count=0
function Assert($ok,$message) { if (-not $ok) { throw $message }; $script:count++ }
try {
    $form.Show(); [Windows.Forms.Application]::DoEvents()
    foreach ($name in @('Monitor','Manager')) {
        Assert ($groups[$name].Commit.Text.Length -gt 0) 'Empty commit'
        Assert ($groups[$name].Notes.Text.Length -gt 0) 'Empty notes'
        Assert ($groups[$name].Version.Text.Length -gt 0) 'Empty version'
    }
    $old=$groups.Monitor.Version.Text
    $test.Checked=$true
    Assert ($groups.Monitor.Version.Text -ne $old) 'Channel did not recalculate'
    $choices.Both.Checked=$true
    Assert ($groups.Monitor.Box.Visible -and $groups.Manager.Box.Visible) 'Both groups not visible'
    $groups.Monitor.Notes.Text=''
    $ok.PerformClick()
    Assert ($script:testError -match '更新说明不能为空' -and $script:testCalls.Count -eq 0) 'Empty notes accepted'
    $groups.Monitor.Notes.Text="中文说明 '引号' `"双引号`"`r`n第二行"
    $groups.Manager.Notes.Text='管理器说明'
    $ok.PerformClick()
    Assert ($script:testCalls.Count -eq 2) ('Hook not called: '+$script:testError)
    Assert ($script:testCalls[0].Request.Product -eq 'Monitor' -and $script:testCalls[1].Request.Product -eq 'Manager') 'Wrong order'
    foreach ($call in $script:testCalls) {
        $r=$call.Request
        Assert ($r.Channel -eq 'test' -and $r.Version -eq $groups[$r.Product].Version.Text -and $r.Commit -eq $groups[$r.Product].Commit.Text) 'Wrong publish request'
        Assert ($call.Notes -ceq $r.Notes) 'Notes UTF8 file mismatch'
        Assert ($call.Arguments -contains '-RequestFile') 'Missing JSON request argument'
    }
    Assert ($script:testConfirmations[0] -match '是否确定发布？') 'Confirmation missing'
    $choices.Manager.Checked=$true
    Assert ($groups.Manager.Box.Visible -and -not $groups.Monitor.Box.Visible) 'Single product visibility failed'
    $groups.Manager.Version.Text='9.8.7.6'
    $choices.Monitor.Checked=$true; $choices.Manager.Checked=$true
    Assert ($groups.Manager.Version.Text -eq '9.8.7.6') 'Product switch lost edited version'
    $official.Checked=$true
    Assert ($groups.Manager.Version.Text -eq '9.8.7.6') 'Declined replacement lost version'
    $script:testReplaceVersion=$true; $test.Checked=$true
    Assert ($groups.Manager.Version.Text -ne '9.8.7.6' -and -not $groups.Manager.VersionEdited) 'Accepted replacement failed'
    $fixture=Join-Path $script:work 'space directory'
    [void][IO.Directory]::CreateDirectory($fixture)
    $requestFile=Join-Path $fixture 'request file.json'
    $special=[string][char]0x2019+' '+[char]0x2018+' " ; # $ ` '+[char]0x4e2d+[char]0x6587
    $request=@{Product='Monitor';Channel='test';Version='1.2.3.4';Commit=$special;Notes=($special+"`r`nnext line");MonitorRepo=(Join-Path $fixture 'monitor repo');ManagerRepo=(Join-Path $fixture 'manager repo')}
    $marker=Join-Path $fixture 'injected.txt'
    $request.Notes += "`r`n" + "'; [IO.File]::WriteAllText('"+$marker+"','injected'); #"
    [IO.File]::WriteAllText($requestFile,($request | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    $psi=New-Object Diagnostics.ProcessStartInfo
    $psi.FileName=Join-Path $PSHOME 'powershell.exe'
    $psi.Arguments='-NoProfile -File '+(Quote-Argument (Join-Path (Split-Path $PSScriptRoot -Parent) 'Delivery\Run-PublishRequest.ps1'))+' -RequestFile '+(Quote-Argument $requestFile)+' -EchoOnly'
    $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.RedirectStandardOutput=$true
    $psi.StandardOutputEncoding=[Text.Encoding]::UTF8
    $probe=[Diagnostics.Process]::Start($psi)
    $output=$probe.StandardOutput.ReadToEnd(); $probe.WaitForExit()
    Assert ($probe.ExitCode -eq 0) 'Runner failed'
    $echo=$output | ConvertFrom-Json
    foreach ($key in $request.Keys) { Assert ($echo.$key -ceq $request[$key]) ('JSON roundtrip failed: '+$key) }
    Assert (-not (Test-Path $marker)) 'Request executed an extra command'
    $probe.Dispose()
    # A real sleeping process remains owned even when both log destinations fail.
    $script:logPath=$fixture; $oldWork=$script:work; $script:work=$requestFile
    $sleeper=Join-Path $fixture 'sleeper.ps1'
    $pidFile=Join-Path $fixture 'descendant.pid'
    $sleeperBody="`$p=Start-Process '"+(Join-Path $PSHOME 'powershell.exe')+"' -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru`r`n[IO.File]::WriteAllText('"+$pidFile+"',[string]`$p.Id)`r`nStart-Sleep -Seconds 60"
    [IO.File]::WriteAllText($sleeper,$sleeperBody,[Text.Encoding]::UTF8)
    $script:child=Start-Process (Join-Path $PSHOME 'powershell.exe') -ArgumentList ('-NoProfile -File "'+$sleeper+'"') -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path $pidFile) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
    Assert (Test-Path $pidFile) 'Descendant did not start'
    $descendantId=[int][IO.File]::ReadAllText($pidFile)
    Set-Busy $true
    Add-Log 'Injected log failure'
    Finish-Publish 'Injected monitor exception'
    $timer.Stop()
    Assert ($busy -and -not $child.HasExited -and (Test-Path $requestFile)) 'Logging released live child'
    Assert ($stop.Enabled -and -not $ok.Enabled) 'Stop button state wrong'
    $script:work=$oldWork
    Save-RunState 'Running' $request
    $state=Get-Content (Join-Path $script:work 'state.json') -Raw | ConvertFrom-Json
    Assert ($state.Pid -eq $child.Id -and $state.StartUtc -and $state.Request.Commit -ceq $special) 'Missing crash evidence'
    Stop-PublisherTree
    Assert ($child.WaitForExit(10000)) 'Force stop did not terminate child'
    Assert (-not (Get-Process -Id $descendantId -ErrorAction SilentlyContinue)) 'Force stop left descendant alive'
    $child.Dispose(); $script:child=$null; Set-Busy $false
    Write-Output "PublishWindowTests: PASS ($count assertions)"
} finally { if ($script:child -and -not $child.HasExited) { Stop-PublisherTree; $child.WaitForExit(); $child.Dispose(); $script:child=$null }; $script:busy=$false; $timer.Dispose(); $form.Close(); $form.Dispose() }
