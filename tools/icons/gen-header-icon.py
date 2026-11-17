"""Header glyph for the Loadout Switcher window: the launcher icon's swap-arrows motif (top arrow right, bottom arrow
left), redrawn PER SIZE on the pixel grid (house icon rule, Stellar-Design-Language.md section 3: never one master scaled
down — thin strokes go soft grey). White flat glyph on transparent; the accent tile behind it is drawn by the plugin.

Every stroke is built from whole-pixel w x w blocks, so shafts sit exactly on pixel rows and the 45-degree arrow heads are
clean stairs (no anti-aliasing at all). Coordinate maths uses floor(v + 0.5), NOT round(): Python's round() is banker's
rounding and snaps neighbouring sizes inconsistently (KB Stellar-Design-Language.md, icon generator gotcha).

Usage:  python -I gen-header-icon.py <out_dir> [--contact <sheet.png>]
"""
import math
import sys

from PIL import Image

SIZES = (16, 18, 24, 32, 36, 48)   # header glyph = 24 canvas units: 18 @1080p, 24 @1440p, 36 @4K (+ spares)
WHITE = (255, 255, 255, 255)


def rnd(v: float) -> int:
    return math.floor(v + 0.5)


def draw(size: int) -> Image.Image:
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    px = im.load()
    w = max(2, rnd(size / 13))            # stroke: 2 px up to 32, 3 px at 36, 4 px at 48
    x0 = rnd(size * 0.21)                  # shaft ink spans columns x0 .. x1 (inclusive)
    x1 = size - 1 - x0
    ty = rnd(size * 0.35 - w / 2)          # top shaft rows ty .. ty+w-1
    by = size - ty - w                     # bottom shaft mirrors it vertically
    arm = max(2, rnd(size * 0.11))         # arrow-head arm length in 45-degree steps (head ≈ a quarter of the size)

    def block(x: int, y: int) -> None:
        for yy in range(y, y + w):
            for xx in range(x, x + w):
                if 0 <= xx < size and 0 <= yy < size:
                    px[xx, yy] = WHITE

    # top arrow → right
    for x in range(x0, x1 - w + 2):
        block(x, ty)
    tip = x1 - w + 1
    for i in range(1, arm + 1):
        block(tip - i, ty - i)
        block(tip - i, ty + i)

    # bottom arrow ← left
    for x in range(x0, x1 - w + 2):
        block(x, by)
    for i in range(1, arm + 1):
        block(x0 + i, by - i)
        block(x0 + i, by + i)
    return im


def contact(images, path: str) -> None:
    # ×4 nearest-neighbour review sheet on the default teal accent, so the pixel grid is visible.
    zoom, pad = 4, 8
    width = sum(s * zoom + pad for s in SIZES) + pad
    height = max(SIZES) * zoom + 2 * pad
    sheet = Image.new("RGBA", (width, height), (35, 41, 47, 255))
    x = pad
    for s, im in zip(SIZES, images):
        tile = Image.new("RGBA", (s, s), (95, 184, 196, 255))
        tile.alpha_composite(im)
        sheet.paste(tile.resize((s * zoom, s * zoom), Image.NEAREST), (x, pad))
        x += s * zoom + pad
    sheet.save(path)


def main() -> None:
    out = sys.argv[1]
    images = []
    for s in SIZES:
        im = draw(s)
        im.save(f"{out}/loadoutswitcher-{s}.png")
        images.append(im)
    if "--contact" in sys.argv:
        contact(images, sys.argv[sys.argv.index("--contact") + 1])


if __name__ == "__main__":
    main()
