# 内部代码签名 Claude 复核

真实 Claude Code 只读读取源码。首轮 15 轮工具交互确认无阻断项，并建议加固公开证书导入、明确批量部署和换证更新器流程；Codex 实施后再次真实复核，确认修正正确、可交付。

原始记录：C:\Users\kuasong\AppData\Local\Temp\JmdInternalSigningReview-20261110

## 最终复核原文

**结论：可以交付。** 这一轮的实质修正都正确，未发现新的阻断问题。上一轮的 P2-1/P2-2/P2-3 都已处理。以下结论只基于读代码，没有执行命令。

## 1. `Install-InternalPublisherTrust.ps1`（单次读取同一份字节）
- **大小上限查了两次**：读文件前查一次（第 16 行），读完后按实际字节长度再查一次（第 18 行）。文件在两次检查之间变大也拦得住。
- **从头到尾用同一份内存数据**：计算 SHA-256（第 20 行）、创建证书对象（第 24 行）、导入存储（第 40 行）用的都是同一个 `$taskBytes` / `$taskCertificate`。不再按路径重复读取，读取之间换文件的漏洞已经消除。
- **指纹检查**：先用 `^[A-F0-9]{64}$` 检查格式，再严格比较，所以空值或截断的预期值都会被拒绝。
- **只接受单张 DER 证书**：要求原始字节与证书的 RawData 完全一致（第 26 行），PEM、PKCS#7 证书包、PFX 都通不过。带私钥的情况另有第 27 行拦截。
- **证书策略保持不变**：必须是自签名、在有效期内、扩展用途只有代码签名（1.3.6.1.5.5.7.3.3），不能是 CA。
- **确认在写入之前**：每个存储都先经过 ShouldProcess 确认，才打开存储写入，所以 `-WhatIf` 不会写任何存储。
- **失败即停止**：导入失败（LocalMachine 没有管理员权限、用户在 CurrentUser\Root 确认框点了取消）会抛异常。`ErrorActionPreference=Stop` 下脚本立即停止，`finally` 会关闭存储并释放证书对象。
- **可安全重试**：对已存在的同一证书再执行 `X509Store.Add` 不会出错，与文档里"用同一证书重试"的说法一致。

## 2. `SIGNING.md`
补充的几项内容都写到了，且与脚本行为一致：
- CurrentUser 导入可能弹确认框，不适合无人值守
- 批量部署用 LocalMachine 或 GPO
- 两个存储只成功一个时的处理和重试
- DER 文件的 SHA-256 与 SHA-1 证书指纹是两个不同的值
- 续期/换证时也要手动部署更新器，确认后按旧证书的精确 SHA-1 指纹撤下信任
- 正式发布必须用 Publish-Release 脚本，直接调用底层 `JmdUpdater.exe publish` 不经过签名门禁

## 3. `SigningTests.py`
新用例 `test_publish_rejects_existing_but_unsigned_required_files` 有效。`Publish-Release.ps1:29-30` 会把入口程序和 `JmdUpdater/JmdUpdater.exe` 作为必选文件传给 Sign-Release 验证，而且验证在 `publish` 之前（第 32 行起）。这个用例让两个必选文件都存在但都未签名，所以能确认：失败原因确实是签名验证失败，而不是文件缺失；并且失败时没有生成 releases 目录。它和已有的缺失文件用例（第 63 行）一起覆盖了两种失败路径。

## 非阻断小问题（P3，可不改）
- **真正写入存储的那段代码没有被测到。** `-WhatIf` 会跳过第 37-42 行，所以 WhatIf 检查只验证了导入计划。PowerShell 应该能正确选中 `X509Store::new($taskStore, $Scope)` 的 `(string, StoreLocation)` 构造函数，但如果想更稳，可以显式转换类型：`[Security.Cryptography.X509Certificates.StoreLocation]$Scope`。
- 用字节数组创建证书对象时，如果内容是 PFX，可能会留下临时密钥容器。但这必须先通过 IT 独立核对的 SHA-256 才会发生，不构成实际风险。

## 尚未验证的实际边界（交付时请保留说明）
- **使用者电脑 / IT 侧的信任部署尚未验证**：两种范围下的真实导入、确认框的表现、GPO 下发，以及导入后在受管电脑上验签和 SmartScreen/App Control 的实际表现，都还没测过。
- **当前签名没有时间戳**：证书在 2029-11-10 到期，到期前必须续证、重签，并手动重新部署更新器。
- **首次切换签名仍需手动部署一次**：IT 先部署信任，再部署签好名的完整包或 `JmdUpdater/JmdUpdater.exe`，只靠网络更新做不到。
- 5 个文件的签名和验签成功是发布机上的结果，这次没有重新核验。