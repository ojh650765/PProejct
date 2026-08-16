"""Verification contact sheets for the human cast.

    python Tools/Sprites/contact_people.py [key ...]
    python Tools/Sprites/contact_people.py --cast     # everyone on one image
    python Tools/Sprites/contact_people.py --scale    # people beside creatures

The people-side companion to contact.py, and it exists for the same reason:
the failure modes of this pipeline -- accidental resampling, broken alignment,
a view packed under the wrong name -- are all invisible in code and all obvious
in an image.  Nothing ships without being looked at.

Sheets written per character:

  * _vs_source : every packed cell beside the untouched source cell, at 1x and
                 4x, light and dark.  These panels must be pixel-identical;
                 any softening or colour drift reads immediately as a
                 difference between two pictures that should be the same.
  * _dirs      : all four facings -- front, back, and both sides, the second
                 being the mirror the runtime produces -- each showing its
                 idle pose and its walk cycle in play order, with the ground
                 row and pivot column drawn on.  A character that bobs, slides
                 or turns into someone else shows up against those lines.

And two cast-wide sheets:

  * cast_people : all fourteen, standing, labelled, with role and height.  This
                  is the sheet that catches a character packed under the wrong
                  name, which no per-character sheet can -- those compare a
                  character against its own source, so they pass just as
                  cleanly when the source is the wrong person.
  * scale_check : a resident stood beside creature sprites on a shared metre
                  rule.  This is the one that answers "is a 1.7 m person the
                  right size next to a 0.4 m Pikachu", and it is also the sheet
                  that shows, honestly, how much coarser the Gen 4 field art is
                  than the Gen 5 battle art.

Writes to Tools/Sprites/_verify/ (not shipped; regenerate on demand).
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import people as P
import species as S
from extract_people import MANIFEST, ROOT, load_strip

VERIFY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_verify")
LIGHT = (226, 228, 224)
DARK = (24, 26, 32)
INK = (20, 20, 20)
INK_D = (232, 232, 228)
GRID = (232, 96, 96)
RULE = (96, 140, 200)


def _zoom(im, z):
    return im.resize((im.width * z, im.height * z), Image.NEAREST)


def _cells(entry):
    im = Image.open(os.path.join(ROOT, entry["sheet"].replace("/", os.sep))).convert("RGBA")
    out = []
    for i in range(entry["unique_frames"]):
        r, c = divmod(i, P.COLS)
        out.append(im.crop((c * P.CELL, r * P.CELL,
                            (c + 1) * P.CELL, (r + 1) * P.CELL)))
    return out


def sheet_vs_source(entry, src):
    """Packed cells beside the raw source cells -- these must match exactly."""
    packed = _cells(entry)
    pairs = []
    blocks = [("walk", 0)] + ([("run", 16)] if len(src) == 32 else [])
    for kind, base in blocks:
        for view in ("front", "back", "side"):
            g = base + P.GROUP[view]
            for off in (0, 1, 3):
                s = src[g + off]
                hit = next(i for i, p in enumerate(packed)
                           if np.array_equal(np.asarray(p), s))
                pairs.append((f"{kind[0]}{view[0]}{off}",
                              Image.fromarray(s, "RGBA"), packed[hit]))

    z = 4
    cw = P.CELL * z
    W = len(pairs) * (cw + 10) + 10
    H = cw * 2 + 46
    out = Image.new("RGB", (W, H * 2), LIGHT)
    d = ImageDraw.Draw(out)
    d.rectangle([0, H, W, H * 2], fill=DARK)
    for oy, ink in ((0, INK), (H, INK_D)):
        x = 8
        for label, s, p in pairs:
            d.text((x, oy + 4), label, fill=ink)
            zs, zp = _zoom(s, z), _zoom(p, z)
            out.paste(zs, (x, oy + 18), zs)
            out.paste(zp, (x, oy + 22 + cw), zp)
            x += cw + 10
        d.text((8, oy + H - 14),
               "top row = staged source cell, bottom row = packed cell. "
               "They must be identical.", fill=ink)
    out.save(os.path.join(VERIFY, f"people_{entry['key']}_vs_source.png"))


def sheet_dirs(entry):
    """All four facings, idle and walk, in play order, on the ground line."""
    packed = _cells(entry)
    z = 5
    cw = P.CELL * z

    # front, back, side as drawn, and the side the runtime makes by flipping.
    facings = [("front", False), ("back", False),
               ("side (walks screen-left, as drawn)", False),
               ("side (walks screen-right, runtime U-flip)", True)]
    clip_names = [c for c in ("idle", "walk", "run") if c in entry["views"]["front"]]

    rows = []
    for label, flip in facings:
        view = "side" if label.startswith("side") else label
        for clip in clip_names:
            seq = entry["views"][view][clip]["sequence"]
            ims = []
            for i in seq:
                im = packed[i]
                if flip:
                    im = im.transpose(Image.FLIP_LEFT_RIGHT)
                ims.append(_zoom(im, z))
            rows.append((f"{label}  {clip}", ims))

    label_w = 330
    maxn = max(len(r[1]) for r in rows)
    W = label_w + maxn * (cw + 8) + 16
    H = len(rows) * (cw + 12) + 36
    out = Image.new("RGB", (W, H), LIGHT)
    d = ImageDraw.Draw(out)
    gy_off = (P.GROUND_ROW + 1) * z          # ground line, from cell top
    px_off = (P.CELL // 2) * z               # pivot column

    y = 8
    for label, ims in rows:
        d.text((8, y + cw // 2 - 4), label, fill=INK)
        x = label_w
        for im in ims:
            out.paste(im, (x, y), im)
            d.line([x, y + gy_off, x + cw, y + gy_off], fill=GRID)
            d.line([x + px_off, y, x + px_off, y + cw], fill=GRID)
            x += cw + 8
        y += cw + 12
    d.text((8, H - 22),
           f"{entry['name']} ({entry['key']})  -  red = ground row "
           f"{P.GROUND_ROW} and pivot column {P.CELL // 2}.  "
           f"{entry['display']['sprite_height_px']}px tall = "
           f"{entry['display']['world_height_m']:.2f} m at "
           f"{entry['pixels_per_unit']:.3f} px/m.  Every cell of every facing "
           f"is registered to these two lines.", fill=INK)
    out.save(os.path.join(VERIFY, f"people_{entry['key']}_dirs.png"))


def sheet_cast(man, path):
    """Everyone standing, labelled with role and height.

    The sheet that catches a character packed under the wrong name.
    """
    z = 4
    cols = 7
    chars = man["characters"]
    rows = (len(chars) + cols - 1) // cols
    cw = P.CELL * z * 3 + 16
    ch = P.CELL * z + 40
    out = Image.new("RGB", (cols * cw + 16, rows * ch + 34), DARK)
    d = ImageDraw.Draw(out)
    d.text((10, 8), "PEOPLE CAST  -  front / back / side standing poses.  "
                    "Read the artwork against the name printed under it.",
           fill=INK_D)
    for i, entry in enumerate(chars):
        packed = _cells(entry)
        r, c = divmod(i, cols)
        x, y = 8 + c * cw, 30 + r * ch
        for j, view in enumerate(("front", "back", "side")):
            idx = entry["views"][view]["idle"]["sequence"][0]
            im = _zoom(packed[idx], z)
            out.paste(im, (x + j * P.CELL * z, y), im)
        d.text((x + 2, y + P.CELL * z + 4),
               f"{entry['name']}  [{entry['key']}]", fill=INK_D)
        d.text((x + 2, y + P.CELL * z + 18),
               f"{entry['role']}  {entry['display']['sprite_height_px']}px  "
               f"{entry['display']['world_height_m']:.2f}m", fill=(150, 160, 175))
    out.save(path)
    print(f"cast sheet: {len(chars)} characters -> {path}")


def sheet_scale(man, path):
    """People beside creatures on a shared metre rule.

    Everything here is drawn at its real world size: each sprite is magnified
    by (metres it represents) x (pixels per metre of this image) / (its own
    texel height), which for this pair of sources works out to 2x for the
    creatures and 14x for the people.  So the relative sizes on this image are
    exactly the relative sizes in the game.
    """
    PPM = 192          # image pixels per world metre
    cz = int(round(PPM / S.PPU))          # creature zoom: 2
    pz = int(round(PPM / P.PPU))          # people zoom:  14
    assert cz >= 1 and pz >= 1

    with open(os.path.join(ROOT, "Assets", "Game", "Art", "Sprites",
                           "Creatures", "sprite_manifest.json"), encoding="utf-8") as fh:
        cman = json.load(fh)
    want = ["Pikachu", "Caterpie", "Diglett", "Machamp", "Blastoise"]
    creatures = [c for c in cman["creatures"] if c["name"] in want]
    creatures.sort(key=lambda c: want.index(c["name"]))

    picks = ["child", "player", "townsman", "hiker"]
    chars = [c for c in man["characters"] if c["key"] in picks]
    chars.sort(key=lambda c: picks.index(c["key"]))

    columns = []   # (label, sublabel, PIL image already at world scale, metres)

    for entry in chars:
        packed = _cells(entry)
        im = packed[entry["views"]["front"]["idle"]["sequence"][0]]
        # crop to the drawn figure, keeping the ground row as the bottom
        a = np.asarray(im)
        ys = np.nonzero((a[..., 3] > 0).any(1))[0]
        xs = np.nonzero((a[..., 3] > 0).any(0))[0]
        crop = im.crop((int(xs.min()), int(ys.min()),
                        int(xs.max()) + 1, P.GROUND_ROW + 1))
        columns.append((entry["name"], f"{entry['display']['world_height_m']:.2f} m",
                        _zoom(crop, pz), entry["display"]["world_height_m"]))

    for c in creatures:
        vd = c["views"]["front"]
        sh = Image.open(os.path.join(ROOT, vd["sheet"].replace("/", os.sep))).convert("RGBA")
        idx = vd["clips"]["idle"]["sequence"][0]
        cols_n = c["cell"]["columns"]
        r, col = divmod(idx, cols_n)
        cell = sh.crop((col * S.CELL, r * S.CELL, (col + 1) * S.CELL, (r + 1) * S.CELL))
        a = np.asarray(cell)
        ys = np.nonzero((a[..., 3] > 0).any(1))[0]
        xs = np.nonzero((a[..., 3] > 0).any(0))[0]
        crop = cell.crop((int(xs.min()), int(ys.min()),
                          int(xs.max()) + 1, S.GROUND_ROW + 1))
        columns.append((c["name"], f"{c['display']['world_height_m']:.2f} m",
                        _zoom(crop, cz), c["display"]["world_height_m"]))

    pad = 26
    base_y = 60 + int(2.1 * PPM)
    W = sum(im.width + pad for _, _, im, _ in columns) + 120
    H = base_y + 76
    out = Image.new("RGB", (W, H), DARK)
    d = ImageDraw.Draw(out)

    # metre rule
    for m in range(0, 3):
        yy = base_y - m * PPM
        d.line([70, yy, W - 10, yy], fill=RULE if m else (200, 210, 220))
        d.text((14, yy - 7), f"{m}.0 m", fill=RULE if m else (200, 210, 220))
    for half in (0.5, 1.5, 2.5):
        yy = base_y - int(half * PPM)
        for xx in range(70, W - 10, 12):
            d.point([(xx, yy)], fill=(60, 72, 92))

    x = 80
    for name, sub, im, metres in columns:
        out.paste(im, (x, base_y - im.height), im)
        d.text((x, base_y + 8), name, fill=INK_D)
        d.text((x, base_y + 22), sub, fill=(150, 160, 175))
        x += im.width + pad

    d.text((14, H - 42),
           f"Everything is at true world scale: creatures magnified {cz}x "
           f"(96 texels/m), people {pz}x (96/7 texels/m), image rule "
           f"{PPM} px/m.", fill=(150, 160, 175))
    d.text((14, H - 26),
           "The size relationship is the check. The difference in texel size "
           "between the two sets is the known cost of pairing Gen 5 battle art "
           "with Gen 4 field art.", fill=(150, 160, 175))
    out.save(path)
    print(f"scale sheet: {len(columns)} subjects -> {path}")


def main(argv):
    os.makedirs(VERIFY, exist_ok=True)
    with open(MANIFEST, encoding="utf-8") as fh:
        man = json.load(fh)

    if argv and argv[0] == "--cast":
        sheet_cast(man, os.path.join(VERIFY, "people_cast_all.png"))
        return
    if argv and argv[0] == "--scale":
        sheet_scale(man, os.path.join(VERIFY, "people_scale_check.png"))
        return

    keys = set(argv)
    for entry in man["characters"]:
        if keys and entry["key"] not in keys:
            continue
        _, source, *_ = P.BY_KEY[entry["key"]]
        src = load_strip(P.sheet_path(source))
        sheet_vs_source(entry, src)
        sheet_dirs(entry)
        print("contact sheets:", entry["key"])


if __name__ == "__main__":
    main(sys.argv[1:])
