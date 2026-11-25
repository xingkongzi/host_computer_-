"""Read-only first-use validation; never requests elevation, changes trust or launches a product."""
import argparse
import hashlib
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--package", required=True)
parser.add_argument("--certificate-sha256", required=True)
parser.add_argument("--thumbprint", required=True)
options, remaining = parser.parse_known_args()


class FirstRunValidation(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="JMD first use ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.setup = self.root / "JmdUpdater/FirstRun"
        self.setup.mkdir(parents=True)
        source = Path(options.package)
        for name in ("SynJMD_Monitor.exe", "JmdUpdater/JmdUpdater.exe", "首次运行.cmd",
                     "JmdUpdater/FirstRun/SynJMD-Internal-CodeSigning.cer"):
            shutil.copy2(source / name, self.root / name)
        for name in ("First-Run.ps1", "Install-InternalPublisherTrust.ps1"):
            self.assertEqual((MODULE / "Delivery" / name).read_bytes(),
                             (source / "JmdUpdater/FirstRun" / name).read_bytes())
            shutil.copy2(source / "JmdUpdater/FirstRun" / name, self.setup / name)

    def check(self, entry="SynJMD_Monitor.exe", fingerprint=None):
        return subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             str(self.setup / "First-Run.ps1"), "-EntryPoint", entry,
             "-CertificateSha256", fingerprint or options.certificate_sha256,
             "-CertificateThumbprint", options.thumbprint, "-CheckOnly"],
            capture_output=True, timeout=30,
        )

    def test_complete_signed_package(self):
        before = hashlib.sha256((self.root / "SynJMD_Monitor.exe").read_bytes()).digest()
        result = self.check()
        self.assertEqual(0, result.returncode, (result.stdout, result.stderr))
        self.assertIn(b"Package preflight passed", result.stdout)
        self.assertEqual(before, hashlib.sha256((self.root / "SynJMD_Monitor.exe").read_bytes()).digest())

    def test_wrong_certificate_hash_stops(self):
        result = self.check(fingerprint="0" * 64)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Public certificate does not match", result.stdout)

    def test_path_escape_stops(self):
        result = self.check(entry="../SynJMD_Monitor.exe")
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Invalid product entry point", result.stdout)

    def test_missing_updater_stops(self):
        (self.root / "JmdUpdater/JmdUpdater.exe").unlink()
        result = self.check()
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Missing package file", result.stdout)

    def test_certificate_and_pe_signer_mismatch_stops(self):
        # Keep DER structure/usage intact but change its signature byte: no private key or trust mutation.
        certificate = self.setup / "SynJMD-Internal-CodeSigning.cer"
        contents = bytearray(certificate.read_bytes())
        contents[-1] ^= 1
        certificate.write_bytes(contents)
        result = self.check(fingerprint=hashlib.sha256(contents).hexdigest())
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"The public certificate and product signer do not match", result.stdout)

    def test_modified_product_stops(self):
        executable = self.root / "SynJMD_Monitor.exe"
        contents = bytearray(executable.read_bytes())
        contents[1024] ^= 1
        executable.write_bytes(contents)
        result = self.check()
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Product signature is missing, changed or from another publisher", result.stdout)

    def test_missing_script_cmd_reports_failure(self):
        # PowerShell -File returns a negative exit code for a missing script; CMD must retain it as failure.
        (self.setup / "First-Run.ps1").unlink()
        result = subprocess.run(["cmd.exe", "/d", "/c", str(self.root / "首次运行.cmd")],
                                input=b"\r\n", capture_output=True, timeout=30)
        self.assertEqual(1, result.returncode, (result.stdout, result.stderr))


if __name__ == "__main__":
    unittest.main(argv=[__file__, *remaining])
