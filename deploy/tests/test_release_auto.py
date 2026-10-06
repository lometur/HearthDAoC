import json
import os
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, ".."))

import release_tag as rt  # noqa: E402


class NextTagTests(unittest.TestCase):
    def test_next_number_on_the_same_upstream(self):
        self.assertEqual(rt.next_tag("0.34b", ["v0.34b-hearth.1", "v0.34b-hearth.3", "v0.34b-hearth.2"]), "v0.34b-hearth.4")

    def test_new_upstream_restarts_at_one(self):
        self.assertEqual(rt.next_tag("0.35b", ["v0.34b-hearth.3"]), "v0.35b-hearth.1")

    def test_first_release(self):
        self.assertEqual(rt.next_tag("0.34b", []), "v0.34b-hearth.1")

    def test_numbers_compare_as_numbers(self):
        self.assertEqual(rt.next_tag("0.34b", ["v0.34b-hearth.9", "v0.34b-hearth.10"]), "v0.34b-hearth.11")

    def test_latest_release(self):
        tags = ["v0.34b-hearth.2", "v0.34b-hearth.10", "v0.33-hearth.7", "not-a-release"]
        self.assertEqual(rt.latest_release(tags, "0.34b"), "v0.34b-hearth.10")
        self.assertIsNone(rt.latest_release([], "0.34b"))


class ReleaseWorthyTests(unittest.TestCase):
    def test_code_and_deploy_files_need_a_release(self):
        for path in ("source/server/GameServer/x.cs", "deploy/hdc", "deploy/bin/backup.py", "deploy/Dockerfile",
                     "deploy/upstream.lock", "deploy/.env.example", "deploy/HANDOFF.md", "client/README.md",
                     "client/linux/setup.sh", "tools/linux/odaoc_fetch.py", ".dockerignore",
                     "source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs",
                     "source/tools/OfflineDaoc.ProgressImport/Program.cs"):
            with self.subTest(path=path):
                self.assertTrue(rt.release_worthy([path]))

    def test_docs_tests_and_unshipped_sources_alone_do_not(self):
        for path in ("docs/fork/FORK.md", "README.md", ".github/README.md", ".github/workflows/server-image.yml",
                     "deploy/tests/test_x.py", "client/tests/test_play.py", "client/patches/tests/test_patchset.py",
                     "tools/linux/tests/t.py", "source/server/Tests/UnitTests/UT_X.cs", "deploy/release_tag.py",
                     "source/server/CLAUDE.md", "source/server/docs/reports/x.md", "source/reference/daocportal/x",
                     "source/development-tools/OpenDAoC-Core/x.cs", "source/tools/OfflineDaoc.Launcher/MainForm.cs"):
            with self.subTest(path=path):
                self.assertFalse(rt.release_worthy([path]))


class NextReleaseTests(unittest.TestCase):
    """next_release against a throwaway git repository: the tag a main build publishes, or ''."""

    def setUp(self):
        import subprocess
        self.sp = subprocess
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@example.com")
        self.git("config", "user.name", "t")
        self.lock("0.34b")
        self.commit("feat: first", "deploy/hdc")
        self.git("tag", "v0.34b-hearth.3")

    def tearDown(self):
        self.tmp.cleanup()

    def git(self, *args):
        return self.sp.run(["git", "-C", self.root, *args], check=True, capture_output=True, text=True).stdout

    def lock(self, version):
        os.makedirs(os.path.join(self.root, "deploy"), exist_ok=True)
        with open(os.path.join(self.root, "deploy", "upstream.lock"), "w") as f:
            json.dump({"version": version}, f)

    def commit(self, subject, path):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "a") as f:
            f.write(subject + "\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", subject)

    def test_nothing_new_since_the_last_release(self):
        self.assertEqual(rt.next_release(self.root), "")

    def test_docs_tests_and_ci_alone_make_no_release(self):
        self.commit("docs: typo", "docs/fork/FORK.md")
        self.commit("test: more", "deploy/tests/test_x.py")
        self.commit("ci: tweak", ".github/workflows/server-image.yml")
        self.commit("chore: release tooling", "deploy/release_tag.py")
        self.assertEqual(rt.next_release(self.root), "")

    def test_shipped_change_gets_the_next_number(self):
        self.commit("fix(deploy): something", "deploy/bin/backup.py")
        self.assertEqual(rt.next_release(self.root), "v0.34b-hearth.4")

    def test_released_once_tagged(self):
        self.commit("fix: a", "deploy/hdc")
        self.git("tag", "v0.34b-hearth.4")
        self.assertEqual(rt.next_release(self.root), "")

    def test_moving_a_shipped_file_into_tests_is_a_change(self):
        self.commit("feat: helper", "deploy/bin/helper.py")
        self.git("tag", "v0.34b-hearth.4")
        os.makedirs(os.path.join(self.root, "deploy", "tests"))
        self.git("mv", "deploy/bin/helper.py", "deploy/tests/helper.py")
        self.git("commit", "-q", "-m", "test: move the helper")
        self.assertEqual(rt.next_release(self.root), "v0.34b-hearth.5")

    def test_an_older_commit_than_a_release_publishes_nothing(self):
        # A re-run of an old run on main must not publish older code as the newest release.
        self.commit("fix: a", "deploy/hdc")
        old = self.git("rev-parse", "HEAD").strip()
        self.commit("fix: b", "deploy/hdc")
        self.git("tag", "v0.34b-hearth.4")
        self.git("checkout", "-q", old)
        self.assertEqual(rt.next_release(self.root), "")

    def test_new_upstream_version_starts_at_one(self):
        self.lock("0.35b")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "chore: sync upstream 0.35b")
        self.assertEqual(rt.next_release(self.root), "v0.35b-hearth.1")

    def test_first_release_without_any_tag(self):
        self.git("tag", "-d", "v0.34b-hearth.3")
        self.assertEqual(rt.next_release(self.root), "v0.34b-hearth.1")

    def test_cli_prints_the_tag_or_an_empty_line(self):
        run = lambda: self.sp.run([sys.executable, os.path.join(HERE, "..", "release_tag.py"), "--root", self.root, "next"],  # noqa: E731
                                  capture_output=True, text=True)
        r = run()
        self.assertEqual((r.returncode, r.stdout), (0, "\n"))
        self.commit("fix: b", "client/linux/setup.sh")
        r = run()
        self.assertEqual((r.returncode, r.stdout), (0, "v0.34b-hearth.4\n"))


if __name__ == "__main__":
    unittest.main()
