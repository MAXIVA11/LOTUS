"""Builds docs/images/demo.gif, the walkthrough at the top of README.md.

It needs Windows, Pillow and ffmpeg:

    src\\Lotus.App\\bin\\Release\\LOTUS.exe --shots build\\shots
    python tools/make-demo.py build\\shots

The first command makes LOTUS draw its own pages in demo mode. This script puts
a caption under each page, adds a short glitch between pages and hands the
frames to ffmpeg, which writes the GIF.
"""

import os
import random
import shutil
import subprocess
import sys
import tempfile

from PIL import Image, ImageChops, ImageDraw, ImageFont

INK = (8, 8, 12)
PANEL = (17, 17, 24)
LINE = (43, 43, 58)
TEXT = (244, 244, 246)
DIM = (141, 141, 163)
CYAN = (0, 240, 255)
YELLOW = (252, 238, 10)

STEPS = [
    ("01-control-disarmed.png", "After setup LOTUS is disarmed. Nothing is blocked yet."),
    ("02-control-pending.png", "Choose the two rules and turn the master switch on."),
    ("03-apply-dialog.png", "Apply lists what the new policy would switch off."),
    ("04-control-armed.png", "Armed. The guard service enforces the policy from here on."),
    ("05-devices.png", "Blocked devices are at the top. Trust one from this page."),
    ("06-trusted.png", "Devices on the trusted list are never blocked."),
    ("07-activity.png", "The activity log records each block and release."),
]

CAPTION_HEIGHT = 64
HOLD_SECONDS = 2.8
GLITCH_SECONDS = 0.08


def font(name, size, variation=None):
    path = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", name)
    loaded = ImageFont.truetype(path, size)
    if variation:
        try:
            loaded.set_variation_by_name(variation)
        except (OSError, ValueError):
            pass
    return loaded


def compose(page, number, caption, total):
    """One page of the app with its caption strip underneath."""
    width, height = page.size
    frame = Image.new("RGB", (width, height + CAPTION_HEIGHT), INK)
    frame.paste(page.convert("RGB"), (0, 0))
    draw = ImageDraw.Draw(frame)
    top = height
    draw.rectangle([0, top, width, top + CAPTION_HEIGHT], fill=PANEL)
    draw.line([0, top, width, top], fill=LINE, width=2)

    # The slanted yellow tag with the step number.
    draw.polygon([(34, top + 14), (104, top + 14), (92, top + 50), (22, top + 50)], fill=YELLOW)
    tag = font("bahnschrift.ttf", 26, "Bold Condensed")
    label = "%02d" % number
    box = draw.textbbox((0, 0), label, font=tag)
    draw.text((63 - (box[2] - box[0]) / 2 - box[0], top + 32 - (box[3] - box[1]) / 2 - box[1]), label, font=tag, fill=INK)

    body = font("bahnschrift.ttf", 24, "SemiBold SemiCondensed")
    draw.text((128, top + 17), caption, font=body, fill=TEXT)

    # One pip per step on the right. The current one is lit.
    x = width - 30 - total * 22
    for i in range(total):
        colour = CYAN if i == number - 1 else LINE
        draw.polygon([(x + 6, top + 26), (x + 20, top + 26), (x + 14, top + 38), (x, top + 38)], fill=colour)
        x += 22
    return frame


def glitch(frame, rng, strength):
    """Shifts a few horizontal bands sideways and pulls the red and blue channels apart."""
    width, height = frame.size
    red, green, blue = frame.split()
    red = ImageChops.offset(red, int(6 * strength), 0)
    blue = ImageChops.offset(blue, -int(6 * strength), 0)
    out = Image.merge("RGB", (red, green, blue))
    for _ in range(int(5 + 6 * strength)):
        band_height = rng.randint(8, 46)
        y = rng.randint(0, height - band_height)
        shift = rng.randint(-int(70 * strength), int(70 * strength))
        band = out.crop((0, y, width, y + band_height))
        out.paste(ImageChops.offset(band, shift, 0), (0, y))
    return out


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: python tools/make-demo.py <folder with the --shots pictures> [output.gif]")
    shots = sys.argv[1]
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = sys.argv[2] if len(sys.argv) > 2 else os.path.join(root, "docs", "images", "demo.gif")
    if not shutil.which("ffmpeg"):
        sys.exit("ffmpeg was not found on the PATH")

    rng = random.Random(2016)
    pages = []
    for number, (name, caption) in enumerate(STEPS, start=1):
        page = Image.open(os.path.join(shots, name))
        pages.append(compose(page, number, caption, len(STEPS)))

    work = tempfile.mkdtemp(prefix="lotus-demo-")
    try:
        entries = []

        def add(image, seconds):
            path = os.path.join(work, "f%03d.png" % len(entries))
            image.save(path)
            entries.append((path, seconds))

        for index, page in enumerate(pages):
            add(page, HOLD_SECONDS)
            following = pages[(index + 1) % len(pages)]
            add(glitch(Image.blend(page, following, 0.5), rng, 1.0), GLITCH_SECONDS)
            add(glitch(following, rng, 0.5), GLITCH_SECONDS)

        listing = os.path.join(work, "frames.txt")
        with open(listing, "w", encoding="utf-8") as handle:
            for path, seconds in entries:
                handle.write("file '%s'\nduration %.3f\n" % (path.replace("\\", "/"), seconds))
            # The concat reader ignores the last duration unless the file is named again.
            handle.write("file '%s'\n" % entries[-1][0].replace("\\", "/"))

        palette = os.path.join(work, "palette.png")
        subprocess.check_call([
            "ffmpeg", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", listing,
            "-vf", "palettegen=max_colors=128:stats_mode=full", "-update", "1", palette])
        subprocess.check_call([
            "ffmpeg", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", listing, "-i", palette,
            "-lavfi", "paletteuse=dither=none:diff_mode=rectangle", "-fps_mode", "vfr", "-loop", "0", target])
    finally:
        shutil.rmtree(work, ignore_errors=True)
    print("wrote %s (%d KB)" % (target, os.path.getsize(target) // 1024))


if __name__ == "__main__":
    main()
