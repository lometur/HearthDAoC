"""Region-wide reachability audit: which game locations a bot can walk to (read only).

Each zone navmesh is split into connected walkable areas (navcomp), and areas of neighbouring
zones are joined where both meshes have floor at the same spot on the shared zone edge (the
seams bots cross). The region's main network is the one holding the most bind points. Every
location (bind points, zone point sources and targets, NPC and monster spawns) is then placed
on a mesh and reported if it is off the mesh or not on the main network.

python regionaudit.py <zones.dat> <db> <navdir> <region> <out.txt> [overridedir]
overridedir: zoneNNN.nav files there replace the ones in navdir (to audit a rebuild).
"""
import os, sqlite3, sys
from collections import Counter, defaultdict
from navcomp import NavAreas, height_at

sys.path.insert(0, os.environ.get("ZONESDAT_DIR", "."))

def load_zones(path):
    zones = {}; cur = None
    for line in open(path, encoding="utf-8-sig", errors="replace"):
        line = line.split(";")[0].strip()
        if line.startswith("[") and line.endswith("]"):
            cur = line[1:-1].lower(); zones[cur] = {}
        elif "=" in line and cur:
            k, v = line.split("=", 1); zones[cur][k.strip().lower()] = v.strip()
    out = {}
    for k, d in zones.items():
        if not k.startswith("zone") or not k[4:].isdigit():
            continue
        zid = int(k[4:])
        out[zid] = dict(name=d.get("name", ""), region=int(d.get("region", "-1") or -1),
                        x=int(d.get("region_offset_x", "0")) * 8192, y=int(d.get("region_offset_y", "0")) * 8192,
                        w=int(d.get("width", "8")) * 8192, h=int(d.get("height", "8")) * 8192)
    return out

def main(zonesdat, db, navdir, region, out, override=None):
    zones = {z: d for z, d in load_zones(zonesdat).items() if d["region"] == region}
    meshes = {}
    for z in sorted(zones):
        name = f"zone{z:03d}.nav"
        path = os.path.join(override, name) if override and os.path.exists(os.path.join(override, name)) else os.path.join(navdir, name)
        if os.path.exists(path):
            meshes[z] = NavAreas(path)
    parent = {}
    def find(k):
        parent.setdefault(k, k)
        while parent[k] != k:
            parent[k] = parent[parent[k]]; k = parent[k]
        return k
    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb: parent[ra] = rb
    for z, m in meshes.items():
        for r in set(m.root):
            find((z, r))
    # Seams: sample the shared edge of every pair of neighbouring zones.
    ids = sorted(meshes)
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            A, B = zones[a], zones[b]
            for vertical in (True, False):
                if vertical:
                    if A["x"] + A["w"] == B["x"]: border, sa = B["x"], 1
                    elif B["x"] + B["w"] == A["x"]: border, sa = A["x"], -1
                    else: continue
                    lo, hi = max(A["y"], B["y"]), min(A["y"] + A["h"], B["y"] + B["h"])
                else:
                    if A["y"] + A["h"] == B["y"]: border, sa = B["y"], 1
                    elif B["y"] + B["h"] == A["y"]: border, sa = A["y"], -1
                    else: continue
                    lo, hi = max(A["x"], B["x"]), min(A["x"] + A["w"], B["x"] + B["w"])
                if hi - lo < 256: continue
                for c in range(int(lo) + 32, int(hi), 64):
                    pa = (border - sa * 48, c) if vertical else (c, border - sa * 48)
                    pb = (border + sa * 48, c) if vertical else (c, border + sa * 48)
                    for ia in candidates(meshes[a], *pa):
                        # Same rule as the server's seam crossing (AutonomousZoneBoundaryRouting): the
                        # two floors 96 units apart may differ by at most 128 in height.
                        za = meshes[a].height(ia, *pa)
                        ib = meshes[b].locate(pb[0], pb[1], za, tolerance=128)
                        if ib is not None:
                            union((a, meshes[a].root[ia]), (b, meshes[b].root[ib]))
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    points = []
    for x, y, zz in con.execute("select X, Y, Z from bindpoint where Region=?", (region,)):
        points.append(("bind", "", x, y, zz))
    for zid, x, y, zz in con.execute("select Id, SourceX, SourceY, SourceZ from zonepoint where SourceRegion=? and SourceX>0", (region,)):
        points.append(("zonepoint-source", str(zid), x, y, zz))
    for zid, x, y, zz in con.execute("select Id, TargetX, TargetY, TargetZ from zonepoint where TargetRegion=?", (region,)):
        points.append(("zonepoint-target", str(zid), x, y, zz))
    for name, cls, x, y, zz in con.execute("select Name, ClassType, X, Y, Z from mob where Region=?", (region,)):
        kind = "npc" if cls and cls not in ("DOL.GS.GameNPC",) and "Merchant" in cls or "Trainer" in (cls or "") or "Stable" in (cls or "") else "mob"
        points.append((kind, name, x, y, zz))
    placed = []
    for kind, label, x, y, zz in points:
        z = zone_at(zones, x, y)
        if z is None or z not in meshes:
            placed.append((kind, label, x, y, zz, z, None)); continue
        i = meshes[z].locate(x, y, zz, tolerance=200)
        net = None if i is None else find((z, meshes[z].root[i]))
        if kind == "zonepoint-source":
            # Bots cross when they reach floor within 190 of the trigger (ZonePointArrivalRadius);
            # the server searches that circle for a connected approach point.
            nets = Counter()
            for r in range(0, 191, 24):
                for k in range(max(1, r // 12)):
                    import math
                    a = 2 * math.pi * k / max(1, r // 12)
                    j = meshes[z].locate(x + r * math.cos(a), y + r * math.sin(a), zz, tolerance=300)
                    if j is not None:
                        nets[find((z, meshes[z].root[j]))] += 1
            approach_nets = set(nets)
            placed.append((kind, label, x, y, zz, z, net, approach_nets))
            continue
        placed.append((kind, label, x, y, zz, z, net))
    binds = Counter(p[6] for p in placed if p[0] == "bind" and p[6] is not None)
    main_net = binds.most_common(1)[0][0] if binds else None
    placed = [p[:6] + (main_net if p[6] != main_net and main_net in p[7] else p[6],) if len(p) > 7 else p for p in placed]
    lines = [f"region {region}: {len(meshes)} zone meshes, main network holds {binds[main_net] if main_net else 0}/{sum(1 for p in placed if p[0]=='bind')} bind points"]
    summary = defaultdict(Counter)
    for kind, label, x, y, zz, z, net in placed:
        state = "nomesh" if z not in meshes else "offmesh" if net is None else "main" if net == main_net else "cutoff"
        summary[kind][state] += 1
        if state in ("cutoff", "offmesh") and kind != "mob":
            lines.append(f"  {state} {kind} {label} at {x},{y},{zz} zone {z}")
    for kind, c in summary.items():
        lines.insert(1, f"  {kind}: " + ", ".join(f"{k} {v}" for k, v in sorted(c.items())))
    cut = [p for p in placed if p[0] == "mob" and p[5] in meshes and p[6] is not None and p[6] != main_net]
    by = Counter((p[5], p[1]) for p in cut)
    lines.append("  monsters off the main network (zone, name, spawns):")
    for (z, name), n in by.most_common(80):
        lines.append(f"    zone {z} {name} x{n}")
    with open(out, "w") as f:
        f.write("\n".join(lines) + "\n")
    with open(out + ".points.tsv", "w") as f:
        for kind, label, x, y, zz, z, net in placed:
            state = "nomesh" if z not in meshes else "offmesh" if net is None else "main" if net == main_net else "cutoff"
            f.write(f"{kind}\t{label}\t{x}\t{y}\t{zz}\t{z}\t{state}\n")
    print(lines[0]); print("\n".join(lines[1:1 + len(summary)]))

def candidates(m, x, y):
    seen = []
    for i in m._grid().get((int(x // m.CELL), int(y // m.CELL)), ()):
        _, pts, bb, _, om = m.polys[i]
        if not om and bb[0] <= x <= bb[2] and bb[1] <= y <= bb[3]:
            from navcomp import _inside
            if _inside(pts, x, y):
                seen.append(i)
    return seen

def poly_z(m, i):
    pts = m.polys[i][1]
    return sum(p[2] for p in pts) / len(pts)

def zone_at(zones, x, y):
    for z, d in zones.items():
        if d["x"] <= x < d["x"] + d["w"] and d["y"] <= y < d["y"] + d["h"]:
            return z
    return None

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5], sys.argv[6] if len(sys.argv) > 6 else None)
