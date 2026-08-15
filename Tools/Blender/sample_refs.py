"""
Pull real palettes off the official reference artwork instead of guessing hex values.

    blender --background --python Tools/Blender/sample_refs.py

Reads each reference PNG, drops background/outline pixels, k-means clusters the
rest, and writes Tools/Blender/ref_palettes.json plus a printed summary. Creature
scripts read their colours from that file, so what ships matches the reference.
"""

import bpy
import json
import math
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import numpy as np

REF_DIR = (r"C:\Users\ojh65\AppData\Local\Temp\claude"
           r"\C--PProejct\8fbd9adb-7cdd-4c1d-a6d3-ce52c84c54e8"
           r"\scratchpad\repo\data\pokemon_images")

REFS = {
    1: ("Bulbasaur", "000101.png"),
    5: ("Charmander", "000401.png"),
    10: ("Squirtle", "000701.png"),
    21: ("Pidgey", "001601.png"),
    25: ("Rattata", "001901.png"),
    31: ("Pikachu", "002501.png"),
    47: ("Zubat", "004101.png"),
    49: ("Oddish", "004301.png"),
    66: ("Poliwag", "006001.png"),
    73: ("Machop", "006601.png"),
    81: ("Geodude", "007401.png"),
    100: ("Gastly", "009201.png"),
}


def load_rgb(path):
    """Returns display-referred (sRGB) 0..1 values.

    Verified empirically against the artwork: applying a linear->sRGB encode on top
    of these pixels double-encodes and produces washed-out pastels, so what comes
    out of `pixels` here is already sRGB and is used as-is.
    """
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = 'Non-Color'
    w, h = img.size
    buf = np.zeros(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(buf)
    bpy.data.images.remove(img)
    arr = np.clip(buf.reshape(h, w, 4), 0.0, 1.0)
    return arr[:, :, :3], arr[:, :, 3], w, h


def kmeans(pts, k, iters=26, seed=7):
    rnd = random.Random(seed)
    n = len(pts)
    idx = rnd.sample(range(n), min(k, n))
    cent = pts[idx].copy()
    for _ in range(iters):
        d = ((pts[:, None, :] - cent[None, :, :]) ** 2).sum(axis=2)
        lab = d.argmin(axis=1)
        for j in range(len(cent)):
            m = lab == j
            if m.any():
                cent[j] = pts[m].mean(axis=0)
    d = ((pts[:, None, :] - cent[None, :, :]) ** 2).sum(axis=2)
    lab = d.argmin(axis=1)
    counts = np.bincount(lab, minlength=len(cent))
    order = np.argsort(-counts)
    return cent[order], counts[order] / float(n)


def to_hex(c):
    return "#%02x%02x%02x" % tuple(int(round(max(0.0, min(1.0, v)) * 255)) for v in c)


def region_colour(rgb, alpha, mask, x0, x1, y0, y1):
    """Average colour of a normalised image region (y measured from the top)."""
    h, w = alpha.shape
    ya, yb = int((1.0 - y1) * h), int((1.0 - y0) * h)
    xa, xb = int(x0 * w), int(x1 * w)
    sub = rgb[ya:yb, xa:xb]
    sm = mask[ya:yb, xa:xb]
    if sm.sum() < 8:
        return None
    return sub[sm].mean(axis=0)


def main():
    out = {}
    for cid, (name, fn) in sorted(REFS.items()):
        path = os.path.join(REF_DIR, fn)
        if not os.path.exists(path):
            print("!! missing reference %s" % path)
            continue
        rgb, alpha, w, h = load_rgb(path)
        lum = rgb.mean(axis=2)
        # drop transparent background, near-white paper and the black ink outline
        mask = (alpha > 0.5) & (lum < 0.90) & (lum > 0.10)
        pts = rgb[mask]
        if len(pts) > 60000:
            sel = np.random.RandomState(3).choice(len(pts), 60000, replace=False)
            pts = pts[sel]
        cent, frac = kmeans(pts.astype(np.float64), 9)
        entry = {
            "name": name,
            "file": fn,
            "palette": [{"hex": to_hex(c), "rgb": [round(float(v), 4) for v in c],
                         "share": round(float(f), 4)}
                        for c, f in zip(cent, frac) if f > 0.012],
        }
        # a few anatomical probes, useful for belly vs back vs head
        probes = {
            "upper": (0.30, 0.70, 0.62, 0.88),
            "middle": (0.30, 0.70, 0.38, 0.62),
            "lower": (0.30, 0.70, 0.12, 0.38),
            "left": (0.05, 0.28, 0.35, 0.70),
            "right": (0.72, 0.95, 0.35, 0.70),
        }
        entry["regions"] = {}
        for k, (x0, x1, y0, y1) in probes.items():
            c = region_colour(rgb, alpha, mask, x0, x1, y0, y1)
            if c is not None:
                entry["regions"][k] = to_hex(c)
        out[str(cid)] = entry
        print("%3d %-11s %s" % (cid, name,
                                " ".join("%s(%.2f)" % (p["hex"], p["share"])
                                         for p in entry["palette"])))
        print("      regions: %s" % entry["regions"])

    dst = os.path.join(HERE, "ref_palettes.json")
    with open(dst, 'w', encoding='utf-8') as fh:
        json.dump(out, fh, indent=2)
    print("\n-> %s" % dst)


main()
