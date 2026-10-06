import os
import re
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
WORKFLOWS = os.path.join(ROOT, ".github", "workflows")


def read(name):
    with open(os.path.join(WORKFLOWS, name), encoding="utf-8") as f:
        return f.read()


def job(text, name):
    """The text of one job in a workflow (up to the next job)."""
    body = text.split("\njobs:\n", 1)[1]
    rest = body[re.search(rf"(?m)^  {re.escape(name)}:\s*$", body).end():]
    following = re.search(r"(?m)^  [a-z-]+:\s*$", rest)
    return rest[:following.start()] if following else rest


class MergeIsReleaseTests(unittest.TestCase):
    """Merging to main is the release: one workflow tests every PR and, on main, publishes the next tag
    from the same run when shipped files changed (deploy/release_tag.py next decides). No release PR, no
    bot pushes, no tag-push builds."""

    def setUp(self):
        self.text = read("server-image.yml")
        self.on = self.text.split("\non:", 1)[1].split("\nconcurrency:", 1)[0]

    def test_no_release_pr_workflow(self):
        self.assertFalse(os.path.exists(os.path.join(WORKFLOWS, "release-pr.yml")))

    def test_triggers(self):
        self.assertRegex(self.on, r"push:\s*\n\s+branches:\s*\[\s*main\s*\]")
        self.assertIn("pull_request:", self.on)
        self.assertIn("workflow_dispatch:", self.on)
        self.assertNotIn("tags:", self.on)
        self.assertNotIn("workflow_run", self.text)

    def test_main_runs_queue_and_pr_runs_cancel(self):
        self.assertRegex(self.text, r"(?m)^concurrency:\s*\n\s+group:\s*server-image-\$\{\{ github.ref \}\}")
        self.assertRegex(self.text, r"cancel-in-progress:\s*\$\{\{ github.event_name == 'pull_request' \}\}")

    def test_only_main_computes_a_release(self):
        build = job(self.text, "test-build-publish")
        self.assertRegex(build, r'refs/heads/main\b[\s\S]*release_tag.py next')
        self.assertIn("HOLD_RELEASES", build)

    def test_publishes_only_the_release_it_computed(self):
        build = job(self.text, "test-build-publish")
        self.assertIn("release: ${{ steps.rel.outputs.tag }}", build)
        self.assertIn("if: steps.rel.outputs.tag != ''", build[build.index("Publish image"):])
        self.assertIn("if: needs.test-build-publish.outputs.release != ''", job(self.text, "release-assets"))

    def test_bundles_are_built_before_the_release_is_created(self):
        assets = job(self.text, "release-assets")
        self.assertLess(assets.index('deploy/build_bundles.sh "$TAG" dist'), assets.index("gh release create"))

    def test_release_is_tagged_on_this_commit_with_both_bundles(self):
        assets = job(self.text, "release-assets")
        self.assertIn('--target "$GITHUB_SHA"', assets)
        self.assertIn("--generate-notes", assets)
        self.assertIn('"dist/hearthdaoc-deploy-$TAG.tar.gz"', assets)
        self.assertIn('"dist/hearthdaoc-client-$TAG.zip"', assets)

    def test_only_the_release_job_can_write_contents(self):
        self.assertEqual(self.text.count("contents: write"), 1)
        self.assertIn("contents: write", job(self.text, "release-assets"))


if __name__ == "__main__":
    unittest.main()
