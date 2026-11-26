"""Draws the animated artwork used by README.md.

Run it from the repository root with any Python 3:

    python tools/make-readme-art.py

It writes four SVG files to docs/assets. GitHub shows an SVG in a README as a
plain image, so the files use only inline CSS and SMIL: no scripts, no web
fonts and no outside files. Text that has to line up is placed one character
at a time, because the reader's monospace font is not known in advance.

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
    "@keyframes slide{to{transform:translateX(32px)}}"
    "@keyframes slideback{to{transform:translateX(-32px)}}"
    "@keyframes blink{0%%,49%%{opacity:1}50%%,100%%{opacity:0}}"
    "@keyframes pulse{0%%,100%%{opacity:1}50%%{opacity:.25}}"
    ".slide{animation:slide 1.4s linear infinite}"
    ".slideback{animation:slideback 1.4s linear infinite}"
    ".blink{animation:blink 1s steps(1) infinite}"
    ".pulse{animation:pulse 1.6s ease-in-out infinite}"
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


def hazard_strip(x, y, width, height, clip_id, back=False):
    """A strip of warning stripes that slides sideways for ever."""
    return (
        '<clipPath id="%s"><rect x="%g" y="%g" width="%g" height="%g"/></clipPath>'
        '<g clip-path="url(#%s)"><rect class="%s" x="%g" y="%g" width="%g" height="%g" fill="url(#hz)"/></g>'
        % (clip_id, x, y, width, height, clip_id, "slideback" if back else "slide",
           x - 64, y, width + 128, height)
    )


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
    return (
        '<rect width="%d" height="%d" fill="url(#scan)"/>'
        '<rect class="sweep" x="0" y="-80" width="%d" height="80" fill="url(#sweepfill)"/>'
        % (width, height, width)
    )


SCAN_DEFS = (
    '<pattern id="scan" width="4" height="4" patternUnits="userSpaceOnUse">'
    '<rect width="4" height="1" fill="#000" opacity=".16"/></pattern>'
    '<linearGradient id="sweepfill" x1="0" y1="0" x2="0" y2="1">'
    '<stop offset="0" stop-color="#fff" stop-opacity="0"/>'
    '<stop offset=".5" stop-color="#fff" stop-opacity=".05"/>'
    '<stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient>'
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

    css = BASE_CSS + (
        "@keyframes pop{from{opacity:0;transform:scale(.2)}to{opacity:1;transform:scale(1)}}"
        ".pop{animation:pop .55s cubic-bezier(.2,1.5,.4,1) both;transform-box:fill-box;transform-origin:50% 100%}"
        "@keyframes breath{0%,100%{opacity:.25}50%{opacity:.7}}"
        ".breath{animation:breath 4s ease-in-out infinite}"
        "@keyframes turn{to{transform:rotate(360deg)}}"
        "@keyframes turnback{to{transform:rotate(-360deg)}}"
        ".ring{animation:turn 40s linear infinite;transform-origin:189px 187px}"
        ".arc{animation:turnback 9s linear infinite;transform-origin:189px 187px}"
        "@keyframes k{from{opacity:0}to{opacity:1}}"
        ".k{animation:k .01s steps(1) both}"
        ".late{animation:k .01s steps(1) both,blink 1s steps(1) infinite}"
        "@keyframes ga{0%,84%,100%{transform:translate(5px,3px)}85%{transform:translate(18px,3px)}"
        "87%{transform:translate(-9px,5px)}89%{transform:translate(26px,0)}91%{transform:translate(5px,3px)}"
        "95%{transform:translate(12px,-2px)}96%{transform:translate(5px,3px)}}"
        "@keyframes gb{0%,84%,100%{transform:translate(-5px,-3px)}85%{transform:translate(-20px,-3px)}"
        "87%{transform:translate(8px,-6px)}89%{transform:translate(-28px,0)}91%{transform:translate(-5px,-3px)}"
        "95%{transform:translate(-13px,2px)}96%{transform:translate(-5px,-3px)}}"
        "@keyframes s1{0%,84%,100%{transform:translate(0,0)}85%{transform:translate(34px,0)}"
        "88%{transform:translate(-22px,0)}91%{transform:translate(0,0)}}"
        "@keyframes s2{0%,86%,100%{transform:translate(0,0)}87%{transform:translate(-40px,0)}"
        "89%{transform:translate(16px,0)}91%{transform:translate(0,0)}95%{transform:translate(-10px,0)}"
        "96%{transform:translate(0,0)}}"
        "@keyframes hide{0%,84%,100%{opacity:0}85%,90%{opacity:1}91%{opacity:0}}"
        "@keyframes hide2{0%,86%,100%{opacity:0}87%,90%{opacity:1}91%{opacity:0}95%{opacity:1}96%{opacity:0}}"
        ".ga{transform:translate(5px,3px);animation:ga 5s steps(1) infinite}"
        ".gb{transform:translate(-5px,-3px);animation:gb 5s steps(1) infinite}"
        ".s1{animation:s1 5s steps(1) infinite}.s2{animation:s2 5s steps(1) infinite}"
        ".h1{animation:hide 5s steps(1) infinite}.h2{animation:hide2 5s steps(1) infinite}"
        "@keyframes sweep{to{transform:translateY(SWEEPpx)}}"
        ".sweep{animation:sweep 7s linear infinite}"
        "@keyframes port{0%,100%{opacity:.25}50%{opacity:1}}"
        ".port{animation:port 2.4s steps(2) infinite}"
    ).replace("SWEEP", str(h + 160)) + STILL_CSS
    if still:
        css += "*{animation:none!important}"

    o = [svg_open(w, h, "LOTUS", "Local Operational Trusted USB System. Offline device control for Windows.")]
    o.append("<defs>")
    o.append(hazard_pattern())
    o.append(grid_pattern())
    o.append(SCAN_DEFS)
    o.append('<clipPath id="frame"><polygon points="%s"/></clipPath>' % points(frame))
    o.append('<clipPath id="band1"><rect x="-60" y="22" width="640" height="26"/></clipPath>')
    o.append('<clipPath id="band2"><rect x="-60" y="74" width="640" height="18"/></clipPath>')
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
    o.append('<g transform="translate(0 %g) scale(%g)">' % (round(oy, 2), round(zoom, 4)))
    o.append('<ellipse cx="640" cy="160" rx="430" ry="200" fill="url(#halo)"/>')

    # The mark, with two slow rings behind it.
    o.append(
        '<circle class="ring" cx="189" cy="187" r="142" fill="none" stroke="%s" stroke-width="2" '
        'stroke-dasharray="3 15" opacity=".9"/>' % LINE
    )
    o.append(
        '<circle class="arc" cx="189" cy="187" r="156" fill="none" stroke="%s" stroke-width="2" '
        'stroke-dasharray="70 910" opacity=".8"/>' % CYAN
    )
    o.append(
        '<circle class="ring" cx="189" cy="187" r="156" fill="none" stroke="%s" stroke-width="2" '
        'stroke-dasharray="26 954" opacity=".8"/>' % PINK
    )
    o.append('<g class="breath" filter="url(#glow)">%s</g>' % petals(2.5, 64, 62))
    o.append(petals(2.5, 64, 62, "pop"))

    # The word, with its two colour ghosts and two slices that jump sideways.
    o.append('<g transform="translate(372 78)">')
    o.append('<g class="ga">%s</g>' % word(PINK))
    o.append('<g class="gb">%s</g>' % word(CYAN))
    o.append(word(TEXT))
    # Each slice is hidden until its moment, so the word is clean between glitches.
    o.append('<g class="h1" opacity="0"><rect x="-60" y="22" width="640" height="26" fill="%s"/>' % INK)
    o.append('<g clip-path="url(#band1)"><g class="s1">%s%s</g></g></g>' % (
        '<g transform="translate(-7 0)">%s</g>' % word(CYAN), word(TEXT)))
    o.append('<g class="h2" opacity="0"><rect x="-60" y="74" width="640" height="18" fill="%s"/>' % INK)
    o.append('<g clip-path="url(#band2)"><g class="s2">%s%s</g></g></g>' % (
        '<g transform="translate(7 0)">%s</g>' % word(PINK), word(TEXT)))
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
    markup, end = typed(372 + 3 * 11.4, 300, phrase, 19, 1.1, 0.045, TEXT, "k", initials)
    o.append(markup)
    o.append(
        '<rect class="late" style="animation-delay:%.2fs,%.2fs" x="%g" y="284" width="11" height="19" fill="%s"/>'
        % (end, end, round(372 + 3 * 11.4 + len(phrase) * 11.4 + 6, 1), CYAN)
    )
    o.append(mono(372, 338, "NO SERVER AND NO ACCOUNT. NOTHING LEAVES THE PC.", 15, DIM))

    # A small status panel on the right.
    o.append('<rect x="930" y="78" width="232" height="216" fill="%s" stroke="%s"/>' % (PANEL, LINE))
    o.append('<rect x="930" y="78" width="4" height="216" fill="%s"/>' % CYAN)
    o.append(mono(952, 106, "//  GUARD", 14, DIM))
    rows = [
        ("SERVICE", "RUNNING", GREEN),
        ("POLICY", "ARMED", CYAN),
        ("STORAGE", "BLOCK", PINK),
        ("PNP", "TRUSTED ONLY", PINK),
        ("NETWORK", "NONE", YELLOW),
    ]
    for i, (label, value, colour) in enumerate(rows):
        y = 138 + i * 27
        o.append(mono(952, y, label, 15, DIM))
        o.append(mono(1146, y, value, 15, colour, "end"))
    o.append('<rect class="pulse" x="1058" y="127" width="10" height="10" fill="%s"/>' % GREEN)
    o.append('<path d="M952 256H1146" stroke="%s"/>' % LINE)
    o.append(mono(952, 280, "PORTS", 15, DIM))
    for i in range(8):
        colour = PINK if i in (2, 5) else CYAN
        o.append(
            '<rect class="port" style="animation-delay:%.2fs" x="%d" y="270" width="10" height="10" fill="%s"/>'
            % (-0.3 * ((i * 5) % 8), 1018 + i * 16, colour)
        )

    o.append("</g>")

    # Warning stripes and corner marks on the frame itself.
    o.append(hazard_strip(0, 0, 430, 10, "hzt"))
    o.append(hazard_strip(w - 430, h - 10, 430, 10, "hzb", back=True))
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
    w, h = 1200, 14
    css = BASE_CSS + (
        "@keyframes run{from{transform:translateX(-140px)}to{transform:translateX(1260px)}}"
        "@keyframes runback{from{transform:translateX(1260px)}to{transform:translateX(-140px)}}"
        ".run{animation:run 5s linear infinite}.runback{animation:runback 7s linear infinite}"
    ) + STILL_CSS
    o = [svg_open(w, h, "Divider", "A strip of warning stripes.")]
    o.append("<defs>%s" % hazard_pattern())
    o.append('<clipPath id="bar"><polygon points="14,0 1200,0 1186,14 0,14"/></clipPath>')
    o.append(
        '<linearGradient id="tail" x1="0" x2="1"><stop offset="0" stop-color="%s" stop-opacity="0"/>'
        '<stop offset="1" stop-color="%s"/></linearGradient>' % (CYAN, CYAN)
    )
    o.append(
        '<linearGradient id="tailback" x1="1" x2="0"><stop offset="0" stop-color="%s" stop-opacity="0"/>'
        '<stop offset="1" stop-color="%s"/></linearGradient>' % (PINK, PINK)
    )
    o.append("</defs><style>%s</style>" % css)
    o.append('<g clip-path="url(#bar)">')
    o.append('<rect width="%d" height="%d" fill="%s"/>' % (w, h, INK))
    o.append('<path d="M0 7H1200" stroke="%s" stroke-width="2"/>' % LINE)
    o.append('<rect class="run" x="0" y="5" width="140" height="2" fill="url(#tail)"/>')
    o.append('<rect class="runback" x="0" y="7" width="140" height="2" fill="url(#tailback)"/>')
    o.append(hazard_strip(0, 0, 210, 14, "dl"))
    o.append(hazard_strip(990, 0, 210, 14, "dr", back=True))
    o.append("</g></svg>")
    return "".join(o)


# ---------------------------------------------------------------- guard feed


def usb_stick(colour):
    return (
        '<g fill="none" stroke="%s" stroke-width="3" stroke-linejoin="miter">'
        '<path d="M-24 -66H24V-30H-24Z"/><path d="M-12 -54V-44M12 -54V-44"/>'
        '<path d="M-36 -30H36V52L22 66H-36Z"/><path d="M-14 4H14M-14 20H14"/></g>' % colour
    )


def headset(colour):
    return (
        '<g fill="none" stroke="%s" stroke-width="3" stroke-linejoin="miter">'
        '<path d="M-46 8V-12A46 46 0 0 1 46 -12V8"/>'
        '<path d="M-58 8H-34V56H-58Z"/><path d="M34 8H58V56H34Z"/>'
        '<path d="M-46 56V70H-6"/></g>' % colour
    )


def feed():
    w, h = 1000, 400
    cycle = 20.0
    css = BASE_CSS + (
        "@keyframes c{0%{opacity:0}.1%{opacity:1}60%{opacity:0}100%{opacity:0}}"
        ".c{animation:c 20s steps(1) infinite both}"
        "@keyframes wa{0%{opacity:1}48%{opacity:0}100%{opacity:0}}"
        "@keyframes wb{0%{opacity:0}50%{opacity:1}98%{opacity:0}100%{opacity:0}}"
        ".wa{animation:wa 20s steps(1) infinite}.wb{animation:wb 20s steps(1) infinite}"
        "@keyframes stamp{0%{opacity:0;transform:scale(1.9) rotate(-6deg)}"
        "1.4%{opacity:1;transform:scale(1) rotate(-6deg)}58%{opacity:1;transform:scale(1) rotate(-6deg)}"
        "60%{opacity:0;transform:scale(1) rotate(-6deg)}100%{opacity:0;transform:scale(1) rotate(-6deg)}}"
        ".stamp{animation:stamp 20s ease-out infinite both;transform-box:fill-box;transform-origin:center;"
        "transform:rotate(-6deg)}"
        "@keyframes sweep{to{transform:translateY(560px)}}"
        ".sweep{animation:sweep 7s linear infinite}"
    ) + STILL_CSS

    frame = [0, 0, w - 26, 0, w, 26, w, h, 26, h, 0, h - 26]
    o = [svg_open(
        w, h, "What LOTUS Guard does when a device is plugged in",
        "A replay in two parts. An unknown SanDisk flash drive is plugged in while removable storage is set "
        "to block, and LOTUS switches it off. A HyperX headset whose model is on the trusted list is plugged "
        "in while USB devices are set to trusted only, and it keeps working.")]
    o.append("<defs>%s%s%s" % (hazard_pattern(), grid_pattern(), SCAN_DEFS))
    o.append('<clipPath id="frame"><polygon points="%s"/></clipPath>' % points(frame))
    o.append("</defs><style>%s</style>" % css)
    o.append('<g clip-path="url(#frame)">')
    o.append('<rect width="%d" height="%d" fill="%s"/>' % (w, h, INK))
    o.append('<rect y="46" width="%d" height="%d" fill="url(#grid)"/>' % (w, h - 46))

    # Title bar.
    o.append('<rect width="%d" height="46" fill="%s"/>' % (w, PANEL))
    o.append('<polygon points="0,0 196,0 180,46 0,46" fill="%s"/>' % CYAN)
    o.append(
        '<text x="24" y="31" class="cond" font-size="22" fill="%s" textLength="132" '
        'lengthAdjust="spacingAndGlyphs">LOTUS GUARD</text>' % INK
    )
    o.append(mono(216, 29, "//  WHAT HAPPENS WHEN A DEVICE IS PLUGGED IN", 13, DIM))
    o.append('<rect class="pulse" x="882" y="18" width="10" height="10" fill="%s"/>' % PINK)
    o.append(mono(902, 29, "REPLAY", 13, TEXT))
    o.append('<path d="M0 46H%d" stroke="%s"/>' % (w, LINE))

    # The device card on the right.
    o.append('<rect x="700" y="74" width="268" height="292" fill="%s" stroke="%s"/>' % (PANEL, LINE))
    o.append('<rect x="700" y="74" width="268" height="4" fill="%s"/>' % LINE)
    o.append(mono(720, 106, "//  DEVICE", 12, DIM))

    scenes = [
        {
            "window": "wa", "offset": 0.0, "icon": usb_stick, "accent": PINK,
            "name": "SANDISK CRUZER BLADE", "kind": "REMOVABLE STORAGE", "stamp": "BLOCKED",
            "prompt": "a USB flash drive is plugged in",
            "rows": [
                ("FOUND", "SanDisk Cruzer Blade USB Device", TEXT),
                ("ID", "USB\\VID_0781&PID_5567\\4C530001230505116353", TEXT),
                ("KIND", "storage, from USB class 08", TEXT),
                ("POLICY", "removable storage is set to block", TEXT),
                ("TRUSTED", "no", PINK),
                ("ACTION", "switch it off and keep it off after a replug", TEXT),
            ],
            "result": "Blocked because removable storage is blocked",
            "result2": "and this device is not trusted.",
        },
        {
            "window": "wb", "offset": 10.0, "icon": headset, "accent": GREEN,
            "name": "HYPERX CLOUD III WIRELESS", "kind": "AUDIO + INPUT", "stamp": "ALLOWED",
            "prompt": "a USB headset is plugged in",
            "rows": [
                ("FOUND", "HyperX Cloud III Wireless", TEXT),
                ("ID", "USB\\VID_03F0&PID_0C9D\\0000000000000000", TEXT),
                ("KIND", "audio and input, from USB classes 01 and 03", TEXT),
                ("POLICY", "USB devices are set to trusted only", TEXT),
                ("TRUSTED", "yes, the whole model VID_03F0&PID_0C9D", GREEN),
                ("ACTION", "none", TEXT),
            ],
            "result": "Trusted. Every device of this model is allowed.",
            "result2": "",
        },
    ]

    size = 14
    cell = size * 0.6
    for index, scene in enumerate(scenes):
        base = scene["offset"]
        hidden = ' opacity="0"' if index else ""
        o.append('<g class="%s"%s>' % (scene["window"], hidden))

        # Left column: the log.
        y = 92
        o.append(mono(32, y, ">", size, CYAN))
        markup, end = typed(32 + 2 * cell, y, scene["prompt"], size, base + 0.4, 0.03, CYAN, "c")
        o.append(markup)
        t = end + 0.5
        for number, (label, value, colour) in enumerate(scene["rows"], start=1):
            y += 31
            line_start = t
            markup, end = typed(32, y, "%02d" % number, size, line_start, 0.0, DIM, "c")
            o.append(markup)
            markup, end = typed(32 + 4 * cell, y, label, size, line_start, 0.0, YELLOW, "c")
            o.append(markup)
            markup, end = typed(32 + 13 * cell, y, value, size, line_start + 0.12, 0.014, colour, "c")
            o.append(markup)
            t = end + 0.3
        y += 40
        result_at = t + 0.2
        o.append(
            '<rect class="c" style="animation-delay:%.2fs" x="32" y="%d" width="4" height="%d" fill="%s"/>'
            % (result_at, y - 15, 44 if scene["result2"] else 20, scene["accent"])
        )
        markup, end = typed(32 + 2 * cell, y, scene["result"], size, result_at, 0.012, scene["accent"], "c")
        o.append(markup)
        if scene["result2"]:
            markup, end = typed(32 + 2 * cell, y + 24, scene["result2"], size, end, 0.012, scene["accent"], "c")
            o.append(markup)

        # Right column: the device, and the verdict stamped across it.
        o.append('<g transform="translate(834 206)">%s</g>' % scene["icon"](CYAN))
        o.append(
            '<g class="c" style="animation-delay:%.2fs" transform="translate(834 206)">%s</g>'
            % (result_at, scene["icon"](scene["accent"]))
        )
        o.append(mono(834, 312, scene["name"], 13, TEXT, "middle"))
        o.append(mono(834, 334, scene["kind"], 12, DIM, "middle"))
        o.append('<g class="stamp" style="animation-delay:%.2fs">' % result_at)
        o.append('<polygon points="744,186 936,186 924,232 732,232" fill="%s"/>' % scene["accent"])
        o.append(
            '<text x="834" y="220" class="cond" font-size="32" fill="%s" text-anchor="middle" '
            'textLength="130" lengthAdjust="spacingAndGlyphs">%s</text>' % (INK, scene["stamp"])
        )
        o.append("</g>")
        o.append("</g>")
        assert result_at - base < 8.5, "scene %d runs past its window" % index
        assert result_at < cycle

    o.append(hazard_strip(0, h - 8, 300, 8, "fz"))
    o.append(scanlines(w, h))
    o.append('<polygon points="%s" fill="none" stroke="%s" stroke-width="2"/>' % (points(frame), LINE))
    o.append("</g></svg>")
    return "".join(o)


# ---------------------------------------------------------------- decision flow


def flow():
    w, h = 1000, 440
    rail = 222
    up = 78
    down = 356
    centres = [200, 350, 500, 650, 800]
    checks = [
        ("01", "LOTUS IS", "DISARMED?", True),
        ("02", "WINDOWS", "BOOT DISK?", True),
        ("03", "ON THE", "TRUSTED LIST?", True),
        ("04", "HAS STORAGE,", "SET TO BLOCK?", False),
        ("05", "ANYTHING ELSE,", "TRUSTED ONLY?", False),
    ]
    css = BASE_CSS + (
        "@keyframes dash{to{stroke-dashoffset:-24}}"
        ".flowline{stroke-dasharray:4 8;animation:dash 1.2s linear infinite}"
        "@keyframes lamp{0%,100%{opacity:.35}50%{opacity:1}}"
        ".lamp{animation:lamp 2s ease-in-out infinite}"
    ) + STILL_CSS

    frame = [0, 0, w - 26, 0, w, 26, w, h, 26, h, 0, h - 26]
    o = [svg_open(
        w, h, "The order in which LOTUS decides",
        "A device on a USB port passes five checks from left to right. If LOTUS is disarmed, if Windows boots "
        "from the device, or if the device is on the trusted list, it is allowed. If it has storage and storage "
        "is set to block, or if it does anything else and USB devices are set to trusted only, it is blocked. "
        "A device that no check stops is allowed.")]
    o.append("<defs>%s%s%s" % (hazard_pattern(), grid_pattern(), SCAN_DEFS))
    o.append('<clipPath id="frame"><polygon points="%s"/></clipPath>' % points(frame))
    o.append('<filter id="glow" x="-200%" y="-200%" width="500%" height="500%"><feGaussianBlur stdDeviation="4"/></filter>')
    o.append("</defs><style>%s</style>" % css)
    o.append('<g clip-path="url(#frame)">')
    o.append('<rect width="%d" height="%d" fill="%s"/>' % (w, h, INK))
    o.append('<rect width="%d" height="%d" fill="url(#grid)"/>' % (w, h))
    o.append(mono(28, 34, "//  THE DECISION, IN ORDER", 13, DIM))

    # Lines: the rail, the way up to "allow" and the way down to "block".
    o.append('<path d="M112 %d H927 V104" fill="none" stroke="%s" stroke-width="2"/>' % (rail, LINE))
    for (number, a, b, allow), cx in zip(checks, centres):
        if allow:
            o.append('<path class="flowline" d="M%d %d V%d H870" fill="none" stroke="%s" stroke-width="2"/>'
                     % (cx, rail, up, GREEN))
        else:
            o.append('<path class="flowline" d="M%d %d V%d H870" fill="none" stroke="%s" stroke-width="2"/>'
                     % (cx, rail, down, PINK))
    o.append('<path class="flowline" d="M112 %d H927 V104" fill="none" stroke="%s" stroke-width="2" opacity=".55"/>'
             % (rail, CYAN))

    # Three devices travelling through the checks.
    trips = [
        # A negative start time puts all three on the rail from the first frame.
        (GREEN, "M112 %d H500 V%d H870" % (rail, up), "0s"),
        (PINK, "M112 %d H650 V%d H870" % (rail, down), "-4.8s"),
        (CYAN, "M112 %d H927 V104" % rail, "-2.4s"),
    ]
    for colour, path, begin in trips:
        for radius, extra in ((9, ' filter="url(#glow)" opacity=".9"'), (5, "")):
            o.append(
                '<circle r="%d" fill="%s"%s><animateMotion dur="7.2s" begin="%s" repeatCount="indefinite" '
                'path="%s" calcMode="linear"/></circle>' % (radius, colour, extra, begin, path)
            )

    # The device on the left.
    o.append('<polygon points="22,196 112,196 112,248 34,248 22,236" fill="%s" stroke="%s" stroke-width="2"/>'
             % (PANEL2, CYAN))
    o.append(mono(67, 219, "DEVICE ON", 12, TEXT, "middle"))
    o.append(mono(67, 236, "A USB PORT", 12, TEXT, "middle"))

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
    o.append('<polygon points="870,52 985,52 985,104 882,104 870,92" fill="%s"/>' % GREEN)
    o.append('<text x="927" y="79" class="cond" font-size="22" fill="%s" text-anchor="middle" '
             'textLength="76" lengthAdjust="spacingAndGlyphs">ALLOWED</text>' % INK)
    o.append(mono(927, 96, "IT WORKS", 11, INK, "middle"))
    o.append('<polygon points="870,330 985,330 985,382 882,382 870,370" fill="%s"/>' % PINK)
    o.append('<text x="927" y="357" class="cond" font-size="22" fill="%s" text-anchor="middle" '
             'textLength="76" lengthAdjust="spacingAndGlyphs">BLOCKED</text>' % INK)
    o.append(mono(927, 374, "SWITCHED OFF", 11, INK, "middle"))

    # What the three moving dots stand for.
    legend = [
        (GREEN, "a headset on the trusted list"),
        (PINK, "an unknown flash drive"),
        (CYAN, "a device no check stops"),
    ]
    x = 28
    for colour, label in legend:
        o.append('<rect class="lamp" x="%d" y="%d" width="9" height="9" fill="%s"/>' % (x, h - 31, colour))
        o.append(mono(x + 17, h - 22, label, 13, DIM))
        x += 17 + len(label) * 7.8 + 34

    o.append(scanlines(w, h).split("<rect class")[0])
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
        "guard-feed.svg": feed(),
        "decision-flow.svg": flow(),
    }
    for name, content in files.items():
        path = os.path.join(folder, name)
        with open(path, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(content + "\n")
        print("wrote %s (%d bytes)" % (os.path.relpath(path, root), len(content)))


if __name__ == "__main__":
    main()
