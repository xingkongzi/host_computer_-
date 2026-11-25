# 更新器自更新与安装进度

日期：2026-11-11。

## 起因

原保留规则把整个 JmdUpdater 目录排除，导致网络更新无法升级更新器。Agent 随宿主退出后，安装过程没有可见反馈；失败提示需要用户选择是否重开，且常见原因是英文。

## 设计

- JmdUpdater 目录只开放 JmdUpdater.exe 和可选的 JmdUpdater.exe.config。其他路径继续保留，尤其 public-key.xml 信任根不能从包内更换。Publisher 只对该目录增加遍历入口，文件仍按统一策略过滤。现有 Deploy-Client → Sign-Release → Publisher 顺序保证干净目录中的更新器先签名，再计算清单哈希；发布脚本补充说明这一约束。
- Agent 在事务 installer/ 中复制自己的 exe/config，分别校验 SHA-256 后运行副本。安装目标始终由 job.InstallDirectory 解析。
- 变更前检查与实际替换前，按完整映像路径等待其他更新器进程退出，上限使用 LockWaitSeconds。更新器走普通程序文件的 SwapIn 路径，允许映像改名回退；宿主入口仍禁止改名。更新器自动进入 journal 和备份，recover / recover --restart 可以恢复正在运行的自身映像。
- 宿主正常退出确认通过后，原线程继续安装，独立 STA 线程管理置顶进度窗口。安装期间拒绝关闭，逐文件显示进度。成功约 1.5 秒后关闭。失败在释放安装锁后处理：RolledBack 自动重开原程序并约 5 秒后关闭提示；RollbackFailed / StartupUnconfirmed 保留可复制、可滚动查看的恢复命令，等待用户关闭。
- JMD_UPDATER_NO_UI=1 不创建 UI 线程或窗口，也维持原有静默失败时不重开的行为。三层检查、入口文件替换限制、清理策略和宿主自动恢复协议没有放宽。
- 旧安装器不能接受新包；旧电脑需使用新首装包就地重装一次，保留现场数据与信任配置。

## 实际验证结果

| 套件 | 结果 |
|---|---|
| powershell -File ./build.ps1 | 通过；现有反射测试注入字段 SimulateReplaceRefused 有 CS0649 警告 |
| Tests/run-core.ps1 | 39 项断言通过，含精确路径保护与 exe/config 副本哈希 |
| Tests/DeliveryPackageTests.py | 18 项通过，含更新器入包、公钥排除、事务回退、真实 STA 窗口关闭保护/进度/自动关闭，以及四类失败结果的自动重开行为 |
| Tests/DeliveryServerTests.py | 5 项通过 |
| Tests/SigningTests.py | 8 项通过；只验证签名门禁，未请求生产签名 |
| Tests/DeliveryProcessTests.py，仓库原路径 | 21 项在初始化事务目录时受沙箱 WinError 5 阻止，无法完成原路径验证 |
| Tests/AgentFlow/run.ps1，仓库原路径 | 编译通过；因沙箱无法写 LocalApplicationData，首场景无法创建事务 |
| DeliveryProcessTests，临时副本补充运行 | 21 项全部通过，含同路径更新器进程等待、自身 recover、宿主自动 recover --restart 恢复自身、无 UI、原有锁等待/不强杀/清理场景 |
| AgentFlow，临时副本补充运行 | 4 场景、76 项断言通过，含真实下载、事务副本启动、更新器替换、公钥保留和拒绝退出 |

补充运行只在获准写入的临时目录复制必要源码，将测试副本的 LocalApplicationData 定位表达式替换到独立 UserCache，连同测试适配器/合成宿主保持路径一致；进程套件还使用独立合成 EXE 名，避免机器上无法检查路径的同名 Fixture 进程触发现有安全拒绝。未改变生产文件的缓存定位或进程安全检查。临时副本的路径替换不等同于原路径套件通过。

补充测试最初发现 Python 默认 GBK 解码中文 journal，以及合成程序在普通重开时尝试写空确认路径。测试已显式使用 UTF-8，并让合成程序只在确认路径存在时写确认，随后上述补充套件通过。

未修改 Adapters，因此没有触发 Monitor/Manager 额外构建。未验证真实设备、另一台局域网电脑、生产 Authenticode 签名或实际发布；没有访问 deployment、操作发布服务、提交或推送。UI 生命周期和结果分支经过真实 WinForms 自动化验证，未做不同 DPI/桌面主题下的人工视觉验收。

## 本轮对抗复审修复（2026-11-11）

复审确认三处问题：恢复入口依赖正在恢复的安装目录更新器；旧 Publisher 包缺少更新器导致差集删除；MainModule 查询同名高权限进程会因权限不足失败。

- Qt、DotNet 宿主从已绑定校验的 pending 事务 job 路径定位 installer/JmdUpdater.exe，自动 recover --restart 优先启动 Agent 已复制并校验的事务副本。仅副本不存在时回退到安装目录更新器，副本存在但启动失败仍拒绝启动宿主。NotifyFailure 显示事务副本恢复命令，同时列出安装目录版本的备用命令。恢复安装目录更新器时不再依赖运行中的目标映像，正常走原子替换。
- TransactionCleanup 原有非终态 journal 保留逻辑不变，增加 pending 引用保护：收集和实际删除前均检查；仍被 pending 指向的事务，即使 journal 已写终态也保留；pending 不可读时保守保留。
- PlanChanges 永不因新清单缺席而删除 JmdUpdater.exe / JmdUpdater.exe.config。旧清单仍验签，新包存在这些文件时仍按原有哈希比较和替换流程更新。
- WaitForUpdaterExit 使用 OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 和 QueryFullProcessImageNameW 比较完整路径；EnsureNoRunningProduct / EnsureNoOtherRunningProduct 复用此方法。无法核实身份时给出含 PID、Windows 错误码的中文原因并拒绝，未降低安全门禁。
- 回归覆盖：事务副本恢复现存/缺失的更新器；宿主在安装目录更新器无效/缺失时自动恢复；上一签名清单含 exe/config 而新包缺席时完整安装仍保留二者；映像路径查询成功/无法确认分支；失败窗口显示主恢复命令和备用命令；pending 引用与损坏记录的保留判定。

本轮修改文件：Adapters/Qt/updatehost.cpp、Adapters/DotNet/UpdateHost.cs、Agent/TransactionCleanup.cs、Installer/InstallerService.cs、Tests/CoreTests.cs、Tests/DeliveryPackageTests.py、Tests/DeliveryProcessTests.py 及本日志。上一轮其他未提交改动保留。

### 本轮实际验证

| 检查 | 结果 |
|---|---|
| build.ps1 | 通过；仅现有测试注入字段 SimulateReplaceRefused 的 CS0649 警告 |
| run-core.ps1 | 42 项断言通过，新增 pending 保留判定 |
| DeliveryPackageTests.py | 19 项通过，含低权限映像查询及失败窗口双恢复命令 |
| DeliveryServerTests.py | 5 项通过 |
| SigningTests.py | 8 项通过，未请求生产签名 |
| DeliveryProcessTests.py 原路径 | 24 项均在 setUp 创建 LocalAppData 事务时被沙箱 WinError 5 阻止 |
| AgentFlow/run.ps1 原路径 | 编译通过；valid 场景因 LocalAppData 事务写入受限失败 |
| DeliveryProcessTests.py 临时副本 | 24 项通过，含本轮恢复入口及旧包保留回归 |
| AgentFlow/run.ps1 临时副本 | 4 场景、76 项断言通过 |
| Monitor Release/x64 | 通过，实际编译 updatehost.cpp；QtMsBuild=%LOCALAPPDATA%\QtMsBuild，补充 /p:QTDIR=D:\Qt\5.12.2\msvc2017_64 后解决默认 QTDIR 缺失导致的 moc_predefs 失败；OutDir 指向临时目录。保留 MSB8029 警告 |
| ManagerLog.csproj Release | 通过，依赖 DriveSysView 编译链接本仓库 DotNet adapter；OutDir、IntermediateOutputPath、PublishDir 指向临时目录，DocumentationFile 设空，避免依赖项目 XML 输出和默认 app.publish 构建清理触及只读目录。仅运行 Build，未执行 Publish。仍有现有 COM 类型库未注册等警告 |
| git diff --check | 通过 |

临时源码副本位于 `%TEMP%/JmdReview-gwwma0eq`；仅替换副本中的 LocalApplicationData 表达式及 Python 缓存路径为独立 UserCache，并用 ReviewFixture.exe 隔离机器上已有同名进程。生产缓存定位和进程检查未改。临时副本通过不代表原路径通过。补充运行最初旧包回归因测试复用已有发布目录发布较低版本而被正常拒绝，已改用独立 previous 发布目录后通过。

未验证：真实管理员发布服务的权限组合（本轮使用映像查询函数单元级覆盖）、Qt 宿主真实自动恢复运行（DotNet 宿主有进程集成测试，Qt 已编译）、真实设备、生产签名、实际发布及人工 DPI/主题视觉检查。本轮未修改 deployment，未提交 Git，未启停服务。
