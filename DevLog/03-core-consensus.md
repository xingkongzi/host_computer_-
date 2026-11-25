# JmdUpdater 只读收敛审查（第 3 轮）

**结论：B0 的范围认可，B1 到 B5 都已在当前源码中修复。没有发现确定的阻断项，也没有修复带来的 P0/P1 级新缺陷。认可用当前基线做本机部署，并分发基础版本。**

本轮只用 Read/Grep/Glob 读了源码和配置，没有修改文件，没有执行命令或测试，没有联网，也没有读 keys/ 和 deployment/ 下的私钥。列出的测试结果未复现，下面引用时以提供的结果为准。

---

## 1) B0 范围，以及 B1–B5 逐项核实

**B0：认可。**
- 核心已经和业务解耦。在 Shared、Publisher、Installer、Agent 里 grep `SynJMD|parms|default.txt|xlsx`，没有任何匹配。
- 现在的规则全靠配置声明，三处一致：
  - `Contracts.cs:24,32`：`RequiredPreservePaths` 默认是空数组；
  - `PublisherService.cs:242-258`：`ValidateRequired` 对每条声明构造一个探针路径，用 `IsPreserved` 检查是否被保留规则覆盖，空声明直接拒绝。发布时（`:110`）、安装预检时（`InstallerService.cs:262`）、应用文件前（`:184`）都会调用；
  - `Deploy-Client.ps1:10,26-45,64`：用同样的覆盖逻辑做校验。
- 两个实际部署配置已核对，都是 `Enabled=true`：
  - `dist/SynJMD_Monitor_V01.17.47.../jmd-update.json`：Required 是 6 项，全部被 Preserve 覆盖，还包含 `*.db-wal/-shm/-journal`、`data`、`Logs`；
  - `dist/SynJMD_Manager_1.1.1.128.../jmd-update.json`：Required 是 3 项，也都被覆盖。
- README 第 49、51 行写清了"库是混合库，整库保留，未来定义升级需要宿主自己做迁移"。本版库和 `0x46` 都没变，所以这个限制本版不构成阻断。
- 要提醒的一点：通过网络更新，补不上客户端缺失的库，这类情况只能靠基础部署包解决。README 已有这层含义。

**B1：已修复。**
- `Agent.cs:269`：prepare 之前检查 `installer.HasExited`；`:280` 在发 shutdown 之前再检查一次。
- `:284`：原子写入 `shutdown-requested.json`，之后才在 `:288` 发 RPC。
- `InstallerService.cs:176-178`：先写 staged，再进 `WaitForShutdownRequest`，然后才进 `WaitForNormalExit`。
- `:453-469`：等待循环每一轮先做 cancel 检查，再看信号是否匹配，再看原 host 是否还在，最后判断 StageTimeout（180s）。
- `:438-451`：ExitTimeout 只从这时才开始计时，退出后必须拿到匹配的 ack。
- Agent 那边的 `WaitForAcknowledgedExit`（`Agent.cs:369-379`）给的窗口是 ExitTimeout+2，而且从 RPC 返回后才开始计时，所以总比 Installer 的窗口宽，两边判断不会互相矛盾。

**B2：已修复。**
- `InstallerService.cs:95-104`：`outcome==null` 时写 `stage-failed.json`，内容包括 PrepareId、Message 和 Code（Cancelled 或 PreflightFailed）。
- `Program.cs:23-32`：只有 `recover` 才弹窗，install 失败只输出到控制台。
- `Agent.cs:337-352`：读取失败记录。Cancelled 映射成"已取消"，不会重复提醒；其他情况统一提示"原软件保持运行"并附上原因。
- 应用文件之后的失败，仍然在 `:107` 解锁之后由 `NotifyFailure` 负责。

**B3：已修复。**
- `UpdateHost.cs:502-522`：在同一目录写随机临时文件，用 `Share.None` 和 `Flush(true)`，再 `File.Move`。目标文件已存在时 Move 会抛异常，所以不会覆盖已有确认。
- `:505/507` 的 `SamePath(path,path)` 看上去是空操作，实际是在做沿路径逐级的 reparse 检查（`:485-497`），功能没问题，只是命名容易让人误解。
- `:226-231`：ready 已存在时，只有 Token 和 PID 都匹配才当作幂等返回，不会覆盖。
- `InstallerService.cs:471-479`：瞬时的 IO、JSON 或权限异常一律返回 null，继续轮询。`:218` 中 Token 或 PID 不对时不会通过，会等到超时或进程退出。

**B4/B5：已修复。**
- `updatehost.cpp:318-379`：
  - pending 记录读不出来时，给出具体文件路径；
  - 事务未完成时，给出完整的 `"…JmdUpdater.exe" recover --job "…job.json"` 命令（`:324-328, 346`）；
  - mutex 被持有（`WAIT_TIMEOUT`）时，给出"正在进行，请等待"，只放行身份匹配的重启进程（`:371-373`）；
  - abandoned 的 mutex 释放后，交给 pending 判定（`:365-368`），和 .NET 的行为一致。
- `:184-194`：`cancelPreparation` 的第一步就是 `if (m_committed) return;`。
- `:264-275`：只有在 ready 写入成功后，才清除两个环境变量。
- `main.cpp:15-19`：直接显示 `startupBlockReason` 返回的确切原因。

## 2) 阻断项和新缺陷

**确定的阻断项：无。修复引入的重要缺陷：无。** 只有下面几个 P3，可以以后再处理：
1. `Agent.cs:280` 的检查和 `:288` 的 RPC 之间理论上有一个竞争窗口：Installer 恰好在这段时间退出，host 收到 ack 后关闭，结果是没有安装。前提是 Installer 的 180s 等待恰好在这几毫秒里到期，实际上几乎不会发生；即使发生也是 fail-closed，文件不会受损。
2. stage-failed 里的 Message 是 Installer 原来的英文文本，会以"原因：Another product instance…"的形式夹在中文提示里，只是观感问题。
3. 在 ready 已存在的情况下，Qt 用 QSaveFile 会覆盖，.NET 不会覆盖。Installer 启动新进程前会删除旧的 ready，还会校验 PID，所以不影响正确性。

## 3) 能否用于部署

**认可当前基线进行本机部署，并分发基础版本。** 前提是基础包里带上默认参考库，并且当前的 `jmd-update.json` 原样随包下发。

这次只读审查不能代替下面这些验证，它们也还没有做：
- 实际电机的使能/失能测试、多轴和网络模式，以及 3 秒状态新鲜度在真机上的表现；
- 第二台局域网电脑上的端到端验收；
- `LOCALAPPDATA` 被重定向的场景，目前按分析会 fail-closed，但没有实测；
- Monitor 关闭主窗体后，如果还有其他顶层窗口，进程是否真的退出，需要现场冒烟确认。

建议首次分发后，在一台现场机器上做一次真实的"停机状态更新"，同时观察一次"使能状态被拦截"，然后再扩大范围。

本轮读取的文件：
- `Agent.cs` 235-404
- `InstallerService.cs` 50-299、440-549、655-675
- `Program.cs` 全文
- `PublisherService.cs` 95-259
- `Contracts.cs` 1-40
- `UpdateHost.cs` 210-284、470-522
- `updatehost.cpp` 150-379
- `Monitor/main.cpp`
- `Deploy-Client.ps1` 1-70
- 两个示例配置、两个 dist 下的部署配置
- README 中 grep 到的相关段落