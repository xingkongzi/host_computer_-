"""Windows signing release gates; no private keys, trust-store writes or signing requests."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1]


class SigningGates(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="JmdSigningGate-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.stage = self.root / "stage"
        self.stage.mkdir()
        # A malformed unsigned executable is sufficient to exercise the refusal gate.
        (self.stage / "app.exe").write_bytes(b"MZ-not-a-signed-program")
        self.policy = self.root / "policy.json"
        self.settings = {
            "CertificateThumbprint": "A" * 40,
            "StoreLocation": "CurrentUser",
            "RequireTimestamp": False,
            "TimestampUrl": "",
            "Files": ["app.exe"],
        }

    def run_script(self, name, *args):
        return subprocess.run(
            ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             str(MODULE / "Delivery" / name), *map(str, args)],
            capture_output=True, timeout=30,
        )

    def sign_refused(self, expected):
        self.policy.write_text(json.dumps(self.settings), encoding="utf-8")
        result = self.run_script("Sign-Release.ps1", "-SourceDirectory", self.stage,
                                 "-SigningConfig", self.policy, "-VerifyOnly")
        self.assertNotEqual(0, result.returncode)
        self.assertIn(expected.encode(), result.stdout + result.stderr)
        self.assertEqual(b"MZ-not-a-signed-program", (self.stage / "app.exe").read_bytes())

    def test_traversal(self):
        self.settings["Files"] = ["../app.exe"]
        self.sign_refused("Unsafe signing path")

    def test_missing_file(self):
        self.settings["Files"] = ["missing.exe"]
        self.sign_refused("Signing target is not an existing EXE/DLL")

    def test_unsigned_rejected(self):
        self.sign_refused("Authenticode verification failed")

    def test_certificate_fingerprint_required(self):
        self.settings["CertificateThumbprint"] = ""
        self.sign_refused("exact code-signing certificate thumbprint")

    def test_local_only_policy_cannot_send_timestamp_request(self):
        self.settings["TimestampUrl"] = "http://timestamp.example.invalid"
        self.sign_refused("Local-only signing requires an empty TimestampUrl")

    def test_publish_does_not_advertise_on_signing_failure(self):
        self.policy.write_text(json.dumps(self.settings), encoding="utf-8")
        releases = self.root / "releases"
        result = self.run_script("Publish-Release.ps1", "-SourceDirectory", self.stage,
                                 "-ClientConfig", self.root / "unused.json", "-EntryPoint", "app.exe",
                                 "-Version", "1.0.0.1", "-SigningConfig", self.policy,
                                 "-UseExistingSignatures", "-ReleaseRoot", releases)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Missing required signing target", result.stdout + result.stderr)
        self.assertFalse(releases.exists())

    def test_trust_requires_matching_public_certificate_hash(self):
        result = self.run_script("Install-InternalPublisherTrust.ps1", "-CertificatePath",
                                 self.stage / "app.exe", "-ExpectedSha256", "0" * 64, "-WhatIf")
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Public certificate SHA-256 does not match", result.stdout + result.stderr)

    def test_publish_rejects_existing_but_unsigned_required_files(self):
        helper = self.stage / "JmdUpdater"
        helper.mkdir()
        (helper / "JmdUpdater.exe").write_bytes(b"MZ-unsigned-helper")
        self.settings["Files"].append("JmdUpdater/JmdUpdater.exe")
        self.policy.write_text(json.dumps(self.settings), encoding="utf-8")
        releases = self.root / "releases"
        result = self.run_script("Publish-Release.ps1", "-SourceDirectory", self.stage,
                                 "-ClientConfig", self.root / "unused.json", "-EntryPoint", "app.exe",
                                 "-Version", "1.0.0.1", "-SigningConfig", self.policy,
                                 "-UseExistingSignatures", "-ReleaseRoot", releases)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(b"Authenticode verification failed", result.stdout + result.stderr)
        self.assertFalse(releases.exists())


if __name__ == "__main__":
    unittest.main()
