"""Draw a top-down picture of a Detour MSET .nav around a world point (read only).
python navrender.py zoneNNN.nav centerX centerY halfSize out.png [marks: x,y;x,y] [paths.txt]
paths.txt: UT_PathProbe output (status length x,y,z x,y,z ...), each line drawn as a coloured route.
Walkable polygons are shaded by height (dark = low, light = high); mesh edges with no
neighbour (walls, drops, the mesh border) are drawn red; marks are drawn as blue crosses."""
import struct, sys
from PIL import Image, ImageDraw
from navdiff import tiles

def polys(data):
    h = struct.unpack_from("<5iI9i3f3f3ff", data, 0)
    poly_count, vert_count = h[6], h[7]
    off = 100
    verts = struct.unpack_from(f"<{vert_count * 3}f", data, off)
    off += vert_count * 12
    for i in range(poly_count):
        p = off + i * 32
        vs = struct.unpack_from("<6H", data, p + 4)
        neis = struct.unpack_from("<6H", data, p + 16)
        flags, count, area_type = struct.unpack_from("<HBB", data, p + 28)
        if area_type >> 6:  # off-mesh connection
            continue
        pts = [(verts[v * 3] * 32, verts[v * 3 + 2] * 32, verts[v * 3 + 1] * 32) for v in vs[:count]]
        yield pts, neis[:count], flags, area_type & 0x3F

def render(nav, cx, cy, half, out, marks=(), paths=()):
    size = 1024
    scale = size / (2 * half)
    img = Image.new("RGB", (size, size), (20, 20, 20))
    d = ImageDraw.Draw(img)
    _, ts, _ = tiles(nav)
    shapes = []
    for (_, data) in ts.values():
        for pts, neis, flags, area in polys(data):
            if all(abs(x - cx) > half or abs(y - cy) > half for x, y, _ in pts):
                continue
            shapes.append((pts, neis, flags, area))
    if not shapes:
        print("no polygons in view"); return
    zs = [z for s in shapes for _, _, z in s[0]]
    lo, hi = min(zs), max(zs)
    def px(x, y): return ((x - cx + half) * scale, (half - (y - cy)) * scale)
    for pts, neis, flags, area in sorted(shapes, key=lambda s: sum(p[2] for p in s[0]) / len(s[0])):
        z = sum(p[2] for p in pts) / len(pts)
        g = int(60 + 180 * (z - lo) / max(1, hi - lo))
        fill = (g, g, int(g * 0.8)) if flags & 1 else (90, 40, 120)
        if area == 3: fill = (200, 160, 40)  # door
        d.polygon([px(x, y) for x, y, _ in pts], fill=fill, outline=(g // 2, g // 2, g // 2))
    for pts, neis, flags, area in shapes:
        for i, n in enumerate(neis):
            if n == 0:
                a, b = pts[i], pts[(i + 1) % len(pts)]
                d.line([px(a[0], a[1]), px(b[0], b[1])], fill=(230, 40, 40), width=2)
    colours = [(60, 220, 255), (255, 120, 220), (120, 255, 120), (255, 200, 60)]
    for i, route in enumerate(paths):
        if len(route) > 1:
            d.line([px(x, y) for x, y in route], fill=colours[i % len(colours)], width=3)
    for mx, my in marks:
        x, y = px(mx, my)
        d.line([(x - 8, y), (x + 8, y)], fill=(60, 140, 255), width=3)
        d.line([(x, y - 8), (x, y + 8)], fill=(60, 140, 255), width=3)
    d.text((8, 8), f"{nav.split('/')[-1].split(chr(92))[-1]}  center {cx:.0f},{cy:.0f}  width {2 * half:.0f}  z {lo:.0f}-{hi:.0f}", fill=(255, 255, 255))
    img.save(out)
    print(f"{len(shapes)} polygons, z {lo:.0f}-{hi:.0f} -> {out}")

if __name__ == "__main__":
    marks = [tuple(map(float, m.split(","))) for m in sys.argv[6].split(";") if m] if len(sys.argv) > 6 else []
    paths = []
    if len(sys.argv) > 7:
        for line in open(sys.argv[7]):
            parts = line.split()[2:]
            paths.append([tuple(map(float, p.split(",")[:2])) for p in parts])
    render(sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4]), sys.argv[5], marks, paths)
