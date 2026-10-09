"""Draw the realm war map tiles from the classic frontier zone maps (owner 2026-10-08), for the isolated 1.127 client.

Each realm war map window shows four 128x128 tiles cut from one 256x256 texture (ui/isles/albmap.tga, midmap.tga,
hibmap2.tga; both UI skins read the isles copies). The stock textures are New Frontiers zone maps. This writes the
classic frontier zone map (the client's own ui/maps/zNNN.dds, scaled to 128x128) into the quadrant each tile
control reads, so the tile at each window position shows the classic zone that lies there - the same zones and
positions patch_classic_warmap_client.py uses for the keep icons:

  window control (XML)  quadrant  Albion               Midgard              Hibernia
  1000                  (0,128)   Forest Sauvage (11)  Yggdra Forest (112)  Cruachan Gorge (211)
  1001                  (0,0)/(128,0) see QUADRANTS: the template each control uses decides its quadrant
  ...

The output keeps the original file format (24-bit uncompressed TGA, bottom-up rows, TGA 2.0 footer).
    python build_classic_warmap_textures.py <client app folder> [--out <folder>]   (default: write in place)
"""

import argparse
from pathlib import Path
from PIL import Image

# texture -> {quadrant top-left: classic zone id}; the quadrant is the one the template of the control at that zone's
# window position reads (realmwar_*.xml): Albion 1000 warmap_pennine (0,128) -> Forest Sauvage, 1001 warmap_hadrians
# (0,0) -> Hadrian's Wall, 1002 warmap_sauvage (128,128) -> Snowdonia, 1003 warmap_snowdonia (128,0) -> Pennine.
QUADRANTS = {
    'albmap.tga': {(0, 0): 15, (128, 0): 14, (0, 128): 11, (128, 128): 12},
    'midmap.tga': {(0, 0): 113, (128, 0): 111, (0, 128): 112, (128, 128): 115},
    'hibmap2.tga': {(0, 0): 210, (128, 0): 214, (0, 128): 211, (128, 128): 212},
}
FOOTER = bytes(8) + b'TRUEVISION-XFILE.' + bytes(1)


def write_tga(image, path):
    image = image.convert('RGB')
    w, h = image.size
    header = bytes([0, 0, 2]) + bytes(9) + w.to_bytes(2, 'little') + h.to_bytes(2, 'little') + bytes([24, 0])
    rows = []
    px = image.tobytes()
    for y in range(h - 1, -1, -1):                     # bottom-up (descriptor 0)
        row = px[y * w * 3:(y + 1) * w * 3]
        rows.append(bytes(b for i in range(0, len(row), 3) for b in (row[i + 2], row[i + 1], row[i])))
    path.write_bytes(header + b''.join(rows) + FOOTER)


def build(app, out):
    maps = app / 'ui' / 'maps'
    for name, quads in QUADRANTS.items():
        texture = Image.new('RGB', (256, 256))
        for (x, y), zone in quads.items():
            tile = Image.open(maps / f'z{zone:03d}.dds').convert('RGB').resize((128, 128), Image.LANCZOS)
            texture.paste(tile, (x, y))
        target = out / name
        write_tga(texture, target)
        check = Image.open(target).convert('RGB')
        assert check.size == (256, 256) and check.getpixel((5, 5)) == texture.getpixel((5, 5)), name
        print('wrote', target)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app', type=Path, help='client app folder (holds ui/maps and ui/isles)')
    parser.add_argument('--out', type=Path, help='output folder (default: <app>/ui/isles)')
    args = parser.parse_args()
    build(args.app, args.out or (args.app / 'ui' / 'isles'))
