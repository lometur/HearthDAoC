"""Disable walkable 'shadow' islands: small navmesh regions that no entrance or spawn uses and
that sit within 256 units above/below a used floor (crawlspaces under floors, tops of blocks).
Floor snapping (vertical tolerance up to 256) could otherwise land on them and strand a route."""
import sys, sqlite3, math, struct, os
from collections import defaultdict
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from navparse import *
if len(sys.argv) != 5:
    sys.exit("usage: shadowfix.py <in.nav> <out.nav> <region id> <opendaoc.sqlite3.db>")
src, dst, region, DB = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4]
db = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
ents = db.execute("select TargetX,TargetY,TargetZ from ZonePoint where TargetRegion=?", (region,)).fetchall()
ents += db.execute("select SourceX,SourceY,SourceZ from ZonePoint where SourceRegion=?", (region,)).fetchall()
mobs = db.execute("select X,Y,Z from Mob where Region=?", (region,)).fetchall()
tiles = load(src)
g = Graph(tiles)
comps = components(g)
area = {r: sum(poly_area([(w[0], w[1]) for w in g.poly_world(n)]) for n in ns) for r, ns in comps.items()}
protected = set()
for p in list(ents) + list(mobs):
    n = locate(g, p, 250, 300)
    if n is not None: protected.add(g.find(n))
big = {r for r in comps if area[r] >= 2e6}
# Never touch a region joined to a ladder/climb link (its pads sit close to floors by design).
for ti, tl in enumerate(tiles):
    for pi, poly in enumerate(tl.polys):
        if poly[4] != 0: protected.add(g.find(g.key[(ti, pi)]))
for ti, tl in enumerate(tiles):
    for (s, e, rad, poly, bidir, side) in tl.offmesh:
        for q in (s, e):
            n = g.nearest(q, rad + 2, tl.wc + 4)
            if n is not None: protected.add(g.find(n))
protected |= big
print(f"regions {len(comps)}, protected {len(protected)} (large {len(big)})")
# spatial hash of protected polys
cell = defaultdict(list)
for r in protected:
    for n in comps[r]:
        ws = g.poly_world(n)
        xs = [w[0] for w in ws]; ys = [w[1] for w in ws]
        for cx in range(int(min(xs)) // 256, int(max(xs)) // 256 + 1):
            for cy in range(int(min(ys)) // 256, int(max(ys)) // 256 + 1):
                cell[(cx, cy)].append(n)
def inside(pt, ws):
    s = 0; n = len(ws)
    for i in range(n):
        a, b = ws[i], ws[(i + 1) % n]
        c = (b[0] - a[0]) * (pt[1] - a[1]) - (b[1] - a[1]) * (pt[0] - a[0])
        if c != 0:
            if s == 0: s = 1 if c > 0 else -1
            elif (c > 0) != (s > 0): return False
    return True
def floor_z(pt, ws):
    return sum(w[2] for w in ws) / len(ws)
# Climb/ladder chains use small pads and landings near floors: keep every island near one.
link_cells = set()
for tl in tiles:
    for (s, e, rad, poly, bidir, side) in tl.offmesh:
        for q in (s, e):
            link_cells.add((int(q[0] * INV) // 600, int(q[2] * INV) // 600))
def near_link(ns):
    for n in ns:
        for w in g.poly_world(n):
            cx, cy = int(w[0]) // 600, int(w[1]) // 600
            if any((cx + dx, cy + dy) in link_cells for dx in (-1, 0, 1) for dy in (-1, 0, 1)):
                return True
    return False
disable = []
for r, ns in comps.items():
    if r in protected or area[r] >= 2e6: continue
    near = 0
    for n in ns:
        ws = g.poly_world(n)
        c = (sum(w[0] for w in ws) / len(ws), sum(w[1] for w in ws) / len(ws), sum(w[2] for w in ws) / len(ws))
        for m in cell.get((int(c[0]) // 256, int(c[1]) // 256), []):
            mw = g.poly_world(m)
            if inside(c, mw) and abs(floor_z(c, mw) - c[2]) <= 256:
                near += 1; break
    if near and not near_link(ns):
        disable.append((r, near))
b = bytearray(open(src, "rb").read())
count = 0
for r, near in disable:
    for n in comps[r]:
        ti, pi = g.nodes[n]
        t = tiles[ti]
        vs, ns_, flags, area_id, ptype = t.polys[pi]
        if ptype != 0 or flags & 0x04: continue
        off = t.flag_offsets[pi]
        assert struct.unpack_from("<H", b, off)[0] == flags
        struct.pack_into("<H", b, off, 0x10)  # Disabled only: the include filter passes any other bit
        count += 1
    ws = [w for n in comps[r] for w in g.poly_world(n)]
    print(f"  shadow island polys={len(comps[r])} overlapping={near} area={area[r]:.0f} at {sum(w[0] for w in ws)/len(ws):.0f},{sum(w[1] for w in ws)/len(ws):.0f},{sum(w[2] for w in ws)/len(ws):.0f}")
open(dst, "wb").write(b)
print(f"disabled {count} polygons in {len(disable)} islands -> {dst}")
