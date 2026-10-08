"""Whole-zone picture of a .nav coloured by connected walkable area (biggest = grey), with
optional wall lines (bounds file from export_bounds) and points. Read only.
python areamap.py zone.nav offsetX offsetY out.png [bounds.txt zoneId] [x,y;x,y]"""
import sys
from PIL import Image, ImageDraw
from navcomp import NavAreas
nav, ox, oy, out = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), sys.argv[4]
a = NavAreas(nav)
order = sorted(a.area, key=a.area.get, reverse=True)
palette = [(110, 110, 110), (230, 80, 80), (80, 160, 240), (240, 200, 60), (120, 220, 120), (200, 120, 230), (240, 140, 60), (60, 220, 220)]
colour = {r: palette[i] if i < len(palette) else (90, 60, 60) for i, r in enumerate(order)}
size = 1024; s = size / 65536
img = Image.new("RGB", (size, size), (15, 15, 15)); d = ImageDraw.Draw(img)
def px(x, y): return ((x - ox) * s, size - (y - oy) * s)
for i, (_, pts, _, _, om) in enumerate(a.polys):
    if not om and len(pts) > 2:
        d.polygon([px(x, y) for x, y, _ in pts], fill=colour[a.root[i]])
if len(sys.argv) > 6:
    for line in open(sys.argv[5]):
        if line.split(" ")[0] != sys.argv[6]: continue
        pts = [tuple(map(float, p.split(","))) for p in line.split("|")[1].strip().split(";")]
        d.line([px(x, y) for x, y in pts], fill=(255, 255, 255), width=2)
if len(sys.argv) > 7:
    for p in sys.argv[7].split(";"):
        x, y = px(*map(float, p.split(",")))
        d.ellipse([x - 6, y - 6, x + 6, y + 6], outline=(255, 0, 255), width=3)
tot = sum(a.area.values())
d.text((6, 6), " ".join(f"{a.area[r] / tot:.0%}" for r in order[:6]), fill=(255, 255, 255))
img.save(out); print(out)
