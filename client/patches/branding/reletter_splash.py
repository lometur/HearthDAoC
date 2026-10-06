#!/usr/bin/env python3
"""Re-letter OfflineDAoC's loading splash to "HEARTH DAoC" (dev-time only; needs Pillow).

The art is OfflineDAoC's: splash1.tga of its 0.34 pregame/splash.mpk (the same bytes as
source/tools/OfflineDaoc.Launcher/Assets/offline-daoc-client-splash.mpk). Only the title
between the two gold rules changes: the old letters are painted over with sky (a smooth fill
from the pixels around them plus cloud texture sampled from the sky above), and "HEARTH DAoC"
is drawn in Cinzel Bold (SIL Open Font License, downloaded from a pinned Google Fonts commit
and checked) as gold with a dark outline and shadow. "CLASSIC + SHROUDED ISLES", the rules
and the rest of the art stay byte for byte.

    pip install Pillow
    python3 client/patches/branding/reletter_splash.py

writes client/patches/branding/splash.png (committed). build_splash_mpk.py turns it into the
client's splash.mpk.
"""
import argparse
import hashlib
import os
import random
import sys
import tempfile
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PATCHES = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PATCHES))
sys.path.insert(0, PATCHES)
# Pillow is imported inside the functions, so the tests can read the constants without it.
from mpk import read_mpk  # noqa: E402
from splash_entry import HEIGHT, STOCK_SPLASH_SHA256, WIDTH  # noqa: E402

FONT_URL = ("https://raw.githubusercontent.com/google/fonts/"
            "45071f07c63e863a539442ef3562b71ab1f147a6/ofl/cinzel/Cinzel%5Bwght%5D.ttf")
FONT_SHA256 = "f4d83d34d1f6c741193e4acf4b3dff9531e5a67b6aa65228d00a7db72a4e0f34"
DEFAULT_SOURCE = os.path.join(REPO, "source", "tools", "OfflineDaoc.Launcher", "Assets",
                              "offline-daoc-client-splash.mpk")
DEFAULT_OUT = os.path.join(HERE, "splash.png")

# Every changed pixel lies in TITLE_BOX (left, top, right, bottom; right and bottom
# exclusive). Above it is the upper gold rule (rows 74-75), below it the lower one (row 182).
TITLE_BOX = (180, 77, 886, 182)
OLD_LETTERS = (204, 79, 864, 172)  # the "OFFLINE DAoC" letters
FILL_BOX = (148, 45, 918, 214)  # the art the smooth fill is computed from
GROW = 6  # pixels the old letters' mask grows by, to take in their outline and shadow
SKY_ROWS = (2, 73)  # sky above the upper rule: the cloud texture's source
ORNAMENT, ORNAMENT_SHIFT = (480, 546), 70  # the ornament's columns, replaced from further left
# A soft dark plate behind the new title: opacity, inset from TITLE_BOX (x, y), edge blur.
PLATE, PLATE_INSET, PLATE_BLUR = 0.4, (26, 12), 9

# The new title: (text, big capitals). Cinzel draws lower case as small capitals.
TITLE = [("H", True), ("EARTH", False), (" ", False), ("D", True), ("A", False), ("o", False),
         ("C", True)]
BIG_SIZE, SMALL_SIZE, CONDENSE = 106, 86, 0.9
BASELINE = 164  # the old title's baseline
CENTRE_X = 533  # the old title's and the subtitle's centre
GOLD = [(0.0, (250, 222, 150)), (0.3, (220, 170, 78)), (0.55, (160, 108, 38)),
        (0.75, (204, 150, 66)), (1.0, (110, 70, 24))]  # top to baseline


def fetch_font(folder):
    """Download the pinned Cinzel font into folder and return its path."""
    with urllib.request.urlopen(FONT_URL, timeout=60) as r:
        data = r.read()
    digest = hashlib.sha256(data).hexdigest()
    if digest != FONT_SHA256:
        raise SystemExit(f"Cinzel download has SHA-256 {digest}, expected {FONT_SHA256}")
    path = os.path.join(folder, "Cinzel-wght.ttf")
    with open(path, "wb") as f:
        f.write(data)
    return path


def load_art(source):
    """Return splash1.tga of OfflineDAoC 0.34's splash.mpk as an RGB image."""
    from PIL import Image
    with open(source, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    if digest != STOCK_SPLASH_SHA256:
        raise SystemExit(f"{source} has SHA-256 {digest}; this script re-letters OfflineDAoC "
                         f"0.34's splash.mpk ({STOCK_SPLASH_SHA256}) only")
    data = dict(read_mpk(source)[1])["splash1.tga"]
    pixels = data[18:18 + WIDTH * HEIGHT * 4]
    return Image.frombytes("RGBA", (WIDTH, HEIGHT), pixels, "raw", "BGRA", 0, -1).convert("RGB")


def gold_pixels(art, box):
    """Mask (as big as the art) of the gold pixels inside box."""
    from PIL import Image
    mask = Image.new("L", art.size, 0)
    src, dst = art.load(), mask.load()
    left, top, right, bottom = box
    for y in range(top, bottom):
        for x in range(left, right):
            r, g, b = src[x, y]
            # Bright, warm and yellow (green at least 0.69 of red): the sunset clouds are redder.
            if r * 299 + g * 587 + b * 114 > 60000 and r > b + 20 and g * 100 >= r * 69:
                dst[x, y] = 255
    return mask


def old_letter_mask(art):
    """Mask of the old title: its gold pixels, grown over outline and shadow, in TITLE_BOX."""
    from PIL import Image, ImageChops, ImageFilter
    grown = gold_pixels(art, OLD_LETTERS).filter(ImageFilter.MaxFilter(GROW * 2 + 1))
    box = Image.new("L", art.size, 0)
    box.paste(255, TITLE_BOX)
    return ImageChops.multiply(grown.filter(ImageFilter.GaussianBlur(1)), box)


def smooth_fill(img, hole):
    """Fill the hole (255) from the pixels around it, coarse to fine (a membrane fill)."""
    from PIL import Image, ImageFilter
    w, h = img.size
    cur = None
    for scale, rounds in ((16, 300), (8, 150), (4, 80), (2, 40), (1, 20)):
        size = (w // scale, h // scale)
        known = img.resize(size, Image.BOX)
        cells = hole.resize(size, Image.BOX).point(lambda v: 255 if v else 0)
        guess = known if cur is None else cur.resize(size, Image.BILINEAR)
        cur = Image.composite(guess, known, cells)
        for _ in range(rounds):
            cur = Image.composite(cur.filter(ImageFilter.BoxBlur(1)), known, cells)
    return cur


def sky_texture(art):
    """Cloud texture for TITLE_BOX as a high-pass image around 128.

    The sky above the upper rule is mirrored down over the title rows again and again, so the
    copies meet without seams; the ornament's columns come from further left.
    """
    from PIL import Image, ImageChops, ImageFilter, ImageOps
    left, top, right, bottom = TITLE_BOX
    band = art.crop((left, SKY_ROWS[0], right, SKY_ROWS[1]))
    o_left, o_right = ORNAMENT[0] - left, ORNAMENT[1] - left
    band.paste(band.crop((o_left - ORNAMENT_SHIFT, 0, o_right - ORNAMENT_SHIFT, band.height)),
               (o_left, 0))
    tiles = [ImageOps.flip(band), band]
    src = Image.new("RGB", (right - left, bottom - top))
    for k, y in enumerate(range(0, bottom - top, band.height)):
        src.paste(tiles[k % 2], (0, y))
    return ImageChops.subtract(src, src.filter(ImageFilter.GaussianBlur(3)), 1.0, 128)


def cover_old_title(art):
    """Return the art with the old title painted over with sky."""
    from PIL import Image, ImageChops, ImageFilter
    hole = old_letter_mask(art)
    # The rules, the ornament and the subtitle mustn't tint the fill: leave them out of it.
    decorations = gold_pixels(art, FILL_BOX).filter(ImageFilter.MaxFilter(5))
    unknown = ImageChops.lighter(hole, decorations)
    sky = art.copy()
    sky.paste(smooth_fill(art.crop(FILL_BOX), unknown.crop(FILL_BOX)), FILL_BOX[:2])
    sky.paste(ImageChops.add(sky.crop(TITLE_BOX), sky_texture(art), 1.0, -128), TITLE_BOX[:2])
    covered = Image.composite(sky, art, hole)
    plate = Image.new("L", art.size, 0)
    left, top, right, bottom = TITLE_BOX
    dx, dy = PLATE_INSET
    plate.paste(round(255 * PLATE), (left + dx, top + dy, right - dx, bottom - dy))
    plate = plate.filter(ImageFilter.GaussianBlur(PLATE_BLUR))
    return Image.composite(Image.new("RGB", art.size, (6, 5, 8)), covered, plate)


def title_mask(font_path, size):
    """Return (coverage mask of the new title, top row of its big capitals)."""
    from PIL import Image, ImageDraw, ImageFont

    def font(px):
        f = ImageFont.truetype(font_path, px)
        f.set_variation_by_name("Bold")
        return f

    big, small = font(BIG_SIZE), font(SMALL_SIZE)
    glyphs = [(ch, big if is_big else small) for text, is_big in TITLE for ch in text]
    width = sum(f.getlength(ch) for ch, f in glyphs)
    wide = Image.new("L", (round(size[0] / CONDENSE), size[1]), 0)
    draw = ImageDraw.Draw(wide)
    x = CENTRE_X / CONDENSE - width / 2
    for ch, f in glyphs:
        draw.text((x, BASELINE), ch, font=f, fill=255, anchor="ls")
        x += f.getlength(ch)
    top = BASELINE + big.getbbox("H", anchor="ls")[1]
    return wide.resize(size, Image.LANCZOS), top


def gold(mask, top):
    """Gold letters: a metal gradient, worn blotches, grain and a bevel lit from top left."""
    from PIL import Image, ImageChops, ImageFilter
    w, h = mask.size
    column = Image.new("RGB", (1, h))
    for y in range(h):
        t = min(1.0, max(0.0, (y - top) / float(BASELINE - top)))
        for (t0, c0), (t1, c1) in zip(GOLD, GOLD[1:]):
            if t <= t1:
                k = (t - t0) / (t1 - t0)
                column.putpixel((0, y), tuple(round(a + (b - a) * k) for a, b in zip(c0, c1)))
                break
    fill = column.resize((w, h))
    rng = random.Random(1)
    blots = Image.frombytes("L", (w // 6, h // 6), rng.randbytes((w // 6) * (h // 6)))
    blots = blots.resize((w, h), Image.BICUBIC).filter(ImageFilter.GaussianBlur(2))
    blots = blots.point(lambda v: 255 if v > 100 else 165 + v * 9 // 10)
    grain = Image.frombytes("L", (w, h), rng.randbytes(w * h)).point(lambda v: 220 + v // 8)
    fill = ImageChops.multiply(fill, Image.merge("RGB", [blots] * 3))
    fill = ImageChops.multiply(fill, Image.merge("RGB", [grain] * 3))
    soft = mask.filter(ImageFilter.GaussianBlur(1.5))
    moved = ImageChops.offset(soft, 2, 2)
    lit = ImageChops.subtract(soft, moved).point(lambda v: min(255, v * 2))
    shade = ImageChops.subtract(moved, soft).point(lambda v: min(255, v * 2))
    fill = ImageChops.screen(fill, Image.merge("RGB", [lit, lit, lit.point(lambda v: v * 3 // 4)]))
    return ImageChops.multiply(fill, Image.merge("RGB", [ImageChops.invert(shade)] * 3))


def reletter(art, font_path):
    """Return the re-lettered splash as a new RGB image; only TITLE_BOX changes."""
    from PIL import Image, ImageChops, ImageFilter
    work = cover_old_title(art)
    mask, cap_top = title_mask(font_path, art.size)

    def paint(colour, alpha):
        return Image.composite(Image.new("RGB", art.size, colour), work, alpha)

    halo = mask.filter(ImageFilter.MaxFilter(7)).filter(ImageFilter.GaussianBlur(5))
    work = paint((10, 8, 12), halo.point(lambda v: v * 55 // 100))
    shadow = ImageChops.offset(mask.filter(ImageFilter.GaussianBlur(2.5)), 2, 3)
    work = paint((0, 0, 0), shadow.point(lambda v: v * 85 // 100))
    outline = mask.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.GaussianBlur(0.6))
    work = paint((52, 32, 12), outline)
    work = Image.composite(gold(mask, cap_top), work, mask)
    # Blend into the art over the box's outer 2 pixels, so its edge leaves no seam.
    left, top, right, bottom = TITLE_BOX
    edge = Image.new("L", art.size, 0)
    edge.paste(255, (left + 2, top + 2, right - 2, bottom - 2))
    box = Image.new("L", art.size, 0)
    box.paste(255, TITLE_BOX)
    edge = ImageChops.multiply(edge.filter(ImageFilter.GaussianBlur(1)), box)
    return Image.composite(work, art, edge)


def main(argv=None):
    ap = argparse.ArgumentParser(description="Re-letter OfflineDAoC's splash to HEARTH DAoC.")
    ap.add_argument("--source", default=DEFAULT_SOURCE,
                    help="OfflineDAoC 0.34's pregame/splash.mpk (default: the repo's copy)")
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--font", help="a local copy of the pinned Cinzel font (default: download)")
    args = ap.parse_args(argv)
    art = load_art(args.source)
    with tempfile.TemporaryDirectory(prefix="hdc-font-") as tmp:
        font_path = args.font or fetch_font(tmp)
        with open(font_path, "rb") as f:
            if hashlib.sha256(f.read()).hexdigest() != FONT_SHA256:
                raise SystemExit(f"{font_path} isn't the pinned Cinzel font")
        result = reletter(art, font_path)
    result.save(args.out, optimize=True)
    print(f"Wrote {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
