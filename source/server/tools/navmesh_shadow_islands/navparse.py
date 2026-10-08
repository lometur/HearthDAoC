"""Parse a Detour MSET .nav (64-bit poly refs) and compute connected regions.
World coords: (X, Y, Z) with Z up. Nav coords: (X/32, Z/32, Y/32)."""
import struct, sys, math
from collections import defaultdict

INV = 32.0
EXT = 0x8000


class Tile:
    pass


def load(path):
    b = open(path, "rb").read()
    magic, ver, ntiles = struct.unpack_from("<3i", b, 0)
    assert magic == 0x4D534554 and ver == 1
    off = 40
    tiles = []
    for _ in range(ntiles):
        ref, size = struct.unpack_from("<Qi", b, off)
        off += 16
        if ref == 0 or size == 0:
            break
        tiles.append(parse_tile(b, off, size))
        off += size
    return tiles


def a4(x):
    return (x + 3) & ~3


def parse_tile(b, o, size):
    h = struct.unpack_from("<5iI9i3f3f3ff", b, o)
    t = Tile()
    (_, _, t.x, t.y, t.layer, _, pc, vc, mlc, dmc, dvc, dtc, bvc, omc, omb,
     t.wh, t.wr, t.wc) = h[:18]
    t.bmin = h[18:21]
    t.bmax = h[21:24]
    p = o + a4(struct.calcsize("<5iI9i3f3f3ff"))
    t.verts = [struct.unpack_from("<3f", b, p + 12 * i) for i in range(vc)]
    p += a4(12 * vc)
    t.polys = []
    t.flag_offsets = []
    for i in range(pc):
        t.flag_offsets.append(p + 28)
        fl, = struct.unpack_from("<I", b, p)
        vs = struct.unpack_from("<6H", b, p + 4)
        ns = struct.unpack_from("<6H", b, p + 16)
        flags, nv, at = struct.unpack_from("<HBB", b, p + 28)
        t.polys.append((vs[:nv], ns[:nv], flags, at & 0x3F, at >> 6))
        p += 32
    p += a4(16 * mlc)          # links (64-bit ref)
    p += a4(12 * dmc)          # detail meshes
    p += a4(12 * dvc)          # detail verts
    p += a4(4 * dtc)           # detail tris
    p += a4(16 * bvc)          # bv nodes
    t.offmesh = []
    for i in range(omc):
        pos = struct.unpack_from("<6f", b, p)
        rad, = struct.unpack_from("<f", b, p + 24)
        poly, cflags, side = struct.unpack_from("<HBB", b, p + 28)
        t.offmesh.append((pos[:3], pos[3:], rad, poly, cflags & 1, side))
        p += 36
    t.omb = omb
    return t


def to_world(v):
    return (v[0] * INV, v[2] * INV, v[1] * INV)


class Graph:
    def __init__(self, tiles):
        self.tiles = tiles
        self.key = {}
        self.nodes = []      # (tile index, poly index)
        for ti, t in enumerate(tiles):
            for pi in range(len(t.polys)):
                self.key[(ti, pi)] = len(self.nodes)
                self.nodes.append((ti, pi))
        self.parent = list(range(len(self.nodes)))
        self.directed = []   # (from node, to node) one-way off-mesh
        self.bytile = {(t.x, t.y, t.layer): ti for ti, t in enumerate(tiles)}
        self.build()

    def find(self, a):
        while self.parent[a] != a:
            self.parent[a] = self.parent[self.parent[a]]
            a = self.parent[a]
        return a

    def union(self, a, b):
        a, b = self.find(a), self.find(b)
        if a != b:
            self.parent[a] = b

    def edge(self, t, poly, k):
        vs = poly[0]
        return t.verts[vs[k]], t.verts[vs[(k + 1) % len(vs)]]

    def build(self):
        tiles = self.tiles
        # Index external edges per tile per direction.
        extedges = defaultdict(list)   # (ti, dir) -> [(pi, a, b)]
        for ti, t in enumerate(tiles):
            for pi, poly in enumerate(t.polys):
                if poly[4] != 0:
                    continue
                for k, n in enumerate(poly[1]):
                    if n == 0:
                        continue
                    if n & EXT:
                        a, b = self.edge(t, poly, k)
                        extedges[(ti, n & 0xFF)].append((pi, a, b))
                    else:
                        self.union(self.key[(ti, pi)], self.key[(ti, n - 1)])
        # Cross-tile portals. dir 0:+x 2:+z 4:-x 6:-z
        offs = {0: (1, 0), 1: (1, 1), 2: (0, 1), 3: (-1, 1), 4: (-1, 0), 5: (-1, -1), 6: (0, -1), 7: (1, -1)}
        for (ti, d), edges in extedges.items():
            t = tiles[ti]
            dx, dy = offs[d]
            od = (d + 4) % 8
            for tj, u in enumerate(tiles):
                if u.x != t.x + dx or u.y != t.y + dy:
                    continue
                other = extedges.get((tj, od), [])
                axis = 2 if d in (0, 4) else 0   # along-edge axis
                for pi, a, b in edges:
                    lo, hi = sorted((a[axis], b[axis]))
                    for pj, c, e in other:
                        lo2, hi2 = sorted((c[axis], e[axis]))
                        ov_lo, ov_hi = max(lo, lo2), min(hi, hi2)
                        if ov_hi - ov_lo < 0.01:
                            continue
                        # height at overlap ends
                        def hy(p, q, s):
                            if abs(q[axis] - p[axis]) < 1e-6:
                                return p[1]
                            f = (s - p[axis]) / (q[axis] - p[axis])
                            return p[1] + f * (q[1] - p[1])
                        ok = any(abs(hy(a, b, s) - hy(c, e, s)) <= t.wc * 2 for s in (ov_lo, ov_hi))
                        if ok:
                            self.union(self.key[(ti, pi)], self.key[(tj, pj)])
        # Off-mesh connections.
        for ti, t in enumerate(tiles):
            for (s, e, rad, poly, bidir, side) in t.offmesh:
                on = self.key[(ti, poly)]
                sp = self.nearest(s, rad, t.wc)
                ep = self.nearest(e, rad, t.wc)
                if sp is not None:
                    self.union(on, sp)        # start side always joins the link node
                if ep is not None:
                    if bidir:
                        self.union(on, ep)
                    else:
                        self.directed.append((on, ep))

    def poly_world(self, n):
        ti, pi = self.nodes[n]
        t = self.tiles[ti]
        return [to_world(t.verts[v]) for v in t.polys[pi][0]]

    def nearest(self, p, rad, wc):
        best = None
        for ti, t in enumerate(self.tiles):
            if not (t.bmin[0] - rad <= p[0] <= t.bmax[0] + rad and t.bmin[2] - rad <= p[2] <= t.bmax[2] + rad):
                continue
            for pi, poly in enumerate(t.polys):
                if poly[4] != 0:
                    continue
                vs = [t.verts[v] for v in poly[0]]
                d = dist_point_poly(p, vs)
                if d is None:
                    continue
                dh, dy = d
                if dh <= rad and dy <= max(wc, 1.0) + 2:
                    score = dh + dy
                    if best is None or score < best[0]:
                        best = (score, self.key[(ti, pi)])
        return best[1] if best else None


def dist_point_poly(p, vs):
    """2D distance (xz) from p to polygon and vertical gap to its plane-ish height."""
    inside = True
    n = len(vs)
    best = 1e9
    for i in range(n):
        a, b = vs[i], vs[(i + 1) % n]
        cross = (b[0] - a[0]) * (p[2] - a[2]) - (b[2] - a[2]) * (p[0] - a[0])
        if cross < 0:
            inside = False
        best = min(best, seg_dist2d(p, a, b))
    dh = 0 if inside else best
    ys = [v[1] for v in vs]
    lo, hi = min(ys), max(ys)
    dy = 0 if lo - 0.5 <= p[1] <= hi + 0.5 else min(abs(p[1] - lo), abs(p[1] - hi))
    return dh, dy


def seg_dist2d(p, a, b):
    ax, az, bx, bz = a[0], a[2], b[0], b[2]
    dx, dz = bx - ax, bz - az
    L = dx * dx + dz * dz
    t = 0 if L == 0 else max(0, min(1, ((p[0] - ax) * dx + (p[2] - az) * dz) / L))
    return math.hypot(p[0] - ax - t * dx, p[2] - az - t * dz)


def poly_area(ws):
    s = 0
    for i in range(len(ws)):
        a, b = ws[i], ws[(i + 1) % len(ws)]
        s += a[0] * b[1] - b[0] * a[1]
    return abs(s) / 2


def components(g):
    comps = defaultdict(list)
    for n in range(len(g.nodes)):
        ti, pi = g.nodes[n]
        if g.tiles[ti].polys[pi][4] != 0:
            continue
        comps[g.find(n)].append(n)
    return comps


def locate(g, world, tol_h=200, tol_v=150):
    """Node whose polygon contains world point (or nearest within tolerance)."""
    p = (world[0] / INV, world[2] / INV, world[1] / INV)
    best = None
    for n, (ti, pi) in enumerate(g.nodes):
        t = g.tiles[ti]
        poly = t.polys[pi]
        if poly[4] != 0:
            continue
        if not (t.bmin[0] - 8 <= p[0] <= t.bmax[0] + 8 and t.bmin[2] - 8 <= p[2] <= t.bmax[2] + 8):
            continue
        vs = [t.verts[v] for v in poly[0]]
        dh, dy = dist_point_poly(p, vs)
        if dh * INV <= tol_h and dy * INV <= tol_v:
            s = dh * INV + dy * INV
            if best is None or s < best[0]:
                best = (s, n)
    return best[1] if best else None
