#!/usr/bin/env python3
"""Offline self-update acceptance tests: no root, network or real systemd needed."""
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = (ROOT / "distribution/update-node-agent.sh").read_text()
VERSION = "9.8.7"
ARCHIVE = f"llmproxy-node-agent-{VERSION}-linux-arm64.tar.gz"


class AgentUpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="llmproxy-self-update-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.state = self.root / "state"
        self.opt = self.root / "opt"
        self.env_file = self.root / "node-agent.env"
        self.mock_bin = self.root / "bin"
        self.mock_bin.mkdir()
        self.opt.mkdir()
        (self.opt / "marker").write_text("original binary\n")
        self.env_file.write_text("NodeAgent__BearerToken=" + "a" * 48 + "\n")
        self.mock_archive = self.root / ARCHIVE
        with tarfile.open(self.mock_archive, "w:gz") as tar:
            for name, payload in (
                ("LlmProxy.NodeAgent", b"#!/bin/sh\nexit 0\n"),
                ("update-node-agent.sh", b"#!/bin/sh\nexit 0\n"),
                ("marker", b"new binary\n"),
            ):
                item = tarfile.TarInfo(name)
                item.mode = 0o755 if name.endswith(".sh") or name == "LlmProxy.NodeAgent" else 0o644
                item.size = len(payload)
                tar.addfile(item, io.BytesIO(payload))
        digest = hashlib.sha256(self.mock_archive.read_bytes()).hexdigest()
        (self.root / f"{ARCHIVE}.sha256").write_text(f"{digest}  dist/{ARCHIVE}\n")
        source = SOURCE.replace('$(id -u)', '0')
        source = source.replace('$(uname -m)', 'printf aarch64')
        source = source.replace('/var/lib/llmproxy-node-agent', str(self.state))
        source = source.replace('/opt/llmproxy-node-agent', str(self.opt))
        source = source.replace('/etc/llmproxy/node-agent.env', str(self.env_file))
        source = source.replace('seq 1 60', 'seq 1 2').replace('sleep 2', 'sleep 0.01')
        self.test_script = self.root / "update.sh"
        self.test_script.write_text(source)

        curl = self.mock_bin / "curl"
        curl.write_text("#!" + sys.executable + "\n" + r'''
import os, pathlib, shutil, sys
args = sys.argv[1:]
root = pathlib.Path(os.environ["MOCK_UPDATE_ROOT"])
if any(arg.endswith("I") and arg.startswith("-") for arg in args) or "-fsSIL" in args:
    print("content-length: " + str((root / os.environ["MOCK_ARCHIVE"]).stat().st_size))
elif "--write-out" in args:
    target = pathlib.Path(args[args.index("--output") + 1])
    value = "0.0.0" if os.environ.get("MOCK_WRONG_VERSION") else os.environ["MOCK_VERSION"]
    target.write_text('{"status":"ok","service":"llmproxy-node-agent","version":"' + value + '"}')
    print("200", end="")
else:
    output = pathlib.Path(args[args.index("-o") + 1])
    output.parent.mkdir(parents=True, exist_ok=True)
    filename = output.name
    if os.environ.get("MOCK_CORRUPT_ARCHIVE") and filename.endswith(".tar.gz"):
        output.write_bytes(b"broken archive")
    else:
        shutil.copyfile(root / filename, output)
''')
        curl.chmod(0o755)
        systemctl = self.mock_bin / "systemctl"
        systemctl.write_text("#!" + sys.executable + "\n" + r'''
import os, pathlib, sys
args = sys.argv[1:]
root = pathlib.Path(os.environ["MOCK_UPDATE_ROOT"])
with (root / "systemctl.log").open("a") as stream:
    stream.write(" ".join(args) + "\n")
if "start" in args and (root / "fail-start-once").exists():
    (root / "fail-start-once").unlink()
    sys.exit(1)
sys.exit(0)
''')
        systemctl.chmod(0o755)

    def run_update(self, **extra):
        env = os.environ | {
            "PATH": str(self.mock_bin) + os.pathsep + os.environ["PATH"],
            "MOCK_UPDATE_ROOT": str(self.root),
            "MOCK_ARCHIVE": ARCHIVE,
            "MOCK_VERSION": VERSION,
        } | extra
        return subprocess.run(["bash", str(self.test_script), VERSION],
                              cwd=self.root, capture_output=True, text=True,
                              env=env, timeout=30)

    def state_json(self):
        return json.loads((self.state / "update-status.json").read_text())

    def test_success_swaps_binary_only_after_authenticated_version_health(self):
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.state_json()["status"], "succeeded")
        self.assertEqual(self.state_json()["version"], VERSION)
        self.assertEqual((self.opt / "marker").read_text(), "new binary\n")

    def test_failed_service_start_rolls_back_and_keeps_failure_phase(self):
        (self.root / "fail-start-once").touch()
        result = self.run_update()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.opt / "marker").read_text(), "original binary\n")
        self.assertEqual(self.state_json()["stage"], "healthcheck")
        self.assertEqual(self.state_json()["errorCode"], "service_start_failed")
        self.assertIn("start llmproxy-node-agent", (self.root / "systemctl.log").read_text())

    def test_wrong_health_version_never_marks_update_successful(self):
        result = self.run_update(MOCK_WRONG_VERSION="true")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.opt / "marker").read_text(), "original binary\n")
        self.assertEqual(self.state_json()["errorCode"], "healthcheck_failed")

    def test_bad_checksum_fails_before_stopping_working_agent(self):
        result = self.run_update(MOCK_CORRUPT_ARCHIVE="true")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state_json()["stage"], "verifying")
        self.assertEqual(self.state_json()["errorCode"], "checksum_mismatch")
        self.assertFalse((self.root / "systemctl.log").exists())
        self.assertEqual((self.opt / "marker").read_text(), "original binary\n")

    def test_orphaned_staging_from_old_attempt_does_not_block_retry(self):
        stale = self.root / "opt.upgrade"
        stale.mkdir()
        (stale / "garbage").write_text("old")
        result = self.run_update()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(stale.exists())
        self.assertEqual((self.opt / "marker").read_text(), "new binary\n")


if __name__ == "__main__":
    unittest.main()
