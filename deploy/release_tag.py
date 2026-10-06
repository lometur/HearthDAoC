#!/usr/bin/env python3
"""Name the release a build on main publishes.

Merging to main is the release (.github/workflows/server-image.yml): when files that end up in the image
or the bundles changed since the last release, the build on main publishes the next tag from that commit.
The tag is not written into the repository; deploy/build_bundles.sh stamps it into the bundle's
.env.example, where `./hdc update` reads it.

    deploy/release_tag.py next     # the tag to publish for HEAD, or an empty line
"""
import argparse
import json
import os
import re
import subprocess
import sys

TAG = re.compile(r"^v(?P<upstream>\d+(?:\.\d+)*[a-z]?)-hearth\.\d+$")

# Files that end up in the image (deploy/Dockerfile's COPY lines) or the bundles (deploy/build_bundles.sh).
# Tests, docs, CI and this script don't need a release; the only shipped Markdown files are SHIPPED_DOCS.
RELEASE_PATHS = ("source/server/", "source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs",
                 "source/tools/OfflineDaoc.ProgressImport/", "tools/linux/", "deploy/", "client/", ".dockerignore")
NOT_RELEASE_PATHS = ("source/server/docs/", "deploy/release_tag.py")
SHIPPED_DOCS = ("deploy/HANDOFF.md", "client/README.md")
TESTS = re.compile(r"(^|/)tests/", re.IGNORECASE)


def _number(tag):
    return int(tag.rsplit(".", 1)[1])


def latest_release(tags, upstream):
    """Newest vUPSTREAM-hearth.N among tags, or None."""
    ours = [t for t in tags if TAG.match(t) and TAG.match(t).group("upstream") == upstream]
    return max(ours, key=_number) if ours else None


def next_tag(upstream, tags):
    last = latest_release(tags, upstream)
    return f"v{upstream}-hearth.{_number(last) + 1 if last else 1}"


def release_worthy(paths):
    return any(p.startswith(RELEASE_PATHS) and not p.startswith(NOT_RELEASE_PATHS) and not TESTS.search(p)
               and (not p.endswith(".md") or p in SHIPPED_DOCS) for p in paths)


def _git(root, *args):
    return subprocess.run(["git", "-C", root, *args], check=True, capture_output=True, text=True).stdout


def _upstream(root):
    with open(os.path.join(root, "deploy", "upstream.lock"), encoding="utf-8") as f:
        return json.load(f)["version"]


def next_release(root):
    """Tag to publish for HEAD, or '' when only docs, tests or CI changed since the last release, or when
    HEAD is older than a release (a re-run of an old run must not publish older code)."""
    if _git(root, "tag", "--list", "v*-hearth.*", "--no-merged", "HEAD").split():
        return ""
    try:
        last = _git(root, "describe", "--tags", "--abbrev=0", "--match", "v*-hearth.*", "HEAD").strip()
    except subprocess.CalledProcessError:
        last = ""  # no release yet
    # --no-renames: moving a shipped file (into tests, say) lists its old path too
    if last and not release_worthy(_git(root, "diff", "--no-renames", "--name-only", f"{last}..HEAD").split()):
        return ""
    tags = _git(root, "tag", "--list", "v*-hearth.*").split()
    return next_tag(_upstream(root), tags)  # a new upstream version in upstream.lock restarts at .1


def main(argv=None):
    ap = argparse.ArgumentParser(description="Name the release a build on main publishes.")
    ap.add_argument("--root", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    sub = ap.add_subparsers(dest="action", required=True)
    sub.add_parser("next", help="print the tag to publish for HEAD, or an empty line")
    a = ap.parse_args(argv)
    print(next_release(a.root))
    return 0


if __name__ == "__main__":
    sys.exit(main())
