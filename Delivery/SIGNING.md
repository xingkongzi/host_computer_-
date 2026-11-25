# Windows 内部代码签名

此方案用于公司内部、由 IT 管理信任的电脑。Windows Authenticode 用于识别 EXE/DLL 发布者；现有 RSA 发布清单签名用于验证局域网更新包，两者同时保留。代码签名不会授予目录写入权限，也不会取消多实例更新保护。

## 证书与信任

使用专用于 SynJMD 的内部代码签名证书。私钥保存在发布账号的 Windows `CurrentUser\My` 证书存储，设置为不可导出；只分发 `.cer` 公共证书。丢失发布账号的私钥后需要换证并重新部署信任，复制项目源码不等于备份签名身份。

IT 应通过独立可信渠道核对 DER `.cer` 文件的 SHA-256（`Get-FileHash -Algorithm SHA256`），另用 SHA-1 证书指纹交叉核对身份，再在受管电脑部署。`Install-InternalPublisherTrust.ps1` 对同一份内存中的 DER 字节验证指纹、有效期和仅限代码签名用途，再把该证书对象加入 `Root` 和 `TrustedPublisher`；拒绝 PEM 和多证书容器。

默认只影响当前用户；导入 `CurrentUser\Root` 可能弹 Windows 确认框，需用户核对，不适用于无人值守批量任务。IT 批量部署使用管理员权限的 `-Scope LocalMachine` 或公司 GPO；LocalMachine 影响本机所有用户。支持 `-WhatIf` 先查看计划。脚本不自动运行，不修改 SmartScreen、杀毒或 App Control 策略。若两个存储只成功导入一个，修正权限后用同一证书重试，并核验两个存储；不自动删除已有信任。

若公司使用 App Control/WDAC 等应用控制，IT 还需按公司策略审核并配置相应发布者规则。导入内部证书不等于获得微软 SmartScreen 云信誉，不能保证所有 Windows 电脑免提示；适用范围是已完成公司信任配置的终端。

## 签名与发布

完整包可以内置首次配置入口：把 `First-Run.ps1`、`Install-InternalPublisherTrust.ps1` 和公开 DER 证书放在产品的 `JmdUpdater/FirstRun/`，根目录的 `首次运行.cmd` 传入入口 EXE、公开证书 SHA-256 和签名者 SHA-1 指纹。使用者完整解压后双击即可；脚本先验证文件、证书策略及实际签名者，缺少信任时请求 Windows 管理员授权，只在提权子进程中写入 LocalMachine 信任，随后由原进程验证并启动产品。`-CheckOnly` 仅检查；导入脚本的 `-ValidateOnly` 仅验证证书，不打开信任存储。

包必须通过可信渠道分发；包内指纹不能独立证明整个包的来源。IT 代输管理员凭据时应从自己的可信原始包运行配置。不要解压到 OneDrive 同步目录或符号链接/联接目录。公司执行策略、App Control 和管理员授权仍然有效，一键入口不承诺绕过它们。

把对应 `signing.*.example.json` 复制到忽略目录 `deployment/signing.<AppId>.json`，填写证书指纹和证书存储。默认 `RequireTimestamp=false`、`TimestampUrl` 为空，签名不请求外部时间戳。文件列表仅包含自有程序：Monitor 主程序、Manager 入口及业务 DLL、随包 `JmdUpdater/JmdUpdater.exe`。不要用自己的身份重签微软或 Qt 等第三方库。

`Sign-Release.ps1` 用明确证书指纹选择身份，使用 SHA-256 签名，然后执行 `signtool verify /pa /all`，核对发布者指纹和有效信任。任何失败都会停止。签名前在干净部署目录操作；不要更改已发布 ZIP 或旧版本目录。没有可信时间戳时，证书到期前需要重新签发、重签程序并更新终端信任；当前内部证书有效至 2029-11-10。

`Publish-Release.ps1` 自动读取该产品的本地签名配置，先签名验证，再生成逐文件哈希和 RSA 签名包。已由独立签名步骤签好的目录可加 `-UseExistingSignatures`，仍必须通过全部验证。复用到新项目时可通过 `-SigningConfig` 指定外部配置。没有配置的历史产品保持原有发布流程；需要强制签名的产品必须保留其发布端签名配置。

正式内部发布必须使用上述脚本；直接调用底层 `JmdUpdater.exe publish` 不执行 Authenticode 门禁。新增自有 DLL/EXE 时同步维护签名清单，脚本不会自动替第三方库重签。

完整分发包遵循：编译 → 部署更新器 → 签名并验证 → 生成 SHA256SUMS → ZIP。现有网络更新保留更新器本身，所以第一次切换内部签名时，需要 IT 部署信任，并将已签名完整包部署一次，或在正常关闭软件后单独替换包内已签名的 `JmdUpdater/JmdUpdater.exe`。现场数据库、工作区和业务数据继续保留。

每次续期或换证也需要上述手动更新器部署：IT 先部署新证书信任，再部署新签名更新器/完整包；不能只靠主程序网络更新完成换证。确认所有受管程序和更新器都换证后，由 IT 按旧证书的精确 SHA-1 指纹从相应 Root/TrustedPublisher 存储撤下旧信任，避免误删其他证书。

如果未来公司批准使用 RFC3161 时间戳服务，再设置 `RequireTimestamp=true` 并填写批准的地址；此时必须获取并验证时间戳，否则停止。时间戳请求只发送签名摘要，不上传 EXE 正文或私钥。目前不使用外部时间戳服务。不得把证书密码、私钥或云签名凭据写进 JSON、源码或发布包。

## 验证及依据

运行 `python ./Tests/SigningTests.py` 检查路径、证书策略、未签名拒绝、发布失败不广告新版本和信任指纹门禁；这些测试不签名、不修改信任存储。

- [Microsoft SignTool 文档](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)
- [Microsoft SmartScreen 信誉说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)：有效 OV/EV 证书也不保证首次分发不提示，EV 已不再自动绕过信誉检查。
