"""Draws the MATERIALS hold's Pyrite pile, the sprite sheet the HUD shows on desktop and in the browser.

    python tools/hud/make_hold_pyrite.py            writes src/Emberward.Desktop/resources/hud/hold_pyrite.png
    python tools/hud/make_hold_pyrite.py out.png    writes it elsewhere (to compare before replacing)

Needs Pillow and NumPy. The output is the same on every run (seeded).

The sheet is 8 x 5 frames of 288 x 120 pixels, drawn at 144 x 60 art pixels in the hold window (HudStrip.cs,
strip.js, style.css). Frame k holds a heap of stones that grows with k; each frame is the one before plus a stone
or two, so the pile never reshuffles as the hold fills. BuildSlotView.OrePileFrame picks the frame: an empty hold
draws none, the last frame is a full hold.

The look: every stone is a faceted crystal lit from the upper left, in a bright ember-gold ramp (white-gold top
face, gold and amber sides, ember-orange shadow side, a deep ember seam), sitting in its own soft gold corona, with
a star glint on some. The corona is drawn as a translucent halo behind all the stones, so it shows in the gaps and
round the heap's edge over the dark window. The PNG is truecolour: a 256-colour palette bands the halo.
"""
import math
import random
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "src/Emberward.Desktop/resources/hud/hold_pyrite.png"

COLS, ROWS = 8, 5
FRAMES = COLS * ROWS
FW, FH = 288, 120
SS = 4  # supersampling for the facets' edges

# The palette (sRGB). The ramp runs from the shadow side to the lit top face.
HOT = (255, 252, 236)      # white-gold top face and glints
LIGHT = (255, 230, 120)    # lit facets
MID = (255, 194, 64)       # gold
DARK = (242, 132, 36)      # amber shadow side
SEAM = (150, 62, 16)       # outline and facet seams
CORONA = (255, 206, 96)    # the halo round every stone
CORONA_ALPHA = 0.70        # its opacity at the stone's rim
HAZE_ALPHA = 0.16          # the wider, fainter shine round the heap
RAMP = [DARK, MID, LIGHT, HOT]

LIGHT_DIR = (-0.62, -0.78)  # towards the upper left, in image coordinates (y down)


def ramp(t):
    t = min(max(t, 0.0), 1.0) * (len(RAMP) - 1)
    i = min(int(t), len(RAMP) - 2)
    f = t - i
    a, b = RAMP[i], RAMP[i + 1]
    return tuple(round(a[c] + (b[c] - a[c]) * f) for c in range(3))


def pile_slots():
    """Every stone's place in a full hold, in the order the heap fills: the bottom middle first, outwards and up."""
    rng = random.Random(7)
    slots = []
    rows = 6
    for row in range(rows):
        y = 103 - row * 17.2
        # Every row spans the window; the fill order (score) makes a mound until the hold is nearly full, then fills
        # the corners.
        step = 27.0
        offset = (row % 2) * step * 0.5
        x = 22.0 + offset
        while x <= FW - 22:
            jx, jy = rng.uniform(-3.5, 3.5), rng.uniform(-2.5, 2.5)
            dx = abs(x - FW / 2) / (FW / 2)
            score = row * 1.0 + dx * dx * 4.2 + rng.uniform(0, 0.35)
            slots.append({"x": x + jx, "y": y + jy, "row": row, "score": score, "seed": rng.randrange(1 << 30)})
            x += step
    slots.sort(key=lambda s: s["score"])
    return slots


def stone_shape(slot):
    rng = random.Random(slot["seed"])
    scale = rng.uniform(0.86, 1.14)
    rx, ry = 13.5 * scale, 10.0 * scale
    turn = rng.uniform(-0.35, 0.35)
    n = rng.choice((5, 6, 6, 7))
    angles = sorted((i + rng.uniform(-0.28, 0.28)) / n * 2 * math.pi for i in range(n))
    pts = []
    for a in angles:
        r = rng.uniform(0.82, 1.08)
        px, py = math.cos(a) * rx * r, math.sin(a) * ry * r
        pts.append((px * math.cos(turn) - py * math.sin(turn), px * math.sin(turn) + py * math.cos(turn)))
    # The top face's centre sits towards the light.
    apex = (LIGHT_DIR[0] * rx * 0.28 + rng.uniform(-1.5, 1.5), LIGHT_DIR[1] * ry * 0.28 + rng.uniform(-1.0, 1.0))
    warmth = rng.uniform(-0.12, 0.10)  # some stones a little more amber, some a little paler
    glint = rng.random() < 0.3
    return {"pts": pts, "apex": apex, "rx": rx, "ry": ry, "warmth": warmth, "glint": glint}


def draw_stone(draw, cx, cy, shape):
    def at(p):
        return ((cx + p[0]) * SS, (cy + p[1]) * SS)

    pts, apex = shape["pts"], shape["apex"]
    n = len(pts)
    draw.polygon([at(p) for p in pts], fill=SEAM + (255,))
    # Side facets: apex to each edge, shaded by which way the edge faces.
    for i in range(n):
        a, b = pts[i], pts[(i + 1) % n]
        mx, my = (a[0] + b[0]) / 2 - apex[0], (a[1] + b[1]) / 2 - apex[1]
        length = math.hypot(mx, my) or 1.0
        facing = (mx * LIGHT_DIR[0] + my * LIGHT_DIR[1]) / length
        shade = 0.48 + 0.40 * facing + shape["warmth"]
        inset = 0.86  # leaves a seam round the rim
        tri = [apex, (a[0] * inset, a[1] * inset), (b[0] * inset, b[1] * inset)]
        draw.polygon([at(p) for p in tri], fill=ramp(shade) + (255,))
    # Seams between the side facets.
    for p in pts:
        draw.line([at(apex), at((p[0] * 0.86, p[1] * 0.86))], fill=ramp(0.22 + shape["warmth"]) + (255,), width=SS // 2 + 1)
    # The top face: a small facet round the apex, the brightest part.
    top = [(apex[0] + p[0] * 0.34, apex[1] + p[1] * 0.34) for p in pts]
    draw.polygon([at(p) for p in top], fill=ramp(0.86 + shape["warmth"] * 0.5) + (255,))
    hot = [(apex[0] + p[0] * 0.16 - 0.6, apex[1] + p[1] * 0.16 - 0.6) for p in pts]
    draw.polygon([at(p) for p in hot], fill=HOT + (255,))


def draw_glint(draw, cx, cy, shape):
    gx, gy = (cx + shape["apex"][0]) * SS, (cy + shape["apex"][1]) * SS
    arm, thin = 7.5 * SS, 0.9 * SS
    for dx, dy in ((1, 0), (0, 1)):
        draw.polygon([(gx - dx * arm, gy - dy * arm), (gx + dy * thin, gy + dx * thin),
                      (gx + dx * arm, gy + dy * arm), (gx - dy * thin, gy - dx * thin)], fill=(255, 255, 240, 235))


def corona(stones):
    """The halos behind the stones, as one layer: each stone's own corona (opaque at its rim, gone about half a
    stone out) and a faint wider haze, combined like stacked veils so overlaps brighten without clipping."""
    ys, xs = np.mgrid[0:FH, 0:FW].astype(np.float64) + 0.5
    clear = np.ones((FH, FW))
    for cx, cy, shape in stones:
        # q: distance from the stone's centre in units of its radius (1 at the rim).
        q = np.sqrt(((xs - cx) / shape["rx"]) ** 2 + ((ys - cy) / shape["ry"]) ** 2)
        rim = np.exp(-(np.maximum(q - 0.85, 0.0) / 0.32) ** 2) * CORONA_ALPHA
        haze = np.exp(-(q / 2.2) ** 2) * HAZE_ALPHA
        clear *= (1.0 - rim) * (1.0 - haze)
    # Fade out over the frame's last few pixels, so the haze ends softly at the window and a filtered sample never
    # picks up the neighbouring frame.
    edge = np.minimum(np.minimum(xs, FW - xs), np.minimum(ys, FH - ys))
    alpha = (1.0 - clear) * np.clip((edge - 0.5) / 4.0, 0.0, 1.0)
    layer = np.zeros((FH, FW, 4), dtype=np.uint8)
    layer[..., 0:3] = CORONA
    layer[..., 3] = np.clip(alpha * 255 + 0.5, 0, 255).astype(np.uint8)
    return Image.fromarray(layer)


def frame(stones):
    img = corona(stones)
    big = Image.new("RGBA", (FW * SS, FH * SS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(big)
    # Back to front: upper stones are further back in the heap.
    for cx, cy, shape in sorted(stones, key=lambda s: s[1]):
        draw_stone(draw, cx, cy, shape)
    for cx, cy, shape in stones:
        if shape["glint"]:
            draw_glint(draw, cx, cy, shape)
    img.alpha_composite(big.resize((FW, FH), Image.Resampling.LANCZOS))
    return img


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else OUT
    slots = pile_slots()
    shapes = [(s["x"], s["y"], stone_shape(s)) for s in slots]
    sheet = Image.new("RGBA", (FW * COLS, FH * ROWS), (0, 0, 0, 0))
    for k in range(FRAMES):
        # One stone in the first frame, every slot in the last, in between evenly.
        count = 1 + round(k / (FRAMES - 1) * (len(shapes) - 1))
        sheet.paste(frame(shapes[:count]), ((k % COLS) * FW, (k // COLS) * FH))
    # A palette keeps the file small; the halo's soft alpha survives 256 colours well enough at this size.
    sheet.save(out, optimize=True)
    print(f"{out}: {len(shapes)} stones, {out.stat().st_size // 1024} KiB")


if __name__ == "__main__":
    main()
