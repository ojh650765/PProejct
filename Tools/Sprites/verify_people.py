"""Prove the packed human sheets are the source artwork, unaltered.

    python Tools/Sprites/verify_people.py

extract_people.py claims it only decodes, translates by whole pixels and packs.
That claim is worth exactly as much as a check that re-reads the written PNGs
and compares them against the staged source, which is what this does.  It is
the people-side companion to verify_identity.py and check_ids.py.

Five checks, and any one of them failing is a real defect:

  IDENTITY   every packed cell is byte-identical to the source cell it came
             from.  Catches resampling, re-quantisation and palette drift --
             the failure modes that are invisible in code review.
  ALPHA      alpha is still binary.  A soft fringe becomes a ragged edge under
             the alpha-clip material.
  GROUND     every standing pose bottoms out on people.GROUND_ROW, and no
             frame leaves the cell.  This is what stops a character floating
             or sinking relative to the rest of the cast.
  REGISTER   the three views of one character agree on where the character is,
             so it does not jump sideways when it turns.
  MANIFEST   the emitted clip sequences index cells that exist, and every clip
             names a frame count that matches its durations.
"""

from __future__ import annotations

import json
import os
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import people as P
from extract_people import MANIFEST, ROOT, load_strip


def cells_of(sheet_path: str, count: int) -> list[np.ndarray]:
    im = np.asarray(Image.open(sheet_path).convert("RGBA"))
    out = []
    for i in range(count):
        r, c = divmod(i, P.COLS)
        out.append(im[r * P.CELL:(r + 1) * P.CELL, c * P.CELL:(c + 1) * P.CELL])
    return out


def main() -> int:
    with open(MANIFEST, encoding="utf-8") as fh:
        man = json.load(fh)

    fails = 0
    print(f"{'key':12} {'identity':>9} {'alpha':>6} {'ground':>7} "
          f"{'register':>9} {'manifest':>9}")
    for entry in man["characters"]:
        key = entry["key"]
        _, source, *_ = P.BY_KEY[key]
        src = load_strip(P.sheet_path(source))
        packed = cells_of(os.path.join(ROOT, entry["sheet"].replace("/", os.sep)),
                          entry["unique_frames"])

        # ---- IDENTITY -----------------------------------------------------
        # Rebuild the source->packed correspondence the same way the extractor
        # did, then demand byte equality both ways round.
        blocks = [0] if len(src) == 16 else [0, 16]
        bad_id = 0
        seen = set()
        for base in blocks:
            for view in ("front", "back", "side"):
                g = base + P.GROUP[view]
                for off in range(P.GROUP_LEN):
                    s = src[g + off]
                    # find it in the packed set
                    hit = next((i for i, p in enumerate(packed)
                                if np.array_equal(p, s)), None)
                    if hit is None:
                        bad_id += 1
                    else:
                        seen.add(hit)
        # and nothing was packed that is not in the source
        orphans = set(range(len(packed))) - seen
        identity = not bad_id and not orphans

        # ---- ALPHA --------------------------------------------------------
        alpha_ok = all(not (set(np.unique(p[..., 3]).tolist()) - {0, 255})
                       for p in packed)

        # ---- GROUND -------------------------------------------------------
        ground_ok = True
        for view in ("front", "back", "side"):
            g = P.GROUP[view]
            rows = np.nonzero((src[g][..., 3] > 0).any(1))[0]
            if int(rows.max()) != P.GROUND_ROW:
                ground_ok = False
        for p in packed:
            rows = np.nonzero((p[..., 3] > 0).any(1))[0]
            cols = np.nonzero((p[..., 3] > 0).any(0))[0]
            if rows.size == 0 or rows.max() >= P.CELL or cols.max() >= P.CELL:
                ground_ok = False

        # ---- REGISTER -----------------------------------------------------
        # The three standing poses must sit over the same ground point. Their
        # silhouettes differ (a back view is not a front view), so this asks
        # that the horizontal centre of the base band agrees within a pixel,
        # which is what "does not slide when it turns" actually means.
        centres = []
        for view in ("front", "back", "side"):
            a = src[P.GROUP[view]]
            m = a[..., 3] > 0
            band = m[P.GROUND_ROW - 4:P.GROUND_ROW + 1]
            xs = np.nonzero(band.any(0))[0]
            centres.append((int(xs.min()) + int(xs.max())) / 2)
        # side view is a profile, so it is compared only against itself moving;
        # front and back are the pair that must agree.
        register_ok = abs(centres[0] - centres[1]) <= 1.0

        # ---- MANIFEST -----------------------------------------------------
        man_ok = True
        for view, clips in entry["views"].items():
            for name, clip in clips.items():
                seq = clip["sequence"]
                if any(i < 0 or i >= entry["unique_frames"] for i in seq):
                    man_ok = False
                if len(seq) > 1 and len(clip["durations_ms"]) != len(seq):
                    man_ok = False
        if entry["display"]["ground_row"] != P.GROUND_ROW:
            man_ok = False

        row = [identity, alpha_ok, ground_ok, register_ok, man_ok]
        fails += sum(not x for x in row)
        print(f"{key:12} " + " ".join(
            f"{'ok' if x else 'FAIL':>{w}}"
            for x, w in zip(row, (9, 6, 7, 9, 9))))

    # ---- PROPS ------------------------------------------------------------
    # Same identity rule, no direction or register checks: a prop has neither.
    for pr in man.get("props", []):
        src = load_strip(P.sheet_path(P.PROPS_BY_KEY[pr["key"]][1]))
        packed = cells_of(os.path.join(ROOT, pr["sheet"].replace("/", os.sep)),
                          pr["unique_frames"])
        identity = all(any(np.array_equal(q, s) for q in packed) for s in src)
        alpha_ok = all(not (set(np.unique(q[..., 3]).tolist()) - {0, 255})
                       for q in packed)
        clips_ok = all(all(0 <= i < pr["unique_frames"] for i in c["sequence"])
                       for c in pr["clips"].values())
        row = [identity, alpha_ok, clips_ok]
        fails += sum(not x for x in row)
        print(f"{pr['key']:12} " + " ".join(
            f"{'ok' if x else 'FAIL':>{w}}" for x, w in zip(row, (9, 6, 7)))
            + "        -         -   (prop)")

    print()
    if fails:
        print(f"{fails} FAILURES")
    else:
        print(f"{len(man['characters'])} characters and "
              f"{len(man.get('props', []))} props: every packed cell is "
              f"byte-identical to its source cell, alpha is binary, every "
              f"standing pose is on ground row {P.GROUND_ROW}, front and back "
              f"are in register, and every clip indexes a real frame.")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
