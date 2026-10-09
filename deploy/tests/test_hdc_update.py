"""hdc update, run for real against a fake docker (and a fake sleep) on PATH: the steps it takes, in
order, and what it tells the owner. The compose and hdc integration test (hdc_integration.sh) runs it
against real Docker in CI."""
import json
import os
import shutil
import subprocess
import tarfile
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
DEPLOY = os.path.abspath(os.path.join(HERE, ".."))

# Stands in for docker. It logs every call to $FAKE_DIR/calls.jsonl and answers from $FAKE_DIR:
# world.json (the volume's world; absent = no world yet) and the FAKE_UPGRADE setting:
# ok | fail (before the swap: the world is unchanged) | fail-after-swap (the world was upgraded).
# The server is stopped, unless FAKE_RUNNING is set.
FAKE_DOCKER = r'''#!/usr/bin/env python3
import json, os, sys
d = os.environ["FAKE_DIR"]
args = sys.argv[1:]
with open(os.path.join(d, "calls.jsonl"), "a") as f:
    f.write(json.dumps(args) + "\n")
world = os.path.join(d, "world.json")
def set_world(version):
    with open(world, "w") as f:
        json.dump({"version": version, "edition": "classic", "navmesh": True}, f)
if args[:1] == ["inspect"]:
    fmt = args[args.index("-f") + 1]
    if "Restarting" in fmt:
        print("false 0 true 0 healthy")
    elif "Health" in fmt:
        print("healthy")
    else:
        print("true" if os.environ.get("FAKE_RUNNING") else "false")  # .State.Running
elif args[:1] == ["run"]:
    if "cat" in args and "/data/world.json" in args:
        if not os.path.exists(world):
            sys.exit(1)
        print(open(world).read())
    elif "backup.py" in " ".join(args):
        print("/data/backups/world-20261008-120000-pre-update.db")
    elif "fetch-clean" in args:
        print("Clean classic world for upstream 0.36b ready.")
    elif "upgrade-world" in args:
        mode = os.environ.get("FAKE_UPGRADE", "ok")
        if mode == "fail":
            print("ERROR: the import failed; the current world is unchanged. Details:\nboom", file=sys.stderr)
            sys.exit(1)
        set_world(json.load(open(os.path.join(d, "new-version.json")))["version"])
        if mode == "fail-after-swap":
            print("Traceback: spawns failed", file=sys.stderr)
            sys.exit(1)
        print("Upgraded to upstream 0.36b: {}. Previous world archived in /data/archive/world-pre-upgrade-x.")
        print("Upgrade report: /data/archive/world-pre-upgrade-x/upgrade-report.txt")
'''


class HdcUpdateTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        t = self.tmp.name
        self.here, self.fake, self.bin = (os.path.join(t, n) for n in ("deploy", "fake", "bin"))
        for d in (self.here, self.fake, self.bin):
            os.makedirs(d)
        shutil.copy(os.path.join(DEPLOY, "hdc"), self.here)
        shutil.copy(os.path.join(DEPLOY, "compose.yml"), self.here)
        with open(os.path.join(self.here, ".env"), "w") as f:
            f.write("HEARTHDAOC_TAG=v0.35b-hearth.3\nHEARTHDAOC_IMAGE=hdc-test\nHEARTHDAOC_PORT=10392\n")
        with open(os.path.join(self.bin, "docker"), "w") as f:
            f.write(FAKE_DOCKER)
        with open(os.path.join(self.bin, "sleep"), "w") as f:
            f.write("#!/bin/sh\nexit 0\n")
        for name in ("docker", "sleep"):
            os.chmod(os.path.join(self.bin, name), 0o755)

    def tearDown(self):
        self.tmp.cleanup()

    def world(self, version):
        with open(os.path.join(self.fake, "world.json"), "w") as f:
            json.dump({"version": version, "edition": "classic", "navmesh": True}, f)

    def bundle(self, tag, version):
        src = os.path.join(self.tmp.name, "bundle-src")
        os.makedirs(src)
        for name in ("hdc", "compose.yml", "HANDOFF.md", ".env.example"):
            shutil.copy(os.path.join(DEPLOY, name), src)
        with open(os.path.join(src, ".env.example"), "a") as f:
            f.write("HEARTHDAOC_NEW_SETTING=hello\n")
        with open(os.path.join(src, ".env.example")) as f:
            text = f.read().replace("HEARTHDAOC_TAG=\n", f"HEARTHDAOC_TAG={tag}\n")
        with open(os.path.join(src, ".env.example"), "w") as f:
            f.write(text)
        with open(os.path.join(src, "upstream.lock"), "w") as f:
            json.dump({"version": version}, f, indent=2)
        with open(os.path.join(self.fake, "new-version.json"), "w") as f:
            json.dump({"version": version}, f)
        path = os.path.join(self.tmp.name, f"hearthdaoc-deploy-{tag}.tar.gz")
        with tarfile.open(path, "w:gz") as tar:
            for name in os.listdir(src):
                tar.add(os.path.join(src, name), arcname=name)
        return path

    def update(self, bundle, upgrade="ok"):
        env = dict(os.environ, PATH=self.bin + os.pathsep + os.environ["PATH"], FAKE_DIR=self.fake, FAKE_UPGRADE=upgrade)
        return subprocess.run(["bash", os.path.join(self.here, "hdc"), "update", "--bundle", bundle],
                              capture_output=True, text=True, env=env, timeout=60)

    def calls(self):
        path = os.path.join(self.fake, "calls.jsonl")
        if not os.path.exists(path):
            return []
        with open(path) as f:
            return [json.loads(line) for line in f]

    def steps(self):
        """The calls that matter, in order: backup, compose stop/up, fetch-clean, upgrade-world."""
        out = []
        for args in self.calls():
            text = " ".join(args)
            if "backup.py" in text:
                out.append("backup")
            elif args[:1] == ["compose"] and ("stop" in args or "up" in args):
                out.append("compose " + ("stop" if "stop" in args else "up"))
            elif "fetch-clean" in args:
                out.append("fetch-clean")
            elif "upgrade-world" in args:
                out.append("upgrade-world")
        return out

    def env_value(self, key):
        with open(os.path.join(self.here, ".env")) as f:
            for line in f:
                if line.startswith(key + "="):
                    return line.strip().split("=", 1)[1]
        return None

    def test_same_upstream_version_installs_and_starts(self):
        self.world("0.35b")
        r = self.update(self.bundle("v0.35b-hearth.4", "0.35b"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(self.steps(), ["backup", "compose stop", "compose up"])
        self.assertIn("Updated to v0.35b-hearth.4.", r.stdout)
        self.assertEqual(self.env_value("HEARTHDAOC_TAG"), "v0.35b-hearth.4")
        self.assertEqual(self.env_value("HEARTHDAOC_NEW_SETTING"), "hello")
        self.assertEqual(self.env_value("HEARTHDAOC_PORT"), "10392")

    def test_another_upstream_version_upgrades_the_world_then_starts(self):
        self.world("0.35b")
        r = self.update(self.bundle("v0.36b-hearth.1", "0.36b"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        # The pre-update backup first, then the steps of ./hdc upgrade-world, then the start.
        self.assertEqual(self.steps(), ["backup", "compose stop", "fetch-clean", "upgrade-world", "compose up"])
        upgrade = next(a for a in self.calls() if "upgrade-world" in a)
        self.assertIn("--network", upgrade)
        self.assertEqual(upgrade[upgrade.index("--network") + 1], "none")  # the import runs without a network
        self.assertIn("Updated to v0.36b-hearth.1.", r.stdout)
        self.assertIn("Upgrade report (what carried over, and server settings to re-check): docker exec hearthdaoc-server "
                      "cat /data/archive/world-pre-upgrade-x/upgrade-report.txt", r.stdout)
        self.assertIn("navmeshes", r.stdout)

    def test_failed_upgrade_leaves_the_server_stopped_and_says_how_to_retry_or_go_back(self):
        self.world("0.35b")
        r = self.update(self.bundle("v0.36b-hearth.1", "0.36b"), upgrade="fail")
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(self.steps(), ["backup", "compose stop", "fetch-clean", "upgrade-world"])  # never started
        self.assertIn("The world is as it was", r.stderr)
        self.assertIn("./hdc upgrade-world", r.stderr)
        self.assertIn("./hdc update v0.35b-hearth.3", r.stderr)  # the release that was installed before
        self.assertIn("the import failed", r.stdout)  # upgrade-world's own error is shown
        self.assertNotIn("Updated to", r.stdout)

    def test_failure_after_the_world_was_upgraded_says_so(self):
        self.world("0.35b")
        r = self.update(self.bundle("v0.36b-hearth.1", "0.36b"), upgrade="fail-after-swap")
        self.assertNotEqual(r.returncode, 0)
        self.assertNotIn("compose up", self.steps())
        self.assertIn("The world was upgraded", r.stderr)
        self.assertIn("./hdc up", r.stderr)
        self.assertNotIn("as it was", r.stderr)

    def test_no_world_yet_installs_and_starts(self):
        r = self.update(self.bundle("v0.36b-hearth.1", "0.36b"))
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertNotIn("upgrade-world", self.steps())
        self.assertEqual(self.steps()[-1], "compose up")
        self.assertIn("Updated to v0.36b-hearth.1.", r.stdout)


class HdcCarryRvrTests(unittest.TestCase):
    """hdc carry-rvr lists the archived worlds whether the server runs or not; it carries one only with the
    server stopped, in a container without a network, like the other world-changing commands."""

    setUp, tearDown, calls = HdcUpdateTests.setUp, HdcUpdateTests.tearDown, HdcUpdateTests.calls

    def hdc(self, *args, running=False):
        env = dict(os.environ, PATH=self.bin + os.pathsep + os.environ["PATH"], FAKE_DIR=self.fake)
        if running:
            env["FAKE_RUNNING"] = "1"
        return subprocess.run(["bash", os.path.join(self.here, "hdc"), *args], capture_output=True, text=True,
                              env=env, timeout=60)

    def admin_call(self):
        return next(a for a in self.calls() if "/app/bin/world_admin.py" in a)

    def test_without_an_archive_it_lists_them_in_a_container_when_stopped(self):
        r = self.hdc("carry-rvr")
        self.assertEqual(r.returncode, 0, r.stderr)
        call = self.admin_call()
        self.assertEqual(call[:1], ["run"])
        self.assertEqual(call[call.index("--network") + 1], "none")
        self.assertEqual(call[-1], "carry-rvr")

    def test_without_an_archive_it_lists_them_in_the_running_server(self):
        r = self.hdc("carry-rvr", running=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        call = self.admin_call()
        self.assertEqual(call[:3], ["exec", "-i", "hearthdaoc-server"])
        self.assertEqual(call[-1], "carry-rvr")

    def test_with_an_archive_it_carries_in_a_container_without_a_network(self):
        r = self.hdc("carry-rvr", "world-pre-upgrade-20261008-120000")
        self.assertEqual(r.returncode, 0, r.stderr)
        call = self.admin_call()
        self.assertEqual(call[:1], ["run"])
        self.assertEqual(call[call.index("--network") + 1], "none")
        self.assertEqual(call[-2:], ["carry-rvr", "world-pre-upgrade-20261008-120000"])

    def test_with_an_archive_it_refuses_while_the_server_runs(self):
        r = self.hdc("carry-rvr", "world-pre-upgrade-20261008-120000", running=True)
        self.assertEqual(r.returncode, 1)
        self.assertIn("Stop it first", r.stderr)
        self.assertEqual([a for a in self.calls() if "/app/bin/world_admin.py" in a], [])

    def test_the_usage_names_it(self):
        self.assertIn("carry-rvr [<archive>]", self.hdc("help").stdout)


if __name__ == "__main__":
    unittest.main()
