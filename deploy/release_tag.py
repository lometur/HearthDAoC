#!/usr/bin/env python3
"""Keep the release tag named in the docs in step with the release.

`.env.example` (HEARTHDAOC_TAG) and HANDOFF.md's install step must name the release being published, so
anyone copying them gets that release. Before tagging, `bump` sets them in a release PR; when a tag is
pushed, CI runs `check` and refuses to publish if they don't match (or the tag's upstream version is not
the one in upstream.lock).

    deploy/release_tag.py bump v0.34b-hearth.4
    deploy/release_tag.py check v0.34b-hearth.4
"""
import argparse
import json
import os
import re
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


def main(argv=None):
    ap = argparse.ArgumentParser(description="Check or bump the release tag named in the deploy docs.")
    ap.add_argument("--root", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    ap.add_argument("action", choices=["check", "bump"])
    ap.add_argument("tag")
    a = ap.parse_args(argv)
    try:
        if a.action == "bump":
            bump(a.root, a.tag)
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
