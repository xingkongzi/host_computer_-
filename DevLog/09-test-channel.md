# 测试通道发布

2026-11-11。正式根目录下新增 test 目录。客户端仍请求 ServerUrl + AppId，服务端按标准 IPv4 精确匹配 ClientChannels，只对两段产品文件路径优先查通道文件，缺文件回退正式。official 是保留通道名（不区分大小写），禁止用作 ClientChannels 的值；/official/<AppId>/<file> 直接读正式根目录，不做 IP 分流。路径穿越、重解析点和 JSON/ZIP 白名单检查继续适用。

正式建议与校验合并正式和测试历史清单文件名及 latest，必须高于两者最高版本，显式冲突发布前拒绝。Manager 仍递增第四段，例如正式 1.1.1.128、测试最高 1.1.1.132，则正式建议 1.1.1.133。Monitor 正式仍为 a.b.(c+1).0，测试在正式前三段基线上递增第四段。

正式发布通过 /official/ 核验后，Mirror-Release.ps1 直接复制正式原始 ZIP 和已签名 release.json，再原子替换测试 latest，字节与正式 latest 相同，不调用 Publisher 重新压缩或签名。复制可重复执行、可补齐部分文件；已有不可变文件哈希不同则拒绝覆盖。镜像通过 /test/ 核验，避免发布电脑自身被分流导致误读。

Server-Lifecycle.ps1 提供可独立测试的重启函数。真正发布前检测配置、EXE 及 exe.config 变化；无变化且身份匹配则不重启。有变化先运行候选二进制 serve --config <路径> --check，共用 LocalUpdateServer.Run 的配置准备逻辑，校验 ClientChannels、端口、根目录存在性、BindAddress 本机存在及 CIDR，不监听、不创建目录。非法候选在停止旧服务前拒绝。

校验通过后才停旧进程并启动候选；HTTP GET /official/__jmd_health__/missing.json 期望 404，确认实际 HTTP 服务及 CIDR 可用。成功启动写入独立 last-good/<时间戳-guid>/，包含 EXE、可选 .config、绝对根路径的 server.json 与 hashes.json，逐文件校验 SHA256 后原子替换 last-good.json 指针。回滚只使用指针指向并通过哈希检查的完整代；无指针才兼容旧 server.last-good.json 与 server-bin.last-good。保留所有旧代（含上一代），暂不自动清理。中途失败留下的未提交代不会成为回滚来源。启动或健康失败恢复上次健康配置及二进制、再次检查并报错退出，候选配置留作 server.failed.json。

复审四项修复：镜像函数在正式及测试产品的 .publish.lock（FileShare.None）内检查测试 latest，较新则说明并跳过，同版必须逐文件字节一致；同时锁定正式源避免复制期间源 latest 变化。Start-LanServer 显式创建缺失 RootDirectory 后才 --check，--check 本身保持无副作用；无进程无快照允许 Restart 首次启动，有运行进程无快照仍拒绝。无需重启判断读取 server.pid 的实际运行 EXE 及其 .config，并要求运行路径等于 server-bin 的 active 路径。保留用户的 Process.Kill 修改。

DryRun 使用 TEMP 密钥、日志、发布目录和临时回环服务，不访问 deployment、不调用线上重启。产品内嵌版本保留源码值，演练只验证构建、打包、显式通道 HTTP 和哈希，不代表安装或真实 Authenticode 验证。

本轮复审修复实际验证（Windows PowerShell 5.1.26100.9818、.NET Framework v4 编译器）：build.ps1 成功，仅已有 CS0649 警告；run-core.ps1 42 项断言通过；OneClickVersionTests.ps1 22 项通过；DeliveryLifecycleTests.ps1 31 项通过；DeliveryServerTests.py 9 项、DeliveryPackageTests.py 19 项、SigningTests.py 8 项全部通过。生命周期新增覆盖：空目录 --check 失败且不创建目录、Start 与 Restart 空部署建立健康服务、实际运行路径迁移、.config 增加/字节变化/移除、较旧镜像重试、同版字节冲突、正式及测试发布锁、EXE 复制后配置复制失败时旧指针及旧代完整且后续可回滚、旧格式快照兼容、运行中无快照拒绝。重启测试仅使用 TEMP 与回环端口。

此前 Monitor test 和 official DryRun 均已通过，本轮未重跑：分别为 1.17.56.1 与 1.17.56.0、每包 38 个文件。test 核验 /test/；official 核验 /official/ 与镜像 /test/，HTTP 版本/长度/哈希均成功，临时服务均停止。演练须加载 VS2017 vcvars64.bat 并设置 QTDIR；首次缺少开发环境失败，一次随机回环端口绑定被系统拒绝，重试成功。一次与测试并行的构建失败，串行复跑成功。

DeliveryProcessTests.py 和 AgentFlow/run.ps1 本轮未重跑：此前运行受沙箱 LocalAppData/JmdUpdater/Transactions 写权限限制，进程测试 WinError 5、AgentFlow 无法创建事务。未改动安装器逻辑或绕过沙箱，需在有用户缓存写权限的环境重跑。未验证 Manager 完整构建、真实 Authenticode 签名、真实发布、线上切换及设备安装。全程不读写 JmdUpdater/deployment、不发布、不启停线上服务、不提交 git。
