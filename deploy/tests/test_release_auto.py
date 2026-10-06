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
                     "deploy/upstream.lock", "deploy/.env.example", "client/linux/setup.sh", "tools/linux/odaoc_fetch.py"):
            with self.subTest(path=path):
                self.assertTrue(rt.release_worthy([path]))

    def test_docs_and_tests_alone_do_not(self):
        self.assertFalse(rt.release_worthy(["docs/fork/FORK.md", "README.md", "deploy/tests/test_x.py",
                                            "client/tests/test_play.py", "tools/linux/tests/t.py",
                                            "source/server/Tests/UnitTests/UT_X.cs", ".github/README.md"]))


class PendingTests(unittest.TestCase):
    def test_docs_naming_an_unpublished_tag_mean_a_release_is_pending(self):
        self.assertEqual(rt.pending_release("v0.34b-hearth.4", ["v0.34b-hearth.3"]), "v0.34b-hearth.4")

    def test_published_tag_is_not_pending(self):
        self.assertIsNone(rt.pending_release("v0.34b-hearth.3", ["v0.34b-hearth.3"]))


class ChangelogTests(unittest.TestCase):
    def test_entry_lists_changes_and_skips_merges(self):
        entry = rt.changelog_entry("v0.34b-hearth.4", "2026-10-06", [
            "fix(deploy): safer restore (#11)", "Merge pull request #56 from lometur/x", "feat: GM-only teleports",
            "chore(release): v0.34b-hearth.4"])
        self.assertIn("## v0.34b-hearth.4 (2026-10-06)", entry)
        self.assertIn("- fix(deploy): safer restore (#11)", entry)
        self.assertIn("- feat: GM-only teleports", entry)
        self.assertNotIn("Merge pull request", entry)
        self.assertNotIn("chore(release)", entry)

    def test_prepend_keeps_older_entries_and_replaces_a_pending_one(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "CHANGELOG.md")
            rt.prepend_changelog(path, "## v0.34b-hearth.4 (x)\n\n- a\n")
            rt.prepend_changelog(path, "## v0.34b-hearth.5 (y)\n\n- b\n")
            rt.prepend_changelog(path, "## v0.34b-hearth.5 (z)\n\n- b\n- c\n")  # release PR updated
            text = open(path).read()
        self.assertEqual(text.count("## v0.34b-hearth.5"), 1)
        self.assertIn("- c", text)
        self.assertLess(text.index("hearth.5"), text.index("hearth.4"))


class NotesTests(unittest.TestCase):
    def test_notes_are_the_changelog_entry_for_the_tag(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "CHANGELOG.md")
            rt.prepend_changelog(path, "## v0.34b-hearth.4 (x)\n\n- a\n")
            rt.prepend_changelog(path, "## v0.34b-hearth.5 (y)\n\n- b\n- c\n")
            self.assertEqual(rt.notes(path, "v0.34b-hearth.5"), "- b\n- c")
            self.assertEqual(rt.notes(path, "v0.34b-hearth.4"), "- a")
            self.assertEqual(rt.notes(path, "v0.34b-hearth.9"), "")


class GitPlanTests(unittest.TestCase):
    """plan/prepare against a throwaway git repository."""

    def setUp(self):
        import subprocess
        self.sp = subprocess
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        os.makedirs(os.path.join(self.root, "deploy"))
        os.makedirs(os.path.join(self.root, "docs", "fork"))
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@example.com")
        self.git("config", "user.name", "t")
        with open(os.path.join(self.root, "deploy", "upstream.lock"), "w") as f:
            json.dump({"version": "0.34b"}, f)
        self.docs("v0.34b-hearth.3")
        self.commit("feat: first", "deploy/hdc")
        self.git("tag", "v0.34b-hearth.3")

    def tearDown(self):
        self.tmp.cleanup()

    def git(self, *args):
        return self.sp.run(["git", "-C", self.root, *args], check=True, capture_output=True, text=True).stdout

    def docs(self, tag):
        with open(os.path.join(self.root, "deploy", ".env.example"), "w") as f:
            f.write(f"HEARTHDAOC_TAG={tag}\n")
        with open(os.path.join(self.root, "deploy", "HANDOFF.md"), "w") as f:
            f.write(f"curl -fLO https://example/{tag}/hearthdaoc-deploy-{tag}.tar.gz\n")

    def commit(self, subject, path):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "a") as f:
            f.write(subject + "\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", subject)

    def test_docs_only_merge_needs_no_release(self):
        self.commit("docs: typo", "docs/fork/FORK.md")
        self.assertEqual(rt.plan(self.root), ("none", ""))

    def test_code_merge_asks_for_the_next_release(self):
        self.commit("fix(deploy): something", "deploy/bin/backup.py")
        self.assertEqual(rt.plan(self.root), ("release-pr", "v0.34b-hearth.4"))

    def test_merged_release_pr_is_pending_until_tagged(self):
        self.commit("fix: a", "deploy/hdc")
        rt.prepare(self.root, "v0.34b-hearth.4")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "chore(release): v0.34b-hearth.4")
        self.assertEqual(rt.plan(self.root), ("pending", "v0.34b-hearth.4"))
        self.git("tag", "v0.34b-hearth.4")
        self.assertEqual(rt.plan(self.root), ("none", ""))

    def test_prepare_bumps_docs_and_writes_the_changelog(self):
        self.commit("fix(deploy): restore checks the edition", "deploy/bin/world_admin.py")
        self.commit("docs: notes", "docs/fork/FORK.md")
        rt.prepare(self.root, "v0.34b-hearth.4")
        self.assertEqual(rt.check(self.root, "v0.34b-hearth.4"), [])
        text = open(os.path.join(self.root, rt.CHANGELOG)).read()
        self.assertIn("## v0.34b-hearth.4", text)
        self.assertIn("- fix(deploy): restore checks the edition", text)
        self.assertNotIn("feat: first", text)  # before the last release

    def test_new_upstream_version_starts_at_one(self):
        with open(os.path.join(self.root, "deploy", "upstream.lock"), "w") as f:
            json.dump({"version": "0.35b"}, f)
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "chore: sync upstream 0.35b")
        self.assertEqual(rt.plan(self.root), ("release-pr", "v0.35b-hearth.1"))


if __name__ == "__main__":
    unittest.main()
