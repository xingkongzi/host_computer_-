// JMD reusable LAN updater: versioned wire and delivery contracts.
// Host checks are project-owned. Unknown or missing results never permit installation.
using System;
using System.Collections.Generic;

namespace JmdUpdater
{
    public sealed class UpdateConfig
    {
        public bool Enabled { get; set; }
        public string AppId { get; set; }
        public string DisplayName { get; set; }
        public string ServerUrl { get; set; }
        public string PublicKeyXml { get; set; }
        public string PublicKeyFile { get; set; }
        public int PollSeconds { get; set; }
        public int RequestTimeoutSeconds { get; set; }
        public int ExitTimeoutSeconds { get; set; }
        public int StartupTimeoutSeconds { get; set; }
        public int StageTimeoutSeconds { get; set; }
        public long MaxPackageBytes { get; set; }
        public string[] PreservePaths { get; set; }
        // Product-owned declarative minimum policy; the reusable core knows no product file names.
        public string[] RequiredPreservePaths { get; set; }
        public UpdateConfig()
        {
            PollSeconds = 60; RequestTimeoutSeconds = 15;
            ExitTimeoutSeconds = 30; StartupTimeoutSeconds = 60;
            StageTimeoutSeconds = 180;
            MaxPackageBytes = 1024L * 1024L * 1024L;
            PreservePaths = new string[0];
            RequiredPreservePaths = new string[0];
        }
    }

    public sealed class ReleaseFile
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class ReleaseManifest
    {
        public int Protocol { get; set; }
        public string AppId { get; set; }
        public string Version { get; set; }
        public string EntryPoint { get; set; }
        // 主程序改名时，列出被本版本替换的旧入口程序（例如 DriveManager.exe）。签名覆盖此字段。
        // 以旧入口运行的安装可以接受本版本：安装后启动新入口，旧入口作为过期文件删除。旧版更新器会忽略此字段。
        public List<string> PreviousEntryPoints { get; set; }
        public string ReleaseNotes { get; set; }
        public string PackageName { get; set; }
        public string PackageSha256 { get; set; }
        public long PackageSize { get; set; }
        public List<ReleaseFile> Files { get; set; }
        public ReleaseManifest() { Protocol = 1; Files = new List<ReleaseFile>(); }
    }

    public sealed class SignedRelease
    {
        // Signature covers the decoded Payload bytes exactly, not reserialized JSON.
        public string Payload { get; set; }
        public string Signature { get; set; }
    }

    public sealed class InstallJob
    {
        public string AppId { get; set; }
        public string InstallDirectory { get; set; }
        public string ExecutablePath { get; set; }
        public int HostPid { get; set; }
        public long HostStartUtcTicks { get; set; }
        public string PrepareId { get; set; }
        public string SessionToken { get; set; }
        public string ShutdownAckPath { get; set; }
        public string PackagePath { get; set; }
        public string EnvelopePath { get; set; }
        public string ConfigPath { get; set; }
        public string TransactionDirectory { get; set; }
    }

    public sealed class CheckResult
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
    }

    public sealed class HostRequest
    {
        public int Protocol { get; set; }
        public string Token { get; set; }
        public string Command { get; set; }
        public string PrepareId { get; set; }
        public string AckPath { get; set; }
        public HostRequest() { Protocol = 1; }
    }

    public sealed class HostResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string AppId { get; set; }
        public string Version { get; set; }
        public string InstallDirectory { get; set; }
        public string ExecutablePath { get; set; }
        public int Pid { get; set; }
        public List<CheckResult> Checks { get; set; }
        public bool Prepared { get; set; }
        public string PrepareId { get; set; }
    }
}
