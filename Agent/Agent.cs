// JMD reusable update agent: discovery, project checks, download and explicit installation handoff.
// The agent never disables motors, saves workspaces, or forcibly terminates host processes.
// With the user's consent it may ask other instances in the same install directory to close normally (WM_CLOSE).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JmdUpdater
{
    internal sealed class InstalledState
    {
        public string AppId { get; set; }
        public string Version { get; set; }
        public SignedRelease Release { get; set; }
    }

    // Written next to the product by the publishing script; display-only, never used for any decision.
    internal sealed class BuildInfo
    {
        public string AppId { get; set; }
        public string Version { get; set; }
        public string SourceCommit { get; set; }
    }

    internal static class Agent
    {
        // Returns the source commit recorded for exactly this installed version, or null.
        internal static string SourceCommitOf(string installDirectory, string appId, string version)
        {
            try
            {
                FileInfo file = new FileInfo(Path.Combine(installDirectory, "build-info.json"));
                if (!file.Exists || file.Length > 64 * 1024) return null;
                BuildInfo info = JsonIO.Read<BuildInfo>(file.FullName);
                if (info == null || info.AppId != appId || info.SourceCommit == null || VersionOf(info.Version) != VersionOf(version)) return null;
                StringBuilder text = new StringBuilder();
                foreach (char c in info.SourceCommit.Trim()) if (!Char.IsControl(c) && text.Length < 64) text.Append(c);
                return text.Length == 0 ? null : text.ToString();
            }
            catch (Exception) { return null; }
        }

        public static int Run(string[] args)
        {
            Dictionary<string, string> options = CliArgs.Parse(args, 0);
            string pipe = CliArgs.Required(options, "pipe");
            if (!pipe.StartsWith("JmdUpdater.", StringComparison.Ordinal) || pipe.Length > 120 || pipe.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) throw new ArgumentException("Invalid host pipe.");
            string token = CliArgs.Required(options, "token"); UpdatePaths.ValidateToken(token);
            int pid = Int32.Parse(CliArgs.Required(options, "pid"));
            string configPath = Path.GetFullPath(CliArgs.Required(options, "config"));
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\JmdUpdater.Agent." + pipe, out created))
            {
                if (!created) { try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\JmdUpdater.Check." + pipe)) signal.Set(); } catch (WaitHandleCannotBeOpenedException) { } return 0; }
                try
                {
                    UpdateConfig config = ManifestIO.LoadConfig(configPath);
                    if (!config.Enabled) return 0;
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    using (AgentContext context = new AgentContext(pipe, token, pid, configPath, config)) Application.Run(context);
                    return 0;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        internal static string Quote(string value)
        {
            // Windows command-line quoting, including backslashes before a quote or closing quote.
            StringBuilder result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); }
                else { result.Append('\\', slashes); result.Append(c); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }
        internal static Version VersionOf(string value)
        {
            return ManifestIO.NormalizedVersion(value);
        }
        internal static Uri ServerUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
                throw new InvalidDataException("请输入有效的更新服务器地址，例如 http://192.168.32.99:8787");
            return uri;
        }
    }

    internal sealed class AgentContext : ApplicationContext
    {
        private readonly string configPath;
        private UpdateConfig config;
        private readonly Process host;
        private readonly long hostStart;
        private readonly HostClient client;
        private readonly string token;
        private readonly EventWaitHandle manualSignal;
        private readonly System.Windows.Forms.Timer poll;
        private readonly System.Windows.Forms.Timer watchdog;
        private readonly UpdateWindow window;
        private HostResponse identity;
        private ReleaseManifest release;
        private string envelope;
        private string currentVersion;
        private string currentCommit;
        private string remindedVersion;
        private bool busy;
        private bool exiting;
        private bool handoff;
        private CancellationTokenSource work;

        public AgentContext(string pipe, string sessionToken, int pid, string path, UpdateConfig options)
        {
            configPath = path; config = options; token = sessionToken;
            host = Process.GetProcessById(pid); hostStart = host.StartTime.ToUniversalTime().Ticks;
            client = new HostClient(pipe, token);
            manualSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\JmdUpdater.Check." + pipe);
            window = new UpdateWindow(config.DisplayName ?? config.AppId);
            window.CheckButton.Click += async delegate { await Discover(true); };
            window.InstallButton.Click += async delegate { await Install(); };
            window.SettingsButton.Click += delegate { EditSettings(); };
            window.CancelRequested += delegate { if (work != null) work.Cancel(); };
            IntPtr windowHandle = window.Handle;
            poll = new System.Windows.Forms.Timer { Interval = config.PollSeconds * 1000 };
            poll.Tick += async delegate { await Discover(false); };
            watchdog = new System.Windows.Forms.Timer { Interval = 1000 };
            watchdog.Tick += async delegate
            {
                if (!HostAlive()) { StopAgent(); return; }
                if (manualSignal.WaitOne(0)) { window.ShowForUser(); await Discover(true); }
            };
            watchdog.Start(); poll.Start();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            window.BeginInvoke(new Action(async delegate { await Discover(false); }));
        }

        private bool HostAlive()
        {
            try { return !host.HasExited && host.StartTime.ToUniversalTime().Ticks == hostStart; }
            catch (InvalidOperationException) { return false; }
        }

        private async Task EnsureIdentity()
        {
            if (identity != null) return;
            HostResponse hello = await Task.Run(() => client.Call("hello", null, null));
            if (!hello.Ok || hello.Pid != host.Id || hello.AppId != config.AppId) throw new InvalidDataException("更新接口与当前软件身份不匹配。" );
            string exe = Path.GetFullPath(hello.ExecutablePath);
            string actualExe = Path.GetFullPath(host.MainModule.FileName);
            string install = Path.GetFullPath(hello.InstallDirectory).TrimEnd('\\');
            if (!String.Equals(exe, actualExe, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetDirectoryName(exe), install, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(configPath, Path.Combine(install, "jmd-update.json"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新安装目录与运行程序不匹配。" );
            UpdatePaths.EnsureNoReparse(install);
            client.SetRegisteredChecks(hello.Checks); currentVersion = hello.Version;
            string installedPath = Path.Combine(install, ".jmd-update", "installed.json");
            if (File.Exists(installedPath))
            {
                InstalledState installed = JsonIO.Read<InstalledState>(installedPath);
                if (installed == null || installed.Release == null || installed.AppId != config.AppId) throw new InvalidDataException("本机更新记录无效。" );
                ReleaseManifest previous = ManifestIO.Verify(JsonIO.Serialize(installed.Release), config.PublicKeyXml);
                if (previous.AppId != config.AppId || previous.Version != installed.Version) throw new InvalidDataException("本机更新版本与签名记录不一致。" );
                currentVersion = installed.Version;
            }
            Agent.VersionOf(currentVersion);
            currentCommit = Agent.SourceCommitOf(install, config.AppId, currentVersion);
            identity = hello;
            await Task.Run(() => TransactionCleanup.Run(config, install));
            window.SetStatus(CurrentLabel() + "\r\n服务器：" + config.ServerUrl);
        }

        private string CurrentLabel()
        {
            return "当前版本：" + currentVersion + (currentCommit == null ? "" : "（源码 " + currentCommit + "）");
        }

        private async Task Discover(bool manual)
        {
            if (busy || exiting || handoff) return;
            busy = true; window.SetBusy(true);
            try
            {
                config = ManifestIO.LoadConfig(configPath);
                if (!config.Enabled) { StopAgent(); return; }
                poll.Interval = config.PollSeconds * 1000;
                await EnsureIdentity();
                Uri server = Agent.ServerUri(config.ServerUrl);
                string body = await Task.Run(() => GetText(new Uri(server, config.AppId + "/latest.json")));
                ReleaseManifest found = ManifestIO.Verify(body, config.PublicKeyXml);
                // 入口程序必须是当前程序，或者是本版本声明替换的旧入口（主程序改名）。
                if (found.AppId != config.AppId || !ManifestIO.AcceptsEntryPoint(found, Path.GetFileName(identity.ExecutablePath)))
                    throw new InvalidDataException("发布包不属于当前软件。" );
                if (found.PackageSize > config.MaxPackageBytes) throw new InvalidDataException("更新包超过配置允许的大小。" );
                if (Agent.VersionOf(found.Version) <= Agent.VersionOf(currentVersion))
                {
                    release = null; envelope = null;
                    window.SetStatus(CurrentLabel() + "\r\n已是最新版本。");
                }
                else
                {
                    release = found; envelope = body;
                    window.SetStatus(CurrentLabel() + "\r\n可更新版本：" + found.Version + "\r\n包大小：" + (found.PackageSize / 1048576.0).ToString("F1") + " MB\r\n\r\n" + found.ReleaseNotes);
                    if (manual || remindedVersion != found.Version) { remindedVersion = found.Version; window.ShowForUser(); }
                }
            }
            catch (Exception error)
            {
                release = null; envelope = null;
                Log(error);
                if (manual) { window.SetStatus("检查更新失败：" + error.Message + "\r\n\r\n可在“服务器设置”中修改发布地址。"); window.ShowForUser(); }
            }
            finally { busy = false; if (!exiting) { window.SetBusy(false); window.InstallButton.Enabled = release != null; } }
        }

        private string GetText(Uri uri)
        {
            HttpWebRequest request = NewRequest(uri);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream bytes = new MemoryStream())
            {
                byte[] buffer = new byte[16384]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (bytes.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("版本清单超过大小限制。" );
                    bytes.Write(buffer, 0, read);
                }
                return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
        }
        private HttpWebRequest NewRequest(Uri uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Proxy = null; request.AllowAutoRedirect = false;
            request.Timeout = config.RequestTimeoutSeconds * 1000; request.ReadWriteTimeout = config.RequestTimeoutSeconds * 1000;
            request.UserAgent = "JmdUpdater/1"; return request;
        }

        // Run from the transaction so Agent/settings can exit and the installed updater can be replaced.
        private static string CopyInstaller(string transaction)
        {
            string source = Application.ExecutablePath;
            string target = UpdatePaths.UnderRoot(transaction, "installer/JmdUpdater.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            foreach (string suffix in new[] { "", ".config" })
            {
                if (suffix.Length != 0 && !File.Exists(source + suffix)) continue;
                UpdatePaths.EnsureNoReparse(source + suffix); UpdatePaths.EnsureNoReparse(target + suffix);
                File.Copy(source + suffix, target + suffix, false);
                if (ManifestIO.Sha256File(source + suffix) != ManifestIO.Sha256File(target + suffix))
                    throw new IOException("安装器副本校验失败，请重新更新。");
            }
            return target;
        }

        private async Task Install()
        {
            if (busy || release == null || exiting || handoff) return;
            busy = true; work = new CancellationTokenSource(); window.SetBusy(true);
            string prepareId = UpdatePaths.NewToken();
            string transaction = UpdatePaths.TransactionDirectory(prepareId);
            string ackPath = Path.Combine(UpdatePaths.SessionDirectory(token), "shutdown.json");
            bool prepared = false;
            bool preparationRequested = false;
            InstallJob job = null;
            Exception failure = null;
            bool cancelled = false;
            try
            {
              try
              {
                HostResponse initial = await Task.Run(() => client.Call("check", null, null));
                string reasons = client.BlockingReasons(initial);
                if (!String.IsNullOrEmpty(reasons)) { ExplainBlocked(reasons); return; }
                if (!await CloseOtherInstances()) return;
                string installProbe = UpdatePaths.UnderRoot(identity.InstallDirectory, ".jmd-update/write-probe-" + prepareId);
                Directory.CreateDirectory(Path.GetDirectoryName(installProbe));
                using (FileStream probe = new FileStream(installProbe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                File.Delete(installProbe);
                Directory.CreateDirectory(transaction); UpdatePaths.EnsureNoReparse(transaction);
                string packagePath = Path.Combine(transaction, "package.zip");
                JsonIO.WriteTextAtomic(Path.Combine(transaction, "release.json"), envelope);
                window.SetStatus("正在下载更新包；完成后会再次检查设备及未保存数据。" );
                ReleaseManifest installing = release;
                await Task.Run(() => Download(installing, packagePath, work.Token));
                work.Token.ThrowIfCancellationRequested();
                if (!HostAlive()) return;
                if (!ManifestIO.LoadConfig(configPath).Enabled) throw new OperationCanceledException("更新已停用。");
                // Prepare the acknowledgement location before the host changes any business state.
                string sessionDirectory = UpdatePaths.SessionDirectory(token);
                UpdatePaths.EnsureNoReparse(sessionDirectory); Directory.CreateDirectory(sessionDirectory); UpdatePaths.EnsureNoReparse(sessionDirectory);
                if (File.Exists(ackPath)) File.Delete(ackPath);
                job = new InstallJob { AppId = config.AppId, InstallDirectory = identity.InstallDirectory, ExecutablePath = identity.ExecutablePath,
                    HostPid = host.Id, HostStartUtcTicks = hostStart, PrepareId = prepareId, SessionToken = token, ShutdownAckPath = ackPath,
                    PackagePath = packagePath, EnvelopePath = Path.Combine(transaction, "release.json"), ConfigPath = configPath, TransactionDirectory = transaction };
                string jobPath = Path.Combine(transaction, "job.json"); JsonIO.WriteAtomic(jobPath, job);
                ProcessStartInfo start = new ProcessStartInfo(CopyInstaller(transaction), "install --job " + Agent.Quote(jobPath))
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = transaction };
                using (Process installer = Process.Start(start))
                {
                    if (installer == null) throw new IOException("无法启动独立更新安装程序。" );
                    // Hashing/extraction and multi-instance checks happen while the host is still fully usable.
                    window.SetStatus("安装器正在校验并准备文件，原软件仍可使用……" );
                    await Task.Run(() => WaitForStaging(installer, job, work.Token));
                    work.Token.ThrowIfCancellationRequested();
                    if (!HostAlive()) { CancelInstall(job); return; }
                    if (!ManifestIO.LoadConfig(configPath).Enabled) throw new OperationCanceledException("更新已停用。");
                    if (installer.HasExited) throw new IOException("安装器已停止，原软件将保持运行，请重新检查更新。" );
                    window.SetStatus("文件已准备就绪，正在执行最后检查……" );
                    preparationRequested = true;
                    HostResponse final = await Task.Run(() => client.Call("prepare", prepareId, ackPath));
                    reasons = client.BlockingReasons(final);
                    if (!String.IsNullOrEmpty(reasons) || !final.Prepared || final.PrepareId != prepareId)
                    {
                        CancelInstall(job); await CancelPreparation(prepareId);
                        ExplainBlocked(String.IsNullOrEmpty(reasons) ? "原程序未能进入更新准备状态，请稍后重试。" : reasons); return;
                    }
                    prepared = true; work.Token.ThrowIfCancellationRequested();
                    if (installer.HasExited) throw new IOException("安装器已停止，已取消退出准备，请重试更新。" );
                    window.SetHandoff(true);
                    window.SetStatus("正在请求原程序正常退出。更新器会等待退出确认，不会强制关闭程序。" );
                    // ExitTimeout starts at this explicit signal, never at completion of expensive staging.
                    JsonIO.WriteAtomic(Path.Combine(transaction, "shutdown-requested.json"), new { PrepareId = prepareId, HostPid = host.Id });
                    HostResponse shutdown = null;
                    try
                    {
                        shutdown = await Task.Run(() => client.Call("shutdown", prepareId, ackPath));
                    }
                    catch (Exception error) { Log(error); /* A closing host may drop IPC. Installer still requires its matching acknowledgement. */ }
                    if (shutdown != null && !shutdown.Ok)
                    {
                        CancelInstall(job); await CancelPreparation(prepareId); prepared = false;
                        throw new InvalidOperationException(client.BlockingReasons(shutdown));
                    }
                    handoff = true;
                    bool acknowledgedExit = await Task.Run(() => WaitForAcknowledgedExit(installer, job));
                    if (!acknowledgedExit)
                    {
                        handoff = false; CancelInstall(job);
                        await CancelPreparation(prepareId); prepared = false;
                        ExplainBlocked("原程序未完成正常退出，本次安装已停止。请结束相关操作后重试。" );
                    }
                    else StopAgent();
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception error)
            {
                failure = error;
            }
              // C# 5 has no await in catch/finally. Release the host before displaying recovery instructions.
              if (!handoff && job != null) CancelInstall(job);
              if ((prepared || preparationRequested) && HostAlive() && !handoff) await CancelPreparation(prepareId);
              if (failure != null) { Log(failure); if (!exiting) ExplainBlocked("本次更新未完成：" + failure.Message); }
              else if (cancelled && !exiting) window.SetStatus("已取消本次更新，可处理完成后重新检查。" );
            }
            finally
            {
                work.Dispose(); work = null; busy = false;
                if (!exiting) { window.SetHandoff(false); window.SetBusy(false); window.InstallButton.Enabled = release != null; }
            }
        }

        // 同一安装目录里、除发起更新的这个之外仍在运行的本程序进程。只按完整 exe 路径匹配：
        // 其他目录的同名程序、其他软件一律不算；读不到路径的进程也不算、不去碰（安装器仍会做最终检查）。
        private List<Process> OtherInstances()
        {
            List<Process> result = new List<Process>();
            string target = Path.GetFullPath(identity.ExecutablePath);
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
            {
                bool same = false;
                try
                {
                    same = process.Id != host.Id && !process.HasExited &&
                        String.Equals(Path.GetFullPath(ImagePath(process.Id)), target, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception) { same = false; }
                if (same) result.Add(process); else process.Dispose();
            }
            return result;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref uint size);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // 只申请“查询有限信息”权限读取进程完整路径；读不到就抛异常，调用方按“不是本目录实例”处理。
        private static string ImagePath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) throw new IOException("无法读取进程路径。");
            try
            {
                StringBuilder path = new StringBuilder(32768);
                uint size = (uint)path.Capacity;
                if (!QueryFullProcessImageNameW(handle, 0, path, ref size)) throw new IOException("无法读取进程路径。");
                return path.ToString();
            }
            finally { CloseHandle(handle); }
        }

        // 返回 true：没有其他实例，可以继续更新。返回 false：用户不同意或仍有窗口没关掉（已提示）。
        // 只发送正常关闭请求（等同于点窗口右上角的关闭），从不强制结束进程。
        private async Task<bool> CloseOtherInstances()
        {
            List<Process> others = OtherInstances();
            try
            {
                if (others.Count == 0) return true;
                string name = config.DisplayName ?? config.AppId;
                StringBuilder ids = new StringBuilder();
                foreach (Process process in others) ids.Append(ids.Length == 0 ? "" : "、").Append(process.Id);
                string intro = "同一目录下还开着 " + others.Count + " 个其他的 " + name + " 窗口（进程号 " + ids + "），更新前需要先关闭它们。";
                if (Environment.GetEnvironmentVariable("JMD_UPDATER_NO_UI") == "1") { ExplainBlocked(intro); return false; }
                DialogResult answer = MessageBox.Show(window, intro + "\r\n\r\n" +
                    "点“是”：请这些窗口正常关闭（等同于点它们右上角的关闭按钮，不会强制结束），然后继续更新。\r\n" +
                    "注意：这些窗口里没保存的参数会丢失；它们连着的电机不会被自动失能。\r\n\r\n" +
                    "点“否”：取消本次更新，您自己处理后再点“更新 / 重试”。",
                    "关闭其他窗口后更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes)
                {
                    window.SetStatus("已取消本次更新：" + intro + "\r\n\r\n关闭它们后点击“更新 / 重试”。");
                    return false;
                }
                window.SetStatus("正在请其他 " + name + " 窗口正常关闭，最多等待 15 秒……");
                foreach (Process process in others)
                {
                    try { process.CloseMainWindow(); } catch (InvalidOperationException) { }
                }
                await Task.Run(delegate
                {
                    Stopwatch waited = Stopwatch.StartNew();
                    foreach (Process process in others)
                    {
                        int left = 15000 - (int)waited.ElapsedMilliseconds;
                        if (left <= 0) break;
                        try { process.WaitForExit(left); } catch (Exception) { }
                    }
                });
                List<Process> remaining = OtherInstances();
                try
                {
                    if (remaining.Count == 0) return true;
                    StringBuilder stillOpen = new StringBuilder();
                    foreach (Process process in remaining) stillOpen.Append(stillOpen.Length == 0 ? "" : "、").Append(process.Id);
                    ExplainBlocked("还有 " + remaining.Count + " 个 " + name + " 窗口没有关闭（进程号 " + stillOpen + "）。\r\n" +
                        "如果那个窗口弹出了提示，请在那个窗口里处理；也可以手动关闭它，或在任务管理器里确认后结束。");
                    return false;
                }
                finally { foreach (Process process in remaining) process.Dispose(); }
            }
            finally { foreach (Process process in others) process.Dispose(); }
        }

        private void WaitForStaging(Process installer, InstallJob job, CancellationToken cancel)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            string stagedPath = Path.Combine(job.TransactionDirectory, "staged.json");
            string failedPath = Path.Combine(job.TransactionDirectory, "stage-failed.json");
            while (elapsed.Elapsed.TotalSeconds < config.StageTimeoutSeconds)
            {
                cancel.ThrowIfCancellationRequested();
                if (!HostAlive()) throw new OperationCanceledException("原程序已退出。");
                if (File.Exists(failedPath))
                {
                    UpdatePaths.EnsureNoReparse(failedPath);
                    if (new FileInfo(failedPath).Length <= 16384)
                    {
                        Dictionary<string, object> failure = JsonIO.Read<Dictionary<string, object>>(failedPath);
                        object id, message, code;
                        if (failure != null && failure.TryGetValue("PrepareId", out id) && Convert.ToString(id) == job.PrepareId)
                        {
                            if (failure.TryGetValue("Code", out code) && Convert.ToString(code) == "Cancelled") throw new OperationCanceledException("本次更新已取消。");
                            throw new IOException("安装器准备失败，原软件保持运行。" +
                                (failure.TryGetValue("Message", out message) ? "\r\n原因：" + Convert.ToString(message) : "请重新检查更新包或关闭其他实例后重试。"));
                        }
                    }
                    throw new InvalidDataException("安装器失败记录与本次更新不匹配。" );
                }
                if (installer.HasExited) throw new IOException("安装器未能完成准备，原程序将保持运行；请查看更新提示或日志。" );
                if (File.Exists(stagedPath))
                {
                    UpdatePaths.EnsureNoReparse(stagedPath);
                    if (new FileInfo(stagedPath).Length > 4096) throw new InvalidDataException("安装器确认无效。" );
                    Dictionary<string, object> staged = JsonIO.Read<Dictionary<string, object>>(stagedPath);
                    object id, pid;
                    if (staged != null && staged.TryGetValue("PrepareId", out id) && Convert.ToString(id) == job.PrepareId &&
                        staged.TryGetValue("HostPid", out pid) && Convert.ToInt32(pid) == job.HostPid) return;
                    throw new InvalidDataException("安装器确认与当前事务不匹配。" );
                }
                Thread.Sleep(100);
            }
            throw new TimeoutException("安装器准备超时，原程序不会关闭，请稍后重试。" );
        }

        private bool WaitForAcknowledgedExit(Process installer, InstallJob job)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < config.ExitTimeoutSeconds + 2)
            {
                if (!HostAlive())
                {
                    // The acknowledgement is written during the host's final shutdown step; allow a brief
                    // moment for it to become readable before treating the exit as unacknowledged.
                    Stopwatch settle = Stopwatch.StartNew();
                    while (true)
                    {
                        if (HasShutdownAcknowledgement(job)) return true;
                        if (settle.Elapsed.TotalSeconds >= 3) return false;
                        Thread.Sleep(100);
                    }
                }
                if (installer.HasExited) return false;
                Thread.Sleep(100);
            }
            return false;
        }

        internal static bool HasShutdownAcknowledgement(InstallJob job)
        {
            try
            {
                UpdatePaths.EnsureNoReparse(job.ShutdownAckPath);
                if (!File.Exists(job.ShutdownAckPath) || new FileInfo(job.ShutdownAckPath).Length > 4096) return false;
                Dictionary<string, object> ack = JsonIO.Read<Dictionary<string, object>>(job.ShutdownAckPath);
                object id, pid, exe;
                return ack.TryGetValue("PrepareId", out id) && Convert.ToString(id) == job.PrepareId &&
                    ack.TryGetValue("Pid", out pid) && Convert.ToInt32(pid) == job.HostPid &&
                    // Both sides normalized: Qt hosts report "C:/..." while GetFullPath yields "C:\...".
                    ack.TryGetValue("ExecutablePath", out exe) &&
                    String.Equals(Path.GetFullPath(Convert.ToString(exe)), Path.GetFullPath(job.ExecutablePath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        private static void CancelInstall(InstallJob job)
        {
            try { JsonIO.WriteAtomic(Path.Combine(job.TransactionDirectory, "cancel.json"), new { PrepareId = job.PrepareId }); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void Download(ReleaseManifest manifest, string path, CancellationToken cancel)
        {
            HttpWebRequest request = NewRequest(new Uri(Agent.ServerUri(config.ServerUrl), config.AppId + "/" + manifest.PackageName));
            string part = path + ".part";
            using (cancel.Register(() => request.Abort()))
            {
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (Stream input = response.GetResponseStream())
                    using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        if (response.ContentLength >= 0 && response.ContentLength != manifest.PackageSize) throw new InvalidDataException("更新包大小与签名清单不一致。" );
                        byte[] bytes = new byte[65536]; long total = 0; int count;
                        while ((count = input.Read(bytes, 0, bytes.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested(); total += count;
                            if (total > manifest.PackageSize || total > config.MaxPackageBytes) throw new InvalidDataException("更新包超出允许大小。" );
                            output.Write(bytes, 0, count);
                        }
                        if (total != manifest.PackageSize) throw new InvalidDataException("更新包下载不完整。" );
                        output.Flush(true);
                    }
                    cancel.ThrowIfCancellationRequested();
                    if (!String.Equals(ManifestIO.Sha256File(part), manifest.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("更新包校验失败。" );
                    File.Move(part, path);
                }
                catch (WebException) { cancel.ThrowIfCancellationRequested(); throw; }
                finally { if (File.Exists(part)) File.Delete(part); }
            }
        }
        private async Task CancelPreparation(string id)
        { try { await Task.Run(() => client.Call("cancel", id, null)); } catch (Exception error) { Log(error); } }

        private void ExplainBlocked(string reason)
        {
            if (exiting) return;
            window.SetStatus("暂时无法安装更新：\r\n\r\n" + reason + "\r\n\r\n请返回原软件处理，然后点击“更新 / 重试”。" );
            window.ShowForUser();
            if (Environment.GetEnvironmentVariable("JMD_UPDATER_NO_UI") == "1") return;
            MessageBox.Show(window, reason + "\r\n\r\n请返回原软件处理完成后重试。", "更新前检查", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void EditSettings()
        {
            if (busy || exiting) return;
            using (Form dialog = new Form { Text = "更新服务器设置", Width = 530, Height = 175, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                Label label = new Label { Left = 15, Top = 15, Width = 480, Text = "服务器地址（可填写 IP，默认端口 8787，或完整 HTTP 地址）" };
                TextBox input = new TextBox { Left = 15, Top = 43, Width = 480, Text = config.ServerUrl };
                Button save = new Button { Left = 330, Top = 82, Width = 80, Text = "保存" };
                Button cancel = new Button { Left = 420, Top = 82, Width = 75, Text = "取消", DialogResult = DialogResult.Cancel };
                save.Click += delegate
                {
                    try
                    {
                        string value = input.Text.Trim(); if (value.IndexOf("://", StringComparison.Ordinal) < 0) value = "http://" + value + (value.IndexOf(':') < 0 ? ":8787" : "");
                        Uri address = Agent.ServerUri(value.TrimEnd('/') + "/");
                        UpdateConfig original = JsonIO.Read<UpdateConfig>(configPath); original.ServerUrl = address.AbsoluteUri.TrimEnd('/');
                        JsonIO.WriteAtomic(configPath, original); config = ManifestIO.LoadConfig(configPath); release = null; envelope = null; remindedVersion = null;
                        window.InstallButton.Enabled = false; window.SetStatus("服务器地址已保存：" + config.ServerUrl + "\r\n请点击“检查更新”。" );
                        dialog.DialogResult = DialogResult.OK;
                    }
                    catch (Exception error) { MessageBox.Show(dialog, error.Message, "无法保存地址", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                dialog.Controls.AddRange(new Control[] { label, input, save, cancel }); dialog.AcceptButton = save; dialog.CancelButton = cancel; dialog.ShowDialog(window);
            }
        }
        private void Log(Exception error)
        {
            try
            {
                string directory = Path.Combine(UpdatePaths.UserRoot, "Logs"); Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, config.AppId + "-agent.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " " + error.GetType().Name + ": " + error.Message + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private void StopAgent()
        {
            if (exiting) return; exiting = true;
            poll.Stop(); watchdog.Stop(); if (work != null) work.Cancel();
            window.AllowClose = true; window.Close(); ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { poll.Dispose(); watchdog.Dispose(); manualSignal.Dispose(); host.Dispose(); window.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal sealed class UpdateWindow : Form
    {
        internal readonly Button CheckButton;
        internal readonly Button InstallButton;
        internal readonly Button SettingsButton;
        private readonly TextBox status;
        private readonly Button later;
        private bool handingOff;
        internal bool AllowClose;
        internal event Action CancelRequested;
        public UpdateWindow(string name)
        {
            Text = name + " — 局域网更新"; Width = 640; Height = 430; MinimumSize = new Size(520, 350); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);
            status = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Left = 15, Top = 15, Width = 594, Height = 300,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom, Text = "正在检查局域网更新……" };
            CheckButton = new Button { Text = "检查更新", Left = 15, Top = 335, Width = 100, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            InstallButton = new Button { Text = "更新 / 重试", Left = 125, Top = 335, Width = 110, Enabled = false, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            SettingsButton = new Button { Text = "服务器设置", Left = 245, Top = 335, Width = 110, Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
            later = new Button { Text = "返回原软件", Left = 485, Top = 335, Width = 120, Anchor = AnchorStyles.Right | AnchorStyles.Bottom };
            later.Click += delegate { if (CancelRequested != null) CancelRequested(); Hide(); };
            Controls.AddRange(new Control[] { status, CheckButton, InstallButton, SettingsButton, later });
        }
        internal void SetStatus(string text) { if (!IsDisposed) status.Text = text; }
        internal void SetBusy(bool value) { if (IsDisposed) return; CheckButton.Enabled = !value; SettingsButton.Enabled = !value; InstallButton.Enabled = false; }
        internal void SetHandoff(bool value) { handingOff = value; if (!IsDisposed) later.Enabled = !value; }
        internal void ShowForUser() { if (IsDisposed || Environment.GetEnvironmentVariable("JMD_UPDATER_NO_UI") == "1") return; Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; if (!handingOff) { if (CancelRequested != null) CancelRequested(); Hide(); } }
            base.OnFormClosing(e);
        }
    }
}
