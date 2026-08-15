#!/usr/bin/env python3
"""
Palette-quantise the environment atlases in place, at 2048x2048.

The key non-obvious point, and the reason this script exists rather than a
downsample: **HD-2D environment textures are not low-resolution, they are
low-colour-count.** Downsampling loses the crisp edges the look depends on and
destroys the normal maps; re-authoring smaller throws away work for no gain. The
"pixel" read comes from palette discipline and flat shading, not from texel count.

Texel density is already right and is deliberately not touched here. The five
atlases are 2048x2048 on a 4x4 grid of 512 px cells, and the existing UV scales
put them in the 100-256 px/m band:

    contact zone (ground, paths, terrain and props under characters)   256 px/m
    everything else (walls, cliff faces, canopies, distant terrain)    128 px/m
    hero close range (bridge planks, well rim)                         256 px/m

The 0.5 m modular grid lands on exactly 128 px, a clean power of two. If a cell
ever needs re-scaling that is a UV change in Tools/Blender/environment, not a
change here.

What this does, per 512 px atlas cell of a base-colour map:

  1. Flatten the value range. Procedural noise variation inside a cell collapses
     to a small number of discrete luminance steps, with hue and chroma
     preserved. This is the stage that turns "a texture" into "painted pixel
     art" -- it matters more than the colour count does.
  2. Palette-quantise to 12-24 colours, k-means seeded from the cell's own
     content and optionally nudged toward the project's reference palettes.
     **No dithering**: dither reads as noise at these densities and fights the
     bloom.
  3. Add a keyline on natural boundaries -- plank edges, stone joints, roof tile
     rows -- in _OutlineColor (0.08, 0.07, 0.12), so geometry and sprites share
     one line language. Enabled per family; off where a cell has no joinery and
     an edge filter would only find noise.

Normal maps are deliberately almost untouched: only a mild strength reduction.
They carry the lighting response that keeps the geometry reading as 3D, which is
the whole point of the settled 3D-level / 2D-character split.

Alpha is never quantised. The foliage atlas carries leaf cutout masks in alpha
and posterising those would chew the silhouettes.

Re-runnable and idempotent. The first run copies each source atlas into
Tools/Textures/_originals/; every run after that quantises from that pristine
copy, so changing a setting and re-running gives the result you asked for rather
than a quantisation of a quantisation.

Usage
    python Tools/Textures/quantise_atlases.py                # all families
    python Tools/Textures/quantise_atlases.py --family Town  # one family
    python Tools/Textures/quantise_atlases.py --dry-run
    python Tools/Textures/quantise_atlases.py --restore      # undo, from originals

Requires numpy and Pillow, both already used by Tools/Blender/environment.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys

import numpy as np
from PIL import Image

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ART = os.path.join(REPO, "Assets", "Game", "Art")
ORIGINALS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_originals")
MANIFEST = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "quantise_manifest.json")
REF_PALETTES = os.path.join(REPO, "Tools", "Blender", "ref_palettes.json")

CELL = 512
GRID = 4
ATLAS = CELL * GRID

# _OutlineColor from PokeLabCreature.shader. Every keyline in the project is this
# colour so props, terrain and the baked sprite keyline agree.
OUTLINE_RGB = np.array([0.08, 0.07, 0.12], dtype=np.float64)


class FamilySpec:
    """Per-family treatment.

    colours       palette size per cell. 12-24; lower is flatter and more
                  graphic, higher preserves gradients that read as painterly.
    value_steps   discrete luminance steps the cell collapses to before
                  quantising. 3-5. This is the flattening stage.
    keyline       draw a boundary line on strong edges after quantisation.
                  On where the family has real joinery, off where an edge filter
                  would only find noise (canopies, rock strata, faces).
    keyline_gain  0..1, how far an edge pixel is pushed toward OUTLINE_RGB.
    normal_scale  tangent XY multiplier on the normal map. <1 softens relief.
    harmonise     0..1 pull of each palette centre toward the nearest colour in
                  the pooled project reference palette.
    """

    def __init__(self, name, path, colours, value_steps, keyline,
                 keyline_gain=0.70, normal_scale=0.80, harmonise=0.10,
                 note=""):
        self.name = name
        self.path = path
        self.colours = colours
        self.value_steps = value_steps
        self.keyline = keyline
        self.keyline_gain = keyline_gain
        self.normal_scale = normal_scale
        self.harmonise = harmonise
        self.note = note


FAMILIES = [
    FamilySpec(
        "Terrain", os.path.join(ART, "Environment", "Terrain", "Textures"),
        colours=20, value_steps=4, keyline=True, keyline_gain=0.50,
        normal_scale=0.85,
        note="Ground and cliff. Keyline is weak: stone joints yes, strata noise no. "
             "Normal scale is the highest of any family because the split-normals "
             "pass leans on it to keep rock reading as faceted."),
    FamilySpec(
        "Town", os.path.join(ART, "Environment", "Town", "Textures"),
        colours=18, value_steps=4, keyline=True, keyline_gain=0.70,
        normal_scale=0.75,
        note="Plank edges, stone joints and roof tile rows are exactly the natural "
             "boundaries the keyline is for. Strongest keyline in the project."),
    FamilySpec(
        "Foliage", os.path.join(ART, "Environment", "Foliage", "Textures"),
        colours=16, value_steps=5, keyline=False,
        normal_scale=0.70,
        note="No keyline: a canopy has no joinery, and an edge filter on leaf "
             "clusters produces speckle. Alpha is passed through untouched -- it is "
             "the cutout mask and posterising it would chew the silhouette."),
    FamilySpec(
        "Props", os.path.join(ART, "Props", "Textures"),
        colours=20, value_steps=4, keyline=True, keyline_gain=0.60,
        normal_scale=0.80,
        note="Manufactured objects: panel lines and rims are real boundaries."),
    FamilySpec(
        "Characters", os.path.join(ART, "Environment", "Characters", "Textures"),
        colours=16, value_steps=4, keyline=False,
        normal_scale=0.75,
        note="The human FBX are slated to become sprites, so this atlas is on "
             "borrowed time. Quantised anyway so the frame stays coherent for as "
             "long as they are in the scene."),
]

FAMILY_BY_NAME = {f.name: f for f in FAMILIES}


# ---------------------------------------------------------------------------
# Colour helpers
# ---------------------------------------------------------------------------

LUMA = np.array([0.2126, 0.7152, 0.0722], dtype=np.float64)


def luminance(rgb):
    return rgb @ LUMA


def flatten_value(rgb, steps):
    """Collapse the luminance range to `steps` discrete levels, keeping chroma.

    Scaling RGB by the ratio of quantised to original luminance is what keeps hue
    and saturation intact: posterising each channel independently would shift hues
    at every step boundary and is the classic way to make a quantised texture look
    cheap.

    The quantisation runs over the cell's *own* luminance range rather than over
    absolute 0..1. A dark slate cell and a bright plaster cell then get the same
    number of readable tonal steps; quantising absolutely would collapse the slate
    to a single flat value and give the plaster all the steps, which is exactly
    backwards from how a painter would treat them.
    """
    steps = max(int(steps), 1)
    lum = luminance(rgb)
    lo = float(np.percentile(lum, 1.0))
    hi = float(np.percentile(lum, 99.0))
    span = max(hi - lo, 1e-3)

    norm = np.clip((lum - lo) / span, 0.0, 1.0)
    quantised = np.round(norm * (steps - 1)) / max(steps - 1, 1) if steps > 1 else norm * 0 + 0.5
    target = lo + quantised * span

    safe = np.maximum(lum, 1e-4)
    scale = (target / safe)[..., None]
    return np.clip(rgb * scale, 0.0, 1.0)


def kmeans_palette(samples, k, iterations=14, seed=1337):
    """Small deterministic k-means. k-means++ init, fixed seed, no dithering.

    Deterministic on purpose: the atlases are committed assets and a re-run that
    produced a different palette would show up as a spurious diff every time.
    """
    rng = np.random.default_rng(seed)
    n = samples.shape[0]
    k = int(min(k, n))
    if k <= 1:
        return samples.mean(axis=0, keepdims=True)

    # k-means++ seeding.
    centres = np.empty((k, 3), dtype=np.float64)
    centres[0] = samples[rng.integers(n)]
    closest = ((samples - centres[0]) ** 2).sum(axis=1)
    for i in range(1, k):
        total = closest.sum()
        if total <= 1e-12:
            centres[i] = samples[rng.integers(n)]
        else:
            pick = rng.choice(n, p=closest / total)
            centres[i] = samples[pick]
        d = ((samples - centres[i]) ** 2).sum(axis=1)
        closest = np.minimum(closest, d)

    for _ in range(iterations):
        d = ((samples[:, None, :] - centres[None, :, :]) ** 2).sum(axis=2)
        labels = d.argmin(axis=1)
        moved = 0.0
        for i in range(k):
            member = samples[labels == i]
            if member.shape[0] == 0:
                continue
            new = member.mean(axis=0)
            moved = max(moved, float(np.abs(new - centres[i]).max()))
            centres[i] = new
        if moved < 1e-4:
            break

    # Sort by luminance so the palette is stable and readable in a diff.
    return centres[np.argsort(luminance(centres))]


def assign_nearest(rgb, centres):
    """Map every pixel to its nearest palette entry. No dithering, by design."""
    flat = rgb.reshape(-1, 3)
    best = None
    best_d = None
    # Chunked so a 512x512 cell against 24 centres never allocates a huge
    # intermediate.
    for i in range(centres.shape[0]):
        d = ((flat - centres[i]) ** 2).sum(axis=1)
        if best is None:
            best = np.zeros(flat.shape[0], dtype=np.int32)
            best_d = d
        else:
            better = d < best_d
            best[better] = i
            best_d = np.where(better, d, best_d)
    return centres[best].reshape(rgb.shape)


def load_reference_palette():
    """Pool every reference palette colour into one array.

    These are the sampled palettes the creature art was built from. Nudging the
    environment centres toward them is a small cohesion win: it pulls the world's
    greens and browns into the same hue family the cast already lives in, without
    overriding the environment artist's intent.
    """
    if not os.path.exists(REF_PALETTES):
        return None
    try:
        with open(REF_PALETTES, "r", encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return None

    colours = []
    for entry in data.values():
        for swatch in entry.get("palette", []):
            rgb = swatch.get("rgb")
            if rgb and len(rgb) >= 3:
                colours.append(rgb[:3])
    if not colours:
        return None
    return np.array(colours, dtype=np.float64)


def harmonise_centres(centres, reference, amount):
    if reference is None or amount <= 0.0:
        return centres
    d = ((centres[:, None, :] - reference[None, :, :]) ** 2).sum(axis=2)
    nearest = reference[d.argmin(axis=1)]
    return np.clip(centres * (1.0 - amount) + nearest * amount, 0.0, 1.0)


# A keyline is only wanted on joinery: plank edges, stone joints, roof tile rows.
# On brick coursing or eroded rock the same filter fires on every quantised step
# and the cell turns into black speckle -- which is what the first pass of this
# script did, and it is worse than no keyline at all. Two guards:
#   * a high magnitude threshold, so only boundaries a painter would draw survive;
#   * a per-cell coverage veto, so a cell whose "edges" cover more than a small
#     fraction of its area is treated as texture rather than as joinery and gets
#     no keyline at all.
KEYLINE_THRESHOLD = 0.26
KEYLINE_RAMP = 0.14
KEYLINE_MAX_COVERAGE = 0.09


def keyline_mask(rgb):
    """Central-difference edge magnitude on quantised luminance.

    Returns (mask, coverage). After quantisation the only strong edges left are
    palette boundaries, so a high threshold here really does isolate structure --
    but only in cells that have structure, which is what coverage measures.
    """
    lum = luminance(rgb)
    gx = np.zeros_like(lum)
    gy = np.zeros_like(lum)
    gx[:, 1:-1] = lum[:, 2:] - lum[:, :-2]
    gy[1:-1, :] = lum[2:, :] - lum[:-2, :]
    mag = np.sqrt(gx * gx + gy * gy)

    mask = np.clip((mag - KEYLINE_THRESHOLD) / KEYLINE_RAMP, 0.0, 1.0)
    coverage = float((mask > 0.5).mean())
    return mask, coverage


# ---------------------------------------------------------------------------
# Atlas processing
# ---------------------------------------------------------------------------

def process_base(image, spec, reference):
    arr = np.asarray(image).astype(np.float64) / 255.0
    has_alpha = arr.shape[2] == 4
    rgb = arr[..., :3].copy()
    alpha = arr[..., 3].copy() if has_alpha else None

    stats = {"cells": 0, "palette_sizes": [], "keylined": 0, "keyline_vetoed": 0}

    for row in range(GRID):
        for col in range(GRID):
            y0, y1 = row * CELL, (row + 1) * CELL
            x0, x1 = col * CELL, (col + 1) * CELL
            cell = rgb[y0:y1, x0:x1]

            # Stage 1: flatten the value range.
            cell = flatten_value(cell, spec.value_steps)

            # Fit the palette on the pixels that actually show. On the foliage
            # atlas most of a cell can be fully transparent, and fitting the
            # palette to the invisible fill would waste half the colours.
            samples = cell.reshape(-1, 3)
            if alpha is not None:
                visible = alpha[y0:y1, x0:x1].reshape(-1) > 0.02
                if visible.sum() > 64:
                    samples = samples[visible]

            rng = np.random.default_rng(9001 + row * GRID + col)
            if samples.shape[0] > 20000:
                samples = samples[rng.choice(samples.shape[0], 20000, replace=False)]

            centres = kmeans_palette(samples, spec.colours,
                                     seed=4242 + row * GRID + col)
            centres = harmonise_centres(centres, reference, spec.harmonise)

            # Stage 2: quantise. Nearest neighbour, never dithered.
            cell = assign_nearest(cell, centres)

            # Stage 3: keyline on natural boundaries.
            if spec.keyline:
                mask, coverage = keyline_mask(cell)
                if coverage <= KEYLINE_MAX_COVERAGE:
                    edge = mask[..., None] * spec.keyline_gain
                    cell = cell * (1.0 - edge) + OUTLINE_RGB[None, None, :] * edge
                    stats["keylined"] += 1
                else:
                    stats["keyline_vetoed"] += 1

            rgb[y0:y1, x0:x1] = cell
            stats["cells"] += 1
            stats["palette_sizes"].append(int(centres.shape[0]))

    out = np.clip(rgb, 0.0, 1.0)
    if has_alpha:
        # Alpha untouched: it is the cutout mask, not a colour.
        out = np.concatenate([out, alpha[..., None]], axis=2)
    return Image.fromarray((out * 255.0 + 0.5).astype(np.uint8)), stats


def process_normal(image, spec):
    """Mild strength reduction only.

    The normal maps are what keep the 3D level reading as 3D next to flat
    sprites, so they survive the pivot essentially intact. Z is rebuilt from XY
    rather than scaled, which keeps the result a unit vector -- scaling all three
    channels would flatten and denormalise at the same time.
    """
    arr = np.asarray(image).astype(np.float64) / 255.0
    rgb = arr[..., :3]

    xy = (rgb[..., :2] * 2.0 - 1.0) * spec.normal_scale
    z = np.sqrt(np.clip(1.0 - (xy ** 2).sum(axis=2), 0.0, 1.0))
    out = np.stack([xy[..., 0] * 0.5 + 0.5,
                    xy[..., 1] * 0.5 + 0.5,
                    z * 0.5 + 0.5], axis=2)

    if arr.shape[2] == 4:
        out = np.concatenate([out, arr[..., 3:4]], axis=2)
    return Image.fromarray((np.clip(out, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8))


def atlas_files(spec):
    base = os.path.join(spec.path, "Env_%s_Atlas_BaseColor.png" % spec.name)
    normal = os.path.join(spec.path, "Env_%s_Atlas_Normal.png" % spec.name)
    return base, normal


def backup_path(spec, kind):
    return os.path.join(ORIGINALS, "Env_%s_Atlas_%s.png" % (spec.name, kind))


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def ensure_backup(source, backup):
    """Copy the pristine atlas aside once, then always work from that copy.

    Without this a second run would quantise an already-quantised image, and a
    palette fitted to 18 colours would collapse further every time.
    """
    os.makedirs(os.path.dirname(backup), exist_ok=True)
    if not os.path.exists(backup):
        shutil.copy2(source, backup)
        return True
    return False


def load_manifest():
    if not os.path.exists(MANIFEST):
        return {}
    try:
        with open(MANIFEST, "r", encoding="utf-8") as fh:
            return {e["family"]: e for e in json.load(fh).get("families", [])}
    except (OSError, ValueError, KeyError):
        return {}


def already_quantised(spec, base, previous):
    """Guard against quantising an output.

    Tools/Textures/_originals is a local working directory and is not committed --
    the atlases are procedurally generated and cost more in LFS than they cost to
    rebuild. So on a fresh clone the archive is absent while the committed atlas is
    already a quantised output, and blindly archiving it would silently make that
    output the new "original". The manifest records the hash we last wrote; if the
    file on disk still matches it and no archive exists, stop and say so.
    """
    if os.path.exists(backup_path(spec, "BaseColor")):
        return False
    record = previous.get(spec.name)
    if not record or "outputSha256" not in record:
        return False
    return sha256(base) == record["outputSha256"]


def write_preview(spec, base):
    """Before/after contact sheet, matching the Tools/Audio and Tools/Blender
    convention of shipping a reviewable preview next to the generator."""
    backup = backup_path(spec, "BaseColor")
    if not os.path.exists(backup):
        return None
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "previews")
    os.makedirs(out_dir, exist_ok=True)
    size = 768
    with Image.open(backup) as before, Image.open(base) as after:
        sheet = Image.new("RGB", (size * 2, size), (12, 12, 16))
        sheet.paste(before.convert("RGB").resize((size, size), Image.LANCZOS), (0, 0))
        # Nearest on the quantised side: resampling the output with a smooth
        # filter would blend the palette back together and hide the very thing
        # the preview exists to show.
        sheet.paste(after.convert("RGB").resize((size, size), Image.NEAREST), (size, 0))
    path = os.path.join(out_dir, "%s_before_after.png" % spec.name)
    sheet.save(path)
    return path


def run_family(spec, reference, dry_run, previous, preview):
    base, normal = atlas_files(spec)
    if not os.path.exists(base):
        print("  skip %-12s (no atlas at %s)" % (spec.name, spec.path))
        return None

    record = {
        "family": spec.name,
        "colours": spec.colours,
        "valueSteps": spec.value_steps,
        "keyline": spec.keyline,
        "keylineGain": spec.keyline_gain if spec.keyline else 0.0,
        "normalScale": spec.normal_scale,
        "harmonise": spec.harmonise,
        "note": spec.note,
    }

    if dry_run:
        print("  would quantise %-12s %2d colours, %d value steps, keyline %s"
              % (spec.name, spec.colours, spec.value_steps,
                 "on" if spec.keyline else "off"))
        return record

    if already_quantised(spec, base, previous):
        print("  REFUSE %-12s the atlas on disk is this script's own output and "
              "Tools/Textures/_originals is missing." % spec.name)
        print("         Rebuild pristine atlases first:")
        print("           blender --background --python "
              "Tools/Blender/environment/build_atlases.py")
        return None

    fresh = ensure_backup(base, backup_path(spec, "BaseColor"))
    with Image.open(backup_path(spec, "BaseColor")) as img:
        result, stats = process_base(img.convert("RGBA" if "A" in img.getbands()
                                                 else "RGB"), spec, reference)
    result.save(base)
    keyline_note = ""
    if spec.keyline:
        keyline_note = ", keyline on %d of %d cells (%d vetoed as texture)" % (
            stats["keylined"], stats["cells"], stats["keyline_vetoed"])
    print("  %-12s base   %d cells, %d colours/cell%s%s"
          % (spec.name, stats["cells"], spec.colours, keyline_note,
             "  (original archived)" if fresh else ""))
    record["keylinedCells"] = stats["keylined"]
    record["keylineVetoedCells"] = stats["keyline_vetoed"]

    if os.path.exists(normal):
        ensure_backup(normal, backup_path(spec, "Normal"))
        with Image.open(backup_path(spec, "Normal")) as img:
            result = process_normal(img.convert("RGB"), spec)
        result.save(normal)
        print("  %-12s normal strength x%.2f" % (spec.name, spec.normal_scale))

    # Recorded so a later run can tell "this file is my own output" from "this
    # file is a fresh atlas". See already_quantised().
    record["outputSha256"] = sha256(base)

    if preview:
        path = write_preview(spec, base)
        if path:
            print("  %-12s preview %s" % (spec.name, os.path.relpath(path, REPO)))

    return record


def restore(spec):
    for kind in ("BaseColor", "Normal"):
        backup = backup_path(spec, kind)
        target = os.path.join(spec.path, "Env_%s_Atlas_%s.png" % (spec.name, kind))
        if os.path.exists(backup):
            shutil.copy2(backup, target)
            print("  restored %s" % os.path.relpath(target, REPO))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    parser.add_argument("--family", action="append",
                        choices=sorted(FAMILY_BY_NAME),
                        help="restrict to one family; repeatable")
    parser.add_argument("--dry-run", action="store_true",
                        help="print what would happen and change nothing")
    parser.add_argument("--restore", action="store_true",
                        help="copy the archived originals back over the atlases")
    parser.add_argument("--preview", action="store_true",
                        help="also write before/after sheets to Tools/Textures/previews")
    args = parser.parse_args(argv)

    specs = [FAMILY_BY_NAME[n] for n in args.family] if args.family else FAMILIES

    if args.restore:
        print("Restoring atlases from %s" % os.path.relpath(ORIGINALS, REPO))
        for spec in specs:
            restore(spec)
        return 0

    reference = load_reference_palette()
    print("Quantising %d atlas famil%s at %dx%d (%d px cells, %dx%d grid)"
          % (len(specs), "y" if len(specs) == 1 else "ies", ATLAS, ATLAS,
             CELL, GRID, GRID))
    print("Reference palette: %s"
          % ("%d swatches" % reference.shape[0] if reference is not None
             else "not found, harmonisation disabled"))

    previous = load_manifest()

    records = []
    for spec in specs:
        record = run_family(spec, reference, args.dry_run, previous, args.preview)
        if record:
            records.append(record)

    if not args.dry_run and records:
        existing = dict(previous)
        for record in records:
            existing[record["family"]] = record

        payload = {
            "atlasSize": ATLAS,
            "cellSize": CELL,
            "grid": GRID,
            "targetDensity": {
                "contactZone": "256 px/m",
                "general": "128 px/m",
                "heroCloseRange": "256 px/m",
                "note": "Density is set by UV scale in Tools/Blender/environment, "
                        "not by this script. The 0.5 m modular grid lands on 128 px.",
            },
            "outlineColor": list(OUTLINE_RGB),
            "families": [existing[k] for k in sorted(existing)],
        }
        with open(MANIFEST, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, indent=2)
            fh.write("\n")
        print("Manifest written to %s" % os.path.relpath(MANIFEST, REPO))

    return 0


if __name__ == "__main__":
    sys.exit(main())
