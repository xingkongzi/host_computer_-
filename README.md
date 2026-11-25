# JMD 局域网更新组件

支持正式与测试通道：正式目录仍为 `server.json` 的 `RootDirectory`（默认 `releases/`），测试目录为其下 `test/`。服务端通过可选 `ClientChannels` 按来访 IPv4 精确选择测试目录，缺文件回退正式目录；客户端协议和代码不变。配置、发布命令及演练说明见 [Delivery/README.md](Delivery/README.md)。

网络更新现在管理 `JmdUpdater/JmdUpdater.exe` 及存在时的 `.exe.config`，两者进入签名清单并支持事务回退。`JmdUpdater/public-key.xml` 是信任根，该目录其余内容、`jmd-update.json` 和 `.jmd-update/` 仍受保护（安装器自身维护的事务元数据除外）。旧版安装器会拒绝包含更新器的包：使用旧版更新器的电脑必须用新首装包重装一次，保留原现场数据与信任配置。

Agent 校验并运行本次事务 `installer/` 内的 exe/config 副本；安装前等待真实安装目录中同路径的 Agent/更新设置进程退出，最长 15 秒，不强杀。恢复可以从真实更新器自身运行，依靠允许改名的普通程序文件回退规则恢复自身映像；宿主入口 EXE 仍禁止改名。

宿主确认正常退出后，独立 STA 进度窗口显示检查、逐文件替换、启动及完成，完成后约 1.5 秒关闭。未改动或成功回退时自动打开原程序，原因窗口约 5 秒后关闭；恢复失败或启动未确认时保留恢复命令，用户手动关闭。`JMD_UPDATER_NO_UI=1` 完全关闭安装界面，并维持静默测试行为，不自动重开失败的程序。

适用于 Windows 桌面程序。主体不引用 Monitor 或 Manager 的业务程序集；检查回调与设备/工作区判定留在各项目。Qt 和 C# 通过各自的薄适配器调用同一个更新程序。

## 目录结构与移植

三个独立 Git 仓库按以下结构放置（命令示例均从 `JmdUpdater/` 根目录执行）：

```text
JMD_host_computer/
├─ JmdUpdater/
├─ SynJMD_Monitor/SynJMD_Monitor/
└─ SynJMD_Manager/SynJMD_Manager/
```

Monitor 仓库根通过 `../../JmdUpdater` 引用公共组件，qmake 的包含目录为 `$$PWD/../..`，Visual Studio 的包含目录为 `$(ProjectDir)..\..`，源码保留 `#include "JmdUpdater/Adapters/Qt/updatehost.h"`。Manager 的 `SynJMD_Manager/DriveSysView.csproj` 默认使用 `$(MSBuildThisFileDirectory)..\..\..\JmdUpdater`，也可用 `/p:JmdUpdaterRoot=绝对路径` 覆盖。

发布脚本以 JmdUpdater 的父目录定位两个产品仓库；`Publish-OneClick.ps1` 和 `Publish-Window.ps1` 支持 `-MonitorRepo`、`-ManagerRepo` 覆盖，窗口会将相同路径传入预览和发布请求。源码布局与产品安装目录不同：安装包仍将更新器放在产品程序旁的 `JmdUpdater/` 子目录。

移植到自己的其他电脑时整体复制 `JMD_host_computer/`，保留以上相对结构。`deployment/` 含私钥，只能本人携带；给他人复用时必须去掉 `deployment/`，并单独安全备份 `deployment/keys/`。Windows 代码签名证书及其私钥位于 Windows 证书库中，不会随目录复制迁移，需要另行迁移证书。

## 当前部署

- 发布电脑：`192.168.32.99`，TCP `8787`。
- 允许网段：`192.168.32.0/24`，在服务端和防火墙共同限制。
- 发布服务配置：`deployment/server.json`。`BindAddress`、`Port`、`AllowedCidrs` 均可修改。
- 客户端：程序旁的 `jmd-update.json`。主界面的更新设置可修改 IP/地址和 Enabled；启用后重启上位机生效。
- 普通检查只在宿主运行时存在。发布电脑可以安装登录启动项，客户端没有开机启动项。

`deployment/`、`bin/` 和测试输出被忽略。私钥仅在 `deployment/keys/private-key.xml`，不要发给客户端、提交版本库或放进公开发布目录。公钥随首次部署包分发；现有信任不会随网络更新被替换。

## 构建

使用 Windows 已安装的 .NET Framework 编译器，不需要下载 NuGet：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

输出 `bin/JmdUpdater.exe`。目标 API 为 .NET Framework 4.5；宿主仍使用各自既有框架。脚本的执行策略参数仅对该次子进程生效，不修改系统策略。

## 宿主接入契约

1. 在业务初始化前调用安装锁检查，防止替换过程中再次启动。
2. 创建适配器并注册固定的检查列表。项目回调返回 `Ready`、`Blocked(原因)` 或 `Unknown(原因)`，只读取已有本地状态。
3. 注册准备/取消钩子：在项目集中调度入口暂停新任务及编辑，失败、取消和租约到期时恢复。
4. 注册正常退出钩子，在真实正常收尾的最后阶段写关闭确认。
5. 主窗口及必要数据初始化成功后报告启动完成，再启动可选检查助手。

模块执行完整检查列表并统一提示。用户在原界面失能、退出任务或保存后手动重试。模块不发送设备指令、不自动保存工作区、不丢弃数据、不强杀宿主。未知、过期和缺失检查均阻止安装。没有受控设备的项目可以不注册电机检查；已注册检查不能在运行中漏报。

Qt 示例接入见 `Adapters/Qt/updatehost.h` 与 Monitor 的 `initializeUpdater()`。C# 示例见 `Adapters/DotNet/UpdateHost.cs` 与 Manager 的 `Updates/ManagerUpdates.cs`。Manager 的 MSBuild 属性 `JmdUpdaterRoot` 可指向单独复制的组件目录，无需依赖 Monitor 业务源码。

完整线协议、线程和确认路径见 [PROTOCOL.md](PROTOCOL.md)。

## 发布新版本

Windows 内部代码签名及 IT 信任部署见 [Delivery/SIGNING.md](Delivery/SIGNING.md)。它与下述 RSA 更新包签名分别验证 Windows 发布者和更新内容；当前内部策略不请求外部时间戳。

从干净的程序部署目录发布，不能从混有现场数据的工作目录直接打包。版本采用两到四段数字，必须递增。同一已发布版本不能被覆盖。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Publish-Release.ps1 -SourceDirectory "完整的干净程序目录" -ClientConfig "该目录\jmd-update.json" -EntryPoint "SynJMD_Monitor.exe" -Version "1.17.48.0" -NotesFile "更新说明.txt"
```

Manager 的入口为 `DriveManager.exe`。发布工具先生成完整 ZIP、SHA-256 和逐文件清单，再签名并原子更新 `<AppId>/latest.json`。程序自动生成与签名版本一致的 `jmd-version.json`。

`PreservePaths` 支持精确文件/目录前缀和 `*.扩展名`。两个产品的部署配置已列出各自的现场参数表、日志、生产记录、参数数据库和工作区数据。更新包不能覆盖这些数据。业务数据库迁移不属于当前版本的自动行为；确有格式升级时需单独设计并验证。

`RequiredPreservePaths` 是项目在配置中声明的最低数据保护要求；通用核心只验证声明，不按产品名写死业务文件。两个示例配置给出了本次的项目策略。只读检查确认 Monitor 的数据库同时包含功能定义表和用户 Config 表，本次数据库二进制与设备参数兼容版本 `0x46` 均未改变，所以网络更新保留整个库。基础包携带默认定义库；未来若功能定义变化，应由宿主设计独立的数据迁移/拆分并验证，然后调整项目的数据策略，不能直接覆盖现场混合数据库。

## 首次部署与 IP 修改

已有旧版没有更新接口，必须先部署一次基础版本。分发基础包时，把程序文件放入原安装目录，保留原现场数据和配置；不要用发布电脑的 Excel/生产记录替代现场文件。Manager 的基础包不携带现场 `DriveParms.xlsx`/`ManagerSet.xlsx`，新电脑需另行配置自己的站点表。

Manager 在构造登录界面前，通过 .NET 的默认设置 Provider 一次性迁移旧版用户配置；失败会停止启动并保留重试标志。通信设置及原有合法授权/使用计数原样继承，许可判定不变。自动继承要求原 EXE 文件名和安装目录保持不变；首次手动部署请备份原目录并就地替换程序文件，不要把“改装到新目录”当作同目录网络升级。若旧用户配置损坏或目录不可写，请先备份并由维护人员修复，不会静默用默认值继续连接设备。

后续通过原程序中的“检查软件更新/检查更新”入口操作。在“更新设置”中输入 IP（例如 `192.168.32.99`，默认端口 8787）或完整地址。改变发布机地址时，同时修改 `deployment/server.json`、客户端服务器地址并按新地址核验防火墙规则。自动检查失败不影响原程序工作。

服务启动与停止：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Start-LanServer.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Stop-LanServer.ps1
```

服务复制到 `deployment/server-bin/` 运行，不锁住开发构建输出。修改服务端后先停止，再用新构建启动。可选登录启动：`Delivery/Install-ServerStartup.ps1`；仅写发布电脑当前用户的启动项，未登录时不会启动服务。

修改监听 IP、端口或允许网段后也需要先停止再启动服务，并核验对应防火墙规则。复用组件请使用去除 `deployment/` 的源码包；不要把本机发布私钥连同整个运行目录复制给其他项目。

防火墙安装需要管理员权限。规则限定本地 IP、端口、程序及远程网段。`FirewallProfiles` 应与实际网络分类匹配；本机 WLAN 当前属于 Public，因此实际部署显式包含 Public，并继续保持上述地址范围。脚本不会修改系统网络分类。

本机发布后的实际 HTTP 下载、签名、逐文件清单和基础包哈希可用 `Delivery/Verify-LanDeployment.ps1 -CompareWithLocalBinaries` 核验；它只下载到独立临时目录，不启动或替换产品。

## 失败处理

- 条件不满足：返回原软件处理，点击更新/重试。
- 下载、签名或校验失败：不安装。
- 关闭被拒、没有匹配关闭确认、存在其他实例：不替换程序文件。
- 安装失败且程序未运行：用持久化备份恢复程序文件。
- 新版仍在运行但未报告初始化成功：保持 `NeedsRecovery`，不强杀、不在线覆盖。先正常关闭，再按错误提示执行 `recover --job <对应事务的job.json>`。
- 文件替换开始后停用更新，不会把事务中断在半写状态；它完成或恢复后停止。

组件保留最近两次已完成/已恢复的备份，其他带 Complete/RolledBack 记录的事务超过 14 天后清理。未完成或无法核实的恢复记录不会自动删除，应先处理恢复再清理缓存。

安装需要当前用户对程序目录有写权限。受保护目录需在部署时安排正确权限；本组件不会自动放宽目录权限。

## 验证

核心签名/路径/检查结果测试：`Tests/run-core.ps1`。服务器 socket 和签名包/安装事务测试见 `Tests/Delivery*Tests.py`。真实 Qt↔.NET 管道测试见 `Tests/QtWire`。Manager 专属状态和保存失败测试位于其 `Updates/Tests`。测试不连接电机。

真实设备联调和另一台局域网电脑验证，需在相应现场完成；本机构建与合成进程测试不能代替这些验证。
