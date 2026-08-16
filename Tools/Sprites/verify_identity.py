"""Prove the packed sheets still contain the source artwork, pixel for pixel.

    python Tools/Sprites/verify_identity.py [game_species_id ...]

contact.py answers this by eye, which is the right check for alignment but a
weak one for a two-pixel colour shift in the middle of a 53-species cast.  This
answers it arithmetically instead, and it is deliberately independent of
extract.py's own reasoning: it does not recompute the anchor or re-run place().
It reads the shipped PNG back off disk, crops each cell to its opaque bounding
box, crops the corresponding source frame to *its* bounding box, and requires
the two arrays to be equal.

That comparison is invariant to the one transformation the pipeline is allowed
to apply -- an integer translation moves a bounding box without changing its
contents -- and sensitive to every transformation it is forbidden to apply:
resampling changes the box's size, filtering and re-quantisation change pixels
inside it, and a premultiply or a colour-space round trip changes them all.

Checked per view: every frame of the play sequence, the packed static, and the
portrait.  Reports totals rather than a verdict, so a partial pass is visible.
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import species as S
from extract import MANIFEST, ROOT, load_gif, load_png


def bbox_crop(a: np.ndarray) -> np.ndarray:
    ys, xs = np.nonzero(a[..., 3] > 0)
    if not len(ys):
        return a[:0, :0]
    return a[ys.min():ys.max() + 1, xs.min():xs.max() + 1]


def cells(entry: dict, view: str) -> list[np.ndarray]:
    vd = entry["views"][view]
    im = np.asarray(Image.open(os.path.join(ROOT, vd["sheet"])).convert("RGBA"))
    cell, cols = entry["cell"]["width"], entry["cell"]["columns"]
    out = []
    for i in range(vd["unique_frames"]):
        r, c = divmod(i, cols)
        out.append(im[r * cell:(r + 1) * cell, c * cell:(c + 1) * cell])
    return out


def main(argv: list[str]) -> None:
    with open(MANIFEST, encoding="utf-8") as fh:
        man = json.load(fh)
    want = {int(a) for a in argv}

    n_cmp = n_px = n_bad = 0
    bad_species: list[str] = []

    for entry in man["creatures"]:
        gid, dex, name = entry["species_id"], entry["dex_number"], entry["name"]
        if want and gid not in want:
            continue
        fails = []

        for view in ("front", "back"):
            packed = cells(entry, view)
            vd = entry["views"][view]
            src_frames, _ = load_gif(S.anim_path(dex, view))
            seq = vd["clips"]["idle"]["sequence"]

            if len(seq) != len(src_frames):
                fails.append(f"{view}: sequence {len(seq)} vs source {len(src_frames)} frames")
                continue
            for i, (src, idx) in enumerate(zip(src_frames, seq)):
                a, b = bbox_crop(src), bbox_crop(packed[idx])
                n_cmp += 1
                if a.shape != b.shape:
                    fails.append(f"{view} f{i}: bbox {a.shape[1]}x{a.shape[0]} "
                                 f"-> {b.shape[1]}x{b.shape[0]} (RESCALED)")
                elif not np.array_equal(a, b):
                    fails.append(f"{view} f{i}: {int((a != b).any(-1).sum())} pixels differ")
                else:
                    n_px += int(a.shape[0] * a.shape[1])

            a = bbox_crop(load_png(S.static_path(dex, view)))
            b = bbox_crop(packed[vd["static_frame"]])
            n_cmp += 1
            if a.shape != b.shape or not np.array_equal(a, b):
                fails.append(f"{view} static: differs")
            else:
                n_px += int(a.shape[0] * a.shape[1])

        port = np.asarray(Image.open(
            os.path.join(ROOT, entry["portrait"]["path"])).convert("RGBA"))
        a, b = bbox_crop(load_png(S.static_path(dex, "front"))), bbox_crop(port)
        n_cmp += 1
        if a.shape != b.shape or not np.array_equal(a, b):
            fails.append("portrait: differs from front static")
        else:
            n_px += int(a.shape[0] * a.shape[1])

        n_bad += len(fails)
        if fails:
            bad_species.append(name)
            print(f"[{gid}] {name} (dex {dex})  {len(fails)} FAILURES")
            for f in fails[:6]:
                print(f"    {f}")
        else:
            print(f"[{gid}] {name:12} (dex {dex:3d})  ok")

    print(f"\n{n_cmp} images compared, {n_px:,} opaque-region pixels identical, "
          f"{n_bad} mismatches")
    if bad_species:
        print("species with mismatches: " + ", ".join(bad_species))
    sys.exit(1 if n_bad else 0)


if __name__ == "__main__":
    main(sys.argv[1:])
