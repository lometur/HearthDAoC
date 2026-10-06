#!/usr/bin/env python3
"""Keep the release tag named in the docs in step with the release.

`.env.example` (HEARTHDAOC_TAG) and HANDOFF.md's install step must name the release being published, so
anyone copying them gets that release. Before tagging, `bump` sets them in a release PR; when a tag is
pushed, CI runs `check` and refuses to publish if they don't match (or the tag's upstream version is not
the one in upstream.lock).

    deploy/release_tag.py bump v0.34b-hearth.4
    deploy/release_tag.py check v0.34b-hearth.4

Automation (.github/workflows): after a merge to main, `plan` says whether a release PR is due (release-
worthy files changed since the last release, and no release is already pending); `prepare <tag>` bumps the
docs and adds a docs/fork/CHANGELOG.md entry for that release PR; `pending` names the tag a merged release
PR asks for (the docs name it but it is not published yet), which the main build then publishes.
"""
import argparse
import datetime
import json
import os
import re
import subprocess
import sys

TAG = re.compile(r"^v(?P<upstream>\d+(?:\.\d+)*[a-z]?)-hearth\.\d+$")
CONCRETE = re.compile(r"v\d+(?:\.\d+)*[a-z]?-hearth\.\d+")


class TagError(Exception):
    pass


def _parse(tag):
    m = TAG.match(tag)
    if not m:
        raise TagError(f"release tags look like v<upstream version>-hearth.<n> (e.g. v0.34b-hearth.4), got {tag!r}")
    return m.group("upstream")


def _paths(root):
    d = os.path.join(root, "deploy")
    return os.path.join(d, ".env.example"), os.path.join(d, "HANDOFF.md"), os.path.join(d, "upstream.lock")


def check(root, tag):
    """Problems that stop `tag` from being released (empty list when it is fine)."""
    upstream = _parse(tag)
    env, handoff, lock = _paths(root)
    problems = []
    with open(lock, encoding="utf-8") as f:
        locked = json.load(f)["version"]
    if upstream != locked:
        problems.append(f"{tag} is for upstream {upstream}, but deploy/upstream.lock pins {locked}")
    with open(env, encoding="utf-8") as f:
        env_tags = [line.split("=", 1)[1].strip().strip('"') for line in f if line.startswith("HEARTHDAOC_TAG=")]
    if env_tags != [tag]:
        problems.append(f"deploy/.env.example has HEARTHDAOC_TAG={','.join(env_tags) or '(missing)'}, expected {tag}")
    with open(handoff, encoding="utf-8") as f:
        named = set(CONCRETE.findall(f.read()))
    if named != {tag}:
        problems.append(f"deploy/HANDOFF.md names {', '.join(sorted(named)) or 'no release'}, expected only {tag}")
    return problems


def bump(root, tag):
    _parse(tag)
    env, handoff, _ = _paths(root)
    with open(env, encoding="utf-8") as f:
        text = f.read()
    with open(env, "w", encoding="utf-8") as f:
        f.write(re.sub(r"(?m)^HEARTHDAOC_TAG=.*$", f"HEARTHDAOC_TAG={tag}", text))
    with open(handoff, encoding="utf-8") as f:
        text = f.read()
    with open(handoff, "w", encoding="utf-8") as f:
        f.write(CONCRETE.sub(tag, text))


CHANGELOG = os.path.join("docs", "fork", "CHANGELOG.md")
# Files that end up in the image or the release bundles. Tests, CI and docs elsewhere don't need a release.
RELEASE_PATHS = ("source/", "deploy/", "client/", "tools/linux/", ".dockerignore")
NOT_RELEASE_PATHS = ("deploy/tests/", "client/tests/", "tools/linux/tests/", "source/server/Tests/")


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
    return any(p.startswith(RELEASE_PATHS) and not p.startswith(NOT_RELEASE_PATHS) for p in paths)


def pending_release(docs_tag, tags):
    """The tag the docs name when it is not published yet (a merged release PR), else None."""
    return docs_tag if docs_tag not in tags else None


def changelog_entry(tag, date, subjects):
    lines = [s for s in subjects if not s.startswith(("Merge pull request", "Merge branch"))
             and not re.match(r"chore\(release\): v\d", s)]  # the release PRs' own commits
    return f"## {tag} ({date})\n\n" + "".join(f"- {s}\n" for s in lines or ["Maintenance release."])


def prepend_changelog(path, entry):
    header = "# HearthDAoC changelog\n\nReleases of the fork (newest first). Upstream's own changes are in CHANGELOG.md.\n\n"
    body = ""
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            text = f.read()
        body = text[text.index("## "):] if "## " in text else ""
    heading = entry.split("\n", 1)[0].split(" (")[0]  # "## vX-hearth.N"
    sections = [s for s in re.split(r"(?m)^(?=## )", body) if s.strip()]
    sections = [s for s in sections if s.split("\n", 1)[0].split(" (")[0] != heading]
    with open(path, "w", encoding="utf-8") as f:
        f.write(header + entry.rstrip("\n") + "\n\n" + "".join(s.rstrip("\n") + "\n\n" for s in sections))


def notes(path, tag):
    """The changelog entry's lines for tag (without its heading), or '' when there is none."""
    if not os.path.exists(path):
        return ""
    with open(path, encoding="utf-8") as f:
        sections = re.split(r"(?m)^(?=## )", f.read())
    for section in sections:
        if section.split("\n", 1)[0].split(" (")[0] == f"## {tag}":
            return section.split("\n", 1)[1].strip()
    return ""


def _git(root, *args):
    return subprocess.run(["git", "-C", root, *args], check=True, capture_output=True, text=True).stdout


def _docs_tag(root):
    env, _, _ = _paths(root)
    with open(env, encoding="utf-8") as f:
        return next(line.split("=", 1)[1].strip().strip('"') for line in f if line.startswith("HEARTHDAOC_TAG="))


def _upstream(root):
    with open(_paths(root)[2], encoding="utf-8") as f:
        return json.load(f)["version"]


def _all_tags(root):
    return _git(root, "tag", "--list", "v*-hearth.*").split()


def plan(root):
    """('none'|'pending'|'release-pr', tag) for the current HEAD of main."""
    tags = _all_tags(root)
    pending = pending_release(_docs_tag(root), tags)
    if pending:
        return "pending", pending
    upstream = _upstream(root)
    last = latest_release(tags, upstream) or latest_release(tags, TAG.match(_docs_tag(root)).group("upstream"))
    changed = _git(root, "diff", "--name-only", f"{last}..HEAD").split() if last else ["source/"]
    if not release_worthy(changed):
        return "none", ""
    return "release-pr", next_tag(upstream, tags)


def prepare(root, tag):
    """Bump the docs to tag and add its changelog entry (commit subjects since the last release)."""
    tags = _all_tags(root)
    last = latest_release(tags, _upstream(root)) or latest_release(tags, TAG.match(_docs_tag(root)).group("upstream"))
    subjects = _git(root, "log", "--format=%s", f"{last}..HEAD" if last else "HEAD").splitlines()
    bump(root, tag)
    prepend_changelog(os.path.join(root, CHANGELOG),
                      changelog_entry(tag, datetime.date.today().isoformat(), subjects))


def main(argv=None):
    ap = argparse.ArgumentParser(description="Check, bump or plan the release named in the deploy docs.")
    ap.add_argument("--root", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    sub = ap.add_subparsers(dest="action", required=True)
    for name in ("check", "bump", "prepare"):
        sub.add_parser(name).add_argument("tag")
    sub.add_parser("plan", help="print action=... and tag=... (for GITHUB_OUTPUT)")
    sub.add_parser("pending", help="print the unpublished tag the docs name, if any")
    sub.add_parser("notes", help="print the changelog entry for a tag").add_argument("tag")
    a = ap.parse_args(argv)
    try:
        if a.action == "plan":
            action, tag = plan(a.root)
            print(f"action={action}\ntag={tag}")
            return 0
        if a.action == "notes":
            print(notes(os.path.join(a.root, CHANGELOG), a.tag))
            return 0
        if a.action == "pending":
            print(pending_release(_docs_tag(a.root), _all_tags(a.root)) or "")
            return 0
        if a.action == "bump":
            bump(a.root, a.tag)
        elif a.action == "prepare":
            _parse(a.tag)
            prepare(a.root, a.tag)
        problems = check(a.root, a.tag)
    except TagError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2
    for p in problems:
        print(f"ERROR: {p}", file=sys.stderr)
    if problems:
        print(f"Fix it in a release PR first: deploy/release_tag.py bump {a.tag}", file=sys.stderr)
        return 1
    print(f"Docs name {a.tag}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
