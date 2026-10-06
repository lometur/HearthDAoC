#!/usr/bin/env python3
"""Generate HearthDAoC's classic character creation patch set (classic-creation.json).

Reads an OfflineDAoC 0.34 classic client's own files, applies HearthDAoC's edits in memory and
writes only patch data: SHA-256 hashes and byte and text edits. No EA file is ever copied into
the patch set. Running it twice on the same inputs gives byte-identical output.

  python3 client/patches/build.py --client ~/Games/HearthDAoC/client \\
      --world-db clean-classic-0.34.db --server-src source/server/GameServer \\
      --out client/patches/classic-creation.json
"""
import argparse
import hashlib
import json
import os
import struct
import sys

import pe

FORMAT = 1
NAME = "classic-creation"
CLIENT = "OfflineDAoC 0.34 classic"
GAME_DLL = "game.dll"
GAME_DLL_SHA256 = "67dcf68a37b95a93946a943b99d5e19b4a03e08cd6469275e25c7b909de21e99"
CUSTOMIZE_STATS = "pregame/character_customize_stats.xml"

# Classic stat flow in game.dll: (VA, original bytes, new bytes), hex. From the 2026-10-06 investigation.
STAT_FLOW = [
    # P1: auto-assign (0x59C086) returns right after its reset: race base stats and 30 points to place.
    (0x59C0B2, "8b465c", "eb6890"),
    # P2: Continue on the customise screen checks unspent points for new characters too,
    # with the client's own "You must use all your points!" popup.
    (0x59A853, "0f859302000080bb28fa0000000f8478020000833dc8bb4502007437",
     "75f6833dc8bb450200751180bb28fa0000000f8473020000eb399090"),
    # P3: the attributes dialog starts visible.
    (0x59C574, "01", "00"),
]

# The attributes dialog's Optimize button (ControlId 1021), removed whole. The pregame XML uses CRLF.
OPTIMIZE_BUTTON = (
    "\t\t<ButtonDef>\r\n"
    "\t\t\t<TemplateName>button_small</TemplateName>\r\n"
    "\t\t\t<ControlId>1021</ControlId>\r\n"
    "\t\t\t<Label>Optimize</Label>\r\n"
    "\t\t\t<Alignment>\r\n"
    "\t\t\t\t<TopLeft>true</TopLeft>\r\n"
    "\t\t\t</Alignment>\r\n"
    "\t\t\t<Position>\r\n"
    "\t\t\t\t<X>364</X>\r\n"
    "\t\t\t\t<Y>224</Y>\r\n"
    "\t\t\t</Position>\r\n"
    "\t\t\t<LabelAlignment>\r\n"
    "\t\t\t\t<CenterVertically>true</CenterVertically>\r\n"
    "\t\t\t\t<CenterHorizontally>true</CenterHorizontally>\r\n"
    "\t\t\t</LabelAlignment>\r\n"
    "\t\t</ButtonDef>\r\n"
)
XML_EDITS = {CUSTOMIZE_STATS: [(OPTIMIZE_BUTTON, "")]}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_client_file(client_dir: str, path: str) -> bytes:
    with open(os.path.join(client_dir, *path.split("/")), "rb") as f:
        return f.read()


def patch_game_dll(original: bytes, cave: bytes | None = None) -> bytes:
    """The classic 0.34 game.dll with the stat-flow patches and a fresh PE checksum.
    `cave` is the slot for the base-class code cave; this version only accepts None."""
    if sha256(original) != GAME_DLL_SHA256:
        raise ValueError(f"{GAME_DLL} is not the OfflineDAoC 0.34 classic file (SHA-256 {GAME_DLL_SHA256})")
    image = pe.PE(original)
    data = bytearray(original)
    for va, before, after in STAT_FLOW:
        start, old, new = image.offset(va), bytes.fromhex(before), bytes.fromhex(after)
        if len(new) != len(old) or data[start:start + len(old)] != old:
            raise ValueError(f"{GAME_DLL}: unexpected bytes at VA {va:#x}")
        data[start:start + len(old)] = new
    if cave is not None:
        raise ValueError("this build.py cannot add the code cave yet")
    offset = image.header_offsets["checksum"]
    struct.pack_into("<I", data, offset, pe.checksum(data, offset))
    return bytes(data)


def edit_text(data: bytes, edits: list[tuple[str, str]]) -> bytes:
    """Apply (find, replace) pairs to `data` decoded as Latin-1; each find must occur exactly once."""
    text = data.decode("latin-1")
    for find, replace in edits:
        count = text.count(find)
        if count != 1:
            raise ValueError(f"expected the text once, found it {count} times: {find[:60]!r}")
        text = text.replace(find, replace)
    return text.encode("latin-1")


def file_entry(path: str, before: bytes, after: bytes, ops: list[dict]) -> dict:
    return {"path": path, "before": sha256(before), "after": sha256(after), "ops": ops}


def build_patchset(client_dir: str, world_db: str, server_src: str, splash_mpk: str | None = None) -> dict:
    """The classic-creation patch set for the client files in `client_dir`.
    `world_db` and `server_src` are the inputs of the base-class list and `splash_mpk` the slot
    for the splash entry; this version uses neither and only accepts splash_mpk=None."""
    if splash_mpk is not None:
        raise ValueError("this build.py cannot add the splash entry yet")
    original = read_client_file(client_dir, GAME_DLL)
    patched = patch_game_dll(original)
    files = [file_entry(GAME_DLL, original, patched, pe.diff_ops(original, patched))]
    for path, edits in XML_EDITS.items():
        data = read_client_file(client_dir, path)
        ops = [{"op": "text-replace", "find": find, "replace": replace} for find, replace in edits]
        files.append(file_entry(path, data, edit_text(data, edits), ops))
    return {"format": FORMAT, "name": NAME, "client": CLIENT, "files": files}


def to_json(patchset: dict) -> str:
    return json.dumps(patchset, indent=1, ensure_ascii=True, sort_keys=False) + "\n"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Generate HearthDAoC's classic-creation patch set.")
    parser.add_argument("--client", required=True, help="OfflineDAoC 0.34 classic client folder (read only)")
    parser.add_argument("--world-db", required=True, help="the classic edition's clean world database (read only)")
    parser.add_argument("--server-src", required=True, help="the server sources, source/server/GameServer")
    parser.add_argument("--out", required=True, help="the patch set to write")
    args = parser.parse_args(argv)
    try:
        patchset = build_patchset(args.client, args.world_db, args.server_src)
    except (OSError, ValueError) as e:
        print(f"build.py: {e}", file=sys.stderr)
        return 1
    with open(args.out, "w", encoding="ascii", newline="\n") as f:
        f.write(to_json(patchset))
    print(f"Wrote {args.out}: {', '.join(entry['path'] for entry in patchset['files'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
