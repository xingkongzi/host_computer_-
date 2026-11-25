# JmdUpdater 只读复核（第 2 轮）

**结论：可以作为后续部署的实现基线。** 第一轮的 P0 和 4 个确定 P1 都已经修好，本轮没有发现新的确定 P0/P1。有一个 P1 级的需求问题待确认（`parms.db` 的定位，见 B-0）。另外有几个 P2，其中 B-1、B-2 改起来很便宜，建议在部署前顺手修掉。

本轮只读了文件，没有编译、运行任何测试，也没有访问网络或读取 `deployment/`、`keys/`、私钥。

---

## A. P0/P1 修复逐项核实

| 项 | 结论 | 证据 |
|---|---|---|
| **P0-1** Session 目录 / closeEvent | **已修复** | `Agent.cs:251-253` 在 prepare 之前创建并检查 Session 目录，还会删除旧的 ack。`updatehost.cpp:221-248` 安全创建目录并做写入探针，`:376-379` 在调用 `m_begin` 之前就探测，失败时宿主状态不会被改动。`mainwindow.cpp:271-284` 先复查，再关闭子窗体，commit 成功之后才 `closePort` 和 `accept`。commit 失败时由 `cancelUpdatePreparation`（`:248-262`）恢复窗口和定时器。`m_pPlot` 在 `mainwindow.cpp:77` 无条件创建。 |
| **P1-1** staged 握手 | **已修复** | `Agent.cs:257-265`：先写 job、启动 Installer，再等待 staged。`InstallerService.cs:135-156`：持有 mutex，完成验签、pending 检查、`ValidatePackage`，排除其他实例并复查原进程，之后才写 staged。`Agent.cs:339-340` 校验 PrepareId 和 HostPid。cancel 在 `:422/426/158/173/175` 均有检查。`shutdown.Ok=false` 的处理在 `Agent.cs:287-291`，不再被吞掉。 |
| **P1-2** 失败后重开被自己的锁挡住 | **已修复** | `InstallerService.cs:80-87`：mutex 在 using/finally 中释放后，才调用 `NotifyFailure`。 |
| **P1-3** 中断后的持久化门禁 | **已修复** | 顺序：先写 Applying journal（`:170`），再写 pending（`:174`），然后才开始 Apply（`:176`）。Complete 时清除 pending 在 `:200`，RolledBack 在 `:366`。`RollbackFailed` 和 `StartupUnconfirmed` 已分开（`:209, :227`）。Qt `canStartApplication` 在 `updatehost.cpp:311-324` 先读 pending，非终态时只放行身份完全匹配的 WaitingForStartup 子进程。.NET 在 `UpdateHost.cs:62-77, 102-149` 同样处理，记录损坏时拒绝启动。 |
| **P1-4** Manager 启动确认 | **已修复** | `ManagerLog/Program.cs:27` 在登录窗 Shown 时调用 `ReportStartupReady`，不需要人工登录，也不打开设备。`UpdateHost.cs:216-237` 有完整身份校验，ready 已存在且 Pid 相同时幂等，确认后清除环境变量。 |
| **P1-5** PreservePaths | **代码层面已修复** | `PublisherService.cs:110-113`：保留策略为空时必须显式 `--allow-empty-preserve`。`:242-259`：对两个已知 AppId 强制最低保留规则。安装前在 `InstallerService.cs:163-166` 再次校验。`Deploy-Client.ps1:23-24` 禁止隐式空数组。`IsPreserved`（`Support.cs:131-132`）固定保留 `JmdUpdater/`、`.jmd-update/`、`jmd-update.json`，所以 pending 和 installed 记录不会被打进安装包。本轮未读取实际部署配置，这部分以提供的内容为准。 |

本轮顺带核对、没发现问题的部分：
- `TransactionCleanup`（`:29-47`）只处理本 AppId、本目录下处于 Complete/RolledBack 的事务，保留最近 2 份，并有 14 天门槛，不删除无法核实的事务。
- 版本比较统一走 `NormalizedVersion`（`InstallerService.cs:311`、`PublisherService.cs:138`）。
- .NET 取消时会删除匹配的 ack（`UpdateHost.cs:263-271`）。

---

## B. 尚存问题

### B-0　P1（待确认，属于需求问题，不是代码缺陷）：`parms.db` 被永久保留后，新版本的数据库内容无法通过更新下发

- `ValidatePackage` 会拒绝任何包含保留路径的包（`InstallerService.cs:254`），所以 `parms.db` / `parms_en.db` 永远进不了更新包。
- 如果这两个库里不只是现场数据，还带有程序自带的参数（功能码）定义表，那么新固件需要的新参数表就永远到不了客户端。另外，新装客户端如果目录里还没有这个库，更新也补不上。
- 未核实这两个库的表结构，需确认它们是否只存用户数据。
- 如果库里也有程序自带的定义，建议拆分：定义表随包发布，现场的 Config 表留在保留库里；或者由宿主在启动时做 schema 迁移。

### B-1　P2：Installer 的退出等待从 staged 开始计时，Agent 也不确认 Installer 是否还在

- **位置**：`InstallerService.cs:156-157` 的 30 秒从写入 staged 开始算，这段时间包括了 prepare（最长约 7 秒）、shutdown（约 7 秒）和宿主关闭过程。`Agent.cs:271` 和 `:284` 在发 prepare/shutdown 之前，都没有检查 `installer.HasExited`。
- **触发场景**：宿主 commit 之后退出变慢（比如关串口卡住），或者部署时把 `ExitTimeoutSeconds` 调小（下限是 5 秒）。Installer 会先超时退出，随后宿主带着 ack 正常退出。
- **影响**：原程序已经关了，但既没有安装也没有重开，Agent 提示的却是"原程序未完成正常退出"。不会损坏文件，但违背了"失败后恢复操作"。
- **最小修复**：
  - 在 prepare 和 shutdown 之前检查 `installer.HasExited`，已退出就 cancel。
  - Agent 发 shutdown 之前写一个 `shutdown-requested.json`，Installer 的退出计时从这个文件出现时开始。

### B-2　P2：Installer 在取消或 staged 之前失败时，会额外弹一个误导性的对话框

- **位置**：`Program.cs:26-29`。`ThrowIfCancelled` 抛出的 `OperationCanceledException`、"Another product instance"、超时等异常，都会弹出"更新未完成：<英文原文>……请正常启动原软件"。
- **触发场景**：最后检查被拦住（例如电机还在使能）时，这是很常见的情况。这时 Agent 已经正确提示了"请失能后重试"，用户却会再看到一个英文对话框，内容还写着"请启动原软件"，而原软件其实一直在运行。
- **最小修复**：
  - 对 `OperationCanceledException` 不弹 UI；
  - 在 staged 之前失败时，由 Agent 统一提示，同时把 Installer 的失败原因写进事务目录（比如 `stage-failed.json`），让 Agent 能显示具体原因。现在 `Agent.cs:332` 只能显示"请查看日志"。

### B-3　P2：Manager 的 ready.json 不是原子写入，Installer 读取时遇到异常会把正常启动判成失败

- **位置**：`UpdateHost.cs:502-512` 的写法是 CreateNew，再 Write，文件对读方共享（FileShare.Read）。`InstallerService.cs:197` 在轮询循环里读取这个文件，解析异常会一路抛到 `:217`，进入回滚流程，然后被标成 `StartupUnconfirmed`，之后启动就会被拒绝。
- **概率**：很低，因为文件很小，一次就写完。但一旦发生，代价是一次正常的更新被判失败、只能 recover。Qt 端用的是 QSaveFile，没有这个问题。
- **最小修复**：二选一即可：
  - ready 改用"临时文件 + Move"写入；
  - 或者让 Installer 在循环里捕获解析异常并继续轮询。

### B-4　P2：Monitor 的启动拦截提示不精确

- **位置**：`main.cpp:15-18` 用的是固定文本 `<事务目录/job.json>`。Qt 的 `canStartApplication` 只返回 bool，而 .NET 端（`UpdateHost.cs:73-75`）会给出精确的恢复命令。
- **其他问题**：正常更新进行中（mutex 被持有）时，也显示同一条"请执行恢复"的提示。不过恢复本身会被 mutex 挡住，所以不会造成危害。
- **修复**：参照 .NET 的 `StartupBlockReason`，让 Qt 也输出原因和命令。

### B-5　P2：两端的小差异

- **被遗弃的 mutex**：Qt 拒绝启动一次（`updatehost.cpp:334`），.NET 放行（`UpdateHost.cs:87`）。因为有 pending 检查，两种做法都安全，只是行为不一致。
- **commit 之后的 cancel**：Qt 的 `cancelPreparation` 没有对 `m_committed` 做保护（`updatehost.cpp:184-191`），.NET 有（`UpdateHost.cs:260`）。如果 commit 之后进程没退出、又收到了 cancel，Qt 会删掉 ack，并重新启用已经关闭的主窗体所在的进程。文件仍然安全，但会留下一个没有窗口的残留进程。建议对齐 .NET 的做法。
- **环境变量**：Qt 的 `notifyStartupReady` 不清除 `JMD_UPDATE_READY_*`（.NET 会清除），目前看不出有影响。

### B-6　P2 / 残余风险（第一轮第 10 条）：在线设备的状态新鲜度窗口只有 3 秒

`mainwindow.cpp:177` 要求状态在 3 秒内，而 prepare 会停掉轮询（`:234`），之后 shutdown（`updatehost.cpp:400`）和 closeEvent（`mainwindow.cpp:271`）都要再检查一次。正常情况下这段时间不到 1 秒，但宿主 UI 一旦卡顿就会判成 Unknown 并取消。这个问题是 fail-closed 的，需要靠真机验证。

### 待确认 / 限制

- Qt 用的是环境变量 `LOCALAPPDATA`，.NET 用的是 `GetFolderPath`。如果两者不一致（比如做了文件夹重定向），目前的分析是会 fail-closed：prepare 失败；新版的合法重启被拒，然后自动回滚。这一点没有实测。
- Monitor 关闭主窗体后，如果还有其他可见的顶层窗口，`quitOnLastWindowClosed` 不会退出进程。这种情况会走 B-1 / B-5 的路径。需要在真机冒烟测试里确认进程真的退出了。

---

## C. 结论

**可以认可为后续部署的实现基线。** 第一轮的 P0-1 和 P1-1 到 P1-4 都已在实际代码中修复；P1-5 在代码层面成立，实际配置以提供的内容为准。

部署前需要做的事：
1. **确认 B-0**：`parms.db` 里是否带有程序自带的定义表。这决定了它能不能一直保留，属于需求层面的阻断。
2. **建议修掉 B-1 和 B-2**：改动小，能避免"原程序已关、却没有更新"，以及每次被拦截都多弹一个英文对话框。
3. B-3 到 B-6 可以排期处理。

**以下内容不能用本次只读审查代替：**
- 真机电机测试，包括使能、失能、多轴和在线状态新鲜度；
- 在第二台局域网机器上的测试；
- 尚在补充的测试：Agent 全链路合成测试、Applying 中断测试、Monitor 无设备关闭烟测；
- `LOCALAPPDATA` 重定向的情况；
- 实际 `jmd-update.json` 和 `server.json` 的内容。

**本轮实际读取的文件：**
- **模块**：`Agent/Agent.cs`（130–410 行）、`Agent/HostClient.cs`、`Agent/TransactionCleanup.cs`、`Installer/InstallerService.cs`（全文）、`Adapters/Qt/updatehost.cpp`（全文）、`Adapters/DotNet/UpdateHost.cs`（全文）、`Shared/Support.cs`（1–70 行及 `IsPreserved`、`LoadConfig`）、`Publisher/PublisherService.cs`（95–261 行）、`Program.cs`，以及对 `Delivery/*.ps1` 的 grep
- **Monitor**：`main.cpp`、`mainwindow.cpp`（100–285 行）、两个 `closeForUpdate` 函数
- **Manager**：`ManagerLog/Program.cs`