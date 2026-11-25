"""Exercise the real installer executable against disposable synthetic processes.

The only per-user cache directories touched are random, recorded 64-hex fixtures.
No Monitor/Manager process or installed product directory is opened or stopped.
"""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time
import unittest
import uuid

from DeliveryPackageTests import HOST_SOURCE


FIXTURE_APP = r'''
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
class Fixture {
    static void Write(string path, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, new JavaScriptSerializer().Serialize(value));
    }
    static int Main() {
        var self=Process.GetCurrentProcess();
        var ready=Environment.GetEnvironmentVariable("JMD_UPDATE_READY_PATH");
        if (!String.IsNullOrEmpty(ready)) {
            if (Environment.GetEnvironmentVariable("FIXTURE_STARTUP")=="exit") return 7;
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory,"new.pid"),self.Id.ToString());
            if(Environment.GetEnvironmentVariable("FIXTURE_STARTUP")=="partial") {
                File.WriteAllText(ready,""); Thread.Sleep(350);
            }
            if(Environment.GetEnvironmentVariable("FIXTURE_STARTUP")!="silent")
                Write(ready,new {Token=Environment.GetEnvironmentVariable("FIXTURE_STARTUP")=="wrong" ? new string('0',64) : Environment.GetEnvironmentVariable("JMD_UPDATE_READY_TOKEN"),Pid=self.Id});
            while(!File.Exists(Path.Combine(Environment.CurrentDirectory,"fixture.stop"))) Thread.Sleep(25);
            return 0;
        }
        Write(Path.Combine(Environment.CurrentDirectory,"old.started.json"),new {Pid=self.Id,StartUtcTicks=self.StartTime.ToUniversalTime().Ticks});
        while(!File.Exists(Path.Combine(Environment.CurrentDirectory,"fixture.exit"))) Thread.Sleep(25);
        if(Environment.GetEnvironmentVariable("FIXTURE_ACK")!="none" &&
            !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("FIXTURE_ACK_PATH")))
            Write(Environment.GetEnvironmentVariable("FIXTURE_ACK_PATH"),new {
                PrepareId=Environment.GetEnvironmentVariable("FIXTURE_PREPARE_ID"),Pid=self.Id,ExecutablePath=self.MainModule.FileName});
        return 0;
    }
}
'''


class DeliveryProcessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="JmdDeliveryProcessTests-")
        cls.root = Path(cls.temp.name)
        module = Path(__file__).resolve().parents[1]
        compiler = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Microsoft.NET" / "Framework" / "v4.0.30319" / "csc.exe"
        host = cls.root / "DeliveryTestHost.cs"
        host.write_text(HOST_SOURCE, encoding="utf-8")
        cls.updater = cls.root / "DeliveryTestHost.exe"
        sources = [module / "Shared" / "Contracts.cs", module / "Shared" / "Support.cs",
                   module / "Publisher" / "LocalUpdateServer.cs", module / "Publisher" / "PublisherService.cs",
                   module / "Installer" / "InstallerService.cs", host]
        subprocess.run([str(compiler), "/nologo", "/target:exe", "/out:" + str(cls.updater),
                        "/r:System.Web.Extensions.dll", "/r:System.IO.Compression.dll", "/r:System.IO.Compression.FileSystem.dll",
                        "/r:System.Windows.Forms.dll"] + [str(p) for p in sources], check=True, capture_output=True)
        fixture = cls.root / "Fixture.cs"
        fixture.write_text(FIXTURE_APP, encoding="utf-8")
        cls.fixture = cls.root / "Fixture.exe"
        subprocess.run([str(compiler), "/nologo", "/target:exe", "/r:System.Web.Extensions.dll", "/out:" + str(cls.fixture), str(fixture)],
                       check=True, capture_output=True)
        gate = cls.root / "GateHost.cs"
        gate.write_text('using System; using JmdUpdater; class GateHost { static int Main(string[] a) { bool allowed=UpdateHost.CanStartApplication(a[0]); Console.WriteLine(allowed?"Allowed":"Blocked"); return allowed?0:2; } }', encoding="utf-8")
        cls.gate = cls.root / "GateHost.exe"
        subprocess.run([str(compiler), "/nologo", "/target:exe", "/r:System.Web.Extensions.dll", "/r:System.Windows.Forms.dll",
                        "/out:" + str(cls.gate), str(gate), str(module / "Adapters" / "DotNet" / "UpdateHost.cs")], check=True, capture_output=True)
        cls.private = cls.root / "keys" / "private.xml"
        cls.public = cls.root / "keys" / "public.xml"
        cls.run_command("keygen", private=cls.private, public=cls.public)

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    @classmethod
    def run_command(cls, mode, expected=0, **options):
        args = [str(cls.updater), mode]
        for key, value in options.items():
            args.extend(["--" + key.replace("_", "-"), str(value)])
        result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=35,
                                env=dict(os.environ, JMD_UPDATER_NO_UI="1"))
        if result.returncode != expected:
            raise AssertionError(result.stdout + result.stderr)
        return result

    def setUp(self):
        self.case = self.root / self._testMethodName
        self.installation = self.case / "installation with spaces"
        self.installation.mkdir(parents=True)
        shutil.copy2(self.fixture, self.installation / "Fixture.exe")
        (self.installation / "payload.txt").write_text("OLD", encoding="utf-8")
        (self.installation / "shared.dat").write_text("SAME-IN-BOTH-VERSIONS", encoding="utf-8")
        (self.installation / "default.txt").write_text("USER-DATA", encoding="utf-8")
        self.config = self.installation / "jmd-update.json"
        self.config.write_text(json.dumps({"Enabled": True, "AppId": "process-fixture", "PublicKeyXml": self.public.read_text(encoding="utf-8-sig"),
                                          "PreservePaths": ["default.txt", "fixture.stop", "fixture.exit", "old.started.json", "new.pid"],
                                          "ExitTimeoutSeconds": 5, "StartupTimeoutSeconds": 10, "MaxPackageBytes": 1024 * 1024}), encoding="utf-8")
        source = self.case / "source"
        source.mkdir()
        shutil.copy2(self.fixture, source / "Fixture.exe")
        (source / "payload.txt").write_text("NEW", encoding="utf-8")
        (source / "shared.dat").write_text("SAME-IN-BOTH-VERSIONS", encoding="utf-8")
        releases = self.case / "public"
        self.source, self.releases = source, releases
        self.run_command("publish", source=source, root=releases, config=self.config, key=self.private,
                         entry="Fixture.exe", version="2.0.0.0")
        self.prepare = uuid.uuid4().hex + uuid.uuid4().hex
        self.session = uuid.uuid4().hex + uuid.uuid4().hex
        self.cache = Path(os.environ["LOCALAPPDATA"]) / "JmdUpdater"
        self.transaction = self.cache / "Transactions" / self.prepare
        self.session_root = self.cache / "Sessions" / self.session
        self.transaction.mkdir(parents=True)
        self.session_root.mkdir(parents=True)
        shutil.copy2(releases / "process-fixture" / "latest.json", self.transaction / "release.json")
        shutil.copy2(releases / "process-fixture" / "process-fixture-2.0.0.0.zip", self.transaction / "package.zip")
        self.old = None
        self.new_pid = None
        self.extra = []

    def tearDown(self):
        (self.installation / "fixture.stop").touch()
        (self.installation / "fixture.exit").touch()
        if self.old:
            self.old.wait(timeout=5)
        for process in self.extra:
            process.wait(timeout=5)
        marker = self.installation / "new.pid"
        if marker.exists():
            pid = int(marker.read_text(encoding="utf-8-sig"))
            handle = ctypes.windll.kernel32.OpenProcess(0x00100000, False, pid)
            if handle:
                try:
                    self.assertEqual(ctypes.windll.kernel32.WaitForSingleObject(handle, 5000), 0)
                finally:
                    ctypes.windll.kernel32.CloseHandle(handle)
        for directory, parent, token in ((self.transaction, self.cache / "Transactions", self.prepare),
                                         (self.session_root, self.cache / "Sessions", self.session)):
            if directory.resolve().parent != parent.resolve() or directory.name != token or len(token) != 64:
                raise AssertionError("refusing unsafe fixture cleanup")
            shutil.rmtree(directory)

    def execute(self, startup="ready", ack="valid", expected=0, reject_before_stage=False, cancel_after_stage=False, extra_instance=False, preparation_delay=0):
        environment = dict(os.environ, FIXTURE_ACK=ack, FIXTURE_ACK_PATH=str(self.session_root / "shutdown.json"),
                           FIXTURE_PREPARE_ID=self.prepare, JMD_UPDATER_NO_UI="1")
        self.old = subprocess.Popen([str(self.installation / "Fixture.exe")], cwd=self.installation, env=environment)
        metadata_path = self.installation / "old.started.json"
        deadline = time.monotonic() + 5
        while not metadata_path.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        metadata = json.loads(metadata_path.read_text(encoding="utf-8-sig"))
        job = {"AppId": "process-fixture", "InstallDirectory": str(self.installation), "ExecutablePath": str(self.installation / "Fixture.exe"),
               "HostPid": self.old.pid, "HostStartUtcTicks": metadata["StartUtcTicks"], "PrepareId": self.prepare, "SessionToken": self.session,
               "ShutdownAckPath": str(self.session_root / "shutdown.json"), "PackagePath": str(self.transaction / "package.zip"),
               "EnvelopePath": str(self.transaction / "release.json"), "ConfigPath": str(self.config), "TransactionDirectory": str(self.transaction)}
        job_path = self.transaction / "job.json"
        job_path.write_text(json.dumps(job), encoding="utf-8")
        if extra_instance:
            self.extra.append(subprocess.Popen([str(self.installation / "Fixture.exe")], cwd=self.installation,
                                               env=dict(environment, FIXTURE_ACK="none")))
            time.sleep(0.1)
        # A started product legitimately outlives Installer and inherits its standard
        # handles. Regular log files let us wait for Installer rather than for EOF
        # from every descendant, which would incorrectly time out a successful update.
        output_path = self.case / "installer.stdout.txt"
        error_path = self.case / "installer.stderr.txt"
        with output_path.open("wb") as output_file, error_path.open("wb") as error_file:
            process = subprocess.Popen([str(self.updater), "install", "--job", str(job_path)], stdout=output_file, stderr=error_file,
                                       env=dict(os.environ, JMD_UPDATER_NO_UI="1", FIXTURE_STARTUP=startup))
            staged = self.transaction / "staged.json"
            deadline = time.monotonic() + 10
            while not staged.exists() and process.poll() is None and time.monotonic() < deadline:
                time.sleep(0.02)
            if reject_before_stage:
                self.assertFalse(staged.exists(), "unsafe package or competing instance must be rejected before staged acknowledgement")
            else:
                self.assertTrue(staged.exists(), "Installer never acknowledged a valid staged package")
                handshake = json.loads(staged.read_text(encoding="utf-8-sig"))
                self.assertEqual(handshake, {"PrepareId": self.prepare, "HostPid": self.old.pid})
                self.assertIsNone(self.old.poll(), "the host must remain alive during staging")
                if cancel_after_stage:
                    (self.transaction / "cancel.json").write_text(json.dumps({"PrepareId": self.prepare}), encoding="utf-8")
                else:
                    if preparation_delay:
                        time.sleep(preparation_delay)
                        self.assertIsNone(process.poll(), "Installer consumed the exit timeout before shutdown was requested")
                    (self.transaction / "shutdown-requested.json").write_text(json.dumps({"PrepareId": self.prepare, "HostPid": self.old.pid}), encoding="utf-8")
                    (self.installation / "fixture.exit").touch()
            process.wait(timeout=30)
        output, error = output_path.read_bytes(), error_path.read_bytes()
        if reject_before_stage or cancel_after_stage:
            self.assertIsNone(self.old.poll(), "preflight failure/cancellation closed the original host")
            failure = json.loads((self.transaction / "stage-failed.json").read_text(encoding="utf-8-sig"))
            self.assertEqual(failure["PrepareId"], self.prepare)
            self.assertEqual(failure["Code"], "Cancelled" if cancel_after_stage else "PreflightFailed")
            self.assertTrue(failure["Message"])
        else:
            self.old.wait(timeout=5)
        self.assertEqual(process.returncode, expected, output.decode(errors="replace") + error.decode(errors="replace"))
        return job_path

    def test_no_ui_creates_no_window_or_ui_thread(self):
        self.run_command("no-ui")

    def test_updater_process_is_waited_for_before_replacement(self):
        folder = self.installation / "JmdUpdater"
        folder.mkdir()
        target = folder / "JmdUpdater.exe"
        shutil.copy2(self.updater, target)
        (self.source / "JmdUpdater").mkdir()
        (self.source / "JmdUpdater" / "JmdUpdater.exe").write_text("NEW-UPDATER")
        self.republish("3.0")
        ready = self.case / "settings.ready"
        with subprocess.Popen([str(target), "hold", "--ready", str(ready)]) as settings:
            deadline = time.monotonic() + 5
            while not ready.exists() and time.monotonic() < deadline:
                time.sleep(0.02)
            self.assertTrue(ready.exists())
            started = time.monotonic()
            self.execute()
            self.assertGreater(time.monotonic() - started, 1.5)
            self.assertIsNotNone(settings.poll())
        self.assertEqual(target.read_text(encoding="utf-8-sig"), "NEW-UPDATER")
        self.assertIn("JmdUpdater/JmdUpdater.exe", {r["Path"] for r in self.journal()["Records"]})

    def test_recover_from_transaction_copy_restores_updater(self):
        self.recover_updater_from_copy(False)

    def test_recover_from_transaction_copy_restores_missing_updater(self):
        self.recover_updater_from_copy(True)

    def recover_updater_from_copy(self, missing):
        folder = self.installation / "JmdUpdater"
        folder.mkdir()
        target = folder / "JmdUpdater.exe"
        shutil.copy2(self.updater, target)
        (self.source / "JmdUpdater").mkdir()
        shutil.copy2(self.updater, self.source / "JmdUpdater" / "JmdUpdater.exe")
        self.republish("3.0")
        job = self.write_interrupted_transaction("Applying", "NEW")
        backup = self.transaction / "backup" / "JmdUpdater" / "JmdUpdater.exe"
        backup.parent.mkdir()
        backup.write_bytes(b"OLD-UPDATER")
        journal = self.journal()
        journal["Records"].append({"Path":"JmdUpdater/JmdUpdater.exe", "OriginallyExisted":True,
            "BackupReady":True, "Touched":True, "BackupSha256":hashlib.sha256(backup.read_bytes()).hexdigest()})
        (self.transaction / "journal.json").write_text(json.dumps(journal), encoding="utf-8")
        copy = self.transaction / "installer" / "JmdUpdater.exe"
        copy.parent.mkdir()
        shutil.copy2(self.updater, copy)
        if missing:
            target.unlink()
        result = subprocess.run([str(copy), "recover", "--job", str(job)], capture_output=True,
            timeout=30, env=dict(os.environ, JMD_UPDATER_NO_UI="1"))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(target.read_bytes(), b"OLD-UPDATER")
        self.assertEqual(self.journal()["State"], "RolledBack")

    def test_legacy_package_preserves_previously_managed_updater(self):
        updater = self.installation / "JmdUpdater"
        updater.mkdir()
        source = self.source / "JmdUpdater"
        source.mkdir()
        for name in ("JmdUpdater.exe", "JmdUpdater.exe.config"):
            (updater / name).write_bytes(b"KEEP")
            (source / name).write_bytes(b"KEEP")
        self.run_command("publish", source=self.source, root=self.case / "previous", config=self.config,
                         key=self.private, entry="Fixture.exe", version="1.0")
        envelope = json.loads((self.case / "previous" / "process-fixture" / "latest.json").read_text(encoding="utf-8-sig"))
        metadata = self.installation / ".jmd-update"
        metadata.mkdir()
        (metadata / "installed.json").write_text(json.dumps({"AppId":"process-fixture", "Version":"1.0", "Release":envelope}), encoding="utf-8")
        self.execute()
        self.assertEqual(self.journal()["State"], "Complete")
        for name in ("JmdUpdater.exe", "JmdUpdater.exe.config"):
            self.assertEqual((updater / name).read_bytes(), b"KEEP")

    def test_normal_ack_and_startup_confirmation_complete_install(self):
        self.execute()
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "NEW")
        self.assertEqual((self.installation / "default.txt").read_text(encoding="utf-8-sig"), "USER-DATA")
        self.assertEqual(json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))["State"], "Complete")
        self.assertFalse((self.installation / ".jmd-update" / "pending.json").exists())

    def test_preparation_does_not_consume_shutdown_exit_timeout(self):
        self.execute(preparation_delay=6)
        self.assertEqual(json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))["State"], "Complete")

    def test_partial_startup_marker_is_retried_until_valid(self):
        self.execute(startup="partial")
        self.assertEqual(json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))["State"], "Complete")

    def test_wrong_startup_token_never_confirms_install(self):
        self.execute(startup="wrong", expected=2)
        self.assertEqual(json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))["State"], "StartupUnconfirmed")

    def test_process_exit_without_ack_cannot_install(self):
        self.execute(ack="none", expected=2)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertFalse((self.transaction / "journal.json").exists())

    def test_early_startup_exit_restores_previous_files(self):
        self.execute(startup="exit", expected=2)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertEqual(json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))["State"], "RolledBack")

    def test_alive_unconfirmed_startup_requires_explicit_recovery(self):
        job = self.execute(startup="silent", expected=2)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "NEW")
        self.assertEqual(json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))["State"], "StartupUnconfirmed")
        self.assertTrue((self.installation / ".jmd-update" / "pending.json").exists())
        self.run_command("recover", expected=2, job=job)
        (self.installation / "fixture.stop").touch()
        pid = int((self.installation / "new.pid").read_text(encoding="utf-8-sig"))
        handle = ctypes.windll.kernel32.OpenProcess(0x00100000, False, pid)
        if handle:
            try:
                self.assertEqual(ctypes.windll.kernel32.WaitForSingleObject(handle, 5000), 0)
            finally:
                ctypes.windll.kernel32.CloseHandle(handle)
        gate = subprocess.run([str(self.gate), str(self.installation)], capture_output=True)
        self.assertEqual(gate.returncode, 2, "unfinished durable transaction must prevent launch even after Installer exits")
        self.run_command("recover", job=job)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertFalse((self.installation / ".jmd-update" / "pending.json").exists())
        self.assertEqual(subprocess.run([str(self.gate), str(self.installation)], capture_output=True).returncode, 0)

    def test_corrupt_package_is_rejected_without_closing_host(self):
        with (self.transaction / "package.zip").open("ab") as package:
            package.write(b"corrupt")
        self.execute(expected=2, reject_before_stage=True)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertFalse((self.installation / ".jmd-update" / "pending.json").exists())

    def test_other_instance_is_rejected_without_closing_host(self):
        self.execute(expected=2, reject_before_stage=True, extra_instance=True)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")

    def test_cancelled_staged_transaction_leaves_host_and_files_unchanged(self):
        self.execute(expected=2, cancel_after_stage=True)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertFalse((self.transaction / "journal.json").exists())
        self.assertFalse((self.installation / ".jmd-update" / "pending.json").exists())

    def journal(self):
        return json.loads((self.transaction / "journal.json").read_text(encoding="utf-8-sig"))

    def republish(self, version):
        self.run_command("publish", source=self.source, root=self.releases, config=self.config, key=self.private,
                         entry="Fixture.exe", version=version)
        shutil.copy2(self.releases / "process-fixture" / "latest.json", self.transaction / "release.json")
        shutil.copy2(self.releases / "process-fixture" / ("process-fixture-%s.zip" % version), self.transaction / "package.zip")

    def write_interrupted_transaction(self, state, current_payload):
        # A transaction that recorded payload.txt as touched, with a verified backup of the original.
        (self.installation / "payload.txt").write_text(current_payload, encoding="utf-8")
        backup = self.transaction / "backup" / "payload.txt"
        backup.parent.mkdir(parents=True)
        backup.write_text("OLD", encoding="utf-8")
        job = {"AppId": "process-fixture", "InstallDirectory": str(self.installation), "ExecutablePath": str(self.installation / "Fixture.exe"),
               "HostPid": 1, "HostStartUtcTicks": 1, "PrepareId": self.prepare, "SessionToken": self.session,
               "ShutdownAckPath": str(self.session_root / "shutdown.json"), "PackagePath": str(self.transaction / "package.zip"),
               "EnvelopePath": str(self.transaction / "release.json"), "ConfigPath": str(self.config), "TransactionDirectory": str(self.transaction)}
        job_path = self.transaction / "job.json"
        job_path.write_text(json.dumps(job), encoding="utf-8")
        record = {"Path": "payload.txt", "OriginallyExisted": True, "BackupSha256": hashlib.sha256(b"OLD").hexdigest(),
                  "BackupReady": True, "Touched": True}
        (self.transaction / "journal.json").write_text(json.dumps({"PrepareId": self.prepare, "State": state, "Records": [record]}), encoding="utf-8")
        metadata = self.installation / ".jmd-update"
        metadata.mkdir(exist_ok=True)
        (metadata / "pending.json").write_text(json.dumps({"PrepareId": self.prepare, "TransactionDirectory": str(self.transaction)}), encoding="utf-8")
        return job_path

    def test_identical_file_is_skipped_even_while_locked(self):
        # shared.dat and Fixture.exe have the same content in both versions; a reader without
        # delete sharing on shared.dat must not matter because the file is never touched.
        with (self.installation / "shared.dat").open("rb"):
            self.execute()
        journal = self.journal()
        self.assertEqual(journal["State"], "Complete")
        touched = {record["Path"] for record in journal["Records"]}
        self.assertNotIn("shared.dat", touched)
        self.assertNotIn("Fixture.exe", touched)
        self.assertIn("payload.txt", touched)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "NEW")

    def test_locked_changed_file_stops_update_before_any_change(self):
        handle = (self.installation / "payload.txt").open("rb")   # no delete sharing, like an editor
        try:
            self.execute(expected=2)
        finally:
            handle.close()
        journal = self.journal()
        self.assertEqual(journal["State"], "RolledBack")
        self.assertEqual(journal["Records"], [], "no file may be touched when the pre-change gate fails")
        self.assertIn("payload.txt", journal["Failure"])
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertFalse((self.installation / ".jmd-update" / "pending.json").exists())
        self.assertEqual(subprocess.run([str(self.gate), str(self.installation)], capture_output=True).returncode, 0)

    def test_briefly_locked_file_is_waited_for(self):
        handle = (self.installation / "payload.txt").open("rb")
        staged = self.transaction / "staged.json"

        def release_later():
            deadline = time.monotonic() + 20
            while not staged.exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            time.sleep(2.5)       # held across host exit and into the pre-change gate
            handle.close()
        releaser = threading.Thread(target=release_later)
        releaser.start()
        try:
            self.execute()
        finally:
            releaser.join()
            handle.close()
        self.assertEqual(self.journal()["State"], "Complete")
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "NEW")

    def test_dll_mapped_by_another_process_is_still_updated(self):
        system32 = Path(os.environ["WINDIR"]) / "System32"
        plugin = self.installation / "plugin.dll"
        shutil.copy2(system32 / "version.dll", plugin)
        shutil.copy2(system32 / "msimg32.dll", self.source / "plugin.dll")
        self.republish("3.0.0.0")
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.LoadLibraryExW.restype = ctypes.c_void_p
        kernel32.LoadLibraryExW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint32]
        kernel32.FreeLibrary.argtypes = [ctypes.c_void_p]
        # DONT_RESOLVE_DLL_REFERENCES maps the file as an executable image without running it, like another
        # program having it loaded. Whether Windows then accepts File.Replace depends on its cache state (the
        # field failure); either way the update must complete, through atomic replace or the rename fallback.
        module = kernel32.LoadLibraryExW(str(plugin), None, 0x1)
        self.assertTrue(module, "could not map test DLL")
        try:
            probe = self.case / "replace-probe.dll"
            shutil.copy2(system32 / "msimg32.dll", probe)
            with self.assertRaises(OSError, msg="the mapping must be effective: a plain overwrite is refused"):
                os.replace(probe, plugin)
            job = self.execute()
            self.assertEqual(self.journal()["State"], "Complete")
            self.assertEqual(plugin.read_bytes(), (system32 / "msimg32.dll").read_bytes())
        finally:
            kernel32.FreeLibrary(module)
        self.run_command("recover", job=job)
        self.assertEqual(list(self.installation.glob("plugin.dll.jmd-*")), [], "no renamed-aside copy or temporary remains")

    def test_cleanup_never_removes_preserved_or_signed_names(self):
        token = uuid.uuid4().hex
        stale = self.installation / ("payload.txt.jmd-old-" + token)          # a genuine leftover
        kept = self.installation / ("default.txt.jmd-old-" + token)           # derived from preserved data
        stale.write_text("STALE", encoding="utf-8")
        kept.write_text("USER", encoding="utf-8")
        self.execute()
        self.assertEqual(self.journal()["State"], "Complete")
        self.assertFalse(stale.exists(), "leftover program copy should be swept")
        self.assertTrue(kept.exists(), "a file under a preserved data path must never be swept")

    def test_recover_accepts_a_file_that_is_still_original_even_if_locked(self):
        # The field failure: rollback was asked to restore a file that never actually changed and
        # that some reader still held. Recovery must see it is already original and finish.
        job = self.write_interrupted_transaction("RollbackFailed", "OLD")
        with (self.installation / "payload.txt").open("rb"):
            self.run_command("recover", job=job)
        self.assertEqual(self.journal()["State"], "RolledBack")
        self.assertFalse((self.installation / ".jmd-update" / "pending.json").exists())

    def test_startup_check_recovers_and_reopens_automatically(self):
        self.auto_recovery_from_copy(False)

    def test_startup_check_recovers_with_missing_installed_updater(self):
        self.auto_recovery_from_copy(True)

    def auto_recovery_from_copy(self, missing):
        helper = self.installation / "JmdUpdater"
        helper.mkdir()
        shutil.copy2(self.updater, helper / "JmdUpdater.exe")
        (self.source / "JmdUpdater").mkdir()
        shutil.copy2(self.updater, self.source / "JmdUpdater" / "JmdUpdater.exe")
        self.republish("3.0")
        self.write_interrupted_transaction("RollbackFailed", "HALF-NEW")
        backup = self.transaction / "backup" / "JmdUpdater" / "JmdUpdater.exe"
        backup.parent.mkdir()
        backup.write_bytes(b"ORIGINAL-UPDATER")
        journal = self.journal()
        journal["Records"].append({"Path": "JmdUpdater/JmdUpdater.exe", "OriginallyExisted": True,
            "BackupReady": True, "Touched": True, "BackupSha256": hashlib.sha256(backup.read_bytes()).hexdigest()})
        (self.transaction / "journal.json").write_text(json.dumps(journal), encoding="utf-8")
        copy = self.transaction / "installer" / "JmdUpdater.exe"
        copy.parent.mkdir()
        shutil.copy2(self.updater, copy)
        # An invalid installed image proves the host selected the transaction copy.
        if missing:
            (helper / "JmdUpdater.exe").unlink()
        else:
            (helper / "JmdUpdater.exe").write_bytes(b"NOT-EXECUTABLE")
        # The recovery helper and the program it reopens inherit the gate's standard handles, so use
        # regular files rather than pipes (a pipe would only reach EOF when the reopened program exits).
        with (self.case / "gate.out.txt").open("wb") as gate_output:
            gate = subprocess.run([str(self.gate), str(self.installation)], stdout=gate_output, stderr=subprocess.STDOUT,
                                  env=dict(os.environ, JMD_UPDATER_NO_UI="1"), timeout=30)
        self.assertEqual(gate.returncode, 2, "this launch must not continue while recovery runs")
        pending = self.installation / ".jmd-update" / "pending.json"
        reopened = self.installation / "old.started.json"
        deadline = time.monotonic() + 20
        while (pending.exists() or not reopened.exists()) and time.monotonic() < deadline:
            time.sleep(0.1)
        self.assertFalse(pending.exists(), "automatic recovery did not finish")
        self.assertTrue(reopened.exists(), "the program was not reopened after recovery")
        self.assertEqual((helper / "JmdUpdater.exe").read_bytes(), b"ORIGINAL-UPDATER")
        reopened_pid = json.loads(reopened.read_text(encoding="utf-8-sig"))["Pid"]
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")
        self.assertEqual(self.journal()["State"], "RolledBack")
        self.assertEqual(subprocess.run([str(self.gate), str(self.installation)], capture_output=True).returncode, 0)
        # The reopened program is a plain launch; let it finish (it exits on fixture.exit, without an ack path).
        (self.installation / "fixture.exit").touch()
        handle = ctypes.windll.kernel32.OpenProcess(0x00100000, False, reopened_pid)
        if handle:
            try:
                ctypes.windll.kernel32.WaitForSingleObject(handle, 5000)
            finally:
                ctypes.windll.kernel32.CloseHandle(handle)

    def test_interrupted_applying_marker_blocks_launch_until_recovered(self):
        # This is the durable boundary immediately before the first replacement:
        # the installer can disappear without holding a named mutex afterwards.
        job = {"AppId": "process-fixture", "InstallDirectory": str(self.installation), "ExecutablePath": str(self.installation / "Fixture.exe"),
               "HostPid": 1, "HostStartUtcTicks": 1, "PrepareId": self.prepare, "SessionToken": self.session,
               "ShutdownAckPath": str(self.session_root / "shutdown.json"), "PackagePath": str(self.transaction / "package.zip"),
               "EnvelopePath": str(self.transaction / "release.json"), "ConfigPath": str(self.config), "TransactionDirectory": str(self.transaction)}
        job_path = self.transaction / "job.json"
        job_path.write_text(json.dumps(job), encoding="utf-8")
        (self.transaction / "journal.json").write_text(json.dumps({"PrepareId": self.prepare, "State": "Applying", "Records": []}), encoding="utf-8")
        metadata = self.installation / ".jmd-update"
        metadata.mkdir()
        (metadata / "pending.json").write_text(json.dumps({"PrepareId": self.prepare, "TransactionDirectory": str(self.transaction)}), encoding="utf-8")
        self.assertEqual(subprocess.run([str(self.gate), str(self.installation)], capture_output=True).returncode, 2)
        self.run_command("recover", job=job_path)
        self.assertFalse((metadata / "pending.json").exists())
        self.assertEqual(subprocess.run([str(self.gate), str(self.installation)], capture_output=True).returncode, 0)
        self.assertEqual((self.installation / "payload.txt").read_text(encoding="utf-8-sig"), "OLD")


if __name__ == "__main__":
    unittest.main(verbosity=2)
