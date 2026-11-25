// Executes production AgentContext, HTTP server, signer and a real child-process Installer.
// Reflection triggers the same Discover/Install methods as the Agent's UI buttons; no flow is reimplemented here.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using JmdUpdater;

internal static class AgentFlowTests
{
    private static int assertions;
    private static string fixtureRoot;
    private static string privateKey;
    private static string publicKey;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("JMD_UPDATER_NO_UI", "1");
            if (args.Length > 0 && (args[0] == "install" || args[0] == "recover")) return DeliveryCommands.Run(args[0], args.Skip(1).ToArray());
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            fixtureRoot = Path.Combine(Path.GetTempPath(), "JmdUpdaterAgentFlow." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtureRoot);
            privateKey = Path.Combine(fixtureRoot, "keys", "private.xml"); publicKey = Path.Combine(fixtureRoot, "keys", "public.xml");
            PublisherService.GenerateKey(new Dictionary<string, string> { { "private", privateKey }, { "public", publicKey } });
            RunScenario("valid", false, false);
            RunScenario("invalid-zip", true, false);
            RunScenario("shutdown-refused", false, true);
            RunScenario("prepare-exceeds-exit-timeout", false, false, 6000);
            // 主程序改名：旧入口 FlowHost.exe 运行中，发布入口 FlowHost2.exe 并声明替换 FlowHost.exe；安装目录没有上一版安装清单。
            RunScenario("rename-entry", false, false, 0, "FlowHost2.exe");
            // 改名后新入口无法启动：必须完整回滚（恢复已删除的旧入口，移除新入口），即使没有上一版安装清单。
            RunScenario("rename-startup-fails", false, false, 0, "FlowHost2.exe", true);
            Console.WriteLine("PASS: " + assertions + " real Agent -> download -> staged Installer -> synthetic-host assertions across 6 scenarios.");
            DeleteOwned(fixtureRoot, Path.GetTempPath());
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (args.Length > 0 && (args[0] == "install" || args[0] == "recover"))
            {
                try { File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "installer-test-exception.txt"), error.ToString()); } catch (Exception) { }
            }
            if (fixtureRoot != null) Console.Error.WriteLine("Retained isolated fixture: " + fixtureRoot);
            return 1;
        }
    }

    private static void Assert(bool condition, string name)
    { if (!condition) throw new Exception("FAIL: " + name); assertions++; Console.WriteLine("  OK " + name); }

    private static Task InvokeAsync(AgentContext context, string name, params object[] args)
    { return (Task)typeof(AgentContext).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(context, args); }

    private static object Field(AgentContext context, string name)
    { return typeof(AgentContext).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context); }

    private static void PumpUntil(Func<bool> complete, int seconds, string operation)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!complete())
        {
            if (timeout.Elapsed.TotalSeconds > seconds) throw new TimeoutException(operation);
            Application.DoEvents(); Thread.Sleep(10);
        }
        Application.DoEvents();
    }

    private static void Pump(Task task, int seconds, string operation)
    { PumpUntil(() => task.IsCompleted, seconds, operation); task.GetAwaiter().GetResult(); }

    private static void RunScenario(string name, bool invalidZip, bool refuseShutdown, int prepareDelayMilliseconds = 0, string newEntry = null, bool brokenEntry = false)
    {
        Console.WriteLine("SCENARIO " + name);
        string root = Path.Combine(fixtureRoot, name), install = Path.Combine(root, "install"), source = Path.Combine(root, "source"), releases = Path.Combine(root, "releases");
        Directory.CreateDirectory(install); Directory.CreateDirectory(source); Directory.CreateDirectory(releases);
        string binaries = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string entry = newEntry ?? "FlowHost.exe";
        string oldExe = Path.Combine(install, "FlowHost.exe"), newExe = Path.Combine(source, entry), installedEntry = Path.Combine(install, entry);
        File.Copy(Path.Combine(binaries, "host-old", "FlowHost.exe"), oldExe);
        if (brokenEntry) File.WriteAllText(newExe, "NOT-A-WINDOWS-EXECUTABLE");
        else File.Copy(Path.Combine(binaries, "host-new", "FlowHost.exe"), newExe);
        File.WriteAllText(Path.Combine(install, "program.txt"), "old"); File.WriteAllText(Path.Combine(source, "program.txt"), "new");
        Directory.CreateDirectory(Path.Combine(install, "JmdUpdater")); Directory.CreateDirectory(Path.Combine(source, "JmdUpdater"));
        File.WriteAllText(Path.Combine(install, "JmdUpdater", "JmdUpdater.exe"), "OLD-UPDATER");
        File.WriteAllText(Path.Combine(source, "JmdUpdater", "JmdUpdater.exe"), "NEW-UPDATER");
        File.WriteAllText(Path.Combine(install, "JmdUpdater", "public-key.xml"), "TRUST-ROOT");
        File.WriteAllText(Path.Combine(source, "JmdUpdater", "public-key.xml"), "DO-NOT-PUBLISH");
        File.WriteAllText(Path.Combine(install, "site-data.txt"), "PRIVATE-SITE-DATA");
        string appId = "Flow" + Guid.NewGuid().ToString("N");
        string session = UpdatePaths.NewToken(), pipe = "JmdUpdater.Flow." + Guid.NewGuid().ToString("N");
        string trace = Path.Combine(root, "host-trace.jsonl");
        string stopName = "Local\\JmdUpdater.Flow.Stop." + session, readyName = "Local\\JmdUpdater.Flow.Ready." + session;
        JsonIO.WriteAtomic(Path.Combine(install, "flow-host.json"), new { AppId = appId, Pipe = pipe, Token = session, TracePath = trace, StopEvent = stopName, ReadyEvent = readyName,
            DenyShutdown = refuseShutdown, PrepareDelayMilliseconds = prepareDelayMilliseconds });
        int port;
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        UpdateConfig config = new UpdateConfig { Enabled = true, AppId = appId, DisplayName = "Synthetic Agent Flow", ServerUrl = "http://127.0.0.1:" + port,
            PublicKeyXml = File.ReadAllText(publicKey), PollSeconds = 3600, RequestTimeoutSeconds = 5, ExitTimeoutSeconds = 5, StartupTimeoutSeconds = 10,
            StageTimeoutSeconds = 30, PreservePaths = new[] { "flow-host.json", "jmd-update.json", "site-data.txt" } };
        string configPath = Path.Combine(install, "jmd-update.json"); JsonIO.WriteAtomic(configPath, config);
        Dictionary<string, string> publish = new Dictionary<string, string> { { "source", source }, { "root", releases }, { "key", privateKey }, { "config", configPath }, { "version", "2.0.0.0" }, { "entry", entry } };
        if (newEntry != null) publish["previous-entry"] = "FlowHost.exe";
        PublisherService.Publish(publish);
        string latest = Path.Combine(releases, appId, "latest.json");
        ReleaseManifest manifest = ManifestIO.Verify(File.ReadAllText(latest), config.PublicKeyXml);
        Assert(manifest.Files.Any(f => f.Path == "JmdUpdater/JmdUpdater.exe") &&
            !manifest.Files.Any(f => f.Path == "JmdUpdater/public-key.xml"), name + ": updater signed, trust root excluded");
        if (invalidZip)
        {
            string package = Path.Combine(releases, appId, manifest.PackageName);
            File.WriteAllText(package, "A deliberately invalid ZIP with a correctly signed size and SHA256.");
            manifest.PackageSize = new FileInfo(package).Length; manifest.PackageSha256 = ManifestIO.Sha256File(package);
            JsonIO.WriteAtomic(latest, ManifestIO.Sign(manifest, File.ReadAllText(privateKey)));
        }
        string oldHash = ManifestIO.Sha256File(oldExe), newHash = ManifestIO.Sha256File(newExe);
        Dictionary<string, string> originalFiles = SnapshotFiles(install);
        Assert(oldHash != newHash, name + ": old and new fixture executables are different builds");
        Process original = null, replacement = null;
        AgentContext context = null;
        string transaction = null;
        bool scenarioPassed = false;
        CancellationTokenSource serverStop = new CancellationTokenSource();
        Task server = null;
        using (var hostStop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName))
        using (var hostReady = new EventWaitHandle(false, EventResetMode.AutoReset, readyName))
        using (var serverReady = new ManualResetEvent(false))
        {
            try
            {
                server = Task.Run(delegate { LocalUpdateServer.Run(releases, "127.0.0.1", port, new[] { "127.0.0.0/8" }, serverStop.Token,
                    delegate(string text) { if (text.StartsWith("Listening", StringComparison.Ordinal)) serverReady.Set(); }); });
                Assert(serverReady.WaitOne(5000), name + ": real loopback update server started");
                original = Process.Start(new ProcessStartInfo(oldExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install });
                Assert(hostReady.WaitOne(5000), name + ": synthetic host pipe started");
                context = new AgentContext(pipe, session, original.Id, configPath, config);
                PumpUntil(() => Field(context, "release") != null && !(bool)Field(context, "busy"), 15, "real Agent discovery did not finish");
                ReleaseManifest discovered = (ReleaseManifest)Field(context, "release");
                Assert(discovered.Version == "2.0.0.0", name + ": real Agent fetched and verified signed release over HTTP");
                Stopwatch installElapsed = Stopwatch.StartNew();
                Pump(InvokeAsync(context, "Install"), 40, "real Agent installation task timed out");
                transaction = FindTransaction(appId);
                Assert(transaction != null, name + ": Agent created fixed LocalAppData transaction");
                InstallJob job = JsonIO.Read<InstallJob>(Path.Combine(transaction, "job.json"));
                Assert(File.Exists(Path.Combine(transaction, "installer", "JmdUpdater.exe")) &&
                    ManifestIO.Sha256File(Path.Combine(transaction, "installer", "JmdUpdater.exe")) == ManifestIO.Sha256File(Application.ExecutablePath),
                    name + ": Agent launched a verified transaction-local installer copy");
                Assert(ManifestIO.Sha256File(job.PackagePath) == manifest.PackageSha256, name + ": Agent downloaded exact signed package bytes");
                List<Dictionary<string, object>> records = Trace(trace);
                if (brokenEntry)
                {
                    PumpUntil(() => JournalState(transaction) == "RolledBack" || JournalState(transaction) == "RollbackFailed", 30, "Installer did not finish rollback");
                    Assert(JournalState(transaction) == "RolledBack", "rename rollback: failed startup rolled back cleanly");
                    Assert(original.HasExited, "rename rollback: original host had exited for the installation");
                    Assert(File.Exists(oldExe) && ManifestIO.Sha256File(oldExe) == oldHash, "rename rollback: deleted old entry restored byte-for-byte");
                    Assert(!File.Exists(installedEntry), "rename rollback: new entry removed");
                    Assert(File.ReadAllText(Path.Combine(install, "program.txt")) == "old", "rename rollback: payload restored");
                    Assert(!File.Exists(Path.Combine(install, ".jmd-update", "pending.json")), "rename rollback: pending startup gate cleared");
                }
                else if (!invalidZip && !refuseShutdown)
                {
                    PumpUntil(() => JournalState(transaction) == "Complete", 15, "Installer did not confirm new startup");
                    Assert(original.HasExited && original.ExitCode == 0, "valid: original host exited normally after matching acknowledgement");
                    Assert(records.Any(row => Command(row) == "prepare") && records.Where(row => Command(row) == "prepare" || Command(row) == "shutdown").All(row => Convert.ToBoolean(row["Staged"])), "valid: Agent called prepare/shutdown only after real Installer staged acknowledgement");
                    Assert(records.Where(row => Command(row) == "shutdown").All(row => Convert.ToBoolean(row["ShutdownRequested"])), "valid: matching shutdown-requested signal existed before host shutdown RPC");
                    Assert(File.Exists(job.ShutdownAckPath), "valid: matching normal-shutdown acknowledgement exists");
                    var ready = JsonIO.Read<Dictionary<string, object>>(Path.Combine(transaction, "ready.json"));
                    replacement = Process.GetProcessById(Convert.ToInt32(ready["Pid"]));
                    Assert(Convert.ToString(ready["Token"]) == job.PrepareId && !replacement.HasExited, "valid: independently launched new process acknowledged startup");
                    Assert(ManifestIO.Sha256File(installedEntry) == newHash && File.ReadAllText(Path.Combine(install, "program.txt")) == "new", "valid: real Installer replaced executable and payload");
                    if (newEntry != null)
                    {
                        Assert(manifest.EntryPoint == entry && manifest.PreviousEntryPoints != null && manifest.PreviousEntryPoints.Contains("FlowHost.exe"), "rename: signed release declares the replaced entry point");
                        Assert(!File.Exists(oldExe), "rename: old entry executable removed without a previous installed inventory");
                        Assert(String.Equals(replacement.MainModule.FileName, installedEntry, StringComparison.OrdinalIgnoreCase), "rename: Installer started the new entry executable");
                        var journalRecords = ((System.Collections.ArrayList)JsonIO.Read<Dictionary<string, object>>(Path.Combine(transaction, "journal.json"))["Records"]).Cast<Dictionary<string, object>>().Select(r => Convert.ToString(r["Path"])).ToList();
                        Assert(journalRecords.IndexOf(entry) >= 0 && journalRecords.IndexOf("FlowHost.exe") > journalRecords.IndexOf(entry), "rename: old entry removed only after the new entry was written");
                    }
                    Assert(File.ReadAllText(Path.Combine(install, "JmdUpdater", "JmdUpdater.exe")) == "NEW-UPDATER" &&
                        File.ReadAllText(Path.Combine(install, "JmdUpdater", "public-key.xml")) == "TRUST-ROOT", "valid: updater replaced while trust root preserved");
                    Assert(Trace(trace).Any(row => Command(row) == "new-ready" && Convert.ToString(row["Build"]) == "2.0.0.0"), "valid: ready came from the newly compiled generation");
                    Assert(!File.Exists(Path.Combine(install, ".jmd-update", "pending.json")), "valid: completed install cleared pending startup gate");
                    if (prepareDelayMilliseconds > 0)
                        Assert(installElapsed.Elapsed.TotalMilliseconds >= prepareDelayMilliseconds, "slow prepare: preparation exceeded 5-second exit timeout yet update completed normally");
                }
                else
                {
                    Assert(!original.HasExited, name + ": original host remained alive");
                    HostResponse ping = new HostClient(pipe, session).Call("check", null, null);
                    Assert(ping.Ok && !ping.Prepared && ping.Checks.Count == 1 && ping.Checks[0].Status == "Ready", name + ": original host still answers real IPC with preparation gate released");
                    Assert(ManifestIO.Sha256File(oldExe) == oldHash && File.ReadAllText(Path.Combine(install, "program.txt")) == "old", name + ": no program file was replaced");
                    Dictionary<string, string> remainingFiles = SnapshotFiles(install);
                    Assert(originalFiles.Count == remainingFiles.Count && originalFiles.All(pair => remainingFiles.ContainsKey(pair.Key) && remainingFiles[pair.Key] == pair.Value), name + ": every installation file retains its original bytes");
                    Assert(!File.Exists(Path.Combine(install, ".jmd-update", "pending.json")), name + ": no pending replacement gate was written");
                    if (invalidZip)
                    {
                        Assert(!File.Exists(Path.Combine(transaction, "staged.json")), "invalid ZIP: real Installer never acknowledged staging");
                        Assert(!records.Any(row => Command(row) == "prepare" || Command(row) == "shutdown"), "invalid ZIP: Agent never acquired host gate or requested shutdown");
                    }
                    else
                    {
                        Assert(records.Any(row => Command(row) == "shutdown") && records.Any(row => Command(row) == "cancel"), "refused shutdown: Agent promptly cancelled host preparation");
                        Assert(File.Exists(Path.Combine(transaction, "cancel.json")) && !File.Exists(job.ShutdownAckPath), "refused shutdown: Installer cancelled without a shutdown acknowledgement");
                        Assert(installElapsed.Elapsed.TotalSeconds < 8, "refused shutdown: cancellation returns promptly rather than waiting for exit timeout");
                    }
                }
                Assert(File.ReadAllText(Path.Combine(install, "site-data.txt")) == "PRIVATE-SITE-DATA", name + ": site-owned data preserved");
                scenarioPassed = true;
            }
            finally
            {
                if (context != null) context.Dispose();
                hostStop.Set();
                if (original != null) { Assert(original.WaitForExit(5000), name + ": owned old fixture stopped normally"); original.Dispose(); }
                if (replacement != null) { Assert(replacement.WaitForExit(5000), name + ": owned new fixture stopped normally"); replacement.Dispose(); }
                serverStop.Cancel(); if (server != null) server.Wait(5000); serverStop.Dispose();
                if (transaction != null && scenarioPassed)
                {
                    // A cancellation is cooperative. Give the real installer time to observe it before deleting its own fixture.
                    if (invalidZip || refuseShutdown) Thread.Sleep(1200);
                    DeleteOwned(transaction, Path.Combine(UpdatePaths.UserRoot, "Transactions"));
                }
                string sessionPath = UpdatePaths.SessionDirectory(session);
                if (scenarioPassed && Directory.Exists(sessionPath)) DeleteOwned(sessionPath, Path.Combine(UpdatePaths.UserRoot, "Sessions"));
                string log = Path.Combine(UpdatePaths.UserRoot, "Logs", appId + "-agent.log"); if (scenarioPassed && File.Exists(log)) File.Delete(log);
                if (!scenarioPassed && transaction != null) Console.Error.WriteLine("Retained transaction: " + transaction);
            }
        }
    }

    private static string FindTransaction(string appId)
    {
        string root = Path.Combine(UpdatePaths.UserRoot, "Transactions");
        foreach (string directory in Directory.GetDirectories(root))
        {
            string path = Path.Combine(directory, "job.json");
            try { if (File.Exists(path) && JsonIO.Read<InstallJob>(path).AppId == appId) return directory; }
            catch (IOException) { }
        }
        return null;
    }

    private static List<Dictionary<string, object>> Trace(string path)
    {
        return File.ReadAllLines(path).Where(line => !String.IsNullOrWhiteSpace(line)).Select(JsonIO.Deserialize<Dictionary<string, object>>).ToList();
    }
    private static Dictionary<string, string> SnapshotFiles(string directory)
    {
        string prefix = Path.GetFullPath(directory).TrimEnd('\\') + Path.DirectorySeparatorChar;
        return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path.Substring(prefix.Length), ManifestIO.Sha256File, StringComparer.OrdinalIgnoreCase);
    }
    private static string Command(Dictionary<string, object> row) { return Convert.ToString(row["Command"]); }
    private static string JournalState(string directory)
    {
        string path = Path.Combine(directory, "journal.json");
        if (!File.Exists(path)) return "";
        try { return Convert.ToString(JsonIO.Read<Dictionary<string, object>>(path)["State"]); } catch (IOException) { return ""; }
    }

    private static void DeleteOwned(string path, string parent)
    {
        string full = Path.GetFullPath(path), prefix = Path.GetFullPath(parent).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || full.Equals(prefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its named parent.");
        UpdatePaths.EnsureNoReparse(full);
        foreach (string child in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)) UpdatePaths.EnsureNoReparse(child);
        Directory.Delete(full, true);
    }
}
