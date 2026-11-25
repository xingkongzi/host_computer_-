"""End-to-end release signing/extraction and transaction recovery checks.

Fixtures are synthetic and live only in a temporary directory. The small host is
compiled together with production delivery code; no product process is launched.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
import zipfile


HOST_SOURCE = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using JmdUpdater;
class DeliveryTestHost {
    static int Main(string[] args) {
        try {
            var options = CliArgs.Parse(args, 1);
            if (args[0] == "image-path") {
                using (var process = System.Diagnostics.Process.GetCurrentProcess()) {
                    if (!String.Equals(InstallerService.ProcessImagePath(process.Id), process.MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("image path differs");
                }
                bool rejected = false;
                try { InstallerService.ProcessImagePath(Int32.MaxValue); } catch (IOException) { rejected = true; }
                if (!rejected) throw new Exception("unknown process accepted");
                return 0;
            }
            if (args[0] == "failure-ui") {
                Environment.SetEnvironmentVariable("JMD_UPDATER_NO_UI", null);
                using (var ui = new InstallProgress()) {
                    ui.Start("Synthetic failure result test");
                    var flags = BindingFlags.Instance|BindingFlags.NonPublic;
                    var form = (System.Windows.Forms.Form)typeof(InstallProgress).GetField("form",flags).GetValue(ui);
                    string displayed = null;
                    form.Invoke(new Action(delegate {
                        var timer = new System.Windows.Forms.Timer { Interval=500 };
                        timer.Tick += delegate {
                            displayed = ((System.Windows.Forms.TextBox)typeof(InstallProgress).GetField("status",flags).GetValue(ui)).Text;
                            timer.Stop(); timer.Dispose(); form.Close();
                        }; timer.Start();
                    }));
                    var progressField = typeof(InstallerService).GetField("progress",BindingFlags.Static|BindingFlags.NonPublic);
                    progressField.SetValue(null,ui);
                    var journal = new InstallJournal { State=options["state"], Failure="Synthetic reason" };
                    if(options.ContainsKey("records")) journal.Records.Add(new InstallRecord());
                    typeof(InstallerService).GetMethod("NotifyFailure",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {
                        new InstallJob { InstallDirectory=options["installation"], ExecutablePath=options["exe"], TransactionDirectory=options["installation"] },journal });
                    progressField.SetValue(null,null);
                    if(displayed == null || !displayed.Contains("Synthetic reason")) throw new Exception("failure reason missing");
                    if(journal.State != "RolledBack" &&
                        (!displayed.Contains(Path.Combine(options["installation"],"installer","JmdUpdater.exe")) ||
                         !displayed.Contains(Path.Combine(options["installation"],"JmdUpdater","JmdUpdater.exe"))))
                        throw new Exception("transaction recovery command or installed fallback missing");
                }
                return 0;
            }
            if (args[0] == "progress-lifecycle") {
                Environment.SetEnvironmentVariable("JMD_UPDATER_NO_UI", null);
                using (var ui = new InstallProgress()) {
                    ui.Start("Synthetic progress test");
                    var flags = BindingFlags.Instance|BindingFlags.NonPublic;
                    var thread = (System.Threading.Thread)typeof(InstallProgress).GetField("thread",flags).GetValue(ui);
                    if (thread == null || thread.GetApartmentState() != System.Threading.ApartmentState.STA) throw new Exception("UI must own an STA thread");
                    var form = (System.Windows.Forms.Form)typeof(InstallProgress).GetField("form",flags).GetValue(ui);
                    form.Invoke(new Action(delegate { form.Close(); }));
                    if (form.IsDisposed) throw new Exception("window cancelled installation");
                    ui.Report("file 2/3",2,3);
                    form.Invoke(new Action(delegate {
                        var bar = (System.Windows.Forms.ProgressBar)typeof(InstallProgress).GetField("bar",flags).GetValue(ui);
                        if (!form.TopMost || bar.Value != 2 || bar.Maximum != 3) throw new Exception("progress not displayed");
                    }));
                    ui.Finish("Complete",100);
                    if (!form.IsDisposed) throw new Exception("completed window did not close");
                }
                return 0;
            }
            if (args[0] == "hold") {
                File.WriteAllText(options["ready"], "ready");
                System.Threading.Thread.Sleep(2500); return 0;
            }
            if (args[0] == "no-ui") {
                using (var ui = new InstallProgress()) {
                    ui.Start("test"); ui.Report("test",1,1); ui.Finish("test",0);
                    if (typeof(InstallProgress).GetField("thread", BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ui) != null)
                        throw new Exception("NO_UI created UI thread");
                }
                return 0;
            }
            if (args[0] == "validate") {
                var config = ManifestIO.LoadConfig(options["config"]);
                var manifest = ManifestIO.Verify(File.ReadAllText(options["envelope"]), config.PublicKeyXml);
                InstallerService.ValidatePackage(manifest, options["package"], options["stage"], config);
                return 0;
            }
            if (args[0] == "sign") {
                JsonIO.WriteAtomic(options["output"], ManifestIO.Sign(JsonIO.Read<ReleaseManifest>(options["manifest"]), File.ReadAllText(options["key"])));
                return 0;
            }
            if (args[0] == "transaction") {
                var config = ManifestIO.LoadConfig(options["config"]);
                var envelope = JsonIO.Read<SignedRelease>(options["envelope"]);
                var manifest = ManifestIO.Verify(JsonIO.Serialize(envelope), config.PublicKeyXml);
                var transaction = options["transaction"];
                Directory.CreateDirectory(transaction);
                var stage = Path.Combine(transaction, "stage");
                InstallerService.ValidatePackage(manifest, options["package"], stage, config);
                File.Copy(options["envelope"], Path.Combine(transaction, "release.json"));
                var job = new InstallJob { AppId=config.AppId, InstallDirectory=options["installation"],
                    ExecutablePath=Path.Combine(options["installation"], manifest.EntryPoint), ConfigPath=options["config"],
                    TransactionDirectory=transaction, EnvelopePath=Path.Combine(transaction, "release.json"),
                    PrepareId=new string('a',64) };
                var journal = new InstallJournal { PrepareId=job.PrepareId, State="Applying" };
                var apply = typeof(InstallerService).GetMethod("Apply", BindingFlags.Static|BindingFlags.NonPublic);
                var rollback = typeof(InstallerService).GetMethod("Rollback", BindingFlags.Static|BindingFlags.NonPublic);
                var plan = typeof(InstallerService).GetMethod("PlanChanges", BindingFlags.Static|BindingFlags.NonPublic)
                    .Invoke(null, new object[] {job,manifest,config,stage});
                apply.Invoke(null, new object[] {job,plan,manifest,envelope,journal});
                if (File.ReadAllText(job.ExecutablePath) != "NEW-PROGRAM") throw new Exception("new files not applied");
                if (options.ContainsKey("forge-protected")) {
                    journal.Records.Add(new InstallRecord { Path="default.txt", Touched=true, OriginallyExisted=false });
                    bool rejected=false;
                    try { rollback.Invoke(null,new object[] {job,journal}); } catch(TargetInvocationException) { rejected=true; }
                    if (!rejected || !File.Exists(Path.Combine(job.InstallDirectory,"default.txt"))) throw new Exception("protected recovery path accepted");
                    return 0;
                }
                if (File.ReadAllText(Path.Combine(job.InstallDirectory,"JmdUpdater/JmdUpdater.exe")) != "NEW-UPDATER") throw new Exception("updater not applied");
                rollback.Invoke(null,new object[] {job,journal});
                if (File.Exists(Path.Combine(job.InstallDirectory,"JmdUpdater/JmdUpdater.exe"))) throw new Exception("new updater not rolled back");
                if (File.ReadAllText(job.ExecutablePath) != "OLD-PROGRAM") throw new Exception("original program not restored");
                if (File.Exists(Path.Combine(job.InstallDirectory,"new.dll"))) throw new Exception("new file not removed");
                if (File.ReadAllText(Path.Combine(job.InstallDirectory,"default.txt")) != "USER-DATA") throw new Exception("user data changed");
                if (journal.State != "RolledBack") throw new Exception("rollback not journaled");
                rollback.Invoke(null,new object[] {job,journal});
                return 0;
            }
            if (args[0] == "swap") {
                // Unit-level check of the replacement step with Windows' atomic replace refused on demand.
                var installer = typeof(InstallerService);
                installer.GetField("LockWaitSeconds", BindingFlags.Static|BindingFlags.NonPublic).SetValue(null, 1);
                if (options.ContainsKey("refuse"))
                    installer.GetField("SimulateReplaceRefused", BindingFlags.Static|BindingFlags.NonPublic).SetValue(null, new Func<string,bool>(p => true));
                if (options.ContainsKey("partial"))   // ReplaceFile error 1176: the old file is already gone when it fails
                    installer.GetField("SimulateReplaceRefused", BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,
                        new Func<string,bool>(p => { if (!File.Exists(p)) return false; File.Delete(p); return true; }));
                try {
                    installer.GetMethod("SwapIn", BindingFlags.Static|BindingFlags.NonPublic)
                        .Invoke(null, new object[] { options["source"], options["target"], options["aside"] == "true" });
                    Console.WriteLine("SWAPPED"); return 0;
                } catch (TargetInvocationException failure) { Console.WriteLine("REFUSED " + failure.InnerException.Message); return 3; }
            }
            var rest = new string[args.Length-1]; Array.Copy(args,1,rest,0,rest.Length);
            return DeliveryCommands.Run(args[0],rest);
        } catch(Exception e) { Console.Error.WriteLine(e.ToString()); return 2; }
    }
}
'''


class DeliveryPackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="JmdDeliveryPackageTests-")
        cls.root = Path(cls.temporary.name)
        cls.exe = cls.root / "DeliveryTestHost.exe"
        host = cls.root / "DeliveryTestHost.cs"
        host.write_text(HOST_SOURCE, encoding="utf-8")
        module = Path(__file__).resolve().parents[1]
        compiler = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework" / "v4.0.30319" / "csc.exe"
        sources = [module / "Shared" / "Contracts.cs", module / "Shared" / "Support.cs",
                   module / "Publisher" / "LocalUpdateServer.cs", module / "Publisher" / "PublisherService.cs",
                   module / "Installer" / "InstallerService.cs", host]
        subprocess.run([str(compiler), "/nologo", "/target:exe", "/out:" + str(cls.exe),
                        "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll", "/r:System.Windows.Forms.dll"]
                       + [str(p) for p in sources], check=True, capture_output=True, text=True)
        cls.private = cls.root / "keys" / "private.xml"
        cls.public = cls.root / "keys" / "public.xml"
        cls.command("keygen", private=cls.private, public=cls.public)

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    @classmethod
    def command(cls, mode, expected=0, **options):
        args = [str(cls.exe), mode]
        for key, value in options.items():
            args.extend(["--" + key.replace("_", "-"), str(value)])
        environment = dict(os.environ, JMD_UPDATER_NO_UI="1")
        result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=40, env=environment)
        if result.returncode != expected:
            raise AssertionError("unexpected result " + str(result.returncode) + ": " + result.stdout + result.stderr)
        return result

    def setUp(self):
        self.case = self.root / self._testMethodName
        self.source = self.case / "source"
        self.source.mkdir(parents=True)
        (self.source / "app.exe").write_text("NEW-PROGRAM", encoding="utf-8")
        (self.source / "new.dll").write_text("NEW-LIBRARY", encoding="utf-8")
        (self.source / "default.txt").write_text("DO-NOT-PUBLISH", encoding="utf-8")
        (self.source / "build.obj").write_text("DO-NOT-PUBLISH", encoding="utf-8")
        (self.source / "record.csv").write_text("DO-NOT-PUBLISH", encoding="utf-8")
        self.config = self.case / "jmd-update.json"
        self.config.write_text(json.dumps({"Enabled": True, "AppId": "fixture", "PublicKeyXml": self.public.read_text(),
                                          "PreservePaths": ["default.txt", "*.csv"], "MaxPackageBytes": 1024 * 1024}), encoding="utf-8")
        updater = self.source / "JmdUpdater"
        updater.mkdir()
        (updater / "JmdUpdater.exe").write_text("NEW-UPDATER")
        (updater / "JmdUpdater.exe.config").write_text("NEW-CONFIG")
        (updater / "public-key.xml").write_text("TRUST-NEVER-PUBLISH")
        (updater / "other.exe").write_text("PRIVATE")
        self.releases = self.case / "public"
        self.command("publish", source=self.source, root=self.releases, config=self.config,
                     key=self.private, entry="app.exe", version="1.2.3.4", notes="fixture")
        self.envelope = self.releases / "fixture" / "latest.json"
        self.package = self.releases / "fixture" / "fixture-1.2.3.4.zip"

    def validate(self, expected=0):
        self.command("validate", expected=expected, config=self.config, envelope=self.envelope,
                     package=self.package, stage=self.case / "stage")

    def alter_package_and_resign(self, modify):
        with zipfile.ZipFile(self.package) as archive:
            entries = [(info, archive.read(info.filename)) for info in archive.infolist()]
        entries = modify(entries)
        self.package.unlink()
        with zipfile.ZipFile(self.package, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for info, content in entries:
                archive.writestr(info, content)
        wrapper = json.loads(self.envelope.read_text())
        manifest = json.loads(base64.b64decode(wrapper["Payload"]))
        data = self.package.read_bytes()
        manifest["PackageSize"] = len(data)
        manifest["PackageSha256"] = hashlib.sha256(data).hexdigest()
        manifest_path = self.case / "mutated-manifest.json"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        self.command("sign", manifest=manifest_path, key=self.private, output=self.envelope)

    def test_publish_and_validate_complete_package(self):
        with zipfile.ZipFile(self.package) as archive:
            self.assertEqual(set(archive.namelist()), {"app.exe", "new.dll", "jmd-version.json", "JmdUpdater/JmdUpdater.exe", "JmdUpdater/JmdUpdater.exe.config"})
        self.validate()
        self.assertEqual((self.case / "stage" / "app.exe").read_text(), "NEW-PROGRAM")

    def test_manifest_tampering_is_rejected(self):
        wrapper = json.loads(self.envelope.read_text())
        payload = json.loads(base64.b64decode(wrapper["Payload"]))
        payload["Version"] = "9.9.9.9"
        wrapper["Payload"] = base64.b64encode(json.dumps(payload).encode()).decode()
        self.envelope.write_text(json.dumps(wrapper), encoding="utf-8")
        self.validate(expected=2)

    def test_limited_process_image_query_and_failure(self):
        self.command("image-path")

    def test_progress_sta_close_guard_and_completion(self):
        self.command("progress-lifecycle")

    def test_failure_result_controls_automatic_restart(self):
        fixture = self.case / "Reopen.cs"
        fixture.write_text('using System.IO; class Reopen { static void Main() { File.WriteAllText("reopened", "yes"); } }', encoding="utf-8")
        exe = self.case / "Reopen.exe"
        compiler = Path(os.environ["WINDIR"]) / "Microsoft.NET/Framework/v4.0.30319/csc.exe"
        subprocess.run([str(compiler), "/nologo", "/out:" + str(exe), str(fixture)], check=True, capture_output=True)
        for state, records in [("RolledBack", False), ("RolledBack", True), ("RollbackFailed", False), ("StartupUnconfirmed", False)]:
            marker = self.case / "reopened"
            if marker.exists():
                marker.unlink()
            extra = {"records": "1"} if records else {}
            self.command("failure-ui", state=state, installation=self.case, exe=exe, **extra)
            self.assertEqual(marker.exists(), state == "RolledBack", state)

    def test_package_tampering_is_rejected(self):
        with self.package.open("ab") as file:
            file.write(b"tampered")
        self.validate(expected=2)

    def test_signed_zip_traversal_is_rejected(self):
        def modify(entries):
            entries[0] = (zipfile.ZipInfo("../outside.exe"), entries[0][1])
            return entries
        self.alter_package_and_resign(modify)
        self.validate(expected=2)
        self.assertFalse((self.case / "outside.exe").exists())

    def test_signed_unlisted_zip_entry_is_rejected(self):
        self.alter_package_and_resign(lambda entries: entries + [(zipfile.ZipInfo("unlisted.dll"), b"unexpected")])
        self.validate(expected=2)

    def test_signed_symbolic_link_is_rejected(self):
        def modify(entries):
            info, content = entries[0]
            info.create_system = 3
            info.external_attr = 0o120777 << 16
            entries[0] = (info, content)
            return entries
        self.alter_package_and_resign(modify)
        self.validate(expected=2)

    def swap_fixture(self):
        folder = self.case / "swap"
        folder.mkdir()
        target, source = folder / "program.dll", folder / "incoming.dll"
        target.write_text("OLD", encoding="utf-8")
        source.write_text("NEW", encoding="utf-8")
        return folder, target, source

    def test_refused_replace_falls_back_to_rename_for_ordinary_files(self):
        folder, target, source = self.swap_fixture()
        result = self.command("swap", target=target, source=source, aside="true", refuse="true")
        self.assertIn("SWAPPED", result.stdout)
        self.assertEqual(target.read_text(), "NEW")
        self.assertEqual(sorted(p.name for p in folder.iterdir()), ["incoming.dll", "program.dll"], "aside copy and temporary removed")

    def test_refused_replace_never_renames_the_entry_executable(self):
        folder, target, source = self.swap_fixture()
        result = self.command("swap", expected=3, target=target, source=source, aside="false", refuse="true")
        self.assertIn("REFUSED", result.stdout)
        self.assertEqual(target.read_text(), "OLD", "the file must stay in place, never renamed aside")
        self.assertEqual(sorted(p.name for p in folder.iterdir()), ["incoming.dll", "program.dll"])

    def test_replace_that_already_removed_the_old_file_is_completed(self):
        # Even for the entry executable (no rename fallback) the name must never stay empty.
        folder, target, source = self.swap_fixture()
        result = self.command("swap", target=target, source=source, aside="false", partial="true")
        self.assertIn("SWAPPED", result.stdout)
        self.assertEqual(target.read_text(), "NEW")
        self.assertEqual(sorted(p.name for p in folder.iterdir()), ["incoming.dll", "program.dll"])

    def test_read_only_program_file_is_replaced(self):
        folder, target, source = self.swap_fixture()
        os.chmod(target, stat.S_IREAD)
        try:
            self.command("swap", target=target, source=source, aside="false")
            self.assertEqual(target.read_text(), "NEW")
        finally:
            os.chmod(target, stat.S_IREAD | stat.S_IWRITE)

    def test_same_version_cannot_be_overwritten(self):
        self.command("publish", expected=2, source=self.source, root=self.releases, config=self.config,
                     key=self.private, entry="app.exe", version="1.2.3.4")
        self.validate()

    def test_equivalent_version_forms_are_not_new_publications(self):
        destination = self.case / "version-public"
        self.command("publish", source=self.source, root=destination, config=self.config,
                     key=self.private, entry="app.exe", version="1.2")
        self.command("publish", expected=2, source=self.source, root=destination, config=self.config,
                     key=self.private, entry="app.exe", version="1.2.0")

    def test_declared_required_data_policy_must_be_covered(self):
        config = json.loads(self.config.read_text())
        config["RequiredPreservePaths"] = ["default.txt", "*.db", "*.curve"]
        config["PreservePaths"] = ["default.txt", "*.csv", "*.log"]
        self.config.write_text(json.dumps(config), encoding="utf-8")
        self.command("publish", expected=2, source=self.source, root=self.case / "unsafe-public", config=self.config,
                     key=self.private, entry="app.exe", version="2.0")

    def test_empty_generic_data_policy_requires_explicit_declaration(self):
        config = json.loads(self.config.read_text())
        config["PreservePaths"] = []
        self.config.write_text(json.dumps(config), encoding="utf-8")
        self.command("publish", expected=2, source=self.source, root=self.case / "empty-public", config=self.config,
                     key=self.private, entry="app.exe", version="2.0")
        self.command("publish", source=self.source, root=self.case / "empty-public", config=self.config,
                     key=self.private, entry="app.exe", version="2.0", allow_empty_preserve="true")

    def test_transaction_restores_original_and_preserves_data(self):
        installation = self.case / "installation"
        installation.mkdir()
        (installation / "app.exe").write_text("OLD-PROGRAM", encoding="utf-8")
        (installation / "default.txt").write_text("USER-DATA", encoding="utf-8")
        config = installation / "jmd-update.json"
        config.write_bytes(self.config.read_bytes())
        self.command("transaction", config=config, envelope=self.envelope, package=self.package,
                     installation=installation, transaction=self.case / "transaction")

    def test_forged_recovery_record_cannot_delete_preserved_data(self):
        installation = self.case / "installation"
        installation.mkdir()
        (installation / "app.exe").write_text("OLD-PROGRAM", encoding="utf-8")
        (installation / "default.txt").write_text("USER-DATA", encoding="utf-8")
        config = installation / "jmd-update.json"
        config.write_bytes(self.config.read_bytes())
        self.command("transaction", config=config, envelope=self.envelope, package=self.package,
                     installation=installation, transaction=self.case / "transaction", forge_protected="true")


if __name__ == "__main__":
    unittest.main(verbosity=2)
