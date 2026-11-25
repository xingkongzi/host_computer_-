# 首次配置一键包：Claude 真实审查记录

日期：2026-11-10。审查对象为当前工作区快照，未提交 Git。

需求：Monitor 完整包内置公开证书与首次配置入口；必要时请求 Windows 管理员授权，配置后验签成功再启动原权限主程序。不导出私钥、不改变目录 ACL、不关闭系统防护、不请求外部时间戳。

通过本机已认证 Claude Code CLI，使用 Read/Glob/Grep 只读能力审查，没有允许执行命令或修改文件。范围：Delivery/First-Run.ps1、Install-InternalPublisherTrust.ps1、Tests/FirstRunTests.py，以及包内首次运行.cmd、先看这里.txt。

Codex 核实并修复了三项反馈：CMD 处理负退出码；提权前绑定公开证书 DER SHA-256 与实际 PE 签名者；明确不信任的发布者在配置前停止。新增 ValidateOnly 避免 WhatIf 提示，且测试核对包内脚本与源码一致。错误映射经本机 Windows PowerShell 5.1 只读反射和 [Microsoft PowerShell 源码](https://raw.githubusercontent.com/PowerShell/PowerShell/master/src/System.Management.Automation/security/MshSignature.cs)核实。

三轮真实调用证据保存在 C:/Users/kuasong/AppData/Local/Temp/JmdInternalSigningReview-20261110 的 oneclick、oneclick-followup、oneclick-final 子目录，每轮均有 request.txt、response.json、review.txt。最终反馈：“通过。上次唯一的可执行建议已解决，这两处改动没有引入新问题。”并确认可以定稿。

验证：首次入口 7 项测试与签名门禁 8 项测试通过；最终包在实际发布账号预检为 publisher trusted=True，在根证书未受信任的沙箱中也通过只读预检。独立测试副本成功启动并正常关闭，原程序与发布服务继续运行。最终 ZIP 的 53 个文件逐项哈希、CRC 通过，不含私钥；主程序和更新器仍与原已签名文件一致。

未验证边界：使用者电脑首次 UAC、另一个管理员凭据、企业策略。包内校验不能代替可信分发渠道；IT 应使用可信原始包操作。信任存储部分写入失败时可用同一证书重试，未实施自动撤回。内部信任不等于 SmartScreen 云信誉。
