"""Verification contact sheets.

The acceptance test for this pipeline is visual and nothing else: does it look
like that Pokemon?  These sheets exist so that question can actually be asked
-- the sprite at the zooms it will really be seen at, on both a light and a
dark ground, next to the official artwork it came from.

    python Tools/Sprites/contact.py [species_id ...]

Writes to Tools/Sprites/_verify/.
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import recipes as R
from build import MANIFEST, OUT_DIR

VERIFY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_verify")
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

LIGHT = (226, 228, 224)
DARK = (26, 28, 34)
INK = (24, 24, 24)
INK_ON_DARK = (232, 232, 228)


def _sheet_frames(path, cell_w, cell_h, cols, count, trim=None):
    """Slice a sheet into frames.

    `trim` crops every frame by the SAME rect (the union of all their content
    boxes), so review sheets are not 90% empty cell padding while the frames
    still stay in register with each other -- cropping each frame to its own
    content would hide exactly the drift these sheets exist to catch.
    """
    im = Image.open(path).convert("RGBA")
    out = []
    for i in range(count):
        r, c = divmod(i, cols)
        out.append(im.crop((c * cell_w, r * cell_h,
                            (c + 1) * cell_w, (r + 1) * cell_h)))
    if trim is None:
        boxes = [f.getbbox() for f in out if f.getbbox()]
        if boxes:
            trim = (min(b[0] for b in boxes) , min(b[1] for b in boxes),
                    max(b[2] for b in boxes), max(b[3] for b in boxes))
    if trim:
        out = [f.crop(trim) for f in out]
    return out, trim


def _zoom(im, z):
    return im.resize((im.width * z, im.height * z), Image.NEAREST)


def _paste_row(canvas, d, items, x, y, label, ink):
    d.text((x, y), label, fill=ink)
    cx = x
    for im in items:
        canvas.paste(im, (cx, y + 14), im)
        cx += im.width + 8
    return y + 14 + (max(i.height for i in items) if items else 0) + 10


def build_contact(entry: dict) -> None:
    cell = entry["cell"]
    cw, chh, cols = cell["width"], cell["height"], cell["columns"]
    name = entry["name"]

    views, trim = {}, None
    for v, vd in entry["views"].items():
        views[v], trim = _sheet_frames(os.path.join(ROOT, vd["sheet"]),
                                       cw, chh, cols, vd["frame_count"], trim)

    idle_f = entry["views"]["front"]["states"]["Idle"]["start"]
    idle_b = entry["views"]["back"]["states"]["Idle"]["start"]
    front, back = views["front"][idle_f], views["back"][idle_b]
    fmirror = front.transpose(Image.FLIP_LEFT_RIGHT)
    bmirror = back.transpose(Image.FLIP_LEFT_RIGHT)

    # ---------------- sheet 1: zoom ladder, light and dark ----------------
    zooms = (1, 2, 4)
    row_items = {z: [_zoom(i, z) for i in (front, fmirror, back, bmirror)]
                 for z in zooms}
    width = max(sum(i.width + 8 for i in row_items[z]) for z in zooms) + 40
    height = sum(max(i.height for i in row_items[z]) + 26 for z in zooms) + 30

    sheet = Image.new("RGB", (width * 2, height), LIGHT)
    d = ImageDraw.Draw(sheet)
    d.rectangle([width, 0, width * 2, height], fill=DARK)
    for bg_i, (ox, ink) in enumerate(((0, INK), (width, INK_ON_DARK))):
        y = 14
        for z in zooms:
            y = _paste_row(sheet, d, row_items[z], ox + 16, y,
                           f"{z}x   front / front-mirrored / back / back-mirrored",
                           ink)
    d.text((16, height - 14), f"{name}  sprite {entry['sprite_height_px']}px  "
                              f"PPU {entry['pixels_per_unit']}  "
                              f"{entry['palette_colours']} colours", fill=INK)
    sheet.save(os.path.join(VERIFY, f"{name.lower()}_contact.png"))

    # ---------------- sheet 2: against the official artwork ----------------
    src = Image.open(os.path.join(R.SOURCE_DIR, entry["source_artwork"])).convert("RGBA")
    target_h = (trim[3] - trim[1]) * 4
    sc = target_h / src.height
    src_r = src.resize((int(src.width * sc), target_h), Image.LANCZOS)
    port = Image.open(os.path.join(ROOT, entry["portrait"]["path"])).convert("RGBA")

    panels = [("official artwork", src_r),
              ("front 4x", _zoom(front, 4)),
              ("back 4x", _zoom(back, 4)),
              ("portrait 3x", _zoom(port, 3))]
    W = sum(p[1].width + 24 for p in panels) + 24
    H = max(p[1].height for p in panels) + 44
    cmp_sheet = Image.new("RGB", (W, H * 2), LIGHT)
    d = ImageDraw.Draw(cmp_sheet)
    d.rectangle([0, H, W, H * 2], fill=DARK)
    for row, (oy, ink) in enumerate(((0, INK), (H, INK_ON_DARK))):
        x = 16
        for label, im in panels:
            d.text((x, oy + 8), label, fill=ink)
            cmp_sheet.paste(im, (x, oy + 26), im)
            x += im.width + 24
    cmp_sheet.save(os.path.join(VERIFY, f"{name.lower()}_vs_official.png"))

    # ---------------- sheet 3: animation frames ----------------
    z = 2
    rows = []
    for v in ("front", "back"):
        states = entry["views"][v]["states"]
        for state, loc in states.items():
            fr = [_zoom(views[v][loc["start"] + i], z) for i in range(loc["count"])]
            rows.append((f"{v}  {state}", fr))
    W = max(sum(i.width + 6 for i in r[1]) for r in rows) + 200
    H = sum(max(i.height for i in r[1]) + 8 for r in rows) + 20
    anim = Image.new("RGB", (W, H), LIGHT)
    d = ImageDraw.Draw(anim)
    y = 10
    for label, fr in rows:
        d.text((10, y + fr[0].height // 2 - 6), label, fill=INK)
        x = 190
        for im in fr:
            anim.paste(im, (x, y), im)
            x += im.width + 6
        y += fr[0].height + 8
    anim.save(os.path.join(VERIFY, f"{name.lower()}_anim.png"))
    print("wrote contact sheets for", name)


def main(argv):
    os.makedirs(VERIFY, exist_ok=True)
    with open(MANIFEST, encoding="utf-8") as fh:
        man = json.load(fh)
    ids = [int(a) for a in argv]
    for entry in man["creatures"]:
        if ids and entry["species_id"] not in ids:
            continue
        build_contact(entry)


if __name__ == "__main__":
    main(sys.argv[1:])
