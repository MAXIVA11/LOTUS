"""Draws the artwork used by README.md.

Run it from the repository root with any Python 3:

    python tools/make-readme-art.py

It writes three SVG files to docs/assets. GitHub shows an SVG in a README as a
plain image, so the files use only inline CSS: no scripts, no web fonts and no
outside files. The pictures hold still. The one exception is the banner, which
builds itself once when the page opens and then stays as it is.

    python tools/make-readme-art.py --social <file.svg>

writes a still 1280x640 picture for the repository's social preview instead.
"""

import os
import sys

INK = "#08080C"
PANEL = "#111118"
PANEL2 = "#191923"
LINE = "#2B2B3A"
TEXT = "#F4F4F6"
DIM = "#8D8DA3"
CYAN = "#00F0FF"
PINK = "#FF2D95"
YELLOW = "#FCEE0A"
GREEN = "#3DFF8B"

MONO = "Consolas,'Cascadia Mono','SF Mono',Menlo,'DejaVu Sans Mono','Liberation Mono',monospace"
COND = "Bahnschrift,'DIN Alternate','DIN Condensed','Roboto Condensed','Arial Narrow','Helvetica Neue',Arial,sans-serif"

# The lotus mark. Same shapes as src/Lotus.App/Controls/LotusMark.cs.
PETALS = [
    (PINK, [10, 52, 28, 60, 44, 77, 21, 76]),
    (PINK, [90, 52, 72, 60, 56, 77, 79, 76]),
    (CYAN, [24, 28, 41, 44, 45, 75, 28, 60]),
    (CYAN, [76, 28, 59, 44, 55, 75, 72, 60]),
    ("#F5F5F5", [50, 14, 62, 44, 50, 77, 38, 44]),
    (YELLOW, [22, 82, 78, 82, 73, 89, 27, 89]),
]

# The word LOTUS as stroked paths, so it looks the same on every computer.
# Each letter sits in a box 84 wide and 120 high.
LETTERS = [
    "M12 0V108H84",
    "M32 12H72V88L52 108H12V32Z",
    "M0 12H84M42 12V120",
    "M12 0V88L32 108H72V0",
    "M84 12H32L12 32V60H72V88L52 108H0",
]
LETTER_STEP = 100

BASE_CSS = (
    ".mono{font-family:%s}"
    ".cond{font-family:%s;font-weight:700;font-stretch:condensed}"
) % (MONO, COND)

# People who ask their system for less motion get the finished picture.
STILL_CSS = "@media (prefers-reduced-motion:reduce){*{animation:none!important}}"


def esc(text):
    return text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def points(flat, scale=1.0, dx=0.0, dy=0.0):
    pairs = []
    for i in range(0, len(flat), 2):
        pairs.append("%g,%g" % (round(flat[i] * scale + dx, 2), round(flat[i + 1] * scale + dy, 2)))
    return " ".join(pairs)


def hazard_pattern(pattern_id="hz"):
    return (
        '<pattern id="%s" width="32" height="32" patternUnits="userSpaceOnUse" patternTransform="skewX(-45)">'
        '<rect width="32" height="32" fill="%s"/><rect width="16" height="32" fill="%s"/></pattern>'
        % (pattern_id, YELLOW, INK)
    )


def hazard_strip(x, y, width, height):
    """A strip of warning stripes."""
    return '<rect x="%g" y="%g" width="%g" height="%g" fill="url(#hz)"/>' % (x, y, width, height)


def mono(x, y, text, size, fill, anchor="start", extra=""):
    """Monospace text with a fixed width of 0.6 em per character."""
    length = len(text) * size * 0.6
    return (
        '<text x="%g" y="%g" class="mono" font-size="%g" fill="%s" text-anchor="%s" '
        'textLength="%g" lengthAdjust="spacing" xml:space="preserve"%s>%s</text>'
        % (x, y, size, fill, anchor, round(length, 1), extra, esc(text))
    )


def typed(x, y, text, size, start, per, fill, css_class, colors=None):
    """Text that appears one character at a time. Returns the markup and the end time."""
    cell = size * 0.6
    out = []
    for i, ch in enumerate(text):
        if ch == " ":
            continue
        colour = fill
        if colors and i in colors:
            colour = colors[i]
        out.append(
            '<text x="%g" y="%g" class="mono %s" font-size="%g" fill="%s" style="animation-delay:%.2fs">%s</text>'
            % (round(x + i * cell, 1), y, css_class, size, colour, start + i * per, esc(ch))
        )
    return "".join(out), start + len(text) * per


def word(stroke, width=24):
    paths = []
    for i, d in enumerate(LETTERS):
        paths.append('<path transform="translate(%d 0)" d="%s"/>' % (i * LETTER_STEP, d))
    return (
        '<g fill="none" stroke="%s" stroke-width="%d" stroke-linejoin="miter" stroke-miterlimit="6">%s</g>'
        % (stroke, width, "".join(paths))
    )


def petals(scale, dx, dy, css_class=None):
    out = []
    for i, (colour, flat) in enumerate(PETALS):
        attrs = ""
        if css_class:
            attrs = ' class="%s" style="animation-delay:%.2fs"' % (css_class, 0.15 + i * 0.12)
        out.append('<polygon points="%s" fill="%s"%s/>' % (points(flat, scale, dx, dy), colour, attrs))
    return "".join(out)


def scanlines(width, height):
    return '<rect width="%d" height="%d" fill="url(#scan)"/>' % (width, height)


SCAN_DEFS = (
    '<pattern id="scan" width="4" height="4" patternUnits="userSpaceOnUse">'
    '<rect width="4" height="1" fill="#000" opacity=".16"/></pattern>'
)


def grid_pattern():
    return (
        '<pattern id="grid" width="40" height="40" patternUnits="userSpaceOnUse">'
        '<path d="M40 0H0V40" fill="none" stroke="%s" stroke-width="1" opacity=".45"/></pattern>' % LINE
    )


def svg_open(width, height, title, desc):
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d" '
        'role="img" aria-labelledby="t d"><title id="t">%s</title><desc id="d">%s</desc>'
        % (width, height, width, height, esc(title), esc(desc))
    )


# ---------------------------------------------------------------- banner


def banner(still=False, width=1200, height=380):
    """The picture at the top of the README. With still=True it is the social preview."""
    w, h = width, height
    cut = 30
    frame = [0, 0, w - cut, 0, w, cut, w, h, cut, h, 0, h - cut]
    if still:
        frame = [0, 0, w, 0, w, h, 0, h]
    zoom = w / 1200.0  # the content is designed for 1200 by 380
    oy = (h - 380 * zoom) / 2.0
    # The social preview has room for a status panel. The README banner leaves
    # it out and moves the rest to the middle.
    shift = 0 if still else 128

    # The petals and the line of text appear once. Nothing repeats.
    css = BASE_CSS
    if not still:
        css += (
            "@keyframes pop{from{opacity:0;transform:scale(.2)}to{opacity:1;transform:scale(1)}}"
            ".pop{animation:pop .55s cubic-bezier(.2,1.5,.4,1) both;transform-box:fill-box;transform-origin:50% 100%}"
            "@keyframes k{from{opacity:0}to{opacity:1}}"
            ".k{animation:k .01s steps(1) both}"
        ) + STILL_CSS

    o = [svg_open(w, h, "LOTUS", "Local Operational Trusted USB System. Offline device control for Windows.")]
    o.append("<defs>")
    o.append(hazard_pattern())
    o.append(grid_pattern())
    o.append(SCAN_DEFS)
    o.append('<clipPath id="frame"><polygon points="%s"/></clipPath>' % points(frame))
    o.append(
        '<radialGradient id="halo" cx=".5" cy=".5" r=".5"><stop offset="0" stop-color="%s" stop-opacity=".16"/>'
        '<stop offset="1" stop-color="%s" stop-opacity="0"/></radialGradient>' % (CYAN, CYAN)
    )
    o.append('<filter id="glow" x="-30%" y="-30%" width="160%" height="160%"><feGaussianBlur stdDeviation="7"/></filter>')
    o.append("</defs>")
    o.append("<style>%s</style>" % css)

    o.append('<g clip-path="url(#frame)">')
    o.append('<rect width="%d" height="%d" fill="%s"/>' % (w, h, INK))
    o.append('<rect width="%d" height="%d" fill="url(#grid)"/>' % (w, h))
    # A large faint flower in the corner, like the one behind the app's pages.
    o.append('<g opacity=".05">%s</g>' % petals(4.6, w - 420, h - 330))
    o.append('<g transform="translate(%g %g) scale(%g)">' % (round(shift * zoom, 2), round(oy, 2), round(zoom, 4)))
    o.append('<ellipse cx="640" cy="160" rx="430" ry="200" fill="url(#halo)"/>')

    # The mark, with a soft glow behind it.
    o.append('<g opacity=".45" filter="url(#glow)">%s</g>' % petals(2.5, 64, 62))
    o.append(petals(2.5, 64, 62, None if still else "pop"))

    # The word, with the two colour ghosts the app's title has.
    o.append('<g transform="translate(372 78)">')
    o.append('<g transform="translate(5 3)">%s</g>' % word(PINK))
    o.append('<g transform="translate(-5 -3)">%s</g>' % word(CYAN))
    o.append(word(TEXT))
    o.append("</g>")

    # The yellow tag under the word.
    o.append('<polygon points="384,226 660,226 648,262 372,262" fill="%s"/>' % YELLOW)
    o.append(
        '<text x="516" y="251" class="cond" font-size="22" fill="%s" text-anchor="middle" '
        'textLength="236" lengthAdjust="spacingAndGlyphs">OFFLINE DEVICE CONTROL</text>' % INK
    )
    o.append(mono(680, 250, "FOR WINDOWS 10 AND 11", 16, DIM))

    # What the name stands for, typed out once.
    phrase = "LOCAL OPERATIONAL TRUSTED USB SYSTEM"
    initials = {0: YELLOW, 6: YELLOW, 18: YELLOW, 26: YELLOW, 30: YELLOW}
    o.append(mono(372, 300, "//", 19, DIM))
    markup, end = typed(372 + 3 * 11.4, 300, phrase, 19, 0.9, 0.035, TEXT, "" if still else "k", initials)
    o.append(markup)
    o.append(mono(372, 338, "BLOCKS REMOVABLE STORAGE AND UNTRUSTED USB DEVICES.", 15, DIM))

    if still:
        # A small status panel on the right.
        o.append('<rect x="930" y="78" width="232" height="216" fill="%s" stroke="%s"/>' % (PANEL, LINE))
        o.append('<rect x="930" y="78" width="4" height="216" fill="%s"/>' % CYAN)
        o.append(mono(952, 106, "//  GUARD", 14, DIM))
        rows = [
            ("SERVICE", "RUNNING", GREEN),
            ("POLICY", "ARMED", CYAN),
            ("STORAGE", "BLOCK", PINK),
            ("PNP", "TRUSTED ONLY", PINK),
            ("STORE", "ENCRYPTED", YELLOW),
        ]
        for i, (label, value, colour) in enumerate(rows):
            y = 138 + i * 27
            o.append(mono(952, y, label, 15, DIM))
            o.append(mono(1146, y, value, 15, colour, "end"))
        o.append('<rect x="1058" y="127" width="10" height="10" fill="%s"/>' % GREEN)
        o.append('<path d="M952 256H1146" stroke="%s"/>' % LINE)
        o.append(mono(952, 280, "PORTS", 15, DIM))
        for i in range(8):
            colour = PINK if i in (2, 5) else CYAN
            o.append('<rect x="%d" y="270" width="10" height="10" fill="%s"/>' % (1018 + i * 16, colour))

    o.append("</g>")

    # Warning stripes and corner marks on the frame itself.
    o.append(hazard_strip(0, 0, 430, 10))
    o.append(hazard_strip(w - 430, h - 10, 430, 10))
    o.append(scanlines(w, h))
    if not still:
        o.append('<polygon points="%s" fill="none" stroke="%s" stroke-width="2"/>' % (points(frame), LINE))
        o.append('<path d="M%d 1H%dL%d %d V70" fill="none" stroke="%s" stroke-width="2"/>'
                 % (w - 110, w - cut, w - 1, cut, CYAN))
        o.append('<path d="M110 %dH%dL1 %d V%d" fill="none" stroke="%s" stroke-width="2"/>'
                 % (h - 1, cut, h - cut, h - 70, PINK))
    o.append("</g></svg>")
    return "".join(o)


# ---------------------------------------------------------------- divider


def divider():
    w, h = 1200, 10
    o = [svg_open(w, h, "Divider", "A line with warning stripes at both ends.")]
    o.append("<defs>%s" % hazard_pattern())
    o.append('<clipPath id="bar"><polygon points="10,0 1200,0 1190,10 0,10"/></clipPath>')
    o.append("</defs>")
    o.append('<g clip-path="url(#bar)">')
    o.append('<path d="M0 5H1200" stroke="%s" stroke-width="2"/>' % LINE)
    o.append(hazard_strip(0, 0, 150, 10))
    o.append(hazard_strip(1050, 0, 150, 10))
    o.append("</g></svg>")
    return "".join(o)


# ---------------------------------------------------------------- decision flow


def flow():
    w, h = 1000, 410
    rail = 208
    up = 64
    down = 342
    centres = [200, 350, 500, 650, 800]
    checks = [
        ("01", "LOTUS IS", "DISARMED?", True),
        ("02", "WINDOWS", "BOOT DISK?", True),
        ("03", "ON THE", "TRUSTED LIST?", True),
        ("04", "HAS STORAGE,", "SET TO BLOCK?", False),
        ("05", "ANYTHING ELSE,", "TRUSTED ONLY?", False),
    ]

    frame = [0, 0, w - 26, 0, w, 26, w, h, 26, h, 0, h - 26]
    o = [svg_open(
        w, h, "The order in which LOTUS decides",
        "A device on a USB port passes five checks from left to right. If LOTUS is disarmed, if Windows boots "
        "from the device, or if the device is on the trusted list, it is allowed. If it has storage and storage "
        "is set to block, or if it does anything else and USB devices are set to trusted only, it is blocked. "
        "A device that no check stops is allowed.")]
    o.append("<defs>%s%s" % (grid_pattern(), SCAN_DEFS))
    o.append('<clipPath id="frame"><polygon points="%s"/></clipPath>' % points(frame))
    o.append("</defs><style>%s</style>" % BASE_CSS)
    o.append('<g clip-path="url(#frame)">')
    o.append('<rect width="%d" height="%d" fill="%s"/>' % (w, h, INK))
    o.append('<rect width="%d" height="%d" fill="url(#grid)"/>' % (w, h))
    o.append(mono(28, 34, "//  THE DECISION, IN ORDER", 13, DIM))

    # Lines: the rail, the way up to "allowed" and the way down to "blocked".
    o.append('<path d="M112 %d H927 V%d" fill="none" stroke="%s" stroke-width="2"/>' % (rail, up + 26, CYAN))
    for (number, a, b, allow), cx in zip(checks, centres):
        o.append('<path d="M%d %d V%d H870" fill="none" stroke="%s" stroke-width="2" stroke-dasharray="4 8"/>'
                 % (cx, rail, up if allow else down, GREEN if allow else PINK))

    # The device on the left.
    o.append('<polygon points="22,%d 112,%d 112,%d 34,%d 22,%d" fill="%s" stroke="%s" stroke-width="2"/>'
             % (rail - 26, rail - 26, rail + 26, rail + 26, rail + 14, PANEL2, CYAN))
    o.append(mono(67, rail - 3, "DEVICE ON", 12, TEXT, "middle"))
    o.append(mono(67, rail + 14, "A USB PORT", 12, TEXT, "middle"))

    # The five checks.
    for (number, a, b, allow), cx in zip(checks, centres):
        x = cx - 61
        o.append(
            '<polygon points="%d,%d %d,%d %d,%d %d,%d %d,%d" fill="%s" stroke="%s" stroke-width="2"/>'
            % (x, rail - 38, x + 122, rail - 38, x + 122, rail + 38, x + 12, rail + 38, x, rail + 26, PANEL, LINE)
        )
        colour = GREEN if allow else PINK
        o.append('<rect x="%d" y="%d" width="122" height="4" fill="%s"/>' % (x, rail - 38, colour))
        o.append(mono(x + 10, rail - 16, number, 11, DIM))
        o.append(mono(cx, rail + 4, a, 13, TEXT, "middle"))
        o.append(mono(cx, rail + 22, b, 13, TEXT, "middle"))
        if allow:
            o.append(mono(cx + 9, rail - 62, "YES", 12, GREEN))
        else:
            o.append(mono(cx + 9, rail + 72, "YES", 12, PINK))
    for cx in centres[:-1]:
        o.append(mono(cx + 75, rail - 8, "NO", 12, DIM, "middle"))
    o.append(mono(895, rail - 8, "NO", 12, DIM, "middle"))

    # Where a device ends up.
    o.append('<polygon points="870,%d 985,%d 985,%d 882,%d 870,%d" fill="%s"/>'
             % (up - 26, up - 26, up + 26, up + 26, up + 14, GREEN))
    o.append('<text x="927" y="%d" class="cond" font-size="22" fill="%s" text-anchor="middle" '
             'textLength="76" lengthAdjust="spacingAndGlyphs">ALLOWED</text>' % (up + 1, INK))
    o.append(mono(927, up + 18, "IT WORKS", 11, INK, "middle"))
    o.append('<polygon points="870,%d 985,%d 985,%d 882,%d 870,%d" fill="%s"/>'
             % (down - 26, down - 26, down + 26, down + 26, down + 14, PINK))
    o.append('<text x="927" y="%d" class="cond" font-size="22" fill="%s" text-anchor="middle" '
             'textLength="76" lengthAdjust="spacingAndGlyphs">BLOCKED</text>' % (down + 1, INK))
    o.append(mono(927, down + 18, "SWITCHED OFF", 11, INK, "middle"))

    o.append(scanlines(w, h))
    o.append('<polygon points="%s" fill="none" stroke="%s" stroke-width="2"/>' % (points(frame), LINE))
    o.append("</g></svg>")
    return "".join(o)


def main():
    args = sys.argv[1:]
    if len(args) == 2 and args[0] == "--social":
        with open(args[1], "w", encoding="utf-8", newline="\n") as handle:
            handle.write(banner(still=True, width=1280, height=640))
        print("wrote", args[1])
        return
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    folder = os.path.join(root, "docs", "assets")
    os.makedirs(folder, exist_ok=True)
    files = {
        "banner.svg": banner(),
        "divider.svg": divider(),
        "decision-flow.svg": flow(),
    }
    for name, content in files.items():
        path = os.path.join(folder, name)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content + "\n")
        print("wrote %s (%d bytes)" % (os.path.relpath(path, root), len(content)))


if __name__ == "__main__":
    main()
