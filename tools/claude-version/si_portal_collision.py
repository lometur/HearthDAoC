"""Make the Classic-side Shrouded Isles portal models solid, like their Shrouded Isles originals.

si_portal_visuals.py (2026-09-27) copied the SI teleporter models into Camelot Hills (zone 000),
Vale of Mularn (100) and Lough Derg (200) with collision switched off on both the NIF row
(nifs.csv "Collide") and the fixture row (fixtures.csv "Collide"), so players and gamebots
walked through the platform, steps and portal. The SI-side rows (zones 051/151/181) have 1 on
both. This sets both to 1 on the three managed "SI Portal Visual" rows and nothing else.

    python si_portal_collision.py check     # read only: show the rows and what would change
    python si_portal_collision.py install   # close the launcher, client and server first
"""
import datetime
import hashlib
import os
import shutil
import sys

import importlib.util
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("spv", HERE / "si_portal_visuals.py")
spv = importlib.util.module_from_spec(spec)
spec.loader.exec_module(spv)

ZONES = (0, 100, 200)
NIF_COLLIDE, FIXTURE_COLLIDE = 7, 8


def patched(entry, name_col, collide_col):
    """Return (new entry, changed rows). Rows are edited in place in the CSV text."""
    lines = entry.content.decode("latin1").split("\r\n")
    changed = []
    for i, line in enumerate(lines):
        cells = line.split(",")
        if len(cells) > max(name_col, collide_col) and cells[name_col].startswith("SI Portal Visual"):
            before = cells[collide_col]
            cells[collide_col] = "1"
            lines[i] = ",".join(cells)
            changed.append((cells[name_col], before))
    content = "\r\n".join(lines).encode("latin1")
    return spv.Entry(entry.name, content, entry.timestamp, entry.flags, entry.memory_offset), changed


def plan():
    out = []
    for zone in ZONES:
        path, name, entries = spv.csv_entries(zone)
        updated = []
        report = []
        for e in entries:
            if e.name.lower() == "nifs.csv":
                e, ch = patched(e, 1, NIF_COLLIDE)
                report += [("nifs.csv", *c) for c in ch]
            elif e.name.lower() == "fixtures.csv":
                e, ch = patched(e, 2, FIXTURE_COLLIDE)
                report += [("fixtures.csv", *c) for c in ch]
            updated.append(e)
        if sorted(r[0] for r in report) != ["fixtures.csv", "nifs.csv"]:
            raise SystemExit(f"zone {zone:03d}: expected exactly one managed NIF row and one fixture row, got {report}")
        for old, new in zip(entries, updated):
            if old.name.lower() not in ("nifs.csv", "fixtures.csv") and old.content != new.content:
                raise SystemExit(f"zone {zone:03d}: unrelated entry changed")
            if old.name.lower() in ("nifs.csv", "fixtures.csv") and len(old.content) != len(new.content):
                raise SystemExit(f"zone {zone:03d}: {old.name} length changed (only one character per row may change)")
        out.append((zone, path, name, updated, report))
    return out


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "check"
    prepared = plan()
    for zone, path, _, _, report in prepared:
        print(f"zone {zone:03d} {path.name}: " + "; ".join(f"{f} '{n}' Collide {b} -> 1" for f, n, b in report))
    if cmd != "install":
        return
    backup = spv.ROOT / "runtime" / "deployment-backups" / (
        "si-portal-collision-" + datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
    backup.mkdir(parents=True, exist_ok=False)
    for zone, path, name, updated, _ in prepared:
        shutil.copy2(path, backup / path.name)
        binary = spv.pack_mpk(name, updated)
        tmp = path.with_name(path.name + ".collision-tmp")
        tmp.write_bytes(binary)
        if hashlib.sha256(tmp.read_bytes()).hexdigest() != hashlib.sha256(binary).hexdigest():
            raise SystemExit(f"could not verify {tmp}")
        os.replace(tmp, path)
        spv.unpack_mpk(path.read_bytes())
    print(f"installed; originals in {backup}")


if __name__ == "__main__":
    main()
