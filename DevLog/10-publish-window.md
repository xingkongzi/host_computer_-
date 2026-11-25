# 10：图形化发布窗口与增量构建

## 原因与实现

WinForms 发布入口提供两产品独立的提交号、通道版本、版本和多行说明；默认数据由只读 PlanOnly JSON 提供，保留默认说明的 CRLF。确认前验证输入，确认框列出请求。Monitor、Manager 串行执行，失败停止。

本轮修复复审的七项问题：

1. 删除编码命令与 PowerShell 源码拼接。请求写入 UTF-8 JSON，ProcessStartInfo 固定调用 Run-PublishRequest.ps1，运行器读取数据后 splatting 调用 Publish-OneClick。EchoOnly 只回显解析结果，供真实子进程测试。
2. 缓存同时保存输入指纹与成功产物 SHA-256；产物缺失或改写必须重编。
3. 日志失败尝试备用文件，再失败仅显示；继续监督发布进程。只有确认退出才解除 busy 和清理 JSON。管道读取故障单独记录。
4. 编译使用 /noconfig /nostdlib+，显式引用全部程序集；指纹包含编译器绝对路径与字节、全部引用路径与字节、参数及源文件。
5. 增加强制停止按钮和风险确认，通过 Toolhelp 快照结束发布进程及子进程；不依赖需要 WMI 权限的 taskkill /T。日志目录 state.json 保存开始 UTC、请求、PID 和状态，异常退出后保留现场记录。
6. 每产品记录手动版本编辑状态，切产品保留，切通道询问是否替换；提交和说明保留。
7. Start-LanServer 使用 CIM Win32_Process.Create，由 WMI 提供程序创建独立服务。serve 自己打开原 stdout/stderr 日志；PID 记录保留启动时间、路径与 ConfigSha256。权限不足明确失败，不回退到原来的父子进程启动方式。

## 本轮实际验证

Windows PowerShell 5.1；未创建 Git 提交，未发布、未启停线上服务。deployment 仅只读 server.json 和 releases，未读取 keys。服务测试只使用临时配置与回环端口。

| 项目 | 本轮结果 |
|---|---|
| build.ps1 | 成功；后续运行复用通过。首次编译有既有 CS0649 警告 |
| run-core.ps1 | 42 项断言通过 |
| OneClickVersionTests.ps1 | 22 项断言通过 |
| OneClickPlanTests.ps1 | 46 项断言通过 |
| PublishWindowTests.ps1 | 35 项断言通过 |
| IncrementalBuildTests.ps1 | 全通过：复用、产物篡改、源码、Force、编译器路径/字节、引用字节、忽略 csc.rsp、编译参数 |
| DeliveryLifecycleTests.ps1 | 未通过：沙箱拒绝 Win32_Process.Create（HRESULT 0x80041003），临时服务未能启动；父进程存活性测试等待 PID 后失败 |
| DeliveryServerTests.py | 9 项通过 |
| DeliveryPackageTests.py | 19 项通过 |
| SigningTests.py | 8 项通过 |

窗口测试使用真实控件，新增真实 EchoOnly 子进程，覆盖弯引号、双引号、分号、井号、美元符、反引号、中文、说明换行、空格路径以及额外命令标记文件不被创建；模拟两个日志位置都失败，确认 busy/请求/子进程仍保持；真实终止父子测试进程；检查状态记录与版本保留/替换。

增量测试私有编译器夹具初次缺少 cscui.dll，补齐语言资源后全部通过。窗口测试期间曾因测试变量与控件变量重名失败，已修正并重跑通过。

## 尚未验证

第七项已补充真实父进程调用 Start-LanServer、结束父树、检查服务健康和身份并最终停止服务的测试，但本环境拒绝 WMI 创建进程，不能声称该项通过。原生命周期的后续重启/回滚测试也被这一前置失败阻断，需要在允许本机 Win32_Process.Create 的 Windows 会话重跑。

真实发布、真实签名、客户端安装与线上服务行为未执行。本轮没有重新进行桌面目视检查；强制停止确认框的文字与绑定已实现，测试直接调用停止处理函数，未自动点击风险弹框。
