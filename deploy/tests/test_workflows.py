import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
WORKFLOWS = os.path.join(ROOT, ".github", "workflows")
sys.path.insert(0, os.path.join(ROOT, "tools", "linux", "tests"))
import release_fixture as fx  # noqa: E402


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


# ClientPatchWorkflowTests read the workflow as data: ruby's YAML to JSON, since Python's standard library
# has no YAML reader. GitHub's Ubuntu runners have ruby; without it these tests are skipped.
RUBY = shutil.which("ruby")
YAML_TO_JSON = "require 'yaml'; require 'json'; puts JSON.generate(YAML.safe_load(File.read(ARGV[0])))"
PATCH_SET = os.path.join(ROOT, "client", "patches", "classic-creation.json")
TEST_JOB, RELEASE_JOB = "test-build-publish", "release-assets"
MPK_PROJECT = "source/tools/OfflineDaoc.Mpk/OfflineDaoc.Mpk.csproj"
MPK_TOOL = "source/tools/OfflineDaoc.Mpk/bin/Release/net10.0/OfflineDaoc.Mpk.dll"
PATCH_TESTS = "python3 -m unittest discover -s client/patches/tests -t client/patches -v"
APP = "runtime/client-opendaoc/app/"


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def listing(folder):
    """Every file below folder, as sorted relative paths with forward slashes."""
    return sorted(os.path.relpath(os.path.join(d, n), folder).replace(os.sep, "/")
                  for d, _, names in os.walk(folder) for n in names)


@unittest.skipUnless(RUBY, "needs ruby to read the workflow's YAML")
class ClientPatchWorkflowTests(unittest.TestCase):
    """The client patch tests get the real classic client files, nasm, upstream's MPK tool and pwsh. The
    fetched EA files stay outside the checkout and are never published. The release job needs no .NET: the
    client bundle carries the committed splash.mpk. Its notes credit the splash art."""

    @classmethod
    def setUpClass(cls):
        run = subprocess.run([RUBY, "-e", YAML_TO_JSON, os.path.join(WORKFLOWS, "server-image.yml")],
                             capture_output=True, text=True, check=True)
        cls.jobs = json.loads(run.stdout)["jobs"]
        with open(PATCH_SET, encoding="utf-8") as f:
            cls.patched = sorted(entry["path"] for entry in json.load(f)["files"])

    def steps(self, job):
        return self.jobs[job]["steps"]

    def step(self, job, text):
        """(index, step) of the one step of `job` whose run script contains `text`."""
        found = [(i, s) for i, s in enumerate(self.steps(job)) if text in s.get("run", "")]
        self.assertEqual(len(found), 1, f"{job}: steps that run {text!r}")
        return found[0]

    def uses(self, job, action):
        """Index of the step of `job` that uses `action`."""
        return [s.get("uses") for s in self.steps(job)].index(action)

    def test_the_fetch_step_gets_exactly_the_patched_files_into_runner_temp(self):
        # Run the step's script the way GitHub does (bash -e, from the checkout) against a small
        # fake release that holds the patched files at the real release's paths.
        _, fetch = self.step(TEST_JOB, "odaoc_fetch.py")
        _, tests = self.step(TEST_JOB, PATCH_TESTS)
        with tempfile.TemporaryDirectory() as tmp:
            files = dict(fx.DEFAULT_FILES)
            for path in self.patched:
                if path != "game.dll":
                    files[APP + path] = path.encode() * 40
            lock, files = fx.build(tmp, files)
            checkout, runner_temp = os.path.join(tmp, "checkout"), os.path.join(tmp, "runner-temp")
            os.makedirs(os.path.join(checkout, "deploy"))
            os.makedirs(os.path.join(checkout, "tools", "linux"))
            os.makedirs(runner_temp)
            shutil.copy(os.path.join(ROOT, "tools", "linux", "odaoc_fetch.py"),
                        os.path.join(checkout, "tools", "linux"))
            with fx.RangeServer(tmp) as srv:
                with open(os.path.join(checkout, "deploy", "upstream.lock"), "w") as f:
                    json.dump(srv.lock(lock), f)
                run = subprocess.run(["bash", "-e", "-c", fetch["run"]], cwd=checkout, capture_output=True,
                                     text=True, env=dict(os.environ, RUNNER_TEMP=runner_temp))
            self.assertEqual(run.returncode, 0, run.stderr)
            client = tests["env"]["HDC_CLIENT_FILES"].replace("${{ runner.temp }}", runner_temp)
            # game.dll comes from the classic edition, the other files from the release's client folder.
            expected = {path: files[lock["editions"]["classic"]["game_dll"] if path == "game.dll" else APP + path]
                        for path in self.patched}
            self.assertEqual({path: read_bytes(os.path.join(client, path)) for path in listing(client)}, expected)
            self.assertEqual(listing(runner_temp),
                             [os.path.relpath(client, runner_temp) + "/" + path for path in self.patched])
            # Nothing lands in the checkout, so nothing fetched can reach the image's build context.
            self.assertEqual(listing(checkout), ["deploy/upstream.lock", "tools/linux/odaoc_fetch.py"])

    def test_the_client_patch_tests_get_nasm_the_mpk_tool_pwsh_and_the_world(self):
        i_apt, apt = self.step(TEST_JOB, "apt-get install")
        i_mpk, _ = self.step(TEST_JOB, f"dotnet build {MPK_PROJECT} -c Release")
        i_fetch, _ = self.step(TEST_JOB, "odaoc_fetch.py")
        i_world, _ = self.step(TEST_JOB, "init_world.py")
        i_tests, tests = self.step(TEST_JOB, PATCH_TESTS)
        self.assertIn("nasm", apt["run"].split())
        self.assertLess(self.uses(TEST_JOB, "actions/setup-dotnet@v5"), i_mpk)
        self.assertLess(max(i_apt, i_mpk, i_fetch, i_world), i_tests)
        env = tests["env"]
        self.assertEqual(env["HDC_MPK_TOOL"], "${{ github.workspace }}/" + MPK_TOOL)
        self.assertEqual(env["HDC_TEST_WORLD"], "${{ runner.temp }}/world/world/opendaoc.sqlite3.db")
        # Named, not looked up: without pwsh the PowerShell cases fail instead of being skipped.
        self.assertEqual(env["HDC_PWSH"], "pwsh")

    def test_a_rebuild_of_the_patch_set_is_compared_with_the_committed_one(self):
        i_tests, tests = self.step(TEST_JOB, PATCH_TESTS)
        i_rebuild, rebuild = self.step(TEST_JOB, "client/patches/build.py")
        self.assertGreater(i_rebuild, i_tests)
        script = rebuild["run"]
        # The splash entry pins the committed splash.mpk's SHA-256; a rebuilt one would have another.
        for part in ('--client "$HDC_CLIENT_FILES"', '--world-db "$HDC_TEST_WORLD"', "--server-src source/server",
                     "--splash-mpk client/patches/splash.mpk", 'diff -u client/patches/classic-creation.json "$RUNNER_TEMP/'):
            self.assertIn(part, script)
        self.assertNotIn("build_splash_mpk", script)
        self.assertEqual(sorted(rebuild["env"]), ["HDC_CLIENT_FILES", "HDC_TEST_WORLD"])
        for name in ("HDC_CLIENT_FILES", "HDC_TEST_WORLD"):
            self.assertEqual(rebuild["env"][name], tests["env"][name], name)

    def test_nothing_fetched_is_published(self):
        for name, job_steps in self.jobs.items():
            for s in job_steps["steps"]:
                self.assertNotIn("upload-artifact", s.get("uses", ""), name)
        for s in self.steps(RELEASE_JOB):
            self.assertNotIn("odaoc_fetch", s.get("run", ""))
            self.assertNotIn("init_world", s.get("run", ""))
        # The release gets the two bundles and nothing else.
        _, create = self.step(RELEASE_JOB, "gh release create")
        assets = create["run"].split("gh release create", 1)[1].split("--target", 1)[0]
        self.assertEqual(assets.replace("\\\n", " ").split(),
                         ['"$TAG"', '"dist/hearthdaoc-deploy-$TAG.tar.gz"', '"dist/hearthdaoc-client-$TAG.zip"'])

    def test_the_release_job_needs_no_dotnet(self):
        # build_bundles.sh copies the committed splash.mpk: checkout, bundles, release, as before the client patches.
        steps = self.steps(RELEASE_JOB)
        self.assertEqual([s.get("uses") or s["name"] for s in steps],
                         ["actions/checkout@v5", "Build bundles (no EA files)", "Create release (and its tag on this commit)"])
        _, bundles = self.step(RELEASE_JOB, "deploy/build_bundles.sh")
        self.assertEqual((bundles["run"], bundles.get("env")), ('deploy/build_bundles.sh "$TAG" dist', None))
        for s in steps:
            self.assertNotIn("dotnet", s.get("run", ""))

    def test_the_release_notes_credit_offlinedaocs_splash_art(self):
        # In the --notes string, which gh puts before the notes it generates from the merged PRs.
        _, create = self.step(RELEASE_JOB, "gh release create")
        notes = create["run"].split('--notes "', 1)[1].split('"', 1)[0]
        self.assertIn("The client's loading splash is OfflineDAoC's art", notes)


SERVER_UNIT_TESTS = ("dotnet test source/server/Tests/Tests.csproj --nologo --filter "
                     '"FullyQualifiedName~UT_CommandPrivLevelOverrides|FullyQualifiedName~UT_SiStartChoice'
                     '|FullyQualifiedName~UT_ClassicBattlegrounds|FullyQualifiedName~UT_DataQuestFinishRewards"')
UNIT_TESTS = os.path.join(ROOT, "source", "server", "Tests", "UnitTests")


class ServerUnitTestWorkflowTests(unittest.TestCase):
    """CI runs the fork's own server unit tests by name. dotnet test passes (exit 0) when its filter matches
    no test at all, so every name in the filter must be a test class in source/server/Tests/UnitTests."""

    def test_the_fork_server_unit_tests_run_with_the_pinned_filter(self):
        steps = job(read("server-image.yml"), TEST_JOB).split("\n      - ")
        found = [s for s in steps if s.startswith("name: Server unit tests for the fork's server changes\n")]
        self.assertEqual(len(found), 1)
        self.assertIn('DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: "0"', found[0])
        self.assertIn("cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml\n", found[0])
        self.assertIn(SERVER_UNIT_TESTS + "\n", found[0])

    def test_every_name_in_the_filter_is_a_test_class(self):
        names = re.findall(r"FullyQualifiedName~(\w+)", SERVER_UNIT_TESTS)
        self.assertEqual(names, ["UT_CommandPrivLevelOverrides", "UT_SiStartChoice", "UT_ClassicBattlegrounds",
                                 "UT_DataQuestFinishRewards"])
        for name in names:
            with open(os.path.join(UNIT_TESTS, name + ".cs"), encoding="utf-8") as f:
                self.assertIn(f"public sealed class {name}\n", f.read(), name)


if __name__ == "__main__":
    unittest.main()
