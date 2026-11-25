**结论：没有发现确定的阻断项。同意在第3轮已认可的基线上加入这项修正，然后做本机部署和基础版分发。**

### 实际读过的文件
- `SynJMD_Manager/Configuration/ManagerUserSettings.cs`（全文）
- `ManagerLog/Program.cs`（全文）
- `ManagerLog/Properties/AssemblyInfo.cs`，`ManagerLog.csproj` 里的 AssemblyName 和 OutputType
- `SynJMD_Manager/Properties/Settings.Designer.cs`、`Settings.settings`（都是全文）
- `DriveSysView.csproj` 中的 Compile 项
- `ManagerLog/Properties/Settings.settings`：里面是空的，不会遗漏其他用户设置
- `Configuration/Tests/UserSettingsMigrationTests.cs`（全文）

没有读真实的 user.config 或私钥，没有执行任何命令。

### 逐项核对
- **新版本已保存的值不会被覆盖。** 迁移标志为 false 时直接返回，不调用 `Upgrade()`。迁移完成后标志是持久保存的，所以本版本之后的修改不会再被旧值覆盖。测试里改成 COM59 后重启的用例也覆盖了这一点。
- **失败不会被当成完成。** 流程是 `Upgrade()` → 标志设 false → `Save()`，任何一步出异常都会把内存里的标志恢复为 true，并返回 false。
  - 如果 `Upgrade` 已经把旧值写进了新版本文件，但 `Save` 失败：因为标志被排除在迁移之外，文件里没有它，读出来还是默认值 true，下次启动会重新迁移。期间没有业务运行，再迁一次结果相同，无害。
- **旧的 false 标志不会被带入。** 标志加了 `NoSettingsVersionUpgradeAttribute`，框架自带的 `LocalFileSettingsProvider.Upgrade` 会跳过带这个特性的属性。这次旧版里本来没有这个标志，这层保护主要是给以后 1.1.1.128 再升级时用的。Designer 里的注释也提醒了重新生成代码时要保留这个特性。
- **异常后不会提前报告 ready。** 迁移失败时，`Main` 弹窗后直接 return，登录窗没有构造，`Shown` 不会触发，也就不会调用 `ReportStartupReady`。另外，`Settings.Default` 静态初始化抛的异常也会被公开重载捕获。
- **许可判定没有改变。** 迁移只调用框架的 `Upgrade/Save`，原样继承 Licenses/UseingTime，判断逻辑和计数都没动。全新安装时仍使用 -1 和 10001 的默认值。
- **构建：** csproj 已包含 `Compile Include="Configuration\ManagerUserSettings.cs"`。类型、命名空间和 `Settings.Default` 的访问权限都对得上，调用可以编译。

### 框架 Upgrade 用法与路径前提
user.config 的路径由入口 EXE（DriveManager，未签名）决定：程序集版本、EXE 路径、文件名，以及 Company/Product 特性都会影响它。现在入口版本是 1.1.1.128，与旧版 1.0.0.0 不同，所以确实必须调用 Upgrade。框架会在同一个哈希目录下找比当前版本低的最高版本来迁移，同目录的网络升级这样用是正确的。

成立的前提是：EXE 文件名、Company、Product（现在是 DriveManager，Company 为空）和安装路径都和旧版一致。没有旧版的 AssemblyInfo，无法对比，**需确认这几项在这次改动中没变**。提出的交付说明（必须部署到原安装目录、备份站点表、不声称跨路径也能自动迁移）是正确且必要的。

### 非阻断建议（不改也可以上线）
1. 如果旧的 user.config 本身损坏，同样会阻止启动。但错误提示只说"请检查写入权限"，建议在提示或交付说明里加一句"配置文件可能损坏"，并给出人工处理的办法。
2. 测试用的 Provider 自己实现了跳过该特性的逻辑，所以"旧 false 标志不导入"那条测试证明的是测试 Provider 的行为，不是真实 Provider 的。真实环境依赖的是框架本身的实现。测试里的生产特性断言已经部分补上了这一点。

### 验证限制
这次是只读审查，没有运行测试，也没有构建。23 条测试、各项回归和 Release 结果以提供的结果为准；Release 当时还在刷新，没有结论，部署前请确认构建成功。真实 LocalFileSettingsProvider 下从 1.0.0.0 升到 1.1.1.128 的实际迁移、硬件、第二台局域网机器、配置目录重定向都还没有验证。建议部署后在本机抽查一项通信配置，确认 COM/IP 确实沿用了旧值。