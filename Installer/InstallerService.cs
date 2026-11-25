// Project: JmdUpdater. Signed-inventory installation with a durable rollback journal.
// A matching normal-shutdown acknowledgement AND process exit are mandatory.
// No path ever kills a product process; an alive unconfirmed startup requires later recovery.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace JmdUpdater
{
    public sealed class InstalledRelease
    {
        public string AppId { get; set; }
        public string Version { get; set; }
        public string PrepareId { get; set; }
        public SignedRelease Release { get; set; }
    }

    internal sealed class ShutdownAcknowledgement
    {
        public string PrepareId { get; set; }
        public int Pid { get; set; }
        public string ExecutablePath { get; set; }
    }

    internal sealed class StartupAcknowledgement
    {
        public string Token { get; set; }
        public int Pid { get; set; }
    }

    internal sealed class PendingInstallation
    {
        public string PrepareId { get; set; }
        public string TransactionDirectory { get; set; }
    }

    internal sealed class CancelInstallation
    {
        public string PrepareId { get; set; }
    }

    internal sealed class ShutdownRequestAcknowledgement
    {
        public string PrepareId { get; set; }
        public int HostPid { get; set; }
    }

    internal sealed class InstallRecord
    {
        public string Path { get; set; }
        public bool OriginallyExisted { get; set; }
        public string BackupSha256 { get; set; }
        public bool BackupReady { get; set; }
        public bool Touched { get; set; }
    }

    // One program file that must actually change. Files whose current content already equals the
    // signed content are never planned, so they are never backed up, renamed or replaced.
    internal sealed class PlannedChange
    {
        public string Path { get; set; }
        public string Source { get; set; }      // staged new file; null deletes an obsolete program file
        public bool Exists { get; set; }
        public long OldSize { get; set; }
        public long NewSize { get; set; }
    }

    internal sealed class InstallJournal
    {
        public string PrepareId { get; set; }
        public string State { get; set; }
        public string Failure { get; set; }
        public int StartedPid { get; set; }
        public long StartedUtcTicks { get; set; }
        // 本次安装启动的入口程序（主程序改名时是新 exe）。产品的启动门禁据此识别“安装器启动的新版本”。
        public string StartedExecutable { get; set; }
        public List<InstallRecord> Records { get; set; }
        public InstallJournal() { Records = new List<InstallRecord>(); }
    }

    public static class InstallerService
    {
        [ThreadStatic] private static InstallProgress progress;

        // Validates the fixed per-user job location before reading any installer instructions.
        // Returns 0 only after the new process acknowledges startup; nonzero preserves the journal.
        public static int Run(string jobPath)
        {
            InstallJob job = LoadJob(jobPath);
            progress = new InstallProgress();
            InstallJournal outcome = null;
            int result;
            try
            {
                using (Mutex installerLock = new Mutex(false, InstallMutexName(job.InstallDirectory)))
                {
                    if (!TryOwn(installerLock)) throw new IOException("Another installer owns this installation.");
                    try { result = RunOwned(job, out outcome); }
                    finally { installerLock.ReleaseMutex(); }
                }
            }
            catch (Exception failure)
            {
                if (outcome == null)
                {
                    try { JsonIO.WriteAtomic(Path.Combine(job.TransactionDirectory, "stage-failed.json"),
                        new { PrepareId = job.PrepareId, Message = failure.Message,
                            Code = failure is OperationCanceledException ? "Cancelled" : "PreflightFailed" }); }
                    catch (Exception reportFailure) { Console.Error.WriteLine("Unable to report staging failure: " + reportFailure.Message); }
                }
                progress.Dispose(); progress = null;
                throw;
            }
            // A restarted old program must not encounter the mutex still held by its installer.
            try
            {
                if (result != 0 && outcome != null) NotifyFailure(job, outcome);
                else if (result == 0) progress.Finish("更新完成", 1500);
            }
            finally { progress.Dispose(); progress = null; }
            return result;
        }

        // Explicitly restores an interrupted transaction only while no product instance is alive.
        // It never interprets a running or unidentifiable process as permission to overwrite files.
        public static int Recover(string jobPath) { return Recover(jobPath, false); }

        // restart=true is the automatic path launched by a product that found an unfinished update at
        // startup: wait for that product to exit, restore the previous files, then open it again.
        public static int Recover(string jobPath, bool restart)
        {
            InstallJob job = LoadJob(jobPath);
            using (Mutex installerLock = new Mutex(false, InstallMutexName(job.InstallDirectory)))
            {
                if (!TryOwn(installerLock)) throw new IOException("Another installer owns this installation.");
                try
                {
                    string journalPath = Path.Combine(job.TransactionDirectory, "journal.json");
                    InstallJournal journal = ReadBounded<InstallJournal>(journalPath, 8 * 1024 * 1024);
                    ValidateJournal(job, journal);
                    if (journal.State != "Complete" && journal.State != "RolledBack")
                    {
                        if (restart) WaitForNoRunningProduct(job,60);
                        EnsureNoRunningProduct(job);
                        try { Rollback(job, journal); }
                        catch (Exception failure)
                        {
                            journal.State = "RollbackFailed"; journal.Failure = failure.Message;
                            WriteJournal(job, journal); throw;
                        }
                    }
                    else
                    {
                        ClearPending(job);
                        UpdateConfig sweepConfig = ManifestIO.LoadConfig(job.ConfigPath);
                        SweepAsideFiles(job.InstallDirectory, sweepConfig, SignedPaths(job, sweepConfig));
                    }
                    Console.WriteLine("Recovered previous program files. Start the product normally.");
                }
                finally { installerLock.ReleaseMutex(); }
            }
            // Started only after the install lock is released, otherwise the product's startup guard would block it.
            if (restart) StartProduct(job);
            return 0;
        }

        private static void WaitForNoRunningProduct(string executable, int seconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                try { EnsureNoRunningProduct(executable); return; }
                catch (IOException) { Thread.Sleep(250); }
            }
        }

        private static void StartProduct(InstallJob job)
        {
            // 回滚后旧入口还在，启动旧入口；已完成的改名安装旧入口已删除，启动新入口。
            string executable = File.Exists(job.ExecutablePath) ? job.ExecutablePath : EntryExecutable(job);
            ProcessStartInfo start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = job.InstallDirectory };
            start.EnvironmentVariables.Remove("JMD_UPDATE_READY_PATH");
            start.EnvironmentVariables.Remove("JMD_UPDATE_READY_TOKEN");
            Process.Start(start).Dispose();
        }

        public static string InstallMutexName(string installDirectory)
        {
            string normalized = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            using (SHA256 hash = SHA256.Create())
                return "Local\\JmdUpdater.Install." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").ToLowerInvariant();
        }

        private static bool TryOwn(Mutex mutex)
        {
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }

        private static int RunOwned(InstallJob job, out InstallJournal outcome)
        {
            outcome = null;
            ThrowIfCancelled(job);
            UpdateConfig config = ManifestIO.LoadConfig(job.ConfigPath);
            if (!config.Enabled || !String.Equals(config.AppId, job.AppId, StringComparison.Ordinal))
                throw new InvalidDataException("Update configuration is disabled or belongs to another product.");
            SignedRelease envelope = ReadBounded<SignedRelease>(job.EnvelopePath, 8 * 1024 * 1024);
            ReleaseManifest manifest = ManifestIO.Verify(JsonIO.Serialize(envelope), config.PublicKeyXml);
            // 入口必须是正在运行的程序，或签名清单声明替换的旧入口（主程序改名，例如 DriveManager.exe -> SynJMD_Manager.exe）。
            if (manifest.AppId != job.AppId || !ManifestIO.AcceptsEntryPoint(manifest, Path.GetFileName(job.ExecutablePath)))
                throw new InvalidDataException("Signed entry point does not match the requesting product.");
            string journalPath = Path.Combine(job.TransactionDirectory, "journal.json");
            if (File.Exists(journalPath)) throw new IOException("Transaction was already attempted; use explicit recovery if necessary.");
            EnsureNoUnfinishedPending(job);
            if (!IsOriginalRunning(job)) throw new IOException("Requesting product is no longer running; staging was not acknowledged.");
            EnsureNoOtherRunningProduct(job);
            string stage = Path.Combine(job.TransactionDirectory, "stage");
            ThrowIfCancelled(job);
            ValidatePackage(manifest, job.PackagePath, stage, config);
            ThrowIfCancelled(job);
            // Layer 1, while the host is still open: anything detectable now (disk space, write access,
            // version/inventory trust) is reported to the user without closing the program.
            CheckSpaceAndAccess(job, PlanChanges(job, manifest, config, stage));
            if (!IsOriginalRunning(job)) throw new IOException("Requesting product exited before staging completed.");
            EnsureNoOtherRunningProduct(job);
            // The host remains freely usable throughout validation and extraction. Only this
            // acknowledgement permits Agent to acquire the short host preparation lease.
            JsonIO.WriteAtomic(Path.Combine(job.TransactionDirectory, "staged.json"), new { PrepareId = job.PrepareId, HostPid = job.HostPid });
            WaitForShutdownRequest(job, config.StageTimeoutSeconds);
            WaitForNormalExit(job, config.ExitTimeoutSeconds);
            if (progress != null) { progress.Start(config.DisplayName + " 正在更新到 " + manifest.Version); progress.Report("已关闭旧版本", 0, 1); }
            List<PlannedChange> plan;
            try
            {
                // Layer 2, host closed but no file touched yet: re-plan against the final state and wait
                // briefly for every file that must change to become replaceable. Any failure here leaves
                // the program exactly as it was, so the outcome is a clean "RolledBack" with no records.
                if (progress != null) progress.Report("正在检查文件", 0, 1);
                ThrowIfCancelled(job);
                EnsureNoRunningProduct(job);
                UpdateConfig finalConfig = ManifestIO.LoadConfig(job.ConfigPath);
                if (!finalConfig.Enabled || finalConfig.AppId != job.AppId)
                    throw new InvalidDataException("安装前更新已被停用。");
                PreservePolicy.ValidateRequired(finalConfig);
                ManifestIO.Verify(JsonIO.Serialize(envelope), finalConfig.PublicKeyXml);
                foreach (ReleaseFile file in manifest.Files)
                    if (UpdatePaths.IsPreserved(finalConfig, file.Path)) throw new InvalidDataException("当前数据保留规则不允许安装此更新包。");
                config = finalConfig;
                plan = PlanChanges(job, manifest, config, stage);
                CheckSpaceAndAccess(job, plan);
                if (plan.Any(c => c.Path.Equals("JmdUpdater/JmdUpdater.exe", StringComparison.OrdinalIgnoreCase))) WaitForUpdaterExit(job);
                WaitUntilReplaceable(job, plan, LockWaitSeconds);
            }
            catch (Exception blocked)
            {
                InstallJournal untouched = new InstallJournal { PrepareId = job.PrepareId, State = "RolledBack",
                    Failure = "更新没有开始，程序文件没有任何改动。原因：" + blocked.Message };
                outcome = untouched;
                WriteJournal(job, untouched);
                Console.Error.WriteLine("Update not started: " + blocked.Message);
                return 2;
            }
            InstallJournal journal = new InstallJournal { PrepareId = job.PrepareId, State = "Applying" };
            outcome = journal;
            try
            {
                WriteJournal(job, journal);
                ThrowIfCancelled(job);
                WritePending(job);
                ThrowIfCancelled(job);
                Apply(job, plan, manifest, envelope, journal);
                if (progress != null) progress.Report("正在启动新版本", plan.Count, Math.Max(1, plan.Count));
                journal.State = "WaitingForStartup"; journal.StartedExecutable = EntryExecutable(job); WriteJournal(job, journal);
                string readyPath = Path.Combine(job.TransactionDirectory, "ready.json");
                if (File.Exists(readyPath)) File.Delete(readyPath);
                // 启动本版本的入口程序；主程序改名时它与旧的 job.ExecutablePath 不同。
                ProcessStartInfo start = new ProcessStartInfo(EntryExecutable(job)) {
                    UseShellExecute = false, WorkingDirectory = job.InstallDirectory
                };
                start.EnvironmentVariables["JMD_UPDATE_READY_PATH"] = readyPath;
                start.EnvironmentVariables["JMD_UPDATE_READY_TOKEN"] = job.PrepareId;
                using (Process started = Process.Start(start))
                {
                    if (started == null) throw new IOException("无法启动新版本程序。");
                    journal.StartedPid = started.Id;
                    journal.StartedUtcTicks = started.StartTime.ToUniversalTime().Ticks;
                    WriteJournal(job, journal);
                    Stopwatch timeout = Stopwatch.StartNew();
                    while (timeout.Elapsed.TotalSeconds < config.StartupTimeoutSeconds)
                    {
                        if (File.Exists(readyPath))
                        {
                            StartupAcknowledgement ready = TryReadStartupAcknowledgement(readyPath);
                            if (ready != null && ready.Token == job.PrepareId && ready.Pid == started.Id)
                            {
                                journal.State = "Complete"; WriteJournal(job, journal); ClearPending(job);
                                SweepAsideFiles(job.InstallDirectory, config, SignedPaths(job, config));
                                Console.WriteLine("Update installed and startup acknowledged: " + manifest.Version);
                                return 0;
                            }
                        }
                        if (started.HasExited) break;
                        Thread.Sleep(100);
                    }
                    if (!started.HasExited)
                    {
                        journal.State = "StartupUnconfirmed";
                        journal.Failure = "新版本未确认启动成功，且仍在运行。请正常关闭后执行恢复。";
                        WriteJournal(job, journal); Console.Error.WriteLine(journal.Failure);
                        return 2;
                    }
                }
                throw new IOException("新版本在确认启动成功前已退出。");
            }
            catch (Exception failure)
            {
                journal.Failure = failure.Message;
                try
                {
                    EnsureNoRunningProduct(job);
                    Rollback(job, journal);
                }
                catch (Exception rollbackFailure)
                {
                    journal.State = IsStartedRunning(job, journal) ? "StartupUnconfirmed" : "RollbackFailed";
                    journal.Failure = failure.Message + "；恢复未完成：" + rollbackFailure.Message;
                    WriteJournal(job, journal);
                }
                Console.Error.WriteLine("Update failed: " + journal.Failure);
                return 2;
            }
        }

        // Extraction is inventory-driven and bounded by the signed lengths, not ZIP metadata alone.
        // This method intentionally has no installation side effects and can be security-tested in isolation.
        public static void ValidatePackage(ReleaseManifest manifest, string packagePath, string stage, UpdateConfig config)
        {
            ManifestIO.Validate(manifest);
            PreservePolicy.ValidateRequired(config);
            UpdatePaths.EnsureNoReparse(packagePath); UpdatePaths.EnsureNoReparse(stage);
            FileInfo package = new FileInfo(packagePath);
            if (!package.Exists || package.Length != manifest.PackageSize || package.Length > config.MaxPackageBytes ||
                !String.Equals(ManifestIO.Sha256File(packagePath), manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update package length or SHA-256 does not match signed release.");
            if (Directory.Exists(stage) && Directory.EnumerateFileSystemEntries(stage).Any())
                throw new IOException("Extraction directory is not empty.");
            Directory.CreateDirectory(stage);
            Dictionary<string, ReleaseFile> expected = new Dictionary<string, ReleaseFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ReleaseFile file in manifest.Files)
            {
                string name = UpdatePaths.Relative(file.Path);
                if (UpdatePaths.IsPreserved(config, name)) throw new InvalidDataException("Release includes a protected data path: " + name);
                expected.Add(name, file);
            }
            HashSet<string> extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (FileStream input = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (ZipArchive archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (archive.Entries.Count != expected.Count) throw new InvalidDataException("ZIP entries differ from signed inventory.");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = UpdatePaths.Relative(entry.FullName);
                    ReleaseFile file;
                    // ExternalAttributes was added after .NET 4.5. On older runtimes entries
                    // are still copied to newly-created regular FileStreams, never materialized
                    // as links or with archive-specified attributes.
                    PropertyInfo attributesProperty = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
                    int attributes = attributesProperty == null ? 0 : (int)attributesProperty.GetValue(entry, null);
                    if (!expected.TryGetValue(name, out file) || !extracted.Add(name) || entry.Length != file.Size ||
                        (((attributes >> 16) & 0xF000) == 0xA000) || (attributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("ZIP entry is invalid, duplicated, linked or unlisted: " + name);
                    string target = UpdatePaths.UnderRoot(stage, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (Stream source = entry.Open())
                    using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (SHA256 hash = SHA256.Create())
                    {
                        byte[] buffer = new byte[65536]; long written = 0; int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            written = checked(written + read);
                            if (written > file.Size) throw new InvalidDataException("Expanded entry exceeds its signed length.");
                            output.Write(buffer, 0, read); hash.TransformBlock(buffer, 0, read, null, 0);
                        }
                        hash.TransformFinalBlock(new byte[0], 0, 0); output.Flush(true);
                        if (written != file.Size || !String.Equals(BitConverter.ToString(hash.Hash).Replace("-", ""), file.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Extracted entry SHA-256 is invalid: " + name);
                    }
                }
            }
            string versionPath = UpdatePaths.UnderRoot(stage, "jmd-version.json");
            Dictionary<string, object> version = ReadBounded<Dictionary<string, object>>(versionPath, 4096);
            if (version == null || !version.ContainsKey("AppId") || !version.ContainsKey("Version") ||
                Convert.ToString(version["AppId"], CultureInfo.InvariantCulture) != manifest.AppId ||
                Convert.ToString(version["Version"], CultureInfo.InvariantCulture) != manifest.Version)
                throw new InvalidDataException("Signed product version metadata does not match release.");
        }

        // How long a file that must change may stay locked (Explorer preview, antivirus scan, indexer)
        // before the update gives up. Applies to the pre-change gate and to each replace/rollback step.
        internal static int LockWaitSeconds = 15;
        // Test seam, reachable only through reflection inside a test process: simulates Windows refusing the
        // atomic replace for chosen targets, which cannot be provoked deterministically from outside.
        internal static Func<string, bool> SimulateReplaceRefused;
        private const string AsideMarker = ".jmd-old-";
        private static readonly Regex LeftoverName = new Regex(@"(\.jmd-old-[0-9a-f]{32}|\.jmd-[0-9a-f]{32}\.tmp|\.jmd-probe-[0-9a-f]{32}\.tmp)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Lists only the files that really change: obsolete files from the previous signed inventory and
        // new files whose current content differs from the signed SHA-256. The version decision has already
        // been made; this per-file content comparison only avoids touching files that would not change.
        private static List<PlannedChange> PlanChanges(InstallJob job, ReleaseManifest manifest, UpdateConfig config, string stage)
        {
            List<PlannedChange> plan = new List<PlannedChange>();
            HashSet<string> next = new HashSet<string>(manifest.Files.Select(f => UpdatePaths.Relative(f.Path)), StringComparer.OrdinalIgnoreCase);
            string installedPath = UpdatePaths.UnderRoot(job.InstallDirectory, ".jmd-update/installed.json");
            if (File.Exists(installedPath))
            {
                InstalledRelease previous = ReadBounded<InstalledRelease>(installedPath, 8 * 1024 * 1024);
                ReleaseManifest previousManifest = ManifestIO.Verify(JsonIO.Serialize(previous.Release), config.PublicKeyXml);
                if (previous.AppId != job.AppId || previousManifest.AppId != job.AppId || previous.Version != previousManifest.Version)
                    throw new InvalidDataException("现有安装清单未通过签名验证。");
                if (ManifestIO.NormalizedVersion(manifest.Version) <= ManifestIO.NormalizedVersion(previous.Version)) throw new InvalidDataException("自动更新不能降级或重复安装相同版本。");
                foreach (ReleaseFile file in previousManifest.Files)
                {
                    string name = UpdatePaths.Relative(file.Path);
                    // Older publishers omit the updater: absence must never remove the recovery entry.
                    if (next.Contains(name) || UpdatePaths.IsPreserved(config, name) ||
                        name.Equals("JmdUpdater/JmdUpdater.exe", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("JmdUpdater/JmdUpdater.exe.config", StringComparison.OrdinalIgnoreCase)) continue;
                    string obsolete = UpdatePaths.UnderRoot(job.InstallDirectory, name);
                    if (Directory.Exists(obsolete)) throw new IOException("程序文件路径被同名目录占用：" + name);
                    if (File.Exists(obsolete)) plan.Add(new PlannedChange { Path = name, Source = null, Exists = true, OldSize = new FileInfo(obsolete).Length });
                }
            }
            // 主程序改名：签名清单声明的旧入口（及其 .config）一定删除，即使本机从未经局域网更新、没有上一版的安装清单。
            // 与其他文件一样先备份再删除，失败可回滚。
            if (manifest.PreviousEntryPoints != null)
            {
                foreach (string previous in manifest.PreviousEntryPoints)
                {
                    foreach (string name in new[] { UpdatePaths.Relative(previous), UpdatePaths.Relative(previous) + ".config" })
                    {
                        if (next.Contains(name) || UpdatePaths.IsPreserved(config, name) ||
                            plan.Any(c => c.Path.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        string obsolete = UpdatePaths.UnderRoot(job.InstallDirectory, name);
                        if (Directory.Exists(obsolete)) throw new IOException("程序文件路径被同名目录占用：" + name);
                        if (File.Exists(obsolete))
                        {
                            UpdatePaths.EnsureNoReparse(obsolete);
                            plan.Add(new PlannedChange { Path = name, Source = null, Exists = true, OldSize = new FileInfo(obsolete).Length });
                        }
                    }
                }
            }
            foreach (ReleaseFile file in manifest.Files)
            {
                string name = UpdatePaths.Relative(file.Path);
                string target = UpdatePaths.UnderRoot(job.InstallDirectory, name);
                if (Directory.Exists(target)) throw new IOException("程序文件路径被同名目录占用：" + name);
                bool exists = File.Exists(target);
                long oldSize = 0;
                if (exists)
                {
                    UpdatePaths.EnsureNoReparse(target);
                    oldSize = new FileInfo(target).Length;
                    if (oldSize == file.Size && String.Equals(HashExisting(target), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                }
                plan.Add(new PlannedChange { Path = name, Source = UpdatePaths.UnderRoot(stage, name), Exists = exists, OldSize = oldSize, NewSize = file.Size });
            }
            // 删除入口程序（改名时的旧 exe 及其 .config）放到最后：任何时刻中断，目录里至少有一个入口程序，
            // 启动时能触发恢复；回滚按相反顺序执行，会先把旧入口恢复回来。其他删除项保持原有顺序。
            List<PlannedChange> entryRemovals = plan.Where(c => c.Source == null && IsEntryFile(job, c.Path)).ToList();
            plan.RemoveAll(c => entryRemovals.Contains(c));
            plan.AddRange(entryRemovals);
            return plan;
        }

        private static bool IsEntryFile(InstallJob job, string relative)
        {
            string target = UpdatePaths.UnderRoot(job.InstallDirectory, relative);
            if (IsProductExecutable(job, target)) return true;
            return relative.EndsWith(".exe.config", StringComparison.OrdinalIgnoreCase) &&
                IsProductExecutable(job, target.Substring(0, target.Length - ".config".Length));
        }

        // Reads a program file that may be in use (a loaded DLL or a file another reader has open).
        private static string HashExisting(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        // Disk space for backups and new files, and write access to every directory that will change.
        private static void CheckSpaceAndAccess(InstallJob job, List<PlannedChange> plan)
        {
            Dictionary<string, long> required = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            AddRequiredSpace(required, job.TransactionDirectory, plan.Where(c => c.Exists).Sum(c => c.OldSize));
            AddRequiredSpace(required, job.InstallDirectory, plan.Where(c => c.Source != null).Sum(c => c.NewSize));
            foreach (KeyValuePair<string, long> volume in required)
            {
                long available;
                try { available = new DriveInfo(volume.Key).AvailableFreeSpace; }
                catch (ArgumentException) { continue; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                long needed = volume.Value + 64L * 1024 * 1024;
                if (available < needed)
                    throw new IOException(String.Format(CultureInfo.InvariantCulture, "磁盘 {0} 剩余空间不足：需要约 {1:F0} MB，当前只有 {2:F0} MB。请清理磁盘后重试。",
                        volume.Key, needed / 1048576.0, available / 1048576.0));
            }
            string root = Path.GetFullPath(job.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
            HashSet<string> directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PlannedChange change in plan)
            {
                string directory = Path.GetDirectoryName(UpdatePaths.UnderRoot(job.InstallDirectory, change.Path));
                while (!Directory.Exists(directory) && directory.Length > root.Length) directory = Path.GetDirectoryName(directory);
                directories.Add(directory);
            }
            directories.Add(root);
            foreach (string directory in directories)
            {
                string probe = Path.Combine(directory, ".jmd-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { } }
                catch (UnauthorizedAccessException)
                {
                    throw new IOException("没有写入权限：" + directory + "。请把软件放在有写权限的目录（不要放在 Program Files），或请 IT 调整权限后重试。");
                }
            }
        }

        private static void AddRequiredSpace(Dictionary<string, long> required, string path, long bytes)
        {
            string volume = Path.GetPathRoot(Path.GetFullPath(path));
            if (String.IsNullOrEmpty(volume) || volume.StartsWith(@"\\", StringComparison.Ordinal)) return;
            long current;
            required.TryGetValue(volume, out current);
            required[volume] = current + bytes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        // Replacement renames the old file aside, which needs DELETE access while sharing with current
        // readers. A loaded DLL passes (Windows allows renaming it); a handle opened without delete sharing
        // (an editor, some scanners) fails, which is exactly what would break the replacement.
        private static bool CanRenameAside(string path)
        {
            const uint DeleteAccess = 0x00010000, ShareAll = 0x7, OpenExisting = 3;
            using (Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFileW(path, DeleteAccess, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                return !handle.IsInvalid;
        }

        private static void WaitUntilReplaceable(InstallJob job, List<PlannedChange> plan, int seconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (true)
            {
                string blocked = null;
                foreach (PlannedChange change in plan)
                    if (change.Exists && !CanRenameAside(UpdatePaths.UnderRoot(job.InstallDirectory, change.Path))) { blocked = change.Path; break; }
                if (blocked == null) return;
                if (elapsed.Elapsed.TotalSeconds >= seconds)
                    throw new IOException("文件 " + blocked + " 正被其他程序占用（可能是打开着软件目录的资源管理器、杀毒软件或其他程序）。请关闭后重新更新。");
                ThrowIfCancelled(job);
                Thread.Sleep(250);
            }
        }

        private static void Apply(InstallJob job, List<PlannedChange> plan, ReleaseManifest manifest, SignedRelease envelope, InstallJournal journal)
        {
            for (int i = 0; i < plan.Count; i++)
            {
                PlannedChange change = plan[i];
                if (progress != null) progress.Report("正在替换文件 " + (i + 1) + "/" + plan.Count + "（" + change.Path + "）", i, plan.Count);
                ReplaceOne(job, change.Path, change.Source, journal);
                if (progress != null) progress.Report("正在替换文件 " + (i + 1) + "/" + plan.Count + "（" + change.Path + "）", i + 1, plan.Count);
            }
            string metadataStage = Path.Combine(job.TransactionDirectory, "installed-next.json");
            JsonIO.WriteAtomic(metadataStage, new InstalledRelease { AppId = job.AppId, Version = manifest.Version, PrepareId = job.PrepareId, Release = envelope });
            ReplaceOne(job, ".jmd-update/installed.json", metadataStage, journal);
        }

        private static void ReplaceOne(InstallJob job, string relative, string source, InstallJournal journal)
        {
            // A product launched by mistake now exits immediately (see the host startup check); wait for it briefly.
            WaitForNoRunningProduct(job,10);
            EnsureNoRunningProduct(job);
            string name = UpdatePaths.Relative(relative);
            string target = UpdatePaths.UnderRoot(job.InstallDirectory, name);
            if (name.Equals("JmdUpdater/JmdUpdater.exe", StringComparison.OrdinalIgnoreCase)) WaitForUpdaterExit(job);
            string backup = UpdatePaths.UnderRoot(Path.Combine(job.TransactionDirectory, "backup"), name);
            if (Directory.Exists(target)) throw new IOException("程序文件路径被同名目录占用：" + name);
            InstallRecord record = new InstallRecord { Path = name, OriginallyExisted = File.Exists(target) };
            journal.Records.Add(record); WriteJournal(job, journal);
            if (record.OriginallyExisted)
            {
                CopyDurable(target, backup, false);
                record.BackupSha256 = ManifestIO.Sha256File(backup); record.BackupReady = true;
                WriteJournal(job, journal);
            }
            record.Touched = true; WriteJournal(job, journal);
            SwapIn(source, target, !IsProductExecutable(job, target));
        }

        private static void Rollback(InstallJob job, InstallJournal journal)
        {
            ValidateJournal(job, journal);
            WaitForNoRunningProduct(job,10);
            EnsureNoRunningProduct(job);
            journal.State = "RollingBack"; WriteJournal(job, journal);
            for (int index = journal.Records.Count - 1; index >= 0; index--)
            {
                InstallRecord record = journal.Records[index];
                if (!record.Touched) continue;
                WaitForNoRunningProduct(job,10);
                EnsureNoRunningProduct(job);
                string target = UpdatePaths.UnderRoot(job.InstallDirectory, record.Path);
                if (record.OriginallyExisted)
                {
                    string backup = UpdatePaths.UnderRoot(Path.Combine(job.TransactionDirectory, "backup"), record.Path);
                    if (!record.BackupReady || !File.Exists(backup) ||!String.Equals(ManifestIO.Sha256File(backup), record.BackupSha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("恢复所需的备份缺失或损坏：" + record.Path);
                    // A file that still (or again) holds the original content needs no restore.
                    bool alreadyOriginal = File.Exists(target) && !Directory.Exists(target) &&
                        String.Equals(HashExisting(target), record.BackupSha256, StringComparison.OrdinalIgnoreCase);
                    if (!alreadyOriginal) SwapIn(backup, target, !IsProductExecutable(job, target));
                }
                else if (File.Exists(target)) SwapIn(null, target, !IsProductExecutable(job, target));
                record.Touched = false; WriteJournal(job, journal);
            }
            journal.State = "RolledBack"; WriteJournal(job, journal); ClearPending(job);
            UpdateConfig config = ManifestIO.LoadConfig(job.ConfigPath);
            SweepAsideFiles(job.InstallDirectory, config, SignedPaths(job, config));
        }

        // Every path named by the transaction's signed release or the currently installed signed inventory.
        // Returns null when either cannot be verified; the sweep then does nothing.
        private static HashSet<string> SignedPaths(InstallJob job, UpdateConfig config)
        {
            try
            {
                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ReleaseManifest current = ManifestIO.Verify(JsonIO.Serialize(ReadBounded<SignedRelease>(job.EnvelopePath, 8 * 1024 * 1024)), config.PublicKeyXml);
                foreach (ReleaseFile file in current.Files) paths.Add(UpdatePaths.Relative(file.Path));
                string installedPath = UpdatePaths.UnderRoot(job.InstallDirectory, ".jmd-update/installed.json");
                if (File.Exists(installedPath))
                {
                    InstalledRelease installed = ReadBounded<InstalledRelease>(installedPath, 8 * 1024 * 1024);
                    foreach (ReleaseFile file in ManifestIO.Verify(JsonIO.Serialize(installed.Release), config.PublicKeyXml).Files)
                        paths.Add(UpdatePaths.Relative(file.Path));
                }
                return paths;
            }
            catch (Exception) { return null; }
        }

        // Puts source at target (or removes target when source is null). The normal path is File.Replace,
        // which is atomic: the target name never disappears, even on power loss. Only if Windows refuses that
        // (typically another process has the old DLL mapped) and allowAside is set, the old file is renamed
        // aside and the new one moved in; that leaves a sub-millisecond window without the file, so the
        // product's entry executable never takes this path. Brief locks are retried for LockWaitSeconds.
        private static void SwapIn(string source, string target, bool allowAside)
        {
            if (source != null) UpdatePaths.EnsureNoReparse(source);
            UpdatePaths.EnsureNoReparse(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temporary = null;
            if (source != null)
            {
                temporary = target + ".jmd-" + Guid.NewGuid().ToString("N") + ".tmp";
                CopyDurable(source, temporary, false);
            }
            try
            {
                // A read-only program file (copied from removable media or some archivers) makes both
                // File.Replace and File.Delete refuse; the file is being replaced anyway.
                if (File.Exists(target))
                {
                    FileAttributes attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                }
                Stopwatch elapsed = Stopwatch.StartNew();
                while (true)
                {
                    Exception refused;
                    try
                    {
                        if (SimulateReplaceRefused != null && SimulateReplaceRefused(target)) throw new IOException("Simulated: replace refused.");
                        if (temporary == null) { if (File.Exists(target)) File.Delete(target); }
                        else if (File.Exists(target)) File.Replace(temporary, target, null);
                        else File.Move(temporary, target);
                        return;
                    }
                    catch (FileNotFoundException) { throw; }
                    catch (DirectoryNotFoundException) { throw; }
                    catch (IOException failure) { refused = failure; }
                    catch (UnauthorizedAccessException failure) { refused = failure; }
                    // ReplaceFile can fail after it has already removed the replaced file
                    // (ERROR_UNABLE_TO_MOVE_REPLACEMENT without a backup name). Never leave the name empty:
                    // finish by moving the verified new copy in, which is what this step was doing anyway.
                    if (temporary != null && !File.Exists(target) && File.Exists(temporary))
                    {
                        try { File.Move(temporary, target); return; }
                        catch (IOException failure) { refused = failure; }
                        catch (UnauthorizedAccessException failure) { refused = failure; }
                    }
                    if (allowAside)
                    {
                        try { SwapAside(temporary, target); return; }
                        catch (IOException failure) { refused = failure; }
                        catch (UnauthorizedAccessException failure) { refused = failure; }
                    }
                    if (elapsed.Elapsed.TotalSeconds >= LockWaitSeconds)
                        throw new IOException("文件 " + Path.GetFileName(target) + " 被其他程序占用，无法替换：" + refused.Message, refused);
                    Thread.Sleep(250);
                }
            }
            finally
            {
                // While the target name is empty, the temporary is the only complete copy next to it; keep it
                // (rollback restores from the transaction backup, and the sweep skips orphaned temporaries).
                if (temporary != null && File.Exists(target)) TryDelete(temporary);
            }
        }

        // Windows allows renaming a mapped image although it refuses to replace or delete it.
        private static void SwapAside(string temporary, string target)
        {
            string aside = null;
            if (File.Exists(target))
            {
                aside = target + AsideMarker + Guid.NewGuid().ToString("N");
                File.Move(target, aside);
            }
            if (temporary != null)
            {
                try { File.Move(temporary, target); }
                catch
                {
                    // Put the old file back so the directory is not left without it.
                    if (aside != null && !File.Exists(target)) { try { File.Move(aside, target); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                    throw;
                }
            }
            if (aside != null) TryDelete(aside);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Removes renamed-aside copies and temporary files left by earlier installs once nothing holds them.
        // A file is removed only if its name has a suffix this installer generates, neither it nor the file it
        // was derived from is a preserved data path, and it is not itself listed in a signed inventory.
        private static void SweepAsideFiles(string installDirectory, UpdateConfig config, HashSet<string> signedPaths)
        {
            if (signedPaths == null) return;
            try
            {
                string root = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
                Stack<string> pending = new Stack<string>(); pending.Push(root);
                int visited = 0;
                while (pending.Count > 0 && visited < 20000)
                {
                    string directory = pending.Pop();
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        string relative = UpdatePaths.Relative(child.Substring(root.Length + 1));
                        if (!UpdatePaths.IsPreserved(config, relative) || relative.StartsWith(".jmd-update", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
                    }
                    foreach (string file in Directory.EnumerateFiles(directory))
                    {
                        visited++;
                        Match suffix = LeftoverName.Match(Path.GetFileName(file));
                        if (!suffix.Success || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                        string relative, original;
                        try
                        {
                            relative = UpdatePaths.Relative(file.Substring(root.Length + 1));
                            original = relative.Substring(0, relative.Length - suffix.Length);
                            if (original.Length == 0 || original.EndsWith("/", StringComparison.Ordinal)) original = relative;   // probe files
                        }
                        catch (ArgumentException) { continue; }
                        catch (InvalidDataException) { continue; }
                        if (signedPaths.Contains(relative)) continue;
                        // A new-file temporary whose target name is currently empty may be the only copy; leave it.
                        bool newCopy = suffix.Value.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                            !suffix.Value.StartsWith(".jmd-probe-", StringComparison.OrdinalIgnoreCase);
                        if (newCopy && !File.Exists(UpdatePaths.UnderRoot(root, original))) continue;
                        // .jmd-update is the updater's own metadata folder (reported as preserved by policy).
                        bool updaterMetadata = relative.StartsWith(".jmd-update/", StringComparison.OrdinalIgnoreCase);
                        if (!updaterMetadata && (UpdatePaths.IsPreserved(config, relative) || UpdatePaths.IsPreserved(config, original))) continue;
                        TryDelete(file);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidDataException) { }
        }

        private static void CopyDurable(string source, string target, bool overwrite)
        {
            UpdatePaths.EnsureNoReparse(source); UpdatePaths.EnsureNoReparse(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output, 65536); output.Flush(true); }
        }

        private static InstallJob LoadJob(string jobPath)
        {
            jobPath = Path.GetFullPath(jobPath); UpdatePaths.EnsureNoReparse(jobPath);
            string transaction = Path.GetDirectoryName(jobPath);
            string prepareId = Path.GetFileName(transaction); UpdatePaths.ValidateToken(prepareId);
            string expected = UpdatePaths.TransactionDirectory(prepareId);
            if (!SamePath(transaction, expected) || !SamePath(jobPath, Path.Combine(expected, "job.json")))
                throw new InvalidDataException("Installer jobs must be inside the current user's transaction directory.");
            InstallJob job = ReadBounded<InstallJob>(jobPath, 65536);
            if (job == null) throw new InvalidDataException("Missing installation job.");
            UpdatePaths.ValidateAppId(job.AppId); UpdatePaths.ValidateToken(job.SessionToken);
            if (job.PrepareId != prepareId || !SamePath(job.TransactionDirectory, expected) || job.HostPid <= 0 || job.HostStartUtcTicks <= 0)
                throw new InvalidDataException("Installation job identity is invalid.");
            job.InstallDirectory = Path.GetFullPath(job.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
            UpdatePaths.EnsureNoReparse(job.InstallDirectory);
            if (!Directory.Exists(job.InstallDirectory) || !SamePath(Path.GetDirectoryName(job.ExecutablePath), job.InstallDirectory) ||
                !SamePath(job.ConfigPath, Path.Combine(job.InstallDirectory, "jmd-update.json")) ||
                !SamePath(job.PackagePath, Path.Combine(expected, "package.zip")) ||
                !SamePath(job.EnvelopePath, Path.Combine(expected, "release.json")) ||
                !SamePath(job.ShutdownAckPath, Path.Combine(UpdatePaths.SessionDirectory(job.SessionToken), "shutdown.json")))
                throw new InvalidDataException("Job paths do not match the installation/session boundaries.");
            UpdatePaths.EnsureNoReparse(job.ConfigPath); UpdatePaths.EnsureNoReparse(job.PackagePath);
            UpdatePaths.EnsureNoReparse(job.EnvelopePath); UpdatePaths.EnsureNoReparse(job.ShutdownAckPath);
            return job;
        }

        private static void WaitForNormalExit(InstallJob job, int seconds)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (IsOriginalRunning(job))
            {
                ThrowIfCancelled(job);
                if (timeout.Elapsed.TotalSeconds >= seconds) throw new IOException("Product did not exit normally before timeout. No files were replaced.");
                Thread.Sleep(100);
            }
            ThrowIfCancelled(job);
            ShutdownAcknowledgement ack = ReadBounded<ShutdownAcknowledgement>(job.ShutdownAckPath, 4096);
            if (ack == null || ack.PrepareId != job.PrepareId || ack.Pid != job.HostPid || !SamePath(ack.ExecutablePath, job.ExecutablePath))
                throw new InvalidDataException("A matching normal-shutdown acknowledgement is required; process disappearance is insufficient.");
        }

        private static void WaitForShutdownRequest(InstallJob job, int seconds)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            string path = Path.Combine(job.TransactionDirectory, "shutdown-requested.json");
            while (true)
            {
                ThrowIfCancelled(job);
                if (File.Exists(path))
                {
                    ShutdownRequestAcknowledgement request = ReadBounded<ShutdownRequestAcknowledgement>(path, 4096);
                    if (request != null && request.PrepareId == job.PrepareId && request.HostPid == job.HostPid) return;
                }
                if (!IsOriginalRunning(job)) throw new IOException("Host exited before a matching shutdown request was issued; no program files were changed.");
                if (timeout.Elapsed.TotalSeconds >= seconds) throw new IOException("Timed out waiting for host preparation; no program files were changed.");
                Thread.Sleep(100);
            }
        }

        private static StartupAcknowledgement TryReadStartupAcknowledgement(string path)
        {
            try { return ReadBounded<StartupAcknowledgement>(path, 4096); }
            catch (IOException) { return null; }
            catch (InvalidDataException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static bool IsOriginalRunning(InstallJob job)
        {
            try
            {
                using (Process host = Process.GetProcessById(job.HostPid))
                {
                    if (host.HasExited || host.StartTime.ToUniversalTime().Ticks != job.HostStartUtcTicks) return false;
                    if (!SamePath(host.MainModule.FileName, job.ExecutablePath)) throw new InvalidDataException("Host process path does not match job.");
                    return true;
                }
            }
            catch (ArgumentException) { return false; }
        }

        private static bool IsStartedRunning(InstallJob job, InstallJournal journal)
        {
            if (journal.StartedPid <= 0 || journal.StartedUtcTicks <= 0) return false;
            try
            {
                using (Process process = Process.GetProcessById(journal.StartedPid))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == journal.StartedUtcTicks && SamePath(process.MainModule.FileName, EntryExecutable(job));
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return true; }
        }

        private static void EnsureNoOtherRunningProduct(InstallJob job)
        {
            foreach (string executable in Entries(job).All) EnsureNoOtherRunningProduct(job, executable);
        }

        private static void EnsureNoOtherRunningProduct(InstallJob job, string executable)
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited || !SamePath(ProcessImagePath(process.Id), executable)) continue;
                        if (process.Id == job.HostPid && process.StartTime.ToUniversalTime().Ticks == job.HostStartUtcTicks) continue;
                        throw new IOException("Another product instance is running. The requesting product has not been closed; close the other instance and retry.");
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }

        private static void ThrowIfCancelled(InstallJob job)
        {
            string path = Path.Combine(job.TransactionDirectory, "cancel.json");
            if (!File.Exists(path)) return;
            CancelInstallation cancellation = ReadBounded<CancelInstallation>(path, 4096);
            if (cancellation != null && cancellation.PrepareId == job.PrepareId)
                throw new OperationCanceledException("更新被取消，程序文件没有改动。");
        }

        private static string PendingPath(InstallJob job)
        { return UpdatePaths.UnderRoot(job.InstallDirectory, ".jmd-update/pending.json"); }

        private static void EnsureNoUnfinishedPending(InstallJob job)
        {
            string path = PendingPath(job);
            if (!File.Exists(path)) return;
            PendingInstallation pending = ReadBounded<PendingInstallation>(path, 4096);
            if (pending == null) throw new InvalidDataException("Pending installation metadata is invalid.");
            UpdatePaths.ValidateToken(pending.PrepareId);
            if (!SamePath(pending.TransactionDirectory, UpdatePaths.TransactionDirectory(pending.PrepareId)))
                throw new InvalidDataException("Pending installation points outside the user transaction directory.");
            InstallJob previous = LoadJob(Path.Combine(pending.TransactionDirectory, "job.json"));
            if (!SamePath(previous.InstallDirectory, job.InstallDirectory) || previous.PrepareId != pending.PrepareId)
                throw new InvalidDataException("Pending transaction belongs to a different installation.");
            InstallJournal journal = ReadBounded<InstallJournal>(Path.Combine(previous.TransactionDirectory, "journal.json"), 8 * 1024 * 1024);
            ValidateJournal(previous, journal);
            if (journal.State != "Complete" && journal.State != "RolledBack")
                throw new IOException("An interrupted installation must be recovered before another update: " + Path.Combine(previous.TransactionDirectory, "job.json"));
            ClearPending(previous);
        }

        private static void WritePending(InstallJob job)
        {
            string path = PendingPath(job);
            if (File.Exists(path)) throw new IOException("Another pending transaction already protects the installation.");
            JsonIO.WriteAtomic(path, new PendingInstallation { PrepareId = job.PrepareId, TransactionDirectory = job.TransactionDirectory });
        }

        private static void ClearPending(InstallJob job)
        {
            string path = PendingPath(job);
            if (!File.Exists(path)) return;
            PendingInstallation pending = ReadBounded<PendingInstallation>(path, 4096);
            if (pending == null || pending.PrepareId != job.PrepareId || !SamePath(pending.TransactionDirectory, job.TransactionDirectory))
                throw new InvalidDataException("Pending marker does not match this transaction; it was not deleted.");
            File.Delete(path);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // Only query limited information, so elevated same-name processes can be distinguished.
        // Failure stays fail-closed: never assume an unreadable image is unrelated to this install.
        internal static string ProcessImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero)
                throw new IOException("无法核实进程映像路径（PID " + pid + "，错误 " + Marshal.GetLastWin32Error() + "），请检查权限后重试。");
            try
            {
                StringBuilder path = new StringBuilder(32768);
                uint size = (uint)path.Capacity;
                if (!QueryFullProcessImageNameW(handle, 0, path, ref size))
                    throw new IOException("无法核实进程映像路径（PID " + pid + "，错误 " + Marshal.GetLastWin32Error() + "），请检查权限后重试。");
                return path.ToString();
            }
            finally { CloseHandle(handle); }
        }

        // Match full image paths; exclude this process so in-place recovery can restore its own image.
        private static void WaitForUpdaterExit(InstallJob job)
        {
            string target = UpdatePaths.UnderRoot(job.InstallDirectory, "JmdUpdater/JmdUpdater.exe");
            Stopwatch timeout = Stopwatch.StartNew();
            int ownPid = Process.GetCurrentProcess().Id;
            while (true)
            {
                bool running = false;
                foreach (Process process in Process.GetProcessesByName("JmdUpdater"))
                    using (process)
                    {
                        if (process.Id == ownPid) continue;
                        try { if (!process.HasExited && SamePath(ProcessImagePath(process.Id), target)) running = true; }
                        catch (InvalidOperationException) { }
                    }
                if (!running) return;
                if (timeout.Elapsed.TotalSeconds >= LockWaitSeconds)
                    throw new IOException("更新器或更新设置窗口仍在运行，请关闭后重新更新。");
                Thread.Sleep(250);
            }
        }

        // 本事务签名清单里的入口程序（绝对路径）。主程序改名时与 job.ExecutablePath（旧入口）不同；
        // 读不到或验签失败时按旧入口处理（等同于改名前的行为）。按事务缓存，避免在循环里反复验签。
        private sealed class ProductEntries
        {
            internal string Entry;                     // 本版本入口（绝对路径）
            internal List<string> All;                 // 发起更新的 exe、新入口、签名声明的所有旧入口（去重）
        }
        private static readonly Dictionary<string, ProductEntries> entryCache = new Dictionary<string, ProductEntries>(StringComparer.Ordinal);
        private static ProductEntries Entries(InstallJob job)
        {
            lock (entryCache)
            {
                ProductEntries cached;
                if (entryCache.TryGetValue(job.PrepareId, out cached)) return cached;
                ProductEntries result = new ProductEntries { Entry = job.ExecutablePath, All = new List<string> { job.ExecutablePath } };
                try
                {
                    UpdateConfig config = ManifestIO.LoadConfig(job.ConfigPath);
                    ReleaseManifest manifest = ManifestIO.Verify(JsonIO.Serialize(ReadBounded<SignedRelease>(job.EnvelopePath, 8 * 1024 * 1024)), config.PublicKeyXml);
                    if (manifest.AppId == job.AppId && ManifestIO.AcceptsEntryPoint(manifest, Path.GetFileName(job.ExecutablePath)))
                    {
                        result.Entry = UpdatePaths.UnderRoot(job.InstallDirectory, manifest.EntryPoint);
                        List<string> all = new List<string> { job.ExecutablePath, result.Entry };
                        if (manifest.PreviousEntryPoints != null)
                            all.AddRange(manifest.PreviousEntryPoints.Select(p => UpdatePaths.UnderRoot(job.InstallDirectory, p)));
                        result.All = new List<string>();
                        foreach (string path in all) if (!result.All.Any(p => SamePath(p, path))) result.All.Add(path);
                    }
                }
                catch (Exception) { result = new ProductEntries { Entry = job.ExecutablePath, All = new List<string> { job.ExecutablePath } }; }
                entryCache[job.PrepareId] = result;
                return result;
            }
        }

        // 本事务签名清单里的入口程序（绝对路径）。主程序改名时与 job.ExecutablePath（旧入口）不同；
        // 读不到或验签失败时按旧入口处理（等同于改名前的行为）。
        private static string EntryExecutable(InstallJob job) { return Entries(job).Entry; }

        // 所有入口程序都算本程序：替换/删除它们时不走“挪到一边”，运行检查也全部覆盖。
        private static bool IsProductExecutable(InstallJob job, string path)
        {
            return Entries(job).All.Any(p => SamePath(p, path));
        }

        private static void EnsureNoRunningProduct(InstallJob job)
        {
            foreach (string executable in Entries(job).All) EnsureNoRunningProduct(executable);
        }

        private static void WaitForNoRunningProduct(InstallJob job, int seconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                try { EnsureNoRunningProduct(job); return; }
                catch (IOException) { Thread.Sleep(250); }
            }
        }

        private static void EnsureNoRunningProduct(string executable)
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && SamePath(ProcessImagePath(process.Id), executable))
                            throw new IOException("程序仍有实例在运行，请正常关闭所有实例后更新或恢复。");
                    }
                    catch (InvalidOperationException) { }
                    // Access-denied is intentionally propagated: an unverified instance is not safe to overwrite.
                }
            }
        }

        private static void ValidateJournal(InstallJob job, InstallJournal journal)
        {
            if (journal == null || journal.PrepareId != job.PrepareId || journal.Records == null || journal.Records.Count > 20001)
                throw new InvalidDataException("Recovery journal does not match this transaction.");
            UpdateConfig config = ManifestIO.LoadConfig(job.ConfigPath);
            if (config.AppId != job.AppId) throw new InvalidDataException("Recovery product configuration differs.");
            SignedRelease release = ReadBounded<SignedRelease>(job.EnvelopePath, 8 * 1024 * 1024);
            ReleaseManifest signedManifest = ManifestIO.Verify(JsonIO.Serialize(release), config.PublicKeyXml);
            if (signedManifest.AppId != job.AppId) throw new InvalidDataException("Recovery release differs.");
            HashSet<string> allowed = new HashSet<string>(signedManifest.Files.Select(f => UpdatePaths.Relative(f.Path)), StringComparer.OrdinalIgnoreCase);
            allowed.Add(".jmd-update/installed.json");
            // 主程序改名时安装会删除签名声明的旧入口及其 .config（见 PlanChanges），回滚必须允许恢复它们。
            if (signedManifest.PreviousEntryPoints != null)
                foreach (string previous in signedManifest.PreviousEntryPoints)
                {
                    allowed.Add(UpdatePaths.Relative(previous));
                    allowed.Add(UpdatePaths.Relative(previous) + ".config");
                }
            // Obsolete files may belong to the previous signed release. The old inventory may
            // still be in place or already backed up; both sources must themselves verify.
            string[] inventories = new string[] {
                UpdatePaths.UnderRoot(job.InstallDirectory, ".jmd-update/installed.json"),
                UpdatePaths.UnderRoot(Path.Combine(job.TransactionDirectory, "backup"), ".jmd-update/installed.json")
            };
            foreach (string inventory in inventories)
            {
                if (!File.Exists(inventory)) continue;
                InstalledRelease previous = ReadBounded<InstalledRelease>(inventory, 8 * 1024 * 1024);
                ReleaseManifest previousManifest = ManifestIO.Verify(JsonIO.Serialize(previous.Release), config.PublicKeyXml);
                if (previous.AppId != job.AppId || previousManifest.AppId != job.AppId || previous.Version != previousManifest.Version)
                    throw new InvalidDataException("Recovery inventory does not verify.");
                foreach (ReleaseFile file in previousManifest.Files) allowed.Add(UpdatePaths.Relative(file.Path));
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstallRecord record in journal.Records)
            {
                string name = UpdatePaths.Relative(record.Path);
                if (!seen.Add(name) || !allowed.Contains(name) ||
                    (!name.Equals(".jmd-update/installed.json", StringComparison.OrdinalIgnoreCase) && UpdatePaths.IsPreserved(config, name)) ||
                    record.BackupReady && !ManifestIO.IsHash(record.BackupSha256))
                    throw new InvalidDataException("Recovery journal contains invalid records.");
            }
        }

        private static void WriteJournal(InstallJob job, InstallJournal journal)
        { JsonIO.WriteAtomic(Path.Combine(job.TransactionDirectory, "journal.json"), journal); }

        private static void NotifyFailure(InstallJob job, InstallJournal journal)
        {
            if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("JMD_UPDATER_NO_UI") == "1") return;
            bool restored = journal.State == "RolledBack";
            string message = restored ? (journal.Records.Count == 0 ? "程序文件没有改动。" : "已成功恢复原程序文件。") :
                "更新尚未完成，备份和恢复记录已保留。请正常关闭所有上位机实例后执行：\r\n\"" +
                Path.Combine(job.TransactionDirectory, "installer", "JmdUpdater.exe") + "\" recover --job \"" + Path.Combine(job.TransactionDirectory, "job.json") + "\"";
            if (!restored) message += "\r\n事务副本不存在时备用命令：\r\n\"" +
                UpdatePaths.UnderRoot(job.InstallDirectory, "JmdUpdater/JmdUpdater.exe") + "\" recover --job \"" + Path.Combine(job.TransactionDirectory, "job.json") + "\"";
            message += "\r\n原因：" + journal.Failure;
            if (restored)
            {
                try { StartProduct(job); message += "\r\n已重新打开原程序。"; }
                catch (Exception error) { restored = false; message += "\r\n原程序未能打开，请手动启动：" + error.Message; }
            }
            if (progress != null) progress.Finish(message, restored ? 5000 : 0);
        }

        private static T ReadBounded<T>(string path, long maximum)
        {
            UpdatePaths.EnsureNoReparse(path);
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length > maximum) throw new InvalidDataException("Required update metadata is absent or too large: " + Path.GetFileName(path));
            return JsonIO.Read<T>(path);
        }

        private static bool SamePath(string first, string second)
        {
            return !String.IsNullOrWhiteSpace(first) && !String.IsNullOrWhiteSpace(second) &&
                String.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
    }
}

namespace JmdUpdater
{
    // Project: JmdUpdater. Presentation only: a dedicated STA owns all controls; installation stays on its caller.
    // No UI is created in unattended tests. UI failure must never interrupt a file transaction.
    internal sealed class InstallProgress : IDisposable
    {
        private Thread thread;
        private Form form;
        private TextBox status;
        private ProgressBar bar;
        private Button close;
        private bool finished;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);

        /// <summary>Shows the non-cancellable progress window after verified host exit.</summary>
        /// <param name="title">Product and signed target version.</param>
        internal void Start(string title)
        {
            if (!Environment.UserInteractive || Environment.GetEnvironmentVariable("JMD_UPDATER_NO_UI") == "1") return;
            thread = new Thread(delegate()
            {
                try
                {
                    using (form = new Form())
                    {
                        form.Text = title; form.Width = 520; form.Height = 280;
                        form.TopMost = true; form.StartPosition = FormStartPosition.CenterScreen;
                        form.FormBorderStyle = FormBorderStyle.FixedDialog; form.MaximizeBox = false; form.MinimizeBox = false;
                        status = new TextBox { Left = 16, Top = 16, Width = 470, Height = 150, Text = "已关闭旧版本",
                            ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
                        bar = new ProgressBar { Left = 16, Top = 174, Width = 470, Height = 20 };
                        close = new Button { Left = 398, Top = 205, Width = 88, Text = "关闭", Enabled = false };
                        close.Click += delegate { form.Close(); };
                        form.Controls.Add(status); form.Controls.Add(bar); form.Controls.Add(close);
                        form.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!finished) e.Cancel = true; };
                        form.Shown += delegate { ready.Set(); };
                        Application.Run(form);
                    }
                }
                catch (Exception error) { Console.Error.WriteLine("Progress UI: " + error.Message); }
                finally { ready.Set(); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.WaitOne();
        }

        private void OnUI(Action action)
        {
            if (thread == null || form == null || form.IsDisposed) return;
            try { form.Invoke(action); } catch (InvalidOperationException) { }
        }

        /// <summary>Updates text and the completed/total progress on the UI thread.</summary>
        internal void Report(string text, int completed, int total)
        { OnUI(delegate { status.Text = text; bar.Maximum = Math.Max(1, total); bar.Value = Math.Min(completed, bar.Maximum); }); }

        /// <summary>Allows closing; zero delay retains recovery instructions until the user closes.</summary>
        internal void Finish(string message, int milliseconds)
        {
            OnUI(delegate
            {
                finished = true; status.Text = message; close.Enabled = true;
                if (milliseconds > 0)
                {
                    var timer = new System.Windows.Forms.Timer { Interval = milliseconds };
                    timer.Tick += delegate { timer.Stop(); timer.Dispose(); form.Close(); }; timer.Start();
                }
            });
            if (thread != null) thread.Join();
        }

        /// <summary>Closes any remaining window and releases synchronization resources.</summary>
        public void Dispose()
        {
            OnUI(delegate { finished = true; form.Close(); });
            if (thread != null) thread.Join();
            ready.Dispose();
        }
    }
}
