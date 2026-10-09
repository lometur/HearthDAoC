#!/usr/bin/env python3
"""Apply HearthDAoC's client patch set to a client folder (Windows: windows/patch-client.bat).

    apply_patches.py --client DIR [--patchset FILE] [--bundle DIR] [--restore | --check]

The patch set defaults to classic-creation.json next to this script. Bundled files, such as
splash.mpk, are looked up in the patch set's folder unless --bundle names another one.
--check only reports each file's state. --restore puts the backed-up originals back, but only
over files that are still the patched ones.

Exit codes:
  0  patched, already patched or restored (--check: every file is known)
  1  a file couldn't be read or written
  2  bad usage, or an invalid patch set or bundle
  3  refused: a client file is unknown or missing, or a backup isn't the original, or
     (--restore) a file has changed since it was patched; nothing was changed
"""
import argparse
import os
import sys

sys.dont_write_bytecode = True  # leave no __pycache__ in a player's client bundle
import patchset  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_PATCHSET = os.path.join(HERE, "classic-creation.json")
UNKNOWN = ("Not patched: {path} is not the file this HearthDAoC release supports (for example the b "
           "edition, or an older or newer upstream client). The client still works with the standard creation screen.")
MISSING = "Not patched: {path} is missing from the client folder."
CHANGED = ("Not restored: {path} has changed since it was patched (for example a newer client was installed); "
           "the saved original is kept as {backup}.")
GONE = "Not restored: {path} is missing from the client folder; the saved original is kept as {backup}."


def run(args):
    try:
        pset = patchset.load(args.patchset)
    except patchset.PatchError as e:
        print(f"Error: invalid patch set: {e}", file=sys.stderr)
        return 2
    bundle = args.bundle or os.path.dirname(os.path.abspath(args.patchset))
    if args.restore:
        states = patchset.restore_status(args.client, pset, bundle)
        wrong = [path + patchset.BACKUP_SUFFIX for path, state in states if state == "wrong-backup"]
        if wrong:
            print(f"Not restored: not the original file: {', '.join(wrong)}. Nothing was changed.")
        for path, state in states:
            if state in ("changed", "missing"):
                message = CHANGED if state == "changed" else GONE
                print(message.format(path=path, backup=path + patchset.BACKUP_SUFFIX))
        if any(state in ("wrong-backup", "changed", "missing") for _path, state in states):
            return 3
        for path, result in patchset.restore(args.client, pset, bundle):
            print(f"Restored: {path}" if result == "restored" else f"Nothing to restore: {path}")
        return 0
    states = patchset.status(args.client, pset, bundle)
    refused = [(path, state) for path, state in states if state in ("unknown", "missing")]
    if args.check:
        for path, state in states:
            print(f"{path}: {state}")
        return 3 if refused else 0
    if refused:
        for path, state in refused:
            print((UNKNOWN if state == "unknown" else MISSING).format(path=path))
        return 3
    for path, result in patchset.apply(args.client, pset, bundle):
        if result == "patched":
            print(f"Patched: {path} (original saved as {path}{patchset.BACKUP_SUFFIX})")
        else:
            print(f"Already patched: {path}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description="Apply HearthDAoC's client patch set to a client folder.")
    parser.add_argument("--client", metavar="DIR", required=True, help="the client folder, the one with game.dll")
    parser.add_argument("--patchset", metavar="FILE", default=DEFAULT_PATCHSET,
                        help="patch set (default: classic-creation.json next to this script)")
    parser.add_argument("--bundle", metavar="DIR",
                        help="folder with the bundled files (default: the patch set's folder)")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--restore", action="store_true", help="put the original files back")
    mode.add_argument("--check", action="store_true", help="only report each file's state")
    args = parser.parse_args(argv)
    if not os.path.isdir(args.client):
        print(f"Error: client folder not found: {args.client}", file=sys.stderr)
        return 2
    try:
        return run(args)
    except patchset.PatchError as e:
        print(f"Error: {e}", file=sys.stderr)
        return 2
    except OSError as e:
        print(f"Error: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
