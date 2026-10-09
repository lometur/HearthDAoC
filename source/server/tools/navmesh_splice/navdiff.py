"""Compare two Detour MSET .nav files tile by tile; optionally splice tiles from B into A."""
import struct, sys

def tiles(path):
    b = open(path, "rb").read()
    magic, ver, n = struct.unpack_from("<3i", b, 0)
    assert magic == 0x4D534554 and ver == 1
    off = 40
    out = {}
    order = []
    for _ in range(n):
        ref, size = struct.unpack_from("<Qi", b, off)
        if ref == 0 or size == 0:
            break
        data = b[off + 16: off + 16 + size]
        # dtMeshHeader: magic, version, x, y, layer
        _, _, x, y, layer = struct.unpack_from("<5i", data, 0)
        out[(x, y, layer)] = (ref, data)
        order.append((x, y, layer))
        off += 16 + size
    return b[:40], out, order

def world_bounds(data):
    h = struct.unpack_from("<5iI9i3f3f3ff", data, 0)
    bmin, bmax = h[18:21], h[21:24]
    return (bmin[0] * 32, bmin[2] * 32, bmax[0] * 32, bmax[2] * 32)

if __name__ == "__main__":
    a, b = sys.argv[1], sys.argv[2]
    ha, ta, oa = tiles(a)
    hb, tb, ob = tiles(b)
    print(f"A {len(ta)} tiles, B {len(tb)} tiles, header same={ha == hb}")
    only_a = set(ta) - set(tb); only_b = set(tb) - set(ta)
    diff = [k for k in ta if k in tb and ta[k][1] != tb[k][1]]
    print(f"only in A: {len(only_a)}, only in B: {len(only_b)}, differing: {len(diff)}")
    for k in sorted(diff)[:40]:
        x0, y0, x1, y1 = world_bounds(tb[k][1])
        print(f"  tile {k} world x {x0:.0f}-{x1:.0f} y {y0:.0f}-{y1:.0f} sizeA={len(ta[k][1])} sizeB={len(tb[k][1])}")

def splice(installed, built, out, px, py, half):
    """Copy every tile of `built` whose bounds touch the square of `half` around (px,py)
    into `installed` (keeping installed tile refs and order). Returns spliced keys."""
    raw = open(installed, "rb").read()
    head, ta, order = tiles(installed)
    _, tb, _ = tiles(built)
    picked = []
    body = bytearray(head)
    off = 40
    for k in order:
        ref, data = ta[k]
        pad = raw[off + 12: off + 16]
        off += 16 + len(data)
        if k in tb:
            x0, y0, x1, y1 = world_bounds(tb[k][1])
            if x1 >= px - half and x0 <= px + half and y1 >= py - half and y0 <= py + half:
                data = tb[k][1]
                picked.append(k)
        body += struct.pack("<Qi", ref, len(data)) + pad + data
    # keep any trailing bytes after the last tile (terminator / padding) exactly
    end = 40
    for k in order:
        end += 16 + len(ta[k][1])
    body += raw[end:]
    open(out, "wb").write(body)
    return picked
