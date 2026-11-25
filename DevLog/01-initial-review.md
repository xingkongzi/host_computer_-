# JmdUpdater 只读审查结论（第 1 轮）

**结论：当前版本还不能认可。** Monitor 有一个 P0：更新路径实际走不通，而且每次尝试都会关掉设备通讯。另外有 4 个 P1 是确定的缺陷，1 个 P1 需要提供部署配置才能定论。

本轮只做了读文件，没有执行、编译或运行任何测试。

## 1. 实际读取的文件

- **模块**：`PROTOCOL.md`、`Shared/Contracts.cs`、`Shared/Support.cs`、`Agent/HostClient.cs`、`Agent/Agent.cs`、`Agent/UpdateSettings.cs`、`Program.cs`、`Installer/InstallerService.cs`、`Publisher/PublisherService.cs`、`Publisher/LocalUpdateServer.cs`、`Adapters/DotNet/UpdateHost.cs`、`Adapters/Qt/updatehost.h/.cpp`、`Delivery/*.ps1`（4 个）、`Tests/QtWire/client.cs`（第 40–69 行）
- **Monitor**：
  - `main.cpp`
  - `mainwindow.cpp` 第 50–310 行
  - `parameters.cpp` 的 840–909、975–1034、2255–2277 行
  - `commapp.cpp` 的 290–391 行，以及若干 grep 结果
  - `mainwindowparms.cpp` 的 195–239、1080–1134 行
  - `mainwindowplot.cpp` 的 735–829、1120–1184、1690–1734 行
- **Manager**：
  - `Updates/ManagerUpdates.cs`（全文）
  - `MainForm.cs` 的 40–199、415–444 行
  - `ManagerLog/Program.cs`、`ManagerLog/LogIn.cs` 前 70 行
  - `TSC_StationFormMediator.cs` 的 1920–1969、2450–2524、2760–2784、2870–2894 行
  - 各 csproj 的输出类型和引用（grep）
- **没有读**：`deployment/`、`keys/`、`*.private.xml`、凭据类文件。

## 2. 缺陷

### P0（确定）

**P0-1　Monitor 永远写不出关闭确认，而且失败前已经关掉了串口和工作窗口**

- **证据**
  - `Agent/Agent.cs:224` 只拼出了 ackPath；第 240 行只创建了 transaction 目录。整个生产代码里没有任何地方创建 `Sessions/<token>/`（已 grep 确认）。
  - `Adapters/Qt/updatehost.cpp:175`：父目录不存在时直接返回 false，所以 `commitShutdown()` 必定失败。.NET 版在 `UpdateHost.cs:425` 会自己建目录，所以 Manager 不受影响。
  - `Tests/QtWire/client.cs:54-55` 是测试自己先建好了这个目录，问题因此被掩盖。
- **触发后的过程**：`mainwindow.cpp:274-279` 先关参数窗口和示波窗口、停定时器、`closePort()`，然后才 `commitShutdown()`。commit 失败后执行 `event->ignore()`。租约恢复时（第 259 行），因为 `m_isOnline` 已经是 false，监控定时器不会重启。
- **实际影响**：Monitor 无法更新；每次尝试都会悄悄断开设备、关掉工作窗口，用户还要额外等大约 35 秒才看到"未正常退出"的提示。
- **最小修复**
  - Agent 在 prepare 之前 `Directory.CreateDirectory(SessionDirectory(token))` 并检查没有重解析点；或者 Qt 适配器在 receiptPath 校验通过后执行 `mkpath`。
  - closeEvent 在做不可逆清理之前，先确认 ack 目录可写。
  - 测试不要再替被测对象创建这个目录。

### P1（确定）

**P1-1　Agent 还没确认 Installer 能接手，就请求宿主关闭**

- **证据**：`Agent.cs:263-276` 在 `Process.Start(installer)` 之后马上发 `shutdown`。而 Installer 要到之后才会做这些事（`InstallerService.cs:66-69, 113-125`）：抢 mutex、读配置/Enabled、验签、检查 journal 是否已存在、ValidatePackage（最大 1GB 的哈希+解压）、`EnsureNoRunningProduct`。
- **触发场景**：
  - 包或签名校验失败，或者被管理员停用；
  - 同目录还有第二个实例；
  - 上一个 Installer 还在等待中（解压慢 + 宿主拒绝关闭 + 用户立刻重试），新 Installer 拿不到 mutex 直接抛异常。
- **影响**：宿主已经正常关闭，但没有更新、也没有重启。这违反了"失败时恢复操作"的要求。
- **最小修复**：Installer 在拿到 mutex、完成 ValidatePackage、确认除宿主 PID 外没有其他实例之后，原子写一个 `staged.json`。Agent 等到它出现（带超时）才发 `shutdown`，超时就 cancel。解压最好放在 prepare 之前，避免占用 30 秒租约。

**P1-2　安装失败回滚后，"是否重新打开原程序"会被自己的 mutex 拦住**

- **证据**：`InstallerService.cs:70` 在 mutex 的 using 块内调用 RunOwned，RunOwned 失败后调用 `NotifyFailure`（第 186 行），后者在第 468-471 行启动原程序并清掉 READY 环境变量。此时 mutex 仍被持有，`UpdateHost.cs:65-81` 和 `updatehost.cpp:237-247` 都会返回 false，原程序弹出"软件正在更新"后退出。
- **修复**：先释放 mutex，再弹提示和重启；或者把 NotifyFailure 移到 using 块之外。

**P1-3　断电或崩溃导致安装中断后，启动时没人检查 journal，会带着新旧混合的文件启动**

- **证据**：
  - `canStartApplication` / `CanStartApplication` 只看 mutex。重启后 mutex 已经不存在，State 为 `Applying` 或 `RollingBack` 的 journal 不会被任何人读取，恢复完全依赖用户知道 `recover` 命令。
  - 回滚自身失败时也写成 `NeedsRecovery`（`InstallerService.cs:181`），和"新版已在运行但未确认"用的是同一个状态，无法区分。
- **修复**：
  - 启动门禁扫描本用户 `Transactions/*/journal.json`：如果某个 job 的 InstallDirectory 与本程序一致，且 State 属于 Applying / RollingBack / RollbackFailed，就拒绝启动并给出恢复指引。
  - 把两种 NeedsRecovery 拆成两个状态。

**P1-4　Manager 的启动确认依赖人工登录，新版正常也可能被判失败**

- **证据**：`NotifyStartupReady` 只在 `MainForm.OnShown` 中调用（`UpdateHost.cs:132`，`MainForm.cs:54-57`）。在那之前，用户必须在登录窗口点登录（`LogIn.cs:33-45`），还要关掉一个提示框（`MainForm.cs:139-146`）。
- **影响**：60 秒内没人操作，就会走到 `InstallerService.cs:161-166`，弹出"更新尚未完成…执行 recover"。如果用户照做，会把一个正常的新版回滚掉。
- **修复**：在 `ManagerLog/Program.cs` 登录窗口显示之后（基本初始化成功）就确认启动；或者把 Manager 的 StartupTimeout 单独调大，并在提示里区分"新版已在运行"。

### P1（待确认：需要提供实际配置）

**P1-5　现场数据保留规则可能为空**

- `Deploy-Client.ps1:9,40` 默认 `PreservePaths=@()`。
- Monitor 会在安装目录写 `default.txt`（`mainwindowparms.cpp:207,1110`）和曲线属性/背景文件（`mainwindowplot.cpp:1159-1165`）。`parms.db` 是否会被写入尚未确认。Manager 的参数 Excel 也可能在程序目录。
- `PublisherService.cs:205-233` 只排除 preserve 规则和中间文件。如果在开发者运行过程序的 build 目录上发布，这些现场文件会进入安装包。安装时会覆盖它们（`InstallerService.cs:272-273`），或者删除旧清单中有而新清单中没有的文件（第 266-269 行）。
- **请提供**：两个产品实际 `jmd-update.json` 中的 PreservePaths，以及发布 ClientConfig 与客户端配置是否一致。建议：发布时如果源目录里出现已知现场文件就拒绝；部署时 PreservePaths 必须显式填写、不允许为空。

### P2

1. **宿主拒绝关闭的结果被吞掉**（`Agent.cs:269-274`）。之后还要等 Installer 退出，最长约 35 秒才 cancel 并提示。宿主自己已经解锁了，但用户看到提示被延迟，旧 Installer 也还挂着。建议拒绝时立刻提示，并通知 Installer 放弃。
2. **mutex 被遗弃时两端行为相反**：Qt 返回 false（`updatehost.cpp:241`），.NET 返回 true（`UpdateHost.cs:71`）。应该统一，最好和 P1-3 的 journal 检查合在一起处理。
3. **Manager 的 `CheckMotors` 有几个问题**：
   - 网络模式的 Unknown 判断（第 171 行）排在"未连接不适用"（第 174 行）之前，所以网络模式下即使从没连过也无法更新。
   - 构造函数会自动打开串口并标记"已连接"（`MainForm.cs:181-182`）。如果 TSC_List 为空，第 198 行会永久返回 Unknown。
   - 已移除的电机、或换过 COM 口后留下的旧记录会一直判为"过期"，只能重启解决。
   - 不在 TSC_SelectList 里、也从未被观测到的电机不会被检查。需要确认这类电机能不能被下发命令。
4. **Manager 缺少模态对话框检查**（Monitor 有 `activeModalWidget`）。`BeginInvoke` 会在模态循环里执行，`shutdown` 可能在一个打开的确认框底下关闭主窗体。
5. **曲线未保存时没有"显式放弃"的入口**。`mainwindowplot.cpp:1171,1701` 还挡住了"打开文件"和"新记录"，这是普通使用场景下的行为变化。是否提供"用户确认后丢弃"，待定。
6. **`Transactions/` 永不清理**：每次尝试都会留下包（最大 1GB）、stage 目录和备份。
7. **版本比较口径不一致**：Installer 用的是未归一化的 `new Version`（`InstallerService.cs:265`），Agent 会归一化（`Agent.cs:63-67`）。"1.2" 和 "1.2.0" 的判断结果会不同。
8. **.NET 的 `CancelPreparation` 不删除 ack 文件**（Qt 会删），两端不一致。因为有 PrepareId 绑定，目前无害。
9. **Qt 的 `checkNow` 在 Agent 退出后静默无效**（`m_started` 一直为 true），菜单点了没有任何反馈。
10. **Monitor 的状态新鲜度窗口只有 3 秒**，而 prepare 会停掉轮询。按 P1-1 修复后，staged 握手必须放在 prepare 之前，否则 shutdown 时状态容易已经过期。

### 已核对、没发现问题的部分

- 验签针对原始 payload 字节。
- ZIP 内容和清单一一对应，并做了逐文件哈希、路径和重解析点检查。
- 检查项缺失、重复或未知时都不放行。
- 过期的 cancel 不会取消更新的事务。
- 租约到期由宿主自己恢复。
- 所有路径都不会强杀产品进程；`Stop-LanServer` 只停它自己记录的那个服务器进程。
- 服务器有 IPv4 绑定、CIDR 白名单和并发上限；防火墙规则限定 Program、LocalAddress 和 RemoteAddress。
- Monitor 在 `sendDat` 加了传输层门禁，有命令深度计数，状态字随每帧应答刷新；多轴设备按 Unknown 处理。
- Manager 的门禁用 `TryEnterOperation`；关闭失败时恢复串口；commit 放在 `base.OnFormClosing` 之后。

## 3. 需求符合度

整体设计与已确认的要求一致：宿主拥有检查回调，Ready/Blocked/Unknown 三态，fail-closed，ack 和进程退出双重授权，不强杀，签名和回退，服务器 IP 可改，Enabled 可以关闭。

但目前不能认可，阻断项是：
- **P0-1**：Monitor 实际无法更新，而且会断开设备；
- **P1-1**："失败要恢复操作"在 Installer 早期失败时不成立；
- **P1-2**、**P1-3**：回退后的恢复和持久化不完整；
- **P1-4**：Manager 正常更新会误报失败；
- **P1-5**：需要给出实际 PreservePaths 才能定论。

## 4. 没有验证的范围

- 没有编译、运行任何代码或测试，也没有访问网络。
- 没有做真机电机测试，也没有在第二台局域网客户端上测试。
- `deployment/` 下的 `server.json` 和实际 `jmd-update.json`（BindAddress、AllowedCidrs、PreservePaths）没有读。
- Monitor 是否会写 `parms.db`、Manager 参数文件的位置、Manager 是否允许多开，都没有核实。
- Qt 的 `LOCALAPPDATA` 与 .NET `GetFolderPath` 在重定向配置下是否一致，没有核实。
- 命名管道 ACL 的实际效果、Qt 适配器在本机的构建，都没有验证。

修改后提供精确的文件和行号，针对这些点复核。