// Actual .NET NamedPipeClientStream interoperability test against the Qt adapter.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class QtWireClient
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string Pipe, Token, DirectoryPath, Ack;
    private static string Option(string[] args, string name) {
        for (int index = 0; index < args.Length - 1; ++index) if (args[index] == name) return args[index + 1];
        throw new ArgumentException(name);
    }
    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static Dictionary<string, object> Call(string command, string prepareId) {
        var request = new Dictionary<string, object> {
            { "Protocol", 1 }, { "Token", Token }, { "Command", command }, { "PrepareId", prepareId }, { "AckPath", Ack }
        };
        using (var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut)) {
            pipe.Connect(5000);
            using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true)))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false))) {
                writer.AutoFlush = true; writer.WriteLine(Json.Serialize(request));
                Task<string> read = Task.Factory.StartNew(() => reader.ReadLine());
                Require(read.Wait(7000), "pipe response timeout");
                Require(read.Result != null, "empty pipe response");
                return (Dictionary<string, object>)Json.DeserializeObject(read.Result);
            }
        }
    }
    private static void Checks(Dictionary<string, object> reply, string motorState) {
        Require(reply.ContainsKey("Checks"), "missing checks");
        var seen = new HashSet<string>();
        foreach (object value in (object[])reply["Checks"]) {
            var check = (Dictionary<string, object>)value;
            string id = (string)check["Id"];
            Require(seen.Add(id), "duplicate check");
            Require((string)check["Status"] == (id == "motor" ? motorState : (motorState == "Unknown" ? "Unknown" : "Ready")), "unexpected check state " + id);
        }
        Require(seen.SetEquals(new[] { "motor", "workspace" }), "registered check inventory");
    }
    private static bool Is(Dictionary<string, object> reply, string key) { return reply.ContainsKey(key) && (bool)reply[key]; }
    private static void Probe(string mode, string token, string readyPath) {
        var start = new ProcessStartInfo(Path.Combine(DirectoryPath, "QtWireFixture.exe"), mode) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = DirectoryPath };
        if (token != null) { start.EnvironmentVariables["JMD_UPDATE_READY_TOKEN"] = token; start.EnvironmentVariables["JMD_UPDATE_READY_PATH"] = readyPath; }
        else { start.EnvironmentVariables.Remove("JMD_UPDATE_READY_TOKEN"); start.EnvironmentVariables.Remove("JMD_UPDATE_READY_PATH"); }
        using (var child = Process.Start(start)) { Require(child.WaitForExit(7000), "probe did not exit"); Require(child.ExitCode == 0, mode + " exit=" + child.ExitCode); }
    }
    private static void AbandonedProbe(string mode) {
        string normalized = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string mutexName;
        using (var sha = SHA256.Create()) mutexName = "Local\\JmdUpdater.Install." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").ToLowerInvariant();
        using (var gate = new Mutex(false, mutexName)) {
            var owner = new Thread(() => gate.WaitOne());
            owner.Start(); owner.Join();
            Probe(mode, null, null);
        }
    }
    private static void LockProbes() {
        Probe("--unlocked-probe", null, null);
        string normalized = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string mutexName;
        using (var sha = SHA256.Create()) mutexName = "Local\\JmdUpdater.Install." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").ToLowerInvariant();
        using (var gate = new Mutex(true, mutexName)) {
            try {
                Probe("--lock-probe", null, null);
                Probe("--waiting-reason-probe", null, null);
                string token = new string('c', 64);
                string transaction = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "JmdUpdater", "Transactions", token);
                System.IO.Directory.CreateDirectory(transaction);
                string ready = Path.Combine(transaction, "ready.json"); if (File.Exists(ready)) File.Delete(ready);
                var job = new Dictionary<string, object> { { "PrepareId", token }, { "AppId", "QtWireFixture" }, { "InstallDirectory", DirectoryPath }, { "ExecutablePath", Path.Combine(DirectoryPath, "wrong.exe") } };
                File.WriteAllText(Path.Combine(transaction, "job.json"), Json.Serialize(job));
                File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = "WaitingForStartup" }));
                Probe("--lock-probe", token, ready);
                job["ExecutablePath"] = Path.Combine(DirectoryPath, "QtWireFixture.exe");
                File.WriteAllText(Path.Combine(transaction, "job.json"), Json.Serialize(job));
                File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = "Applying" }));
                Probe("--lock-probe", token, ready);
                File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = "WaitingForStartup" }));
                Probe("--ready-write-failure-probe", token, ready);
                Probe("--ready-probe", token, ready);
                var receipt = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(ready));
                Require((string)receipt["Token"] == token && Convert.ToInt32(receipt["Pid"]) > 0, "startup receipt mismatch");
            } finally { gate.ReleaseMutex(); }
        }
        AbandonedProbe("--unlocked-probe");
        File.WriteAllText(Path.Combine(DirectoryPath, "lock-result.txt"), "PASS .NET mutex/Qt startup gate, wait reason, abandoned release, bound ready receipt and successful-only environment cleanup");
    }
    private static void PendingProbes() {
        string token = new string('e', 64);
        string transaction = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "JmdUpdater", "Transactions", token);
        string control = Path.Combine(DirectoryPath, ".jmd-update");
        string pointer = Path.Combine(control, "pending.json");
        System.IO.Directory.CreateDirectory(control); System.IO.Directory.CreateDirectory(transaction);
        Require(!File.Exists(pointer), "Use a clean fixture directory without an existing pending transaction.");
        try {
            File.WriteAllText(Path.Combine(transaction, "job.json"), Json.Serialize(new { PrepareId = token, AppId = "QtWireFixture", InstallDirectory = DirectoryPath, ExecutablePath = Path.Combine(DirectoryPath, "QtWireFixture.exe") }));
            File.WriteAllText(pointer, Json.Serialize(new { PrepareId = token, TransactionDirectory = transaction }));
            foreach (string state in new[] { "Applying", "RollingBack", "RollbackFailed", "WaitingForStartup", "StartupUnconfirmed", "NeedsRecovery", "UnknownState" }) {
                File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = state }));
                Probe("--lock-probe", null, null);
            }
            File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = "Applying" }));
            Probe("--recovery-reason-probe", null, null);
            string recoveryReason = File.ReadAllText(Path.Combine(DirectoryPath, "startup-reason.txt"));
            Require(recoveryReason.Contains(Path.Combine(transaction, "job.json")) && !recoveryReason.Contains("<"), "Recovery reason omitted the actual transaction job path");
            AbandonedProbe("--lock-probe");
            foreach (string state in new[] { "Complete", "RolledBack" }) {
                File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = state }));
                Probe("--unlocked-probe", null, null);
            }
            File.Delete(Path.Combine(transaction, "journal.json"));
            Probe("--lock-probe", null, null);
            File.WriteAllText(Path.Combine(transaction, "journal.json"), Json.Serialize(new { PrepareId = token, State = "WaitingForStartup" }));
            string ready = Path.Combine(transaction, "ready.json"); if (File.Exists(ready)) File.Delete(ready);
            Probe("--ready-probe", token, ready);
            var records = new List<object>();
            for (int index = 0; index < 1000; ++index)
                records.Add(new { Path = "files/" + index.ToString("D5") + new string('x', 80) + ".dll", OriginallyExisted = false, BackupSha256 = (string)null, BackupReady = true, Touched = true });
            string largeJournal = Json.Serialize(new { PrepareId = token, State = "WaitingForStartup", Failure = (string)null, StartedPid = 0, StartedUtcTicks = 0L, Records = records });
            string journalPath = Path.Combine(transaction, "journal.json");
            File.WriteAllText(journalPath, largeJournal);
            Require(new FileInfo(journalPath).Length > 65536 && new FileInfo(journalPath).Length < 8 * 1024 * 1024, "Large-journal fixture bounds");
            File.Delete(ready); Probe("--ready-probe", token, ready);
            Require(File.Exists(ready), "A valid inventory journal above 64 KiB prevented startup acknowledgement");
            string jobPath = Path.Combine(transaction, "job.json"), normalJob = File.ReadAllText(jobPath);
            File.WriteAllText(jobPath, normalJob + new string(' ', 65537));
            Probe("--lock-probe", token, ready);
            File.WriteAllText(jobPath, normalJob);
            File.WriteAllText(journalPath, largeJournal + new string(' ', 8 * 1024 * 1024 + 1));
            Probe("--lock-probe", token, ready);
            File.WriteAllText(Path.Combine(DirectoryPath, "pending-result.txt"), "PASS durable pending states, valid >64KiB journal startup acknowledgement, >8MiB journal and >64KiB job rejection");
        } finally { File.Delete(pointer); }
    }
    public static int Main(string[] args) {
        DirectoryPath = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        try {
            if (args.Length > 0 && args[0] == "lockprobes") { LockProbes(); return 0; }
            if (args.Length > 0 && args[0] == "pendingprobes") { PendingProbes(); return 0; }
            File.WriteAllText(Path.Combine(DirectoryPath, "client-started.txt"), "started");
            Pipe = Option(args, "--pipe"); Token = Option(args, "--token");
            string session = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "JmdUpdater", "Sessions", Token);
            Require(!System.IO.Directory.Exists(session), "Fixture must not precreate a production session directory.");
            Ack = Path.Combine(session, "shutdown.json");
            string first = new string('a', 64), second = new string('b', 64);
            string realToken = Token;
            Token = (Token[0] == '0' ? "1" : "0") + Token.Substring(1);
            Require(!Is(Call("hello", ""), "Ok"), "invalid session token was accepted");
            Token = realToken;
            var hello = Call("hello", ""); Require(Is(hello, "Ok"), "hello failed"); Checks(hello, "Unknown");
            bool lease = File.ReadAllText(Path.Combine(DirectoryPath, "fixture-mode.txt")) == "lease";
            if (lease) {
                var prepare = Call("prepare", first); Require(Is(prepare, "Ok") && Is(prepare, "Prepared"), "lease prepare");
                Thread.Sleep(31500);
                var expired = Call("shutdown", first); Require(!Is(expired, "Ok") && !Is(expired, "Prepared"), "expired lease accepted");
                File.WriteAllText(Path.Combine(DirectoryPath, "client-result.txt"), "PASS expired lease refused"); return 0;
            }
            var check = Call("check", ""); Checks(check, "Blocked");
            Require(((Dictionary<string, object>)((object[])check["Checks"])[0])["Message"].ToString() == "first-read", "hello executed a callback");
            string realAck = Ack; Ack = Path.Combine(DirectoryPath, "outside-session.json");
            Require(!Is(Call("prepare", first), "Ok"), "out-of-session acknowledgement path accepted"); Ack = realAck;
            Require(!Is(Call("prepare", "invalid"), "Ok"), "invalid preparation identifier accepted");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(session));
            File.WriteAllText(session, "blocked-path");
            var unwritable = Call("prepare", first);
            Require(!Is(unwritable, "Ok") && !Is(unwritable, "Prepared"), "unwritable receipt path entered the host gate");
            File.Delete(session);
            var failed = Call("prepare", first); Require(!Is(failed, "Ok") && !Is(failed, "Prepared"), "failed preparation accepted");
            Require(System.IO.Directory.Exists(session), "Host did not create and preflight its receipt directory.");
            var unknownChecks = (object[])failed["Checks"];
            Require(((Dictionary<string, object>)unknownChecks[0])["Status"].ToString() == "Unknown", "unknown state lost");
            var prepared = Call("prepare", first); Require(Is(prepared, "Ok") && Is(prepared, "Prepared"), "prepare not held"); Checks(prepared, "Ready");
            var duplicate = Call("prepare", second); Require(!Is(duplicate, "Ok") && Is(duplicate, "Prepared") && (string)duplicate["PrepareId"] == first, "second prepare replaced active lease");
            var wrongCancel = Call("cancel", second); Require(!Is(wrongCancel, "Ok") && Is(wrongCancel, "Prepared"), "wrong cancel released preparation");
            var wrongShutdown = Call("shutdown", second); Require(!Is(wrongShutdown, "Ok") && Is(wrongShutdown, "Prepared"), "wrong shutdown changed preparation");
            var denied = Call("shutdown", first); Require(!Is(denied, "Ok") && !Is(denied, "Prepared"), "close refusal left preparation active");
            Require(!File.Exists(Ack), "refused exit wrote a grant");
            var again = Call("prepare", first); Require(Is(again, "Ok"), "retry failed");
            var canceled = Call("cancel", first); Require(Is(canceled, "Ok") && !Is(canceled, "Prepared"), "cancel failed");
            var final = Call("prepare", second); Require(Is(final, "Ok"), "final prepare failed");
            System.IO.Directory.Delete(session, false); File.WriteAllText(session, "blocked-after-prepare");
            var ioFailure = Call("shutdown", second);
            Require(!Is(ioFailure, "Ok") && !Is(ioFailure, "Prepared"), "receipt I/O failure did not release the host");
            File.Delete(session);
            final = Call("prepare", second); Require(Is(final, "Ok"), "retry after receipt I/O failure failed");
            var shutdown = Call("shutdown", second); Require(Is(shutdown, "Ok"), "normal close not accepted");
            var lateCancel = Call("cancel", second);
            Require(Is(lateCancel, "Prepared") && (string)lateCancel["PrepareId"] == second && File.Exists(Ack), "Cancellation revoked an already committed normal close");
            Require(File.Exists(Ack), "normal close acknowledgement missing");
            var receipt = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(Ack));
            Require((string)receipt["PrepareId"] == second && Convert.ToInt32(receipt["Pid"]) == Convert.ToInt32(hello["Pid"]), "acknowledgement identity mismatch");
            File.WriteAllText(Path.Combine(DirectoryPath, "client-result.txt"), "PASS hello/check/prepare/cancel/refused-close/normal-close");
            return 0;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(DirectoryPath, "client-result.txt"), "FAIL " + error.ToString()); return 1;
        }
    }
}
