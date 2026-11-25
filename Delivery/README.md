# 局域网发布与客户端部署

所有命令均从独立的 `JmdUpdater/` 仓库根目录执行。默认从其父目录定位 `SynJMD_Monitor/SynJMD_Monitor` 和 `SynJMD_Manager/SynJMD_Manager`；发布入口可通过 `-MonitorRepo`、`-ManagerRepo` 覆盖。目录移植与密钥携带要求见 [主 README](../README.md#目录结构与移植)。

## 正式与测试通道

正式通道为 `server.json.RootDirectory`（默认相对配置目录的 `releases/`）；测试通道为 `<RootDirectory>/test/`。两处均使用 `<AppId>/latest.json`、版本 ZIP 和 `*.release.json`。

配置可加入 `"ClientChannels": { "192.168.173.54": "test" }`。键必须为标准点分十进制 IPv4，值必须满足 `^[A-Za-z0-9_-]{1,32}$`，非法配置在监听前报错。IP 精确匹配，不采用 HTTP 转发头；CIDR 与防火墙限制仍须单独允许该 IP。省略此项时保持现有访问行为。

名单 IP 请求 `<AppId>/<file>` 时优先查 `<channel>/<AppId>/<file>`，缺文件回退正式目录，清单和 ZIP 都适用。显式 `/test/<AppId>/...`（以及配置中的其他通道前缀）不会再次加通道。路径安全、重解析点拒绝、JSON/ZIP 白名单和 GET/HEAD 限制仍适用。测试目录是公开只读发布目录；“仅测试通道可见”表示默认路由分流，不是访问权限隔离。本机测试客户端也可将 ServerUrl 设置为 `http://地址:端口/test/`。

选产品后可交互选择 `1 = 正式发布（所有人）` 或 `2 = 测试发布（只给测试通道）`，也可使用：

```powershell
.\Delivery\Publish-OneClick.ps1 -Product Monitor -Channel test -Yes -Notes '测试更新说明'
.\Delivery\Publish-OneClick.ps1 -Product Monitor -Channel official -Yes -Notes '正式更新说明'
```

默认参数为 `official`。正式建议值和校验合并正式与测试两条通道历史（清单文件名及 latest），目标必须高于两者最高版本；显式冲突在发布前拒绝。Monitor 正式版继续递增第三段、第四段归零；测试版基于正式当前版前三段，取本基线已用最大第四段加一，且必须高于测试通道所有历史版本。没有正式版或测试目录已有更高基线时拒绝测试发布。源码仍可作为未发布的更高正式版默认值；若源码残留测试第四段，则建议下一第三段正式版。Monitor 关于页仍显示三段 `Vaa.bb.cc`。

例如正式 `1.17.56.0` 后依次测试 `1.17.56.1`、`1.17.56.2`，下个正式为 `1.17.57.0`。Manager 正式最高 `1.1.1.128`、测试最高 `1.1.1.132` 时，下个正式建议 `1.1.1.133`。

正式发布先写正式目录并通过 `/official/` 核验，再用 `Mirror-Release.ps1` 原样复制正式 ZIP 和已签名 release.json，最后原子替换测试 latest，通过 `/test/` 核验。镜像不重新压缩或签名；中途失败可重试，已有不可变文件哈希不同则拒绝。函数持有正式及测试产品目录中与 Publisher 相同的 `.publish.lock`（FileShare.None），在锁内读取测试 latest：高于目标则跳过并输出说明；等于目标时只有 latest、ZIP 和历史清单字节一致才成功。锁被占用则拒绝本次复制，可稍后重试。测试发布只写测试目录，更新说明末尾追加 `（测试通道版本）`。

真正发布前由 `Server-Lifecycle.ps1` 比较配置及 server.pid 记录的实际运行 EXE 和 .config；运行路径不同于 server-bin 下 active 路径时强制重启。无变化且进程身份有效时不重启。有变化先用候选二进制 `serve --config <路径> --check` 校验 ClientChannels、端口、已存在根目录、本机 BindAddress 和 CIDR，不监听、不创建根目录。`official` 是保留通道名（不区分大小写）；`/official/<AppId>/<file>` 始终读取正式根目录，不做 IP 分流。

校验成功才停旧进程、启动候选，并 GET 已知缺失路径 `/official/__jmd_health__/missing.json`，期望 404。健康成功后写入独立 `last-good/<时间戳-guid>/`，含 EXE、可选 .config、根目录转为绝对路径的 server.json 和 hashes.json。全部写完并校验 SHA256 后原子替换 `last-good.json` 指针；回滚只读取并校验指针指向的一整代。中途失败不影响旧指针及旧代；当前保留所有旧代（含上一代），不自动清理。没有新指针时兼容旧格式 `server.last-good.json` 与 `server-bin.last-good/`；指针损坏时不回退旧格式。启动或健康失败时恢复上次健康配置及二进制、重新健康检查，然后报错退出；候选配置留在 `server.failed.json`。

没有运行服务且没有快照时允许直接首次启动，启动入口显式创建 RootDirectory 后再校验。运行中服务没有快照时，自动重启在停止前拒绝；需在获准维护时建立健康快照。PID、启动时间、路径校验及 Process.Kill 停止方式保留，不自动修改防火墙。DryRun 仅操作临时回环服务。

安全演练（在 VS2017 x64 开发者环境中运行，并设置 `QTDIR` 对应 Qt 安装）：

```powershell
$env:QTDIR = 'D:\Qt\5.12.2\msvc2017_64'
.\Delivery\Publish-OneClick.ps1 -Product Monitor -DryRun -Channel test -Yes -NoPause
.\Delivery\Publish-OneClick.ps1 -Product Monitor -DryRun -Channel official -Yes -NoPause
```

演练不读写 deployment：在 TEMP 中生成临时 RSA 密钥、配置、日志及发布目录，启动并最终停止临时回环服务，实际下载清单和 ZIP 核对版本、大小与 SHA256。演练不调用真实 Authenticode 签名，使用 `Publish-Release -IsolatedDryRun`（要求发布输入均位于 TEMP，跳过默认签名策略发现）。实际编译产品，但不改源码版本：程序内嵌版本保留源码值，清单及 build-info 使用演练目标版本，不用于安装验证。测试演练没有正式目录时以源码版本模拟正式基线。真实发布仍执行产品签名策略；正式镜像原样复制已签名文件。

版本函数在 `OneClick-Versions.ps1`，可用 `Tests/OneClickVersionTests.ps1` 独立验证，无部署配置依赖。`Tests/DeliveryLifecycleTests.ps1` 在 TEMP 用真实 serve 和回环端口验证镜像字节、重复及过期重试、同版字节冲突、发布锁互斥、空部署首次启动、实际运行路径迁移、.config 变化、快照中途写入失败、旧格式回滚、无快照拒绝、无变化不重启、非法配置保留旧进程、健康失败回滚、启动崩溃回滚及端口/根目录变更。

此目录只负责可复用更新器的部署。设备状态和未保存工作区的判断代码仍在各宿主项目中。不要把私钥、生产记录或正在使用的工作目录当成发布源。

## 本机发布服务

构建入口为模块根目录 `build.ps1`，输出 `bin/JmdUpdater.exe`。部署目录 `deployment/` 已由模块自己的 `.gitignore` 排除。

Start-LanServer 按配置目录解析相对 RootDirectory，缺失时显式创建后再校验；直接运行 --check 仍无副作用。首次部署时创建目录并生成密钥（已经存在的密钥不能覆盖；应备份私钥并保留公钥指纹）：

```powershell
New-Item -ItemType Directory -Path .\deployment\keys -Force
.\bin\JmdUpdater.exe keygen --private .\deployment\keys\private-key.xml --public .\deployment\keys\public-key.xml
Copy-Item .\Delivery\server.example.json .\deployment\server.json
# 先编辑 server.json；以下是示例默认 RootDirectory，自定义路径时创建对应目录。
New-Item -ItemType Directory -Path .\deployment\releases -Force
powershell -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Start-LanServer.ps1
```

`server.json` 的 `BindAddress`、`Port`、`AllowedCidrs` 控制监听地址与访问网段。当前部署使用 `192.168.32.99:8787`，仅允许 `192.168.32.0/24` 与本机回环；监听地址必须确实存在于本机。服务仅支持 GET/HEAD，没有远程上传、目录索引和命令执行接口。发布目录之外的文件不可访问，公开文件限 JSON 和 ZIP。

可选 `DeniedCidrs` 是黑名单，优先于 `AllowedCidrs`：例如 `"DeniedCidrs": ["192.168.32.50", "192.168.32.128/28"]`，单个 IP 等同 `/32`。命中黑名单或不在白名单内的地址返回 403，每个被拒 IP 在本次服务运行期间只在 `deployment/logs/server.stdout.log` 记一行 `Refused <IP> (...)`，可据此确认被拒的电脑。修改后按下文先停止再启动服务生效。

脚本会把构建结果复制到 `deployment/server-bin/` 后隐藏启动，因此服务运行期间仍可重新构建模块。`server.pid` 记录实际 PID、启动时间和程序路径。停止脚本核对三者后只结束该只读服务器进程：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Stop-LanServer.ps1
```

修改监听地址或升级服务端程序时，先停止服务、编辑配置，再启动。不会修改 Windows 网卡地址，也不会自动注册开机任务。

## 防火墙

默认不更改防火墙。经审阅后，可在管理员 PowerShell 中给启动脚本增加 `-InstallFirewall`。规则同时限定实际服务程序、TCP 端口、本地地址、远端 CIDR 和网络配置文件，不接受 `Any` 或远端 `0.0.0.0/0`。

示例配置的 `FirewallProfiles` 是 `Private`、`Domain`。若当前 WLAN 被 Windows 分类为 Public，应在确认仅允许指定内网 CIDR 后显式加入 `Public`；仅设置 Private 并不能在 Public 网络上放通。当前实际部署已明确配置这三个 profile，远端仍仅为 `192.168.32.0/24`。

已存在的同名规则默认不会覆盖。审阅变更后用 `-InstallFirewall -UpdateExistingFirewall` 才可更新；脚本还会确认原规则指向同一个程序。

## 首次给客户端启用更新

先编译带宿主接口的程序，再对其完整、干净的部署目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Deploy-Client.ps1 `
  -DestinationDirectory 'D:\ProductFolder' -AppId 'SynJMD_Monitor' -DisplayName 'SynJMD Monitor' `
  -Version '1.17.46' -ServerUrl 'http://192.168.32.99:8787/' `
  -PreservePaths 'default.txt,parms.db,parms_en.db,*.csv,*.log,*.cuv,data,logs' `
  -RequiredPreservePaths 'default.txt,parms.db,parms_en.db,*.csv,*.log,*.cuv'
```

命令会部署 `JmdUpdater/JmdUpdater.exe`、`JmdUpdater/public-key.xml`、根目录的 `jmd-update.json` 和首次版本文件 `jmd-version.json`。已有配置默认保留并拒绝覆盖，确实需要重设时增加 `-ReplaceConfiguration`。`Enabled=false` 可停用更新。只有公钥随客户端分发，私钥留在发布端。

每个产品使用自己的 AppId、入口程序和保留路径。部署必须显式填写保留策略，`RequiredPreservePaths` 由宿主集成声明最低保护范围，通用模块只验证这些声明确实被 `PreservePaths` 覆盖，不包含任何特定产品分支。本次两个项目的声明见模块根目录对应配置示例：Monitor 保留默认工作区、两份参数数据库、CSV、日志和导出曲线，Manager 保留 Excel、CSV 和日志。通用新项目确实没有本地数据时，部署及发布分别用 `-AllowEmptyPreservePaths` 明确声明。路径规则支持具体文件、目录前缀以及 `*.csv` 这种后缀规则；它们描述的是程序目录内的数据保留边界。程序目录之外的 .NET 用户配置仍由宿主按自身规则保留/迁移。

## 发布新版本

当前两个产品已配置内部 Windows 代码签名策略。发布脚本自动读取 `deployment/signing.<AppId>.json`，先签名并验证，再计算更新包哈希；本机私钥只在发布账号的证书存储中。默认不请求外部时间戳。IT 的首次信任部署、完整包签名顺序及证书续期见 [SIGNING.md](SIGNING.md)。

从**干净的完整部署目录**打包，不从混有 OBJ/PDB、日志和现场数据的编译输出目录直接发布。先准备 UTF-8 更新说明文件，然后执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Publish-Release.ps1 `
  -SourceDirectory 'D:\CleanStage\Monitor' -ClientConfig 'D:\CleanStage\Monitor\jmd-update.json' `
  -EntryPoint 'SynJMD_Monitor.exe' -Version '1.17.47' -NotesFile 'D:\ReleaseNotes.txt'
```

Publisher 会排除保留数据、密钥目录、常见构建中间产物和私钥容器，生成包含逐文件 SHA-256 的 RSA-SHA256 签名清单。干净目录中已签名的 `JmdUpdater/JmdUpdater.exe` 和存在时的 `.exe.config` 一并入包；`public-key.xml` 与更新器目录其他内容绝不入包。每个发布版本必须高于该产品现有 latest，不能覆写已发布版本。先写不可变 ZIP 和历史清单，最后原子切换 `<AppId>/latest.json`，已有客户端下载不会读到半个版本。

`jmd-version.json` 由发布工具生成，包含在签名文件清单内。Publish-OneClick 先部署新的更新器到干净目录，Publish-Release / Sign-Release 再签名校验，最后 Publisher 计算哈希打包，不能在签名后用其他副本覆盖它。旧版安装器会以“包内含保留路径”拒绝新包，使用旧版更新器的电脑必须用新首装包重装一次；随后可网络升级更新器本身。首次重装应保留现场数据、配置与信任公钥。

宿主正常退出后会显示置顶安装进度窗口，安装期间不可关闭。成功约 1.5 秒后关闭；未改动或成功恢复时自动重新打开原程序，显示中文原因约 5 秒。RollbackFailed / StartupUnconfirmed 不自动重开，窗口保留 recover 命令。`JMD_UPDATER_NO_UI=1` 不创建窗口，也不自动重开失败的程序。

## 安装失败与恢复

安装器只接受当前用户缓存中固定格式的事务任务。它先在宿主仍正常使用时复核发布签名、包与逐文件哈希、保留数据规则并排除其他实例，写入 `staged.json` 后才允许 Agent 请求宿主进入短暂准备并正常退出。取消或预检失败不会关闭宿主，也不替换程序文件。真正替换还必须有匹配的宿主正常退出确认、PID/启动时间及所有实例退出；进程消失本身不能授权替换文件。

首次替换前先持久化事务记录，再写程序目录 `.jmd-update/pending.json`。断电或安装器异常退出后，宿主即使没有发现活跃安装锁，也会按这份记录阻止混合版本启动，直到完成恢复。终态 `Complete` 或 `RolledBack` 才清除匹配的 pending；回退失败为 `RollbackFailed`。

新版本基础初始化成功的确认不代表设备功能已经通过现场测试。新版本未确认且已经退出时，安装器尝试恢复原程序文件；如果它仍然活着，安装器不会强杀或在运行中覆盖文件，而是留下 `StartupUnconfirmed` 并通知用户。正常关闭相关程序后执行通知中的命令：

```powershell
JmdUpdater.exe recover --job '<当前用户缓存>\JmdUpdater\Transactions\<本次事务ID>\job.json'
```

恢复也会验证签名清单和备份，不能恢复损坏/缺失的备份，也不能把业务数据库迁移回滚当作程序文件回退。本版本不实施业务数据库迁移。不要删除待恢复事务目录或更换其信任公钥。

## 验证

```powershell
python .\Tests\DeliveryServerTests.py
python .\Tests\DeliveryPackageTests.py
python .\Tests\DeliveryProcessTests.py
```

前两组只使用临时文件。进程测试还在当前用户的 `JmdUpdater` 缓存创建随机 64 位十六进制命名的会话和事务，使用合成程序测试退出确认、启动确认、早退恢复及不强杀路径，结束后只清理这些明确记录的测试目录。在受限开发沙箱中，该测试需要相应的用户缓存写权限。它不连接设备，也不替代两个上位机的现场验证。

## 图形化发布窗口

使用 Windows PowerShell 5.1（系统自带 WinForms，无第三方依赖）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File .\Delivery\Publish-Window.ps1
```

窗口上方选择发布产品和通道；中间是 Monitor、Manager 两组可伸缩表单，只显示所选产品。每组提供 git 提交号、未提交改动提示、版本号、两条通道线上版本和多行更新说明。提交号默认取对应仓库 HEAD 短哈希，说明取完整提交消息并移除末尾 trailer，默认说明保持 CRLF。每个产品独立记录版本编辑状态：切换产品保留手动版本，切换通道时询问是否替换手动版本；未编辑值自动更新。提交号和说明保留。

点击“确定”先校验两到四段数字版本、非空且无控制字符的提交号、非空说明，再显示发布确认摘要。一次请求的产品、通道、版本、提交和说明写入 UTF-8 JSON。ProcessStartInfo 固定以 `-File Run-PublishRequest.ps1 -RequestFile <路径>` 启动，运行器用参数绑定调用 Publish-OneClick；没有拼接 PowerShell 源码。运行器 `-EchoOnly` 仅用于真实子进程参数回归，不执行发布。两个都发布时先 Monitor，失败即停止。

发布中禁用输入并阻止关闭。“强制停止”仅发布中可用，确认框提示可能留下未完成发布、须检查日志、版本可能已占用；确认后结束发布进程及编译/签名子进程。日志失败尝试 fallback.log，失败则仅显示；继续监督子进程，确认退出后才解除 busy 和清理请求文件。临时日志目录保留 state.json（开始 UTC、请求、PID、状态），异常退出后可据此检查。

Start-LanServer 通过 CIM `Win32_Process.Create` 启动独立服务，服务不属于发布进程树或调用方作业。服务自身以 `--stdout` / `--stderr` 写原日志文件，PID 记录继续包含真实服务 PID、启动时间、可执行路径和 ConfigSha256。WMI 权限不足时明确报错，不回退到会随发布进程结束的启动方式。

桌面快捷方式可通过以下脚本更新（需要当前 Windows 用户有桌面写权限，脚本本身不会发布）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Delivery\Update-PublishShortcut.ps1
```

`Publish-OneClick.ps1` 继续供自动化使用，新增：

- `-PlanOnly -Product Both -Channel official`：只输出 JSON（Channel、Products；每项 Product、OfficialVersion、TestVersion、SourceVersion、SuggestedVersion、Commit、Dirty、Notes）。不构建、不写日志、不启停服务、不访问密钥。仅读取 deployment/server.json 和 deployment/releases；配置指向其他目录时拒绝预览。
- `-NotesFile <UTF-8 文件>`：从文件读取多行说明，优先于 `-Notes`。

预览与发布共用 OneClick-Versions 的建议版本入口：正式版考虑两条通道历史；测试版基于线上正式版并考虑测试通道历史。Monitor 递增第三段，Manager 递增第四段。不存在正式版基准时测试版预览会报告错误。

`build.ps1` 使用 `/noconfig /nostdlib+`，显式列出全部引用。指纹覆盖编译器绝对路径与 SHA-256、全部引用程序集路径与内容、全部源文件和实际编译参数；成功后在 `bin/JmdUpdater.build.sha256` 保存 JSON 的 InputSha256 与 OutputSha256。输入一致且当前 EXE 哈希一致才复用；旧缓存自动失效。`-Force` 强制重编。现有 Restart-ChangedServer 继续按二进制及配置变化判断是否重启。

无发布自测：`Tests/OneClickPlanTests.ps1`、`Tests/PublishWindowTests.ps1`（使用 `-STA`）、`Tests/IncrementalBuildTests.ps1`。窗口的 `-SelfTest` 模式用内存记录替代发布子进程，只供测试加载。
