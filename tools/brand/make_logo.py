"""Builds the Emberward logo and app icon as SVG, with the Chakra Petch glyphs as outlines (no font needed to show them).

    python tools/brand/make_logo.py                    writes the browser's logo.svg (animated) and icon.svg
    python tools/brand/make_logo.py --static out.svg   also writes the logo without its animation, for rasterising

The PNGs are those SVGs rendered with resvg (@resvg/resvg-js, `new Resvg(svg, {fitTo: {mode: "width", value}})`):
the desktop's resources/emberward-logo.png is the static logo at 2400 wide, the browser's icons/ are icon.svg at
192 and 512.

The idea: the mark carries the concept and the word stays plain. The emblem is the ward, a shield cut as a
cross-section: a cold surface, strata that get hotter with depth, and a shaft bored down to the ember. The wordmark
is clean, unbroken Chakra Petch in one pale ink, for contrast at any size, with a single detail: the A of WARD has
no crossbar and shelters the same ember (it pulses in the browser). The look is the HUD's: flat amber and teal on
dark, chamfered corners.
"""
import sys
from pathlib import Path
from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

ROOT = Path(__file__).resolve().parents[2]
FONT = ROOT / "src/Emberward.Desktop/resources/fonts/ChakraPetch-Medium.ttf"
WWWROOT = ROOT / "src/Emberward.Browser/wwwroot"

AMBER, TEAL = "#ffb240", "#60d6ff"

INK = "#f4ecdc"      # the wordmark: the HUD's text colour, a little brighter

HEIGHT = 400
PAD = 24             # clear space around the lockup
CAP = 168            # cap height of the wordmark, in SVG units
CAP_TOP = 116
TRACKING = 34        # extra space between letters
BOLD = 10            # outline added to every glyph, to take the Medium weight to semibold
SHIELD_W, SHIELD_H, SHIELD_TOP = 232, 268, 66
GAP = 64             # between the emblem and the wordmark
# The A without its crossbar, in font units (Chakra Petch's own A, less the bar), and the ember under it.
OPEN_A = [(266, 700), (362, 700), (614, 0), (515, 0), (314, 566), (114, 0), (15, 0)]
A_EMBER = (314, 156, 114)   # centre x, centre y, radius

font = TTFont(FONT)
glyphs = font.getGlyphSet()
cmap = font.getBestCmap()
scale = CAP / font["OS/2"].sCapHeight
baseline = CAP_TOP + CAP


def n(value):
    return f"{value:.1f}".rstrip("0").rstrip(".")


def outline(text, x0):
    """Returns (svg path data, ember diamond path, end x) for text laid out from x0 on the baseline."""
    x, parts, ember = x0, [], ""
    for i, ch in enumerate(text):
        name = cmap[ord(ch)]
        if i == text.rindex("A"):
            points = " L".join(f"{n(x + px * scale)} {n(baseline - py * scale)}" for px, py in OPEN_A)
            parts.append(f"M{points} Z")
            ex, ey, er = A_EMBER
            ember = diamond(x + ex * scale, baseline - ey * scale, er * scale)
        else:
            pen = SVGPathPen(glyphs, ntos=n)
            glyphs[name].draw(TransformPen(pen, (scale, 0, 0, -scale, x, baseline)))
            parts.append(pen.getCommands())
        x += glyphs[name].width * scale + TRACKING
    return " ".join(parts), ember, x - TRACKING


def diamond(cx, cy, r):
    return f"M{n(cx)} {n(cy - r)} L{n(cx + r)} {n(cy)} L{n(cx)} {n(cy + r)} L{n(cx - r)} {n(cy)} Z"


def shield(x, y, w=SHIELD_W, h=SHIELD_H, inset=0.0):
    """The ward: chamfered shoulders, a point at the bottom. `inset` shrinks it evenly."""
    c = 36
    shoulder = h * 0.55
    i = inset
    return (f"M{n(x + c + i * 0.4)} {n(y + i)} H{n(x + w - c - i * 0.4)} L{n(x + w - i)} {n(y + c + i * 0.4)} "
            f"V{n(y + shoulder - i * 0.45)} L{n(x + w / 2)} {n(y + h - i * 1.4)} L{n(x + i)} {n(y + shoulder - i * 0.45)} "
            f"V{n(y + c + i * 0.4)} Z")


def emblem(x, y, detail=True):
    """Returns (defs, body) for the emblem with its top left at (x, y). `detail` off gives the small-size cut."""
    w, h = SHIELD_W, SHIELD_H
    cx = x + w / 2
    shoulder = y + h * 0.55
    drop = h * 0.45                      # how far the point hangs below the shoulders
    core_y = y + h * 0.56
    chamber, ember = (58, 31) if detail else (66, 40)
    shaft = 30 if detail else 40
    cap = y + (44 if detail else 54)     # the bottom of the cold surface band
    gap = 9 if detail else 14            # dark lines between strata

    def chevron(y0):                     # a stratum line, parallel to the shield's point
        return f"M{n(x - 4)} {n(y0)} L{n(cx)} {n(y0 + drop)} L{n(x + w + 4)} {n(y0)}"

    strata = [shoulder - 44] if detail else []
    cuts = [f'<path d="M{n(x - 4)} {n(cap)} H{n(x + w + 4)}" stroke-width="{gap}"/>']
    cuts += [f'<path d="{chevron(s)}" stroke-width="{gap}"/>' for s in strata]
    mask = f'''<mask id="cut" maskUnits="userSpaceOnUse" x="{n(x - 8)}" y="{n(y - 8)}" width="{w + 16}" height="{h + 16}">
      <rect x="{n(x - 8)}" y="{n(y - 8)}" width="{w + 16}" height="{h + 16}" fill="#fff"/>
      <g fill="none" stroke="#000" stroke-linejoin="miter">{"".join(cuts)}</g>
      <rect x="{n(cx - shaft / 2)}" y="{n(y - 8)}" width="{shaft}" height="{n(core_y - y + 8)}" fill="#000"/>
      <path d="{diamond(cx, core_y, chamber)}" fill="#000"/>
    </mask>
    <clipPath id="ward"><path d="{shield(x, y)}"/></clipPath>
    <linearGradient id="depth" gradientUnits="userSpaceOnUse" x1="0" y1="{n(cap)}" x2="0" y2="{n(y + h)}">
      <stop offset="0" stop-color="#ffd98f"/>
      <stop offset="0.4" stop-color="{AMBER}"/>
      <stop offset="0.8" stop-color="#f4702a"/>
      <stop offset="1" stop-color="#d9431c"/>
    </linearGradient>
    <linearGradient id="core" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#fff6d8"/>
      <stop offset="0.55" stop-color="#ffcf6b"/>
      <stop offset="1" stop-color="#ff8a2e"/>
    </linearGradient>'''
    relay = ""
    if detail:
        relay = (f'<path d="M{n(cx)} {n(y)} V{n(core_y - ember - 12)}" stroke="{TEAL}" stroke-width="5" fill="none"/>'
                 f'<path class="node" d="{diamond(cx, y + 86, 9)}" fill="{TEAL}"/>')
    body = f'''<g mask="url(#cut)" clip-path="url(#ward)">
      <rect x="{n(x)}" y="{n(cap)}" width="{w}" height="{n(y + h - cap)}" fill="url(#depth)"/>
      <rect x="{n(x)}" y="{n(y)}" width="{w}" height="{n(cap - y)}" fill="{TEAL}"/>
    </g>
    {relay}
    <path class="ember" d="{diamond(cx, core_y, ember)}" fill="url(#core)"/>'''
    return mask, body


def build_logo(animated):
    word, word_ember, end = outline("EMBERWARD", SHIELD_W + GAP)
    width = round(end + PAD)
    mask_defs, emblem_body = emblem(0, SHIELD_TOP)
    style = """<style>
    .ember { animation: smoulder 3.2s ease-in-out infinite; }
    @keyframes smoulder { 50% { opacity: 0.62; } }
    @media (prefers-reduced-motion: reduce) { .ember { animation: none; } }
    </style>""" if animated else ""
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="{-PAD} 0 {width + PAD} {HEIGHT}" width="{width + PAD}" height="{HEIGHT}" role="img" aria-label="Emberward">
  <defs>{style}
    {mask_defs}
  </defs>
  {emblem_body}
  <path d="{word}" fill="{INK}" stroke="{INK}" stroke-width="{BOLD}" stroke-linejoin="miter"/>
  <path class="ember" d="{word_ember}" fill="url(#core)"/>
</svg>
'''


def build_icon():
    """The app icon: the emblem alone, cut bolder so that it holds at 16 pixels, on the page's dark plate."""
    k = 372 / SHIELD_H
    x, y = (512 / k - SHIELD_W) / 2, (512 / k - SHIELD_H) / 2 + 4
    defs, body = emblem(x, y, detail=False)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <rect width="512" height="512" rx="104" fill="#0b1419"/>
  <g transform="scale({k:.4f})">
    <defs>
    {defs}
    </defs>
    {body}
  </g>
</svg>
'''


if __name__ == "__main__":
    (WWWROOT / "logo.svg").write_text(build_logo(animated=True), encoding="utf-8", newline="\n")
    (WWWROOT / "icon.svg").write_text(build_icon(), encoding="utf-8", newline="\n")
    print("wrote logo.svg and icon.svg in", WWWROOT.relative_to(ROOT))
    if "--static" in sys.argv:
        target = Path(sys.argv[sys.argv.index("--static") + 1])
        target.write_text(build_logo(animated=False), encoding="utf-8", newline="\n")
        print("wrote", target)
