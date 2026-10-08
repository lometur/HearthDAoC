"""Connected walkable areas of a Detour MSET .nav (read only).

Polygons are joined through their in-tile neighbours and, across tiles, through overlapping
border edges the way Detour links tiles at load time. Off-mesh links (ladders) join their two
end polygons. Use locate() to find which area a world point stands in.
"""
import math, struct
from collections import defaultdict
from navdiff import tiles

SIDE = {0: (1, 0), 2: (0, 1), 4: (-1, 0), 6: (0, -1)}  # Detour tile side -> (dx, dz)

class NavAreas:
    def __init__(self, path):
        _, ts, _ = tiles(path)
        self.polys = []          # (key, verts[(x,y,z) world], bbox)
        self.detail = []         # per polygon: (tile detail data, index in tile)
        self.parent = []
        index = {}
        ext = defaultdict(list)  # (tile x, tile y, side) -> [(gid, a, b)]
        offmesh = []
        inner = []
        self.climb = 0
        for key, (_, data) in ts.items():
            h = struct.unpack_from("<5iI9i3f3f3ff", data, 0)
            tx, ty = h[2], h[3]
            pc, vc = h[6], h[7]
            om_count, om_base = h[13], h[14]
            self.climb = max(self.climb, h[17] * 32)
            verts = struct.unpack_from(f"<{vc * 3}f", data, 100)
            poff = 100 + vc * 12
            base = len(self.polys)
            # Detail mesh (the fine surface Detour uses for heights). Layout after the polygons:
            # links (16 bytes each with 64-bit refs), detail meshes (12), detail verts (12), tris (4).
            max_links, dm_count, dv_count, dt_count = h[8], h[9], h[10], h[11]
            loff = poff + pc * 32
            dmoff = loff + max_links * 16
            dvoff = dmoff + dm_count * 12
            dtoff = dvoff + dv_count * 12
            dverts = struct.unpack_from(f"<{dv_count * 3}f", data, dvoff)
            tile_detail = (data, dmoff, dverts, dtoff)
            for i in range(pc):
                p = poff + i * 32
                vs = struct.unpack_from("<6H", data, p + 4)
                neis = struct.unpack_from("<6H", data, p + 16)
                flags, cnt, at = struct.unpack_from("<HBB", data, p + 28)
                pts = [(verts[v * 3] * 32, verts[v * 3 + 2] * 32, verts[v * 3 + 1] * 32) for v in vs[:cnt]]
                gid = base + i
                xs = [q[0] for q in pts]; ys = [q[1] for q in pts]; zs = [q[2] for q in pts]
                self.polys.append((key, pts, (min(xs), min(ys), max(xs), max(ys), min(zs), max(zs)), flags, at >> 6))
                self.detail.append((tile_detail, i))
                self.parent.append(gid)
                if at >> 6:
                    offmesh.append(gid)
                    continue
                for e, n in enumerate(neis[:cnt]):
                    if n == 0:
                        continue
                    if n & 0x8000:
                        side = n & 0xFF
                        if side in SIDE:
                            ext[(tx, ty, side)].append((gid, pts[e], pts[(e + 1) % cnt]))
                    else:
                        inner.append((gid, base + n - 1))
            # Off-mesh connection polygons have two vertices; join each to the polygon under it.
            index[key] = (base, pc)
        for a, b in inner:
            self._union(a, b)
        self._join_tiles(ext)
        self._join_offmesh(offmesh)
        self.root = [self._find(i) for i in range(len(self.polys))]
        self.area = defaultdict(float)
        for i, (_, pts, _, flags, om) in enumerate(self.polys):
            if not om:
                self.area[self.root[i]] += _area(pts)

    def _find(self, i):
        while self.parent[i] != i:
            self.parent[i] = self.parent[self.parent[i]]
            i = self.parent[i]
        return i

    def _union(self, a, b):
        ra, rb = self._find(a), self._find(b)
        if ra != rb:
            self.parent[ra] = rb

    def _join_tiles(self, ext):
        for (tx, ty, side), edges in ext.items():
            dx, dz = SIDE[side]
            other = ext.get((tx + dx, ty + dz, (side + 4) % 8))
            if not other:
                continue
            along = 1 if dx else 0  # compare the coordinate along the border
            for gid, a, b in edges:
                lo, hi = sorted((a[along], b[along]))
                for gid2, c, d in other:
                    lo2, hi2 = sorted((c[along], d[along]))
                    if min(hi, hi2) - max(lo, lo2) < 1:
                        continue
                    mid = (max(lo, lo2) + min(hi, hi2)) / 2
                    if abs(_height_at(a, b, along, mid) - _height_at(c, d, along, mid)) <= self.climb + 16:
                        self._union(gid, gid2)

    def _join_offmesh(self, offmesh):
        for gid in offmesh:
            for p in self.polys[gid][1][:2]:
                hit = self.locate(p[0], p[1], p[2], skip_offmesh=True, tolerance=64)
                if hit is not None:
                    self._union(gid, hit)

    CELL = 512

    def height(self, i, x, y):
        """Detour's height of polygon i at (x, y), from its detail triangles."""
        (data, dmoff, dverts, dtoff), local = self.detail[i]
        pts = self.polys[i][1]
        if self.polys[i][4]:
            return height_at(pts, x, y)
        vbase, tbase, vcount, tcount = struct.unpack_from("<IIBB", data, dmoff + local * 12)
        def vert(k):
            if k < len(pts):
                return pts[k]
            j = (vbase + k - len(pts)) * 3
            return (dverts[j] * 32, dverts[j + 2] * 32, dverts[j + 1] * 32)
        for t in range(tcount):
            a, b, c, _ = struct.unpack_from("<4B", data, dtoff + (tbase + t) * 4)
            tri = [vert(a), vert(b), vert(c)]
            z = _tri_height(tri, x, y)
            if z is not None:
                return z
        return height_at(pts, x, y)

    def _grid(self):
        if getattr(self, "_cells", None) is None:
            self._cells = defaultdict(list)
            for i, (_, _, bb, _, _) in enumerate(self.polys):
                for cx in range(int(bb[0] // self.CELL), int(bb[2] // self.CELL) + 1):
                    for cy in range(int(bb[1] // self.CELL), int(bb[3] // self.CELL) + 1):
                        self._cells[(cx, cy)].append(i)
        return self._cells

    def locate(self, x, y, z, skip_offmesh=True, tolerance=256):
        best, best_dz = None, tolerance
        for i in self._grid().get((int(x // self.CELL), int(y // self.CELL)), ()):
            _, pts, bb, _, om = self.polys[i]
            if om and skip_offmesh:
                continue
            if x < bb[0] or x > bb[2] or y < bb[1] or y > bb[3]:
                continue
            if z < bb[4] - tolerance or z > bb[5] + tolerance or not _inside(pts, x, y):
                continue
            dz = abs(self.height(i, x, y) - z)
            if dz < best_dz:
                best, best_dz = i, dz
        return best

def _tri_height(tri, x, y):
    (ax, ay, az), (bx, by, bz), (cx, cy, cz) = tri
    den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
    if abs(den) < 1e-9:
        return None
    l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / den
    l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / den
    l3 = 1 - l1 - l2
    if min(l1, l2, l3) < -1e-4:
        return None
    return l1 * az + l2 * bz + l3 * cz

def height_at(pts, x, y):
    """Height of a convex polygon at (x, y), from the fan triangle that contains the point."""
    a = pts[0]
    for i in range(1, len(pts) - 1):
        b, c = pts[i], pts[i + 1]
        den = (b[1] - c[1]) * (a[0] - c[0]) + (c[0] - b[0]) * (a[1] - c[1])
        if abs(den) < 1e-9:
            continue
        l1 = ((b[1] - c[1]) * (x - c[0]) + (c[0] - b[0]) * (y - c[1])) / den
        l2 = ((c[1] - a[1]) * (x - c[0]) + (a[0] - c[0]) * (y - c[1])) / den
        l3 = 1 - l1 - l2
        if min(l1, l2, l3) >= -1e-4:
            return l1 * a[2] + l2 * b[2] + l3 * c[2]
    return sum(q[2] for q in pts) / len(pts)

def _height_at(a, b, along, v):
    if abs(b[along] - a[along]) < 1e-6:
        return (a[2] + b[2]) / 2
    t = (v - a[along]) / (b[along] - a[along])
    return a[2] + (b[2] - a[2]) * t

def _inside(pts, x, y):
    c = False
    j = len(pts) - 1
    for i in range(len(pts)):
        xi, yi = pts[i][0], pts[i][1]; xj, yj = pts[j][0], pts[j][1]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi) + xi:
            c = not c
        j = i
    return c

def _area(pts):
    s = 0.0
    for i in range(len(pts)):
        x1, y1 = pts[i][0], pts[i][1]; x2, y2 = pts[(i + 1) % len(pts)][0], pts[(i + 1) % len(pts)][1]
        s += x1 * y2 - x2 * y1
    return abs(s) / 2
