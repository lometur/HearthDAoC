"""Zone-border crossings that join real, reachable ground (for the server's zone itinerary).

For every region: split each zone mesh into connected walkable areas (navcomp), join areas of
neighbouring zones where both meshes have floor on the shared border within the server's seam
rule (96 units across, at most 128 apart in height), then call a joined network "real" when it
holds a bind point, a zone point (source or target) or a town NPC (merchant, trainer, healer,
stable master...). Walled-off mountain shelves and pockets past the client's invisible walls hold
none of these. Every border sample whose both sides lie on a real network is written out; the
server tries these samples first when the file is present. Each point is
[along, inside floor z, outside floor z, inside area id, outside area id] (area ids: connected
walkable areas of that zone's mesh, stable for one mesh build; regenerate with the meshes).

With a sixth argument, also writes the walled-off pockets: walkable areas of at least POCKET_POLYS
polygons that are not on a real network, as 512-unit cells with the pocket's floor height range in
each (cells where real ground lies in the same height range are left out). The server relocates a
bot found inside one (it can never walk out).

python seamgraph.py <zones.dat> <db> <navdir> <out.json> [regions, default 1,100,200,51,151,181] [pockets.json]
"""
import json, os, sqlite3, sys
from collections import defaultdict
from navcomp import NavAreas
from regionaudit import load_zones, candidates, zone_at

STEP = 64
CELL = 512
POCKET_POLYS = 30

def build(zonesdat, db, navdir, regions):
    allzones = load_zones(zonesdat)
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    out = {}
    pockets = {}
    for region in regions:
        zones = {z: d for z, d in allzones.items() if d["region"] == region}
        meshes = {z: NavAreas(os.path.join(navdir, f"zone{z:03d}.nav")) for z in sorted(zones)
                  if os.path.exists(os.path.join(navdir, f"zone{z:03d}.nav"))}
        parent = {}
        def find(k):
            parent.setdefault(k, k)
            while parent[k] != k:
                parent[k] = parent[parent[k]]; k = parent[k]
            return k
        def union(a, b):
            ra, rb = find(a), find(b)
            if ra != rb: parent[ra] = rb
        samples = []   # (from, to, vertical, border, coord, za, zb, keyA, keyB)
        ids = sorted(meshes)
        for i, a in enumerate(ids):
            for b in ids:
                if a == b: continue
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
                    for c in range(int(lo) + 96, int(hi) - 96, STEP):
                        pa = (border - sa * 48, c) if vertical else (c, border - sa * 48)
                        pb = (border + sa * 48, c) if vertical else (c, border + sa * 48)
                        for ia in candidates(meshes[a], *pa):
                            za = meshes[a].height(ia, *pa)
                            ib = meshes[b].locate(pb[0], pb[1], za, tolerance=128)
                            if ib is None: continue
                            zb = meshes[b].height(ib, *pb)
                            ka, kb = (a, meshes[a].root[ia]), (b, meshes[b].root[ib])
                            union(ka, kb)
                            samples.append((a, b, vertical, border, c, round(za), round(zb), ka, kb))
        # Real networks: those holding a bind point, a zone point or a town NPC.
        anchors = []
        anchors += [(x, y, z) for x, y, z in con.execute("select X, Y, Z from bindpoint where Region=?", (region,))]
        anchors += [(x, y, z) for x, y, z in con.execute("select SourceX, SourceY, SourceZ from zonepoint where SourceRegion=? and SourceX>0", (region,))]
        anchors += [(x, y, z) for x, y, z in con.execute("select TargetX, TargetY, TargetZ from zonepoint where TargetRegion=?", (region,))]
        anchors += [(x, y, z) for x, y, z in con.execute(
            "select X, Y, Z from mob where Region=? and (ClassType like '%Merchant%' or ClassType like '%Trainer%' or "
            "ClassType like '%Healer%' or ClassType like '%Stable%' or ClassType like '%Teleport%')", (region,))]
        real = set()
        for x, y, z in anchors:
            zid = zone_at(zones, x, y)
            if zid not in meshes: continue
            idx = meshes[zid].locate(x, y, z, tolerance=256)
            if idx is not None:
                real.add(find((zid, meshes[zid].root[idx])))
        # Each point also carries the walkable area (connected component of that zone's mesh) on
        # each side, so the server can follow a crossing's ground from border to border exactly.
        edges = defaultdict(list)
        for a, b, vertical, border, c, za, zb, ka, kb in samples:
            if find(ka) in real:
                edges[(a, b, vertical, border)].append([c, za, zb, ka[1], kb[1]])
        out[str(region)] = [{"from": a, "to": b, "vertical": v, "border": bd, "points": pts}
                            for (a, b, v, bd), pts in sorted(edges.items())]
        # Pockets: per zone, cells -> floor height bands of non-real areas, minus real ground at that height.
        for zid, m in meshes.items():
            sizes = defaultdict(int)
            for r in m.root: sizes[r] += 1
            bands, solid = defaultdict(list), defaultdict(list)
            for i, (_, pts, bb, _, om) in enumerate(m.polys):
                if om: continue
                trapped = find((zid, m.root[i])) not in real and sizes[m.root[i]] >= POCKET_POLYS
                for cx in range(int(bb[0] // CELL), int(bb[2] // CELL) + 1):
                    for cy in range(int(bb[1] // CELL), int(bb[3] // CELL) + 1):
                        (bands if trapped else solid)[(cx, cy)].append((bb[4], bb[5]))
            cells = []
            for (cx, cy), spans in bands.items():
                lo, hi = min(s[0] for s in spans), max(s[1] for s in spans)
                if any(s[0] <= hi + 192 and s[1] >= lo - 192 for s in solid.get((cx, cy), ())): continue
                cells.append([cx, cy, round(lo), round(hi)])
            if cells: pockets[str(zid)] = sorted(cells)
        kept = sum(len(p) for p in edges.values())
        print(f"region {region}: {len(meshes)} meshes, {len(samples)} border samples, {kept} on real networks, "
              f"{len(edges)} zone borders usable, {len(real)} real networks, "
              f"{sum(len(pockets.get(str(z), ())) for z in meshes)} pocket cells")
    return out, pockets

if __name__ == "__main__":
    regions = [int(r) for r in (sys.argv[5] if len(sys.argv) > 5 else "1,100,200,51,151,181").split(",")]
    data, pockets = build(sys.argv[1], sys.argv[2], sys.argv[3], regions)
    with open(sys.argv[4], "w") as f:
        json.dump({"version": 1, "step": STEP, "regions": data}, f, separators=(",", ":"))
    if len(sys.argv) > 6:
        with open(sys.argv[6], "w") as f:
            json.dump({"version": 1, "cell": CELL, "zones": pockets}, f, separators=(",", ":"))
