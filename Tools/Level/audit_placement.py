"""Find every object standing in the air, by footprint rather than by pivot.

The earlier check compared each object's Y against the terrain height *at its pivot*
and declared 383 of 386 objects seated to within a millimetre. That check cannot see
the failure it was meant to catch. A boulder 2.7 m across, pivoted at the centre of
its flat base, sitting on a 27 degree slope, passes at exactly 0.000 m -- while the
downhill half of it hangs 1.5 m in the air. Gorge_Rocks_03 was doing precisely that.

So this samples the terrain across each object's real footprint: its asset bounding
box, scaled, rotated by its yaw, and swept on a grid. What matters is the lowest
ground under the object's base plane, because that is where the daylight shows.

    python Tools/Level/audit_placement.py [--gap 0.15] [--fix]

`--fix` writes the corrected Y values back into slice_layout.json, seating each
object at the lowest ground under its footprint plus a small bite, so it beds into
the slope instead of perching on it. Objects that are meant to stand clear of the
ground -- bridges, waterfall lips, stepping stones -- are exempt by name.
"""

import argparse
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)

from emit_unity_layout import Grid

LAYOUT = os.path.join(ROOT, "Assets", "Game", "Data", "Levels", "slice_layout.json")
BOUNDS = os.path.join(HERE, "asset_bounds.json")

# Things that are supposed to be above the ground. A bridge deck that beds into the
# bank is not a bridge, and a waterfall lip is a lip because it overhangs.
EXEMPT = ("Bridge", "Waterfall", "SteppingStones", "Lilypad", "Vine", "Lamp_Wall",
          "Stalactite")

# How far into the ground a corrected object is pushed, so the seam is buried rather
# than exactly coincident (which z-fights).
BITE = 0.06


def is_exempt(name, prefab):
    return any(k.lower() in (name + prefab).lower() for k in EXEMPT)


# What actually touches the ground, as a fraction of the asset's bounding box.
#
# A bounding box is the wrong footprint for most of this kit. A broadleaf tree's box
# is its *canopy*, four metres across, while the thing that meets the ground is a
# trunk you can put your arms around -- measuring the canopy reports every tree on
# every slope as floating, which is how a first pass of this audit produced 171 hits
# and no information. Boulders and buildings are the opposite: their box really is
# what sits on the ground, and the whole of it has to be supported.
GROUND_FRACTION = [
    (("Env_Tree_",), 0.16),          # trunk only
    (("Env_Vine_",), 0.0),           # hangs from a branch
    (("Env_Bush_", "Env_Fern_", "Env_Reed_", "Env_Flower_",
      "Env_Grass_", "Env_TallGrass_", "Env_Moss_"), 0.5),   # soft, splayed base
    (("Env_Rock_", "Env_House_", "Env_Building_", "Env_Wall_",
      "Env_Fence_", "Env_Path_", "Env_Market_", "Env_Well",
      "Env_Cart", "Env_Trough", "Env_Bench", "Env_Crate",
      "Env_Barrel", "Env_Planter", "Env_Ground_"), 1.0),    # a real flat base
]
DEFAULT_FRACTION = 0.7


def ground_fraction(asset):
    for prefixes, fraction in GROUND_FRACTION:
        if asset.startswith(prefixes):
            return fraction
    return DEFAULT_FRACTION


def footprint_samples(obj, bounds, steps=5):
    """Terrain sample points across the part of the object that meets the ground."""
    asset = obj["prefab"].split("/")[-1].replace(".fbx", "")
    b = bounds.get(asset)
    if not b:
        return None, None

    fraction = ground_fraction(asset)
    if fraction <= 0.0:
        return None, None

    scale = obj.get("scale") or [1.0, 1.0, 1.0]
    sx, sy, sz = float(scale[0]), float(scale[1]), float(scale[2])
    x0, y0, z0 = [float(v) for v in b["min"]]
    w, h, d = [float(v) for v in b["size"]]
    # Shrink the box about its own XZ centre to the part that reaches the ground.
    cx, cz = x0 + w * 0.5, z0 + d * 0.5
    w, d = w * fraction, d * fraction
    x0, z0 = cx - w * 0.5, cz - d * 0.5

    yaw = math.radians(float((obj.get("rotation") or [0, 0, 0])[1]))
    cos, sin = math.cos(yaw), math.sin(yaw)
    px, py, pz = [float(v) for v in obj["position"]]

    points = []
    for i in range(steps):
        for j in range(steps):
            lx = (x0 + w * i / (steps - 1)) * sx
            lz = (z0 + d * j / (steps - 1)) * sz
            # Unity yaw: local +X maps to (cos, -sin) in world XZ.
            points.append((px + lx * cos + lz * sin,
                           pz - lx * sin + lz * cos))
    # The object's base plane in world space.
    return points, py + y0 * sy


def audit(grid, layout, bounds, threshold):
    findings = []
    checked = skipped = 0

    for obj in layout.get("objects", []):
        points, base = footprint_samples(obj, bounds)
        if points is None:
            skipped += 1
            continue
        checked += 1

        heights = [grid.at(x, z) for x, z in points]
        lowest = min(heights)
        gap = base - lowest
        if gap <= threshold:
            continue

        findings.append({
            "name": obj["name"],
            "prefab": obj["prefab"].split("/")[-1],
            "parent": obj.get("parent", ""),
            "gap": round(gap, 3),
            "base": round(base, 3),
            "lowestGround": round(lowest, 3),
            "groundSpread": round(max(heights) - lowest, 3),
            "slopeDeg": round(grid.slope_degrees(*points[len(points) // 2]), 1),
            "exempt": is_exempt(obj["name"], obj["prefab"]),
            "correctedY": round(float(obj["position"][1]) - gap - BITE, 3),
        })

    findings.sort(key=lambda f: -f["gap"])
    return findings, checked, skipped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gap", type=float, default=0.15,
                    help="metres of daylight under an object before it is reported")
    ap.add_argument("--fix", action="store_true",
                    help="write corrected Y values back into slice_layout.json")
    ap.add_argument("--max-sink", type=float, default=0.6,
                    help="largest correction --fix will apply. Beyond this, seating the "
                         "object is the wrong repair: a building that needs to drop a "
                         "metre and a half does not want burying, it wants a level pad "
                         "cut for it, and sinking it would hide a design fault.")
    args = ap.parse_args()

    with open(LAYOUT, encoding="utf-8") as fh:
        layout = json.load(fh)
    with open(BOUNDS, encoding="utf-8") as fh:
        bounds = json.load(fh)

    grid = Grid(layout["terrain"]["heightField"]["grid"])
    findings, checked, skipped = audit(grid, layout, bounds, args.gap)

    real = [f for f in findings if not f["exempt"]]
    exempt = [f for f in findings if f["exempt"]]

    print("checked %d objects (%d had no bounds entry)" % (checked, skipped))
    print("floating by more than %.2f m: %d  (+%d exempt by design)"
          % (args.gap, len(real), len(exempt)))
    print()
    if real:
        print("%-28s %-26s %7s %7s %8s %7s" %
              ("name", "prefab", "gap", "spread", "slope", "fixedY"))
        for f in real:
            print("%-28s %-26s %7.3f %7.3f %7.1f° %7.3f" %
                  (f["name"], f["prefab"], f["gap"], f["groundSpread"],
                   f["slopeDeg"], f["correctedY"]))
    if exempt:
        print()
        print("exempt (meant to stand clear):")
        for f in exempt:
            print("  %-26s %-24s gap %.3f" % (f["name"], f["prefab"], f["gap"]))

    deep = [f for f in real if f["gap"] > args.max_sink]
    if deep:
        print()
        print("too deep to seat (%.2f m limit) -- these need the ground fixed under "
              "them, not the object moved:" % args.max_sink)
        for f in deep:
            print("  %-26s %-26s gap %.3f  slope %.1f°"
                  % (f["name"], f["prefab"], f["gap"], f["slopeDeg"]))

    if args.fix and real:
        real = [f for f in real if f["gap"] <= args.max_sink]
        index = {f["name"]: f for f in real}
        for obj in layout["objects"]:
            f = index.get(obj["name"])
            if f is not None:
                obj["position"][1] = f["correctedY"]
        with open(LAYOUT, "w", encoding="utf-8") as fh:
            json.dump(layout, fh, indent=1)
        print()
        print("seated %d objects into the slope (bite %.2f m); re-run the emitter."
              % (len(real), BITE))


if __name__ == "__main__":
    main()
