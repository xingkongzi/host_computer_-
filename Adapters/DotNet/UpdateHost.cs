// JMD shared WinForms update adapter. Business checks remain in the owning application.
// Requests are authenticated locally and dispatched to the UI thread; installation never kills the host.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace JmdUpdater
{
    public sealed class CheckResult
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }

        public static CheckResult Ready(string id) { return new CheckResult { Id = id, Status = "Ready", Message = "" }; }
        public static CheckResult Blocked(string id, string message) { return new CheckResult { Id = id, Status = "Blocked", Message = message }; }
        public static CheckResult Unknown(string id, string message) { return new CheckResult { Id = id, Status = "Unknown", Message = message }; }
    }

    /// <summary>Optional per-window bridge to the independent update agent; call Start after host initialization.</summary>
    public sealed class UpdateHost : IDisposable
    {
        private const int MaxMessageBytes = 65536;
        private readonly Form owner;
        private readonly string appId;
        private readonly string fallbackVersion;
        private readonly string configPath;
        private readonly string executablePath;
        private readonly List<KeyValuePair<string, Func<CheckResult>>> checks = new List<KeyValuePair<string, Func<CheckResult>>>();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer leaseTimer;
        private Func<bool> beginPreparation;
        private Action cancelPreparation;
        private Func<bool> shutdown;
        private string token;
        private string pipeName;
        private string prepareId;
        private string ackPath;
        private DateTime leaseUntil;
        private volatile bool started;
        private bool disposed;
        private bool committed;

        public bool IsPrepared { get { return prepareId != null; } }
        public bool IsShutdownRequested { get; private set; }

        public static string StartupBlockReason { get; private set; }

        /// <summary>True after CanStartApplication handed an unfinished update to "recover --restart":
        /// exit quietly, the updater restores the previous version and reopens the program.</summary>
        public static bool AutoRecoveryStarted { get; private set; }

        /// <summary>True when the program must exit immediately without showing StartupBlockReason itself:
        /// auto-recovery was started, or an install/recovery is running and the updater shows the notice.
        /// A dialog kept open by the program would block that install or recovery.</summary>
        public static bool ExitQuietly { get; private set; }

        private const string BusyNotice = "软件正在更新或恢复上一版本，请等待完成后再打开（完成后会自动打开）。";

        /// <summary>Rejects an unfinished replacement even after its installer process or machine has stopped.</summary>
        public static bool CanStartApplication(string installDirectory)
        {
            string directory = NormalizeDirectory(installDirectory);
            Dictionary<string, object> job, journal;
            string pendingToken, jobPath;
            bool pending = ReadPending(directory, out pendingToken, out jobPath, out job, out journal);
            bool legitimateRestart = pending && IsInstallerRestart(directory, null, pendingToken, job, journal);
            string state = journal == null ? null : GetString(journal, "State");
            if (pending && state != "Complete" && state != "RolledBack")
            {
                if (legitimateRestart) return true;
                // No installer holds the lock: restore the previous version automatically.
                bool lockFree;
                try { lockFree = IsInstallLockFree(directory); }
                catch (UnauthorizedAccessException) { lockFree = false; }
                if (jobPath != null && lockFree && LaunchUpdater(directory, "recover --job \"" + jobPath + "\" --restart true", jobPath))
                {
                    AutoRecoveryStarted = true; ExitQuietly = true;
                    StartupBlockReason = "上次更新没有完成，正在自动恢复上一版本，完成后软件会自动重新打开。";
                    return false;
                }
                if (!lockFree)
                {
                    StartupBlockReason = BusyNotice;
                    ExitQuietly = LaunchNotice(directory, BusyNotice);
                    return false;
                }
                StartupBlockReason = "更新事务尚未完成，已阻止启动以避免运行混合版本。\r\n" +
                    (jobPath == null ? "更新记录无效，请联系发布人员检查 .jmd-update/pending.json 并恢复。" :
                    "请使用恢复命令：\r\n\"" + RecoveryHelper(directory, jobPath) + "\" recover --job \"" + jobPath + "\"");
                return false;
            }
            bool free;
            try { free = IsInstallLockFree(directory); }
            catch (UnauthorizedAccessException) { StartupBlockReason = "无法验证更新锁，请联系发布人员检查权限后重试。"; return false; }
            if (free || legitimateRestart) return true;
            StartupBlockReason = BusyNotice;
            ExitQuietly = LaunchNotice(directory, BusyNotice);
            return false;
        }

        // Shows the notice through Windows PowerShell so this process can exit at once, independent of the
        // deployed updater version. The script travels Base64-encoded, so the text needs no command-line quoting.
        private static bool LaunchNotice(string directory, string text)
        {
            try
            {
                string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                if (!File.Exists(powershell)) return false;
                string script = "Add-Type -AssemblyName System.Windows.Forms; [void][System.Windows.Forms.MessageBox]::Show('" +
                    text.Replace("'", "''") + "', '局域网更新', 'OK', 'Information')";
                ProcessStartInfo start = new ProcessStartInfo(powershell,
                    "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
                using (Process.Start(start)) { }
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        // True when no installer or recovery currently owns this installation's replacement lock.
        private static bool IsInstallLockFree(string directory)
        {
            string hash;
            using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(directory.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
            try
            {
                using (Mutex installation = Mutex.OpenExisting("Local\\JmdUpdater.Install." + hash))
                {
                    bool acquired = false;
                    try { acquired = installation.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    finally { if (acquired) installation.ReleaseMutex(); }
                    return acquired;
                }
            }
            catch (WaitHandleCannotBeOpenedException) { return true; }
        }

        private static string RecoveryHelper(string directory, string jobPath)
        {
            string copy = Path.Combine(Path.GetDirectoryName(jobPath), "installer", "JmdUpdater.exe");
            return File.Exists(copy) ? copy : Path.Combine(directory, "JmdUpdater", "JmdUpdater.exe");
        }

        // Starts the bundled updater detached and without a console window.
        private static bool LaunchUpdater(string directory, string arguments, string jobPath)
        {
            // jobPath comes only from the validated pending transaction binding.
            string helper = RecoveryHelper(directory, jobPath);
            if (!File.Exists(helper)) return false;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo(helper, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
                using (Process.Start(start)) { }
                return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static string NormalizeDirectory(string path)
        { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }

        // Returns true for present-but-invalid records too: corrupted transaction metadata must fail closed.
        private static bool ReadPending(string directory, out string pendingToken, out string jobPath,
            out Dictionary<string, object> job, out Dictionary<string, object> journal)
        {
            pendingToken = null; jobPath = null; job = null; journal = null;
            string pendingPath = Path.Combine(directory, ".jmd-update", "pending.json");
            if (!File.Exists(pendingPath) && !Directory.Exists(pendingPath)) return false;
            try
            {
                if (!SamePath(pendingPath, pendingPath)) return true;
                var pending = ReadJson(pendingPath);
                pendingToken = GetString(pending, "PrepareId");
                if (!IsToken(pendingToken)) return true;
                string transaction = Path.Combine(LocalRoot, "Transactions", pendingToken);
                if (!SamePath(GetString(pending, "TransactionDirectory"), transaction)) return true;
                string expectedJob = Path.Combine(transaction, "job.json");
                string expectedJournal = Path.Combine(transaction, "journal.json");
                if (!SamePath(expectedJob, expectedJob) || !SamePath(expectedJournal, expectedJournal)) return true;
                var loadedJob = ReadJson(expectedJob);
                if (GetString(loadedJob, "PrepareId") != pendingToken ||
                    !String.Equals(NormalizeDirectory(GetString(loadedJob, "InstallDirectory")), directory, StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(NormalizeDirectory(Path.GetDirectoryName(GetString(loadedJob, "ExecutablePath"))), directory, StringComparison.OrdinalIgnoreCase)) return true;
                jobPath = expectedJob;
                var loadedJournal = ReadJson(expectedJournal, 8 * 1024 * 1024);
                if (GetString(loadedJournal, "PrepareId") != pendingToken) return true;
                object rawRecords;
                if (loadedJournal.TryGetValue("Records", out rawRecords))
                {
                    IList records = rawRecords as IList;
                    if (records == null || records.Count > 20001) return true;
                }
                job = loadedJob; journal = loadedJournal;
            }
            catch (Exception ex) { Trace.WriteLine("Unfinished update metadata cannot be verified: " + ex.Message); }
            return true;
        }

        private static bool IsInstallerRestart(string directory, string expectedAppId, string pendingToken,
            Dictionary<string, object> job, Dictionary<string, object> journal)
        {
            if (job == null || journal == null || GetString(journal, "State") != "WaitingForStartup") return false;
            string readyToken = Environment.GetEnvironmentVariable("JMD_UPDATE_READY_TOKEN");
            string readyPath = Environment.GetEnvironmentVariable("JMD_UPDATE_READY_PATH");
            if (!IsToken(readyToken) || readyToken != pendingToken ||
                !SamePath(readyPath, Path.Combine(LocalRoot, "Transactions", readyToken, "ready.json"))) return false;
            // 安装器启动的入口：主程序改名时日志里记的是新 exe（StartedExecutable）；旧日志没有该字段时按 job 的原入口。
            string started = GetString(journal, "StartedExecutable");
            if (String.IsNullOrEmpty(started)) started = GetString(job, "ExecutablePath");
            return (expectedAppId == null || GetString(job, "AppId") == expectedAppId) &&
                !String.IsNullOrEmpty(started) &&
                String.Equals(NormalizeDirectory(Path.GetDirectoryName(Path.GetFullPath(started))), directory, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(Path.GetFullPath(started), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase) &&
                String.Equals(NormalizeDirectory(GetString(job, "InstallDirectory")), directory, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creates an inert adapter; absent configuration or Enabled=false leaves the host unaffected.</summary>
        public UpdateHost(Form owner, string appId, string fallbackVersion, string configPath)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            if (String.IsNullOrWhiteSpace(appId)) throw new ArgumentException("An application ID is required.", "appId");
            this.owner = owner;
            this.appId = appId;
            this.fallbackVersion = fallbackVersion;
            this.configPath = Path.GetFullPath(configPath);
            executablePath = Path.GetFullPath(Application.ExecutablePath);
            leaseTimer = new System.Windows.Forms.Timer { Interval = 500 };
            leaseTimer.Tick += delegate { if (IsPrepared && DateTime.UtcNow >= leaseUntil) CancelPreparation(); };
        }

        /// <summary>Registers a host-owned, side-effect-free UI-thread check. Exceptions and missing results fail closed.</summary>
        public void RegisterCheck(string id, Func<CheckResult> callback)
        {
            if (started) throw new InvalidOperationException("Register checks before Start.");
            if (String.IsNullOrWhiteSpace(id) || callback == null) throw new ArgumentException("A check ID and callback are required.");
            if (checks.Exists(item => item.Key == id)) throw new ArgumentException("Duplicate check ID: " + id);
            checks.Add(new KeyValuePair<string, Func<CheckResult>>(id, callback));
        }

        /// <summary>Begin must atomically block new host work; cancel must restore it after rejection or lease expiration.</summary>
        public void SetPreparationHandlers(Func<bool> begin, Action cancel) { beginPreparation = begin; cancelPreparation = cancel; }

        /// <summary>Returns whether host cleanup permits normal Form.Close; the adapter performs Close afterwards.</summary>
        public void SetShutdownHandler(Func<bool> handler) { shutdown = handler; }

        /// <summary>Starts the optional agent and authenticated pipe. This method must run on the owner's UI thread.</summary>
        public void Start()
        {
            if (started || disposed) return;
            NotifyStartupReady();
            try
            {
                if (!owner.IsHandleCreated || !File.Exists(configPath)) return;
                Dictionary<string, object> config = ReadJson(configPath);
                object enabled;
                if (!config.TryGetValue("Enabled", out enabled) || !(enabled is bool) || !(bool)enabled) return;
                string helper = Path.Combine(Path.GetDirectoryName(executablePath), "JmdUpdater", "JmdUpdater.exe");
                if (!File.Exists(helper)) return;
                byte[] secret = new byte[32];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(secret);
                token = BitConverter.ToString(secret).Replace("-", "").ToLowerInvariant();
                pipeName = "JmdUpdater." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
                started = true;
                Task.Run((Func<Task>)ServeAsync);
                Process.Start(new ProcessStartInfo(helper, "agent --pipe " + Quote(pipeName) + " --token " + token +
                    " --pid " + Process.GetCurrentProcess().Id + " --config " + Quote(configPath))
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executablePath) });
            }
            catch (Exception ex) { Trace.WriteLine("Update agent unavailable: " + ex.Message); Stop(); }
        }

        /// <summary>Signals the optional agent's manual-check event without blocking the application UI.</summary>
        public void CheckNow()
        {
            if (!started) return;
            try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\JmdUpdater.Check." + pipeName)) signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Reports basic UI readiness at the login screen; user authentication and device connection are not required.</summary>
        public static bool ReportStartupReady(string installDirectory, string appId)
        {
            try
            {
                string directory = NormalizeDirectory(installDirectory);
                Dictionary<string, object> job, journal;
                string pendingToken, jobPath;
                if (String.IsNullOrWhiteSpace(appId) || !ReadPending(directory, out pendingToken, out jobPath, out job, out journal) ||
                    !IsInstallerRestart(directory, appId, pendingToken, job, journal)) return false;
                string expected = Path.Combine(LocalRoot, "Transactions", pendingToken, "ready.json");
                if (File.Exists(expected))
                {
                    var ready = ReadJson(expected);
                    if (GetString(ready, "Token") != pendingToken || GetInt(ready, "Pid") != Process.GetCurrentProcess().Id) return false;
                }
                else WriteConfirmation(expected, new { Token = pendingToken, Pid = Process.GetCurrentProcess().Id });
                Environment.SetEnvironmentVariable("JMD_UPDATE_READY_PATH", null);
                Environment.SetEnvironmentVariable("JMD_UPDATE_READY_TOKEN", null);
                return true;
            }
            catch (Exception ex) { Trace.WriteLine("Update startup acknowledgement failed: " + ex.Message); return false; }
        }

        /// <summary>Acknowledges readiness when called by later host initialization; a login-screen acknowledgement is sufficient.</summary>
        public void NotifyStartupReady() { ReportStartupReady(Path.GetDirectoryName(executablePath), appId); }

        /// <summary>Call LAST in the host's OnFormClosing override, after base.OnFormClosing and cancellation checks.</summary>
        public bool CommitShutdown()
        {
            if (!IsShutdownRequested || !IsPrepared || committed || DateTime.UtcNow >= leaseUntil) return false;
            if (!SamePath(ackPath, Path.Combine(LocalRoot, "Sessions", token, "shutdown.json"))) return false;
            try
            {
                WriteConfirmation(ackPath, new { PrepareId = prepareId, Pid = Process.GetCurrentProcess().Id, ExecutablePath = executablePath });
                committed = true;
                leaseTimer.Stop();
                return true;
            }
            catch (Exception ex) { Trace.WriteLine("Update shutdown acknowledgement failed: " + ex.Message); return false; }
        }

        /// <summary>Releases a preparation gate when shutdown is cancelled; no device commands are issued.</summary>
        public void CancelPreparation()
        {
            if (committed) return;
            bool wasPrepared = IsPrepared;
            // A rejected close must not leave a matching acknowledgement for the installer to consume later.
            try
            {
                string expected = token == null ? null : Path.Combine(LocalRoot, "Sessions", token, "shutdown.json");
                if (prepareId != null && SamePath(ackPath, expected) && File.Exists(expected))
                {
                    var confirmation = ReadJson(expected);
                    if (GetString(confirmation, "PrepareId") == prepareId && GetInt(confirmation, "Pid") == Process.GetCurrentProcess().Id &&
                        String.Equals(GetString(confirmation, "ExecutablePath"), executablePath, StringComparison.OrdinalIgnoreCase)) File.Delete(expected);
                }
            }
            catch (Exception ex) { Trace.WriteLine("Cancelled update acknowledgement cleanup failed: " + ex.Message); }
            prepareId = null;
            ackPath = null;
            IsShutdownRequested = false;
            committed = false;
            leaseTimer.Stop();
            if (wasPrepared && cancelPreparation != null)
            {
                try { cancelPreparation(); } catch (Exception ex) { Trace.WriteLine("Update gate release failed: " + ex.Message); }
            }
        }

        /// <summary>Stops discovery/IPC and restores host operation unless a confirmed shutdown is already committed.</summary>
        public void Stop()
        {
            started = false;
            if (!stopping.IsCancellationRequested) stopping.Cancel();
            if (!committed) CancelPreparation();
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            disposed = true;
            leaseTimer.Dispose();
        }

        private async Task ServeAsync()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, MaxMessageBytes, MaxMessageBytes, security))
                    {
                        Task connected = Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null);
                        if (await Task.WhenAny(connected, Task.Delay(Timeout.Infinite, stopping.Token)).ConfigureAwait(false) != connected) return;
                        await connected.ConfigureAwait(false);
                        using (CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
                        {
                            requestTimeout.CancelAfter(10000);
                            string line = await ReadLineAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
                            Dictionary<string, object> request = Serializer().Deserialize<Dictionary<string, object>>(line);
                            object response;
                            if (request == null || GetInt(request, "Protocol") != 1 || !String.Equals(GetString(request, "Token"), token, StringComparison.Ordinal))
                                response = new { Ok = false, Message = "Invalid update protocol or session token." };
                            else response = await DispatchAsync(request).ConfigureAwait(false);
                            byte[] output = Encoding.UTF8.GetBytes(Serializer().Serialize(response) + "\n");
                            if (output.Length > MaxMessageBytes) output = Encoding.UTF8.GetBytes("{\"Ok\":false,\"Message\":\"Response exceeds protocol limit.\"}\n");
                            await pipe.WriteAsync(output, 0, output.Length, requestTimeout.Token).ConfigureAwait(false);
                            await pipe.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!stopping.IsCancellationRequested) Trace.WriteLine("Update pipe request failed: " + ex.Message);
                }
            }
        }

        private async Task<object> DispatchAsync(Dictionary<string, object> request)
        {
            TaskCompletionSource<object> result = new TaskCompletionSource<object>();
            // A queued UI request must never mutate the host after its caller has timed out.
            int state = 0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            try
            {
                owner.BeginInvoke(new Action(delegate
                {
                    if (DateTime.UtcNow >= deadline || Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
                    try { result.TrySetResult(HandleRequest(request)); }
                    catch (Exception ex) { CancelPreparation(); result.TrySetResult(Response(false, ex.Message, null)); }
                }));
            }
            catch (Exception ex) { return Response(false, "Host UI unavailable: " + ex.Message, null); }
            if (await Task.WhenAny(result.Task, Task.Delay(5000, stopping.Token)).ConfigureAwait(false) == result.Task)
                return await result.Task.ConfigureAwait(false);
            Interlocked.CompareExchange(ref state, 2, 0);
            return Response(false, "Host UI did not answer in time; retry after the current operation finishes.", new[] { CheckResult.Unknown("host-ui", "Host UI timeout.") });
        }

        private object HandleRequest(Dictionary<string, object> request)
        {
            if (!started || disposed) return Response(false, "Update integration is disabled.", null);
            if (IsPrepared && DateTime.UtcNow >= leaseUntil) CancelPreparation();
            string command = GetString(request, "Command");
            if (command == "hello")
            {
                List<CheckResult> registered = new List<CheckResult>();
                foreach (KeyValuePair<string, Func<CheckResult>> check in checks)
                    registered.Add(CheckResult.Unknown(check.Key, "Not executed."));
                return Response(true, "", registered.ToArray());
            }
            if (command == "check") return Response(true, "", RunChecks());
            if (command == "cancel")
            {
                if (IsPrepared && !String.Equals(GetString(request, "PrepareId"), prepareId, StringComparison.Ordinal))
                    return Response(false, "Preparation session does not match.", null);
                CancelPreparation();
                return Response(true, "", null);
            }
            if (command == "prepare")
            {
                if (IsPrepared) return Response(false, "An update preparation is already in progress.", null);
                string requestedId = GetString(request, "PrepareId");
                if (!IsToken(requestedId))
                    return Response(false, "A preparation ID is required.", null);
                if (beginPreparation == null || cancelPreparation == null || shutdown == null)
                    return Response(false, "Host preparation and shutdown handlers are required.", null);
                // Set the lease before entering the host gate so exceptions always release it.
                prepareId = requestedId;
                leaseUntil = DateTime.UtcNow.AddSeconds(30);
                leaseTimer.Start();
                if (!beginPreparation()) { CancelPreparation(); return Response(false, "Host is busy; finish its current operation and retry.", RunChecks()); }
                CheckResult[] values = RunChecks();
                if (!AllReady(values)) { CancelPreparation(); return Response(false, "Host conditions prevent update.", values); }
                return Response(true, "", values);
            }
            if (command == "shutdown")
            {
                if (!IsPrepared || !String.Equals(GetString(request, "PrepareId"), prepareId, StringComparison.Ordinal))
                    return Response(false, "Preparation expired or does not match.", null);
                string path = GetString(request, "AckPath");
                if (!SamePath(path, Path.Combine(LocalRoot, "Sessions", token, "shutdown.json")))
                { CancelPreparation(); return Response(false, "Invalid shutdown acknowledgement path.", null); }
                CheckResult[] values = RunChecks();
                if (!AllReady(values)) { CancelPreparation(); return Response(false, "Host conditions changed; update cancelled.", values); }
                ackPath = path;
                IsShutdownRequested = true;
                if (!shutdown()) { CancelPreparation(); return Response(false, "Host cleanup refused shutdown.", values); }
                owner.Close();
                if (!committed) { CancelPreparation(); return Response(false, "Host closing was cancelled.", values); }
                return Response(true, "Host shutdown committed.", values);
            }
            return Response(false, "Unknown update command.", null);
        }

        private CheckResult[] RunChecks()
        {
            if (checks.Count == 0) return new[] { CheckResult.Unknown("registration", "No host safety checks were registered.") };
            List<CheckResult> values = new List<CheckResult>();
            foreach (KeyValuePair<string, Func<CheckResult>> check in checks)
            {
                try
                {
                    CheckResult value = check.Value();
                    if (value == null || (value.Status != "Ready" && value.Status != "Blocked" && value.Status != "Unknown"))
                        value = CheckResult.Unknown(check.Key, "Host check did not provide a valid result.");
                    value.Id = check.Key;
                    if (value.Message != null && value.Message.Length > 2048) value.Message = value.Message.Substring(0, 2048);
                    values.Add(value);
                }
                catch (Exception ex) { values.Add(CheckResult.Unknown(check.Key, "Host check failed: " + ex.Message)); }
            }
            return values.ToArray();
        }

        private object Response(bool ok, string message, CheckResult[] values)
        {
            return new { Ok = ok, Message = message, AppId = appId, Version = ReadVersion(),
                InstallDirectory = Path.GetDirectoryName(executablePath), ExecutablePath = executablePath,
                Pid = Process.GetCurrentProcess().Id, Checks = values ?? new CheckResult[0], Prepared = IsPrepared, PrepareId = prepareId };
        }

        private string ReadVersion()
        {
            string directory = Path.GetDirectoryName(executablePath);
            string installed = VersionFromFile(Path.Combine(directory, ".jmd-update", "installed.json"));
            if (installed != null) return installed;
            string packaged = VersionFromFile(Path.Combine(directory, "jmd-version.json"));
            return packaged ?? fallbackVersion;
        }

        private string VersionFromFile(string path)
        {
            try
            {
                Dictionary<string, object> value = ReadJson(path);
                Version parsed;
                if (GetString(value, "AppId") == appId && Version.TryParse(GetString(value, "Version"), out parsed)) return parsed.ToString();
            }
            catch (Exception) { }
            return null;
        }

        private static bool AllReady(CheckResult[] values) { return values.Length > 0 && Array.TrueForAll(values, item => item.Status == "Ready"); }
        private static string LocalRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JmdUpdater"); } }
        private static bool IsToken(string value) { return value != null && Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z"); }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = MaxMessageBytes, RecursionLimit = 32 }; }
        private static Dictionary<string, object> ReadJson(string path)
        { return ReadJson(path, MaxMessageBytes); }

        private static Dictionary<string, object> ReadJson(string path, int limit)
        {
            if (limit <= 0 || limit > 8 * 1024 * 1024 || new FileInfo(path).Length > limit) throw new InvalidDataException("JSON file is too large.");
            return new JavaScriptSerializer { MaxJsonLength = limit, RecursionLimit = 32 }
                .Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
        }
        private static string GetString(Dictionary<string, object> value, string key)
        { object result; return value.TryGetValue(key, out result) && result is string ? (string)result : null; }
        private static int GetInt(Dictionary<string, object> value, string key)
        { object result; return value.TryGetValue(key, out result) && result is int ? (int)result : -1; }

        private static bool SamePath(string actual, string expected)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(actual) || !String.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) return false;
                // Do not follow a redirected directory when writing either acknowledgement file.
                DirectoryInfo directory = new DirectoryInfo(Path.GetDirectoryName(expected));
                while (directory != null)
                {
                    if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                    directory = directory.Parent;
                }
                return !File.Exists(expected) || (File.GetAttributes(expected) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception) { return false; }
        }

        private static void WriteConfirmation(string path, object value)
        {
            string directory = Path.GetDirectoryName(path);
            if (!SamePath(path, path)) throw new InvalidDataException("Confirmation path is redirected.");
            Directory.CreateDirectory(directory);
            if (!SamePath(path, path)) throw new InvalidDataException("Confirmation path is redirected.");
            string temporary = Path.Combine(directory, ".jmd-confirm-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                // Readers must see either no acknowledgement or complete JSON; never a newly created empty file.
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Serializer().Serialize(value));
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush(true);
                }
                // Same-directory move is atomic and never replaces another transaction's acknowledgement.
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellation)
        {
            using (MemoryStream line = new MemoryStream())
            {
                byte[] block = new byte[1024];
                while (true)
                {
                    int count = await stream.ReadAsync(block, 0, block.Length, cancellation).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("Incomplete update request.");
                    for (int i = 0; i < count; i++)
                    {
                        if (block[i] == 10) return new UTF8Encoding(false, true).GetString(line.ToArray()).TrimEnd('\r');
                        if (line.Length >= MaxMessageBytes) throw new InvalidDataException("Update request exceeds protocol limit.");
                        line.WriteByte(block[i]);
                    }
                }
            }
        }
    }
}
