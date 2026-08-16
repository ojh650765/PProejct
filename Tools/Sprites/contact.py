"""Verification contact sheets.

    python Tools/Sprites/contact.py [game_species_id ...]

The two failure modes of this pipeline -- accidental resampling and broken
alignment -- are both invisible in code and obvious in an image, so nothing
ships without being looked at.  Three sheets per creature:

  * _vs_source : the packed frame beside the untouched source file, at 1x and
                 4x, light and dark.  Any softening or colour drift shows up
                 here immediately as a difference between two panels that must
                 be pixel-identical.
  * _align     : every frame of both views stacked into one image, plus the
                 ground line and centre line drawn on.  A creature that bobs
                 or slides when frames or views change shows up as a smear
                 against those lines.
  * _anim      : the loop laid out in play order.

Writes to Tools/Sprites/_verify/ (not shipped; regenerate on demand).
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageSequence

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import species as S
from extract import MANIFEST, ROOT

VERIFY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_verify")
LIGHT = (226, 228, 224)
DARK = (24, 26, 32)
INK = (20, 20, 20)
INK_D = (232, 232, 228)
GRID = (232, 96, 96)


def _frames(entry, view):
    vd = entry["views"][view]
    im = Image.open(os.path.join(ROOT, vd["sheet"])).convert("RGBA")
    cell, cols = entry["cell"]["width"], entry["cell"]["columns"]
    out = []
    for i in range(vd["unique_frames"]):
        r, c = divmod(i, cols)
        out.append(im.crop((c * cell, r * cell, (c + 1) * cell, (r + 1) * cell)))
    return out, vd


def _zoom(im, z):
    return im.resize((im.width * z, im.height * z), Image.NEAREST)


def sheet_vs_source(entry, name):
    """Packed frame beside the raw source frame -- these must match exactly."""
    panels = []
    for view in ("front", "back"):
        frames, vd = _frames(entry, view)
        src = Image.open(S.anim_path(entry["dex_number"], view))
        src0 = next(ImageSequence.Iterator(src)).convert("RGBA")
        packed = frames[vd["clips"]["idle"]["sequence"][0]]
        static_src = Image.open(S.static_path(entry["dex_number"], view)).convert("RGBA")
        panels += [(f"{view} source gif f0", src0),
                   (f"{view} packed f0", packed),
                   (f"{view} source png", static_src),
                   (f"{view} packed static", frames[vd["static_frame"]])]

    z = 4
    zp = [(l, _zoom(i, z)) for l, i in panels]
    W = sum(i.width + 16 for _, i in zp) + 16
    H = max(i.height for _, i in zp) + 40
    out = Image.new("RGB", (W, H * 2), LIGHT)
    d = ImageDraw.Draw(out)
    d.rectangle([0, H, W, H * 2], fill=DARK)
    for oy, ink in ((0, INK), (H, INK_D)):
        x = 8
        for label, im in zp:
            d.text((x, oy + 6), label, fill=ink)
            out.paste(im, (x, oy + 22), im)
            x += im.width + 16
    out.save(os.path.join(VERIFY, f"{name}_vs_source.png"))


def sheet_align(entry, name):
    """All frames stacked, with the ground and centre lines drawn on."""
    cell = entry["cell"]["width"]
    z = 4
    tiles = []
    for view in ("front", "back"):
        frames, _ = _frames(entry, view)
        acc = np.zeros((cell, cell, 4), np.float32)
        for f in frames:
            a = np.asarray(f, np.float32) / 255.0
            acc[..., :3] += a[..., :3] * a[..., 3:4]
            acc[..., 3] += a[..., 3]
        n = max(1, len(frames))
        rgb = acc[..., :3] / np.maximum(acc[..., 3:4], 1e-5)
        alpha = np.clip(acc[..., 3] / n * 2.2, 0, 1)
        im = Image.fromarray(
            (np.concatenate([rgb, alpha[..., None]], 2) * 255).astype(np.uint8), "RGBA")
        tiles.append((f"{view}: all {len(frames)} frames overlaid", _zoom(im, z)))

    W = sum(i.width + 20 for _, i in tiles) + 20
    H = tiles[0][1].height + 46
    out = Image.new("RGB", (W, H), LIGHT)
    d = ImageDraw.Draw(out)
    x = 10
    for label, im in tiles:
        out.paste(im, (x, 26), im)
        gy = 26 + entry["cell"]["height"] * z - (entry["pivot_px"]["y_from_bottom"] + 1) * z
        d.line([x, gy, x + im.width, gy], fill=GRID)
        cx = x + entry["pivot_px"]["x"] * z
        d.line([cx, 26, cx, 26 + im.height], fill=GRID)
        d.text((x, 8), label, fill=INK)
        x += im.width + 20
    d.text((10, H - 16), "red = ground row and pivot column; every frame of both "
                         "views is registered to these", fill=INK)
    out.save(os.path.join(VERIFY, f"{name}_align.png"))


def sheet_anim(entry, name):
    z = 2
    rows = []
    for view in ("front", "back"):
        frames, vd = _frames(entry, view)
        seq = vd["clips"]["idle"]["sequence"]
        rows.append((view, [_zoom(frames[i], z) for i in seq]))
    per_row = 16
    blocks = []
    for view, ims in rows:
        for i in range(0, len(ims), per_row):
            blocks.append((f"{view} {i}", ims[i:i + per_row]))
    W = per_row * (blocks[0][1][0].width + 4) + 120
    H = sum(b[1][0].height + 6 for b in blocks) + 20
    out = Image.new("RGB", (W, H), LIGHT)
    d = ImageDraw.Draw(out)
    y = 10
    for label, ims in blocks:
        d.text((8, y + ims[0].height // 2), label, fill=INK)
        x = 110
        for im in ims:
            out.paste(im, (x, y), im)
            x += im.width + 4
        y += ims[0].height + 6
    out.save(os.path.join(VERIFY, f"{name}_anim.png"))


def sheet_cast(creatures, path):
    """The whole cast on one image: resting front over resting back, labelled.

    This is the sheet that catches a wrong id mapping, which none of the
    per-species sheets can -- they compare a species against its own source, so
    they pass just as cleanly when the source is the wrong animal.  Here the
    name is printed under the artwork and the two are read together.
    """
    z = 2
    cols = 9
    rows = (len(creatures) + cols - 1) // cols
    cw, ch = 96 * z + 12, 96 * z * 2 + 30
    out = Image.new("RGB", (cols * cw + 16, rows * ch + 16), DARK)
    d = ImageDraw.Draw(out)
    for i, entry in enumerate(creatures):
        r, c = divmod(i, cols)
        x, y = 8 + c * cw, 8 + r * ch
        for j, view in enumerate(("front", "back")):
            frames, vd = _frames(entry, view)
            im = _zoom(frames[vd["clips"]["idle"]["sequence"][0]], z)
            out.paste(im, (x, y + j * (96 * z)), im)
        d.text((x + 2, y + 96 * z * 2 + 4),
               f"{entry['name']}  g{entry['species_id']}/d{entry['dex_number']}",
               fill=INK_D)
    out.save(path)
    print(f"cast sheet: {len(creatures)} species -> {path}")


def main(argv):
    os.makedirs(VERIFY, exist_ok=True)
    with open(MANIFEST, encoding="utf-8") as fh:
        man = json.load(fh)
    if argv and argv[0] == "--cast":
        sheet_cast(man["creatures"], os.path.join(VERIFY, "cast_all.png"))
        return
    ids = [int(a) for a in argv]
    for entry in man["creatures"]:
        if ids and entry["species_id"] not in ids:
            continue
        name = entry["name"].lower()
        sheet_vs_source(entry, name)
        sheet_align(entry, name)
        sheet_anim(entry, name)
        print("contact sheets:", entry["name"])


if __name__ == "__main__":
    main(sys.argv[1:])
