"""
Generates FullRGB's app icon as a multi-resolution .ico (plus PNG previews).

Design: dark rounded-square tile + a three-segment RGB ring (cyan / violet / pink)
with a solid cyan hub.

Geometry method matters here. Stroked arcs with rounded caps produced overlapping
caps and unequal radii, so the ring is instead built as:
    filled pie slice (outer radius)  MINUS  inner disc
using an alpha mask at 8x supersampling. Every segment therefore shares one exact
centre, one outer radius and one inner radius, and the gaps between segments are
true angular gaps rather than overpainted caps.

Small sizes (<= 24 px) are NOT downscaled from the large art: at that scale the
three hues smear into one muddy band and the hub bleeds into the ring. Those sizes
are drawn pixel-by-pixel on the grid with hard edges instead.
"""
from PIL import Image, ImageDraw
import os

SS = 8  # supersample factor for the large variant

CYAN   = (0, 229, 255, 255)
VIOLET = (124, 77, 255, 255)
PINK   = (255, 77, 141, 255)
HUB    = (240, 246, 255, 255)   # neutral white: doesn't duplicate any arc hue
TILE      = (18, 22, 28, 255)
TILE_EDGE = (44, 54, 66, 255)


# ---------------------------------------------------------------- large variant

def _rounded_tile(size, radius_frac=0.22):
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(size * radius_frac)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=r, fill=TILE)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=r,
                        outline=TILE_EDGE, width=max(1, size // 96))
    return img


def _ring_segment(S, cx, cy, r_out, r_in, a0, a1, color):
    """
    One ring segment as its own RGBA layer: pie slice at r_out with the r_in disc
    removed. Exact concentricity by construction.
    """
    mask = Image.new("L", (S, S), 0)
    md = ImageDraw.Draw(mask)
    md.pieslice([cx - r_out, cy - r_out, cx + r_out, cy + r_out],
                start=a0, end=a1, fill=255)
    md.ellipse([cx - r_in, cy - r_in, cx + r_in, cy + r_in], fill=0)

    layer = Image.new("RGBA", (S, S), color[:3] + (0,))
    layer.putalpha(mask)
    return layer


def make_large(size):
    """Three 120-degree segments with equal angular gaps. For 32 px and up."""
    S = size * SS
    img = _rounded_tile(S)

    cx = cy = S / 2
    r_out = S * 0.355          # ring spans ~71% of the tile
    r_in  = r_out - S * 0.115  # stroke width
    gap   = 10                 # degrees of dark between segments

    # Three EXACT 120-degree slots, gaps subtracted symmetrically inside each slot,
    # so all three arcs share one sweep (120 - gap) and all three gaps are equal.
    # Slot boundaries at -90 / 30 / 150 deg puts one gap exactly at top centre.
    for i, color in enumerate((CYAN, VIOLET, PINK)):
        a0 = -90 + i * 120 + gap / 2
        a1 = -90 + (i + 1) * 120 - gap / 2
        img = Image.alpha_composite(img, _ring_segment(S, cx, cy, r_out, r_in, a0, a1, color))

    # hub, big enough to survive the downscale, with a clear dark gap to the ring
    hub = S * 0.105
    d = ImageDraw.Draw(img)
    d.ellipse([cx - hub, cy - hub, cx + hub, cy + hub], fill=HUB)

    return img.resize((size, size), Image.LANCZOS)


# ---------------------------------------------------------------- small variant

def make_small(size):
    """
    Pixel-grid variant for 16 / 20 / 24 px. Hard edges, no antialiasing, exactly
    one dark pixel of separation between hub and ring. Two hues only: cyan ring,
    magenta hub - which reads far better than a tri-colour gradient at this size.
    """
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # tile: square with the 4 corner pixels clipped (a radius at 16px just blurs)
    d.rectangle([0, 0, size - 1, size - 1], fill=TILE)
    for (x, y) in [(0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)]:
        img.putpixel((x, y), (0, 0, 0, 0))
    if size >= 20:
        for (x, y) in [(1, 0), (0, 1), (size - 2, 0), (size - 1, 1),
                       (1, size - 1), (0, size - 2), (size - 2, size - 1), (size - 1, size - 2)]:
            img.putpixel((x, y), TILE)

    # ring: whole-pixel bounds, stroke width scaled to the size
    inset = 2 if size <= 16 else 3
    box = [inset, inset, size - 1 - inset, size - 1 - inset]
    stroke = 2 if size <= 16 else 3
    d.ellipse(box, outline=CYAN, width=stroke)

    # hub: solid neutral block, guaranteed dark gap to the ring
    c0 = size // 2 - (1 if size <= 16 else 2)
    c1 = size // 2 + (0 if size <= 16 else 1)
    d.rectangle([c0, c0, c1, c1], fill=HUB)

    return img


# ---------------------------------------------------------------- entry point

def main():
    out_dir = os.path.normpath(os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "src", "FullRGB", "Assets"))
    os.makedirs(out_dir, exist_ok=True)

    sizes = [16, 20, 24, 32, 48, 64, 128, 256]
    frames = []
    for s in sizes:
        img = make_small(s) if s <= 24 else make_large(s)
        frames.append(img)
        img.save(os.path.join(out_dir, f"icon_{s}.png"))

    ico_path = os.path.join(out_dir, "app.ico")
    frames[-1].save(ico_path, format="ICO",
                    sizes=[(s, s) for s in sizes],
                    append_images=frames[:-1])

    make_large(512).save(os.path.join(out_dir, "icon_preview.png"))

    print("wrote:", ico_path, os.path.getsize(ico_path), "B")
    for s in sizes:
        p = os.path.join(out_dir, f"icon_{s}.png")
        print(f"  {s:>3}px  {os.path.getsize(p):>6} B")


if __name__ == "__main__":
    main()
