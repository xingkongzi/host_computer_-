// Security and protocol acceptance tests. No hardware, network or installed application is modified.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace JmdUpdater
{
    internal static class CoreTests
    {
        private static int passed;
        private static void Require(bool value, string name) { if (!value) throw new Exception(name); passed++; }
        private static void Reject(Action action, string name)
        { try { action(); } catch { passed++; return; } throw new Exception("Expected rejection: " + name); }
        [DllImport("shell32.dll", SetLastError = true)] private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string command, out int count);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr address);
        private static int Main()
        {
            try
            {
                string cleanupRoot = Path.Combine(Path.GetTempPath(), "JmdPendingTest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(cleanupRoot, ".jmd-update"));
                try
                {
                    string pending = Path.Combine(cleanupRoot, ".jmd-update", "pending.json");
                    Require(!TransactionCleanup.HasPendingReference(cleanupRoot, cleanupRoot, "test"), "no pending permits retention policy");
                    JsonIO.WriteAtomic(pending, new { PrepareId = "test", TransactionDirectory = cleanupRoot });
                    Require(TransactionCleanup.HasPendingReference(cleanupRoot, cleanupRoot, "test"), "pending retains its recovery helper even with terminal journal");
                    File.WriteAllText(pending, "broken");
                    Require(TransactionCleanup.HasPendingReference(cleanupRoot, cleanupRoot, "test"), "unverifiable pending retains recovery helper");
                }
                finally { Directory.Delete(cleanupRoot, true); }
                foreach (string invalid in new[] { "../evil.exe", "C:/evil.exe", "x//y", "x/../y", "x/file:stream", "CON.txt", "LPT1.bin", "file.", "x/ " })
                    Reject(() => UpdatePaths.Relative(invalid), invalid);
                Require(UpdatePaths.Relative("assets\\main.bin") == "assets/main.bin", "relative normalization");
                UpdateConfig config = new UpdateConfig { PreservePaths = new[] { "*.csv", "logs", "DriveParms.xlsx" } };
                Require(UpdatePaths.IsPreserved(config, "sub/data.csv") && UpdatePaths.IsPreserved(config, "logs/a.txt") &&
                    UpdatePaths.IsPreserved(config, "DRIVEPARMS.XLSX") && UpdatePaths.IsPreserved(config, "JmdUpdater/public-key.xml") &&
                    UpdatePaths.IsPreserved(config, "jmd-update.json"), "runtime data and updater trust are protected");
                Require(!UpdatePaths.IsPreserved(config, "jMdUpDaTeR/JmdUpdater.exe") &&
                    !UpdatePaths.IsPreserved(config, "JmdUpdater/JmdUpdater.exe.config") &&
                    UpdatePaths.IsPreserved(config, "JmdUpdater/other.exe") &&
                    UpdatePaths.IsPreserved(config, "JmdUpdater/nested/JmdUpdater.exe") &&
                    UpdatePaths.IsPreserved(config, ".jmd-update/installed.json"), "only updater program and config are managed");
                Require(!UpdatePaths.IsPreserved(config, "App.exe"), "program is installable");
                Require(ManifestIO.NormalizedVersion("1.2") == ManifestIO.NormalizedVersion("1.2.0.0") &&
                    ManifestIO.NormalizedVersion("1.2.0") == ManifestIO.NormalizedVersion("1.2.0.0"), "publisher agent and installer use the same version order");
                string token = UpdatePaths.NewToken(); UpdatePaths.ValidateToken(token); Require(token.Length == 64, "random token");
                Reject(() => UpdatePaths.ValidateToken("../token"), "session path traversal");

                ReleaseManifest manifest = new ReleaseManifest { AppId = "test-product", Version = "1.2.3.4", EntryPoint = "App.exe", PackageName = "test.zip", PackageSize = 100, PackageSha256 = new string('a', 64) };
                manifest.Files.Add(new ReleaseFile { Path = "App.exe", Size = 10, Sha256 = new string('b', 64) });
                using (RSACryptoServiceProvider key = new RSACryptoServiceProvider(2048))
                {
                    key.PersistKeyInCsp = false;
                    SignedRelease signed = ManifestIO.Sign(manifest, key.ToXmlString(true));
                    Require(ManifestIO.Verify(JsonIO.Serialize(signed), key.ToXmlString(false)).Version == "1.2.3.4", "valid publisher signature");
                    byte[] bytes = Convert.FromBase64String(signed.Payload); bytes[bytes.Length / 2] ^= 1; signed.Payload = Convert.ToBase64String(bytes);
                    Reject(() => ManifestIO.Verify(JsonIO.Serialize(signed), key.ToXmlString(false)), "tampered signed payload");
                }
                manifest.Files.Add(new ReleaseFile { Path = "app.EXE", Size = 10, Sha256 = new string('b', 64) });
                Reject(() => ManifestIO.Validate(manifest), "case-insensitive duplicate package files");

                HostClient client = new HostClient("unused", token);
                client.SetRegisteredChecks(new List<CheckResult> { new CheckResult { Id = "motor" }, new CheckResult { Id = "workspace" } });
                HostResponse reply = new HostResponse { Ok = true, Checks = new List<CheckResult> {
                    new CheckResult { Id = "motor", Status = "Ready" }, new CheckResult { Id = "workspace", Status = "Ready" } } };
                Require(client.BlockingReasons(reply) == "", "all registered checks permit update");
                reply.Checks[0].Status = "Unknown"; reply.Checks[0].Message = "stale status";
                Require(client.BlockingReasons(reply).Contains("stale status"), "unknown must block");
                reply.Checks[0].Status = "Blocked"; reply.Checks[0].Message = "disable motor";
                reply.Checks[1].Status = "Blocked"; reply.Checks[1].Message = "save workspace";
                string reasons = client.BlockingReasons(reply);
                Require(reasons.Contains("disable motor") && reasons.Contains("save workspace"), "all blocking reasons are shown");
                reply.Ok = false; reply.Message = "conditions changed";
                reasons = client.BlockingReasons(reply);
                Require(reasons.Contains("disable motor") && reasons.Contains("save workspace"), "prepare refusal retains project-owned remedial messages");
                reply.Ok = true;
                reply.Checks[1].Id = "motor";
                Require(client.BlockingReasons(reply) != "", "duplicate cannot hide a required check");
                reply.Checks.RemoveAt(1);
                Require(client.BlockingReasons(reply) != "", "missing callback result must block");
                reply.Checks.Add(new CheckResult { Id = "workspace", Status = "Ready" }); reply.Checks[0].Status = "anything";
                Require(client.BlockingReasons(reply) != "", "unrecognized status must block");
                reply.Ok = false; reply.Checks = null;
                Require(client.BlockingReasons(reply) != "", "host refusal must block");

                foreach (string sample in new[] { "C:\\Program Files\\app\\", "plain", "space and \"quoted\"", "", "a\\\"b\\" })
                {
                    int count; IntPtr argv = CommandLineToArgvW("program.exe " + Agent.Quote(sample), out count);
                    try { Require(count == 2 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, IntPtr.Size)) == sample, "Windows argument quoting"); }
                    finally { LocalFree(argv); }
                }
                // Qt hosts report "C:/dir/app.exe"; a valid acknowledgement must match regardless of separator style.
                string ackDirectory = Path.Combine(Path.GetTempPath(), "JmdAckTest-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(ackDirectory);
                try
                {
                    string selfConfig = System.Windows.Forms.Application.ExecutablePath + ".config";
                    bool createdConfig = !File.Exists(selfConfig);
                    if (createdConfig) File.WriteAllText(selfConfig, "<configuration />");
                    try
                    {
                        string copy = (string)typeof(AgentContext).GetMethod("CopyInstaller",
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                            .Invoke(null, new object[] { ackDirectory });
                        Require(ManifestIO.Sha256File(copy) == ManifestIO.Sha256File(System.Windows.Forms.Application.ExecutablePath), "installer executable copy matches SHA256");
                        Require(ManifestIO.Sha256File(copy + ".config") == ManifestIO.Sha256File(selfConfig), "installer configuration copy matches SHA256");
                    }
                    finally { if (createdConfig) File.Delete(selfConfig); }
                    string exeNative = Path.Combine(ackDirectory, "上位机 app", "SynJMD_Monitor.exe");
                    string exeQt = exeNative.Replace('\\', '/');
                    string ackPath = Path.Combine(ackDirectory, "shutdown.json");
                    InstallJob ackJob = new InstallJob { PrepareId = new string('b', 64), HostPid = 4242, ExecutablePath = exeQt, ShutdownAckPath = ackPath };
                    foreach (string reported in new[] { exeQt, exeNative })
                    {
                        File.WriteAllText(ackPath, JsonIO.Serialize(new { PrepareId = ackJob.PrepareId, Pid = 4242, ExecutablePath = reported }));
                        Require(AgentContext.HasShutdownAcknowledgement(ackJob), "acknowledgement matches across path separator styles");
                    }
                    ackJob.ExecutablePath = exeNative;
                    Require(AgentContext.HasShutdownAcknowledgement(ackJob), "native job path matches Qt-style acknowledgement");
                    File.WriteAllText(ackPath, JsonIO.Serialize(new { PrepareId = ackJob.PrepareId, Pid = 4242, ExecutablePath = exeQt.Replace("SynJMD_Monitor.exe", "other.exe") }));
                    Require(!AgentContext.HasShutdownAcknowledgement(ackJob), "a different executable is still rejected");
                    File.WriteAllText(ackPath, JsonIO.Serialize(new { PrepareId = ackJob.PrepareId, Pid = 1, ExecutablePath = exeQt }));
                    Require(!AgentContext.HasShutdownAcknowledgement(ackJob), "a different process id is still rejected");
                }
                finally { Directory.Delete(ackDirectory, true); }
                Console.WriteLine("PASS: " + passed + " core signature/path/check/quoting assertions"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
