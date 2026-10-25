#!/usr/bin/env python3
"""Generate HearthDAoC's Shrouded Isles portal patch sets (si-portals.json and si-portal-art.json).

Upstream 0.35 (commit 22682dd) put Shrouded Isles portal art in Cotswold, Mularn and Mag Mell, where patch 1.74
moved the portals; the server now has them at their 1.65 spots again, on the Avalon Marsh pier, in Gotar and in
Shannon Estuary. Upstream's tool (tools/claude-version/si_portal_visuals.py) appended one fixture row and one nif
row, named "SI Portal Visual <zone>", to the fixtures and nifs entries of each realm's home zone archive:

- si-portals.json takes those six rows out again (two mpk-text-replace operations per archive), so the town spots
  show nothing. Each entry is then the base client's.
- si-portal-art.json moves Newtowns/zones/Nifs/bavteleporter.npk, the 2006 Albion portal model, aside (a remove
  operation), so the client loads zones/Nifs/BAvTeleporter.npk, the 2002 one, with its purple flames.

Reads the four client files and writes only patch data: their SHA-256 hashes (the "after" ones from
patchset.transform, the appliers' own code) and upstream's six rows as the text to remove. No EA file is copied
into the patch sets. Running it twice on the same files gives byte-identical output.

ALBION_LOOK is the look the release ships. "2006" writes si-portal-art.json retired, so that every client that has
it applied gets the 2006 model back at its next start; the launchers need no change (docs/fork/FORK.md, The
Shrouded Isles portals). PORTALS_RETIRED True writes si-portals.json retired the same way, so every client gets
upstream's three archives back (the way back if a client can't read the rebuilt ones): a set that has shipped is
never dropped, since nothing would run it on a Windows client.

  python3 client/patches/build_si_portals.py --client ~/Games/HearthDAoC/client --out-dir client/patches
"""
import argparse
import os
import sys

import patchset
from build import read_client_file, sha256, to_json
from mpk import read_mpk

FORMAT = 1
CLIENT = "OfflineDAoC 0.36b and 0.37b (both editions)"
ARCHIVES = ("zones/Zone000/csv000.mpk", "zones/zone100/csv100.mpk", "zones/zone200/csv200.mpk")
# The entries upstream's tool appended to, found in any case (csv100's are FIXTURES.CSV and NIFS.CSV); the
# operations name them as the archive does.
ENTRIES = ("fixtures.csv", "nifs.csv")
MARK = "SI Portal Visual"
MODEL = "Newtowns/zones/Nifs/bavteleporter.npk"
LOOKS = ("2002", "2006")
ALBION_LOOK = "2002"
PORTALS_RETIRED = False
# What the appliers print after "Not patched: <file> is not the file this HearthDAoC release supports ".
PORTALS_REFUSED = ("(OfflineDAoC 0.36b's and 0.37b's zone files). The Shrouded Isles portal art upstream added in "
                   "Cotswold, Mularn and Mag Mell stays; it takes you nowhere. Use the portals on the Avalon Marsh "
                   "pier, in Gotar and in Shannon Estuary.")
ART_REFUSED = "(OfflineDAoC 0.36b's and 0.37b's). The Albion Shrouded Isles portal keeps its 2006 look."


def portal_row(text: str, where: str) -> str:
    """The one line of `text` that names MARK, with its CRLF; ValueError unless it is the last line."""
    lines = text.split("\r\n")
    if lines[-1]:
        raise ValueError(f"{where}: does not end in CRLF")
    found = [n for n, line in enumerate(lines) if MARK in line]
    if len(found) != 1:
        raise ValueError(f"{where}: {len(found)} lines name {MARK!r}, not exactly one")
    if found[0] != len(lines) - 2:
        raise ValueError(f"{where}: the {MARK!r} line is not the last one")
    return lines[-2] + "\r\n"


def archive_entry(client_dir: str, path: str) -> dict:
    """The patch-set entry that takes the portal rows out of the archive at `path`."""
    original = read_client_file(client_dir, path)
    _name, entries = read_mpk(os.path.join(client_dir, *path.split("/")))
    ops = []
    for wanted in ENTRIES:
        named = [(name, content) for name, content in entries if name.lower() == wanted]
        if len(named) != 1:
            raise ValueError(f"{path}: {len(named)} entries named {wanted} (in any case), not exactly one")
        [(name, content)] = named
        row = portal_row(content.decode("latin-1"), f"{path}: {name}")
        ops.append({"op": "mpk-text-replace", "entry": name, "find": row, "replace": ""})
    try:
        after = patchset.transform(original, ops, None)
    except patchset.PatchError as e:
        raise ValueError(f"{path}: {e}") from None
    return {"path": path, "before": sha256(original), "after": sha256(after), "ops": ops}


def build_portals_set(client_dir: str, retired: bool = PORTALS_RETIRED) -> dict:
    """si-portals.json, for the archives in `client_dir`: applied, or retired to give upstream's archives back."""
    data = {"format": FORMAT, "name": "si-portals", "client": CLIENT, "refused": PORTALS_REFUSED}
    if retired:
        data["retired"] = True
    data["files"] = [archive_entry(client_dir, path) for path in ARCHIVES]
    return data


def build_art_set(client_dir: str, look: str = ALBION_LOOK) -> dict:
    """si-portal-art.json, for the model in `client_dir`: applied for the 2002 look, retired for the 2006 one."""
    if look not in LOOKS:
        raise ValueError(f"the Albion portal look must be 2002 or 2006, not {look!r}")
    data = {"format": FORMAT, "name": "si-portal-art", "client": CLIENT, "refused": ART_REFUSED}
    if look == "2006":
        data["retired"] = True
    data["files"] = [{"path": MODEL, "before": sha256(read_client_file(client_dir, MODEL)), "after": patchset.ABSENT,
                      "ops": [{"op": "remove"}]}]
    return data


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Generate HearthDAoC's Shrouded Isles portal patch sets.")
    parser.add_argument("--client", required=True,
                        help="a client folder with OfflineDAoC 0.36b's or 0.37b's zone files (read only)")
    parser.add_argument("--out-dir", required=True, help="the folder to write si-portals.json and si-portal-art.json in")
    args = parser.parse_args(argv)
    try:
        sets = [("si-portals.json", build_portals_set(args.client, PORTALS_RETIRED)),
                ("si-portal-art.json", build_art_set(args.client, ALBION_LOOK))]
    except (OSError, ValueError) as e:
        print(f"build_si_portals.py: {e}", file=sys.stderr)
        return 1
    for name, data in sets:
        out = os.path.join(args.out_dir, name)
        with open(out, "w", encoding="ascii", newline="\n") as f:
            f.write(to_json(data))
        if name == "si-portals.json":
            what = " (retired: upstream's archives come back)" if data.get("retired") else ""
            print(f"Wrote {out}: {', '.join(entry['path'] for entry in data['files'])}{what}")
        else:
            what = "retired: the 2006 look" if data.get("retired") else "moved aside: the 2002 Albion portal look"
            print(f"Wrote {out}: {MODEL} ({what})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
