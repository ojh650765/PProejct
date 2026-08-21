# -*- coding: utf-8 -*-
"""Shortens the creature animation atlases by dropping cells, not pixels.

WHY. The web build's wasm heap sits around 600 MB before the player has pressed anything, and
Unity WebGL keeps the whole data file resident -- so build size and heap demand move together
almost one to one. Textures are 88% of the build, and the biggest single item is 135 MB of
creature sheets.

The interesting part is WHERE that 135 MB comes from. A frame is 96x96, which is 36 KB; the
sheets are large because a creature carries a median of 28 distinct cells and up to 93. These
are Gen 5 idle loops, several of them over ten seconds long. Cutting the cell count is the one
saving available here that costs no resolution at all: every remaining pixel is the pixel the
artist drew. What it costs is animation LENGTH, and that is a real cost -- Raichu's back view
goes from 16.9 seconds to 1.6.

HOW. `sequence` is the playback order and indexes into the cells; the cells are already
deduplicated. So the sheet is walked in playback order, cells are collected until the cap is
reached, and the sequence is cut at that step -- which keeps a coherent opening loop rather
than an arbitrary subset. The kept cells are repacked into a smaller sheet, indices are
remapped, and `durationsMs` is cut to match.

Only the copies under Resources/ are touched. Assets/Game/Art/Sprites/Creatures/ holds the
untrimmed originals and is not in the build, so the full-length art remains the source of truth
and this is reversible by re-running the extractor.

    python Tools/Sprites/trim_frames.py --check          # report only
    python Tools/Sprites/trim_frames.py --cap 16         # do it
"""
import argparse
import io
import json
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SHEETS = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Resources", "PokeLabSprites")
MANIFEST = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Resources",
                        "sprite_manifest.json")


def kept_cells(anim, cap):
    """The cells reached in playback order before the cap, and where the sequence was cut.

    Walking the sequence rather than taking cells 0..cap-1 matters: the sheet's cell order is
    packing order, and an animation does not necessarily start at cell 0 or run straight
    through. Following playback keeps the first N/10 of a second of the real loop.
    """
    seen, order, cut = set(), [], len(anim["sequence"])
    for i, cell in enumerate(anim["sequence"]):
        if cell not in seen:
            if len(seen) >= cap:
                cut = i
                break
            seen.add(cell)
            order.append(cell)
    return order, cut


def repack(path, anim, order, frame_size):
    """Writes a smaller sheet holding only `order`, in that order. Returns (columns, rows)."""
    src = Image.open(path).convert("RGBA")
    columns = anim["columns"]
    count = len(order)
    new_cols = min(columns, count) or 1
    new_rows = (count + new_cols - 1) // new_cols

    out = Image.new("RGBA", (new_cols * frame_size, new_rows * frame_size), (0, 0, 0, 0))
    for slot, cell in enumerate(order):
        sx = (cell % columns) * frame_size
        sy = (cell // columns) * frame_size
        tile = src.crop((sx, sy, sx + frame_size, sy + frame_size))
        dx = (slot % new_cols) * frame_size
        dy = (slot // new_cols) * frame_size
        out.paste(tile, (dx, dy))

    out.save(path, "PNG", optimize=True)
    return new_cols, new_rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cap", type=int, default=16)
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args()

    manifest = json.load(io.open(MANIFEST, encoding="utf-8"))
    frame_size = manifest.get("frameSize", 96)

    before = after = 0
    touched = 0

    for creature in manifest["creatures"]:
        for key in ("frontAnim", "backAnim"):
            anim = creature.get(key)
            if not anim:
                continue

            name = os.path.basename(anim["texture"]) + ".png"
            path = os.path.join(SHEETS, name)
            if not os.path.exists(path):
                print("  missing sheet:", name)
                continue

            cols, rows = anim["columns"], anim["rows"]
            before += (cols * frame_size) * (rows * frame_size) * 4

            if anim["frames"] <= args.cap:
                after += (cols * frame_size) * (rows * frame_size) * 4
                continue

            order, cut = kept_cells(anim, args.cap)
            new_cols = min(cols, len(order)) or 1
            new_rows = (len(order) + new_cols - 1) // new_cols
            after += (new_cols * frame_size) * (new_rows * frame_size) * 4
            touched += 1

            old_secs = sum(anim["durationsMs"]) / 1000.0
            new_secs = sum(anim["durationsMs"][:cut]) / 1000.0

            if args.check:
                if touched <= 6:
                    print("  %-28s %3d -> %3d cells, %.1fs -> %.1fs"
                          % (name, anim["frames"], len(order), old_secs, new_secs))
                continue

            new_cols, new_rows = repack(path, anim, order, frame_size)

            remap = {cell: slot for slot, cell in enumerate(order)}
            anim["sequence"] = [remap[c] for c in anim["sequence"][:cut] if c in remap]
            anim["durationsMs"] = anim["durationsMs"][:len(anim["sequence"])]
            anim["columns"] = new_cols
            anim["rows"] = new_rows
            anim["frames"] = len(order)

    print("\n%s: %d animation(s) over the cap of %d" %
          ("would trim" if args.check else "trimmed", touched, args.cap))
    print("sheets: %.0f MB -> %.0f MB  (saves %.0f MB)"
          % (before / 1048576, after / 1048576, (before - after) / 1048576))

    if not args.check:
        io.open(MANIFEST, "w", encoding="utf-8", newline="\n").write(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        print("manifest rewritten")


if __name__ == "__main__":
    sys.exit(main())
