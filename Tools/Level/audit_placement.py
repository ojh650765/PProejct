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
from worldgen import chaikin, closest_on_polyline, resample

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


def obb(obj, bounds, shrink=False):
    """The object's world-space footprint as (corners, yLow, yHigh), or None.

    With `shrink`, the box is reduced to the part that actually occupies space on the
    ground. Bounding boxes make almost every tree in a wood overlap its neighbours,
    which is not a fault -- interlocking canopies are what a wood is -- and reporting
    640 of them buries the handful of solid props that really are inside each other.
    """
    asset = obj["prefab"].split("/")[-1].replace(".fbx", "")
    b = bounds.get(asset)
    if not b:
        return None

    scale = obj.get("scale") or [1.0, 1.0, 1.0]
    sx, sy, sz = float(scale[0]), float(scale[1]), float(scale[2])
    x0, y0, z0 = [float(v) for v in b["min"]]
    w, h, d = [float(v) for v in b["size"]]
    if shrink:
        f = ground_fraction(asset)
        if f <= 0.0:
            return None
        cx, cz = x0 + w * 0.5, z0 + d * 0.5
        w, d = w * f, d * f
        x0, z0 = cx - w * 0.5, cz - d * 0.5

    yaw = math.radians(float((obj.get("rotation") or [0, 0, 0])[1]))
    cos, sin = math.cos(yaw), math.sin(yaw)
    px, py, pz = [float(v) for v in obj["position"]]

    corners = []
    for lx in (x0 * sx, (x0 + w) * sx):
        for lz in (z0 * sz, (z0 + d) * sz):
            corners.append((px + lx * cos + lz * sin, pz - lx * sin + lz * cos))
    # Rectangle order, not the nested-loop order, so edges are real edges.
    corners = [corners[0], corners[1], corners[3], corners[2]]
    return corners, py + y0 * sy, py + (y0 + h) * sy


def overlap_depth(a, b, want_axis=False):
    """Penetration depth of two convex footprints by SAT, or 0 if they are apart.

    With want_axis, also returns the axis of least penetration, which is the shortest
    direction that pushes them apart.
    """
    best = 1e9
    best_axis = (1.0, 0.0)
    for poly in (a, b):
        n = len(poly)
        for i in range(n):
            ex = poly[(i + 1) % n][0] - poly[i][0]
            ez = poly[(i + 1) % n][1] - poly[i][1]
            length = math.hypot(ex, ez)
            if length < 1e-9:
                continue
            ax, az = -ez / length, ex / length
            amin = min(ax * p[0] + az * p[1] for p in a)
            amax = max(ax * p[0] + az * p[1] for p in a)
            bmin = min(ax * p[0] + az * p[1] for p in b)
            bmax = max(ax * p[0] + az * p[1] for p in b)
            gap = min(amax, bmax) - max(amin, bmin)
            if gap <= 0.0:
                return (0.0, (0.0, 0.0)) if want_axis else 0.0
            if gap < best:
                best, best_axis = gap, (ax, az)
    return (best, best_axis) if want_axis else best


def audit_buried(layout, bounds, threshold, grid):
    """Objects whose own geometry is under the ground.

    The mirror of the floating check, and it needs its own pass because the two are
    not one signed number: a prop can be seated correctly at its pivot and still be
    half buried if its pivot is not where the manifest says it is.
    Env_Prop_CaptureBall claims a base pivot and actually has its origin at the
    centre of the sphere, so seating it on the terrain puts half the ball underground.
    """
    found = []
    for obj in layout.get("objects", []):
        # Cave interiors live in their own scene and are never emitted into the
        # overworld; they read as buried here only because the height grid describes
        # the outside of the mountain they are inside.
        if obj.get("parent", "").startswith("Cave/"):
            continue
        box = obb(obj, bounds)
        if box is None:
            continue
        corners, low, high = box
        ground = min(grid.at(x, z) for x, z in corners)
        buried = ground - low
        height = high - low
        # Absolute *or* proportional: a capture ball is 11 cm tall, so half of it is
        # underground at 5.5 cm and no absolute threshold worth setting would catch it.
        if height <= 1e-6:
            continue
        if buried <= threshold and buried / height <= 0.2:
            continue
        found.append({
            "name": obj["name"],
            "prefab": obj["prefab"].split("/")[-1],
            "buried": round(buried, 3),
            "height": round(height, 3),
            "fraction": round(min(1.0, buried / height), 3),
        })
    found.sort(key=lambda f: -f["fraction"])
    return found


def audit_overlaps(layout, bounds, threshold):
    """Pairs of objects standing inside each other."""
    boxes = []
    for obj in layout.get("objects", []):
        if obj.get("parent", "").startswith("Cave/"):
            continue
        box = obb(obj, bounds, shrink=True)
        if box is None:
            continue
        corners, low, high = box
        cx = sum(p[0] for p in corners) / 4.0
        cz = sum(p[1] for p in corners) / 4.0
        radius = max(math.hypot(p[0] - cx, p[1] - cz) for p in corners)
        boxes.append((obj, corners, low, high, cx, cz, radius))

    found = []
    for i in range(len(boxes)):
        oi, ci, loi, hii, xi, zi, ri = boxes[i]
        for j in range(i + 1, len(boxes)):
            oj, cj, loj, hij, xj, zj, rj = boxes[j]
            if math.hypot(xi - xj, zi - zj) > ri + rj:
                continue
            if min(hii, hij) - max(loi, loj) <= 0.0:
                continue  # one is comfortably above the other
            depth, axis = overlap_depth(ci, cj, want_axis=True)
            if depth <= threshold:
                continue
            # Push the *second* one out along the axis of least penetration. Moving one
            # of the pair keeps the authored composition: these are placed relative to
            # a plaza and a street, and splitting the move between them drifts both off
            # the line they were put on.
            sign = 1.0 if ((xj - xi) * axis[0] + (zj - zi) * axis[1]) >= 0 else -1.0
            found.append({
                "a": oi["name"], "aPrefab": oi["prefab"].split("/")[-1],
                "b": oj["name"], "bPrefab": oj["prefab"].split("/")[-1],
                "depth": round(depth, 3),
                "verticalOverlap": round(min(hii, hij) - max(loi, loj), 3),
                "push": [round(axis[0] * sign * (depth + 0.12), 4),
                         round(axis[1] * sign * (depth + 0.12), 4)],
            })
    found.sort(key=lambda f: -f["depth"])
    return found


def audit_road_blockers(layout, bounds, clearance):
    """Solid objects standing in the walkable width of a road.

    A boulder on the road is not a cosmetic fault: the roads are the level's
    circulation, and one sitting across the bridge approach turns the route's single
    crossing into a dead end. The placement pass has a `road_margin`, but it is applied
    per call and several placements do not pass one, so nothing checks the result.
    """
    roads = []
    for p in layout.get("paths", []):
        if not p.get("conformsTerrain", True):
            continue
        pts = resample(chaikin([tuple(float(v) for v in c)
                                for c in p["controlPoints"]], 3), 0.75)
        roads.append((p["name"], pts, float(p["width"]) * 0.5))

    found = []
    for obj in layout.get("objects", []):
        if obj.get("parent", "").startswith("Cave/"):
            continue
        prefab = obj["prefab"]
        # Only things you would have to walk around. Ground cover and anything that is
        # meant to be on the road (bridges, paving, signposts at the verge) are not.
        if not any(k in prefab for k in ("Env_Rock_", "Env_House_", "Env_Building_",
                                         "Env_Tree_", "Env_Bush_", "Env_Fence_",
                                         "Env_Wall_", "Env_Crate", "Env_Barrel",
                                         "Env_Cart", "Env_Well", "Env_Planter",
                                         "Env_Market_", "Env_Trough")):
            continue
        box = obb(obj, bounds, shrink=True)
        if box is None:
            continue
        corners, _, _ = box

        for name, pts, half in roads:
            intrusion = max(half + clearance - closest_on_polyline(c[0], c[1], pts)[0]
                            for c in corners)
            if intrusion <= 0.0:
                continue
            # Push straight away from the centreline at the worst corner.
            worst = max(corners, key=lambda c: half + clearance - closest_on_polyline(c[0], c[1], pts)[0])
            d, pt, _ = closest_on_polyline(worst[0], worst[1], pts)
            ax, az = worst[0] - pt[0], worst[1] - pt[-1]
            n = math.hypot(ax, az) or 1.0
            found.append({
                "name": obj["name"],
                "prefab": prefab.split("/")[-1],
                "road": name,
                "intrusion": round(intrusion, 3),
                "push": [round(ax / n * (intrusion + 0.15), 3),
                         round(az / n * (intrusion + 0.15), 3)],
            })
            break

    found.sort(key=lambda f: -f["intrusion"])
    return found


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

    buried = audit_buried(layout, bounds, 0.08, grid)
    print()
    print("=== buried: own geometry below the ground ===")
    if not buried:
        print("  none")
    for f in buried[:25]:
        print("  %-26s %-26s %5.0f%% of its height under (%.3f of %.3f m)"
              % (f["name"], f["prefab"], f["fraction"] * 100, f["buried"], f["height"]))

    clashes = audit_overlaps(layout, bounds, 0.10)
    print()
    print("=== overlapping pairs ===")
    if not clashes:
        print("  none")
    for f in clashes[:30]:
        print("  %-24s x %-24s  %.2f m into each other (%.2f m of shared height)"
              % (f["a"], f["b"], f["depth"], f["verticalOverlap"]))
    if len(clashes) > 30:
        print("  ... and %d more" % (len(clashes) - 30))

    blockers = audit_road_blockers(layout, bounds, 0.15)
    print()
    print("=== solid objects standing in a road ===")
    if not blockers:
        print("  none")
    for f in blockers[:20]:
        print("  %-26s %-24s %-22s %.2f m into it"
              % (f["name"], f["prefab"], f["road"], f["intrusion"]))
    if len(blockers) > 20:
        print("  ... and %d more" % (len(blockers) - 20))

    if args.fix and (buried or clashes or blockers):
        index = {o["name"]: o for o in layout["objects"]}

        lifted = 0
        for f in buried:
            obj = index.get(f["name"])
            # Lift by what is underground, less a small bite so it still beds in.
            # Env_Prop_CaptureBall is the reason this exists: the manifest calls its
            # pivot "base" and its geometry has the origin at the centre of the
            # sphere, so seating the pivot on the terrain sinks half the ball.
            if obj is not None and f["fraction"] > 0.2:
                obj["position"][1] = round(obj["position"][1] + f["buried"] - 0.02, 3)
                lifted += 1

        moved = set()
        for f in clashes:
            if f["b"] in moved:
                continue   # one push per object, then re-audit rather than stacking
            obj = index.get(f["b"])
            if obj is None:
                continue
            obj["position"][0] = round(obj["position"][0] + f["push"][0], 3)
            obj["position"][2] = round(obj["position"][2] + f["push"][1], 3)
            moved.add(f["b"])

        cleared = 0
        for f in blockers:
            obj = index.get(f["name"])
            if obj is None or f["name"] in moved:
                continue
            obj["position"][0] = round(obj["position"][0] + f["push"][0], 3)
            obj["position"][2] = round(obj["position"][2] + f["push"][1], 3)
            moved.add(f["name"])
            cleared += 1

        with open(LAYOUT, "w", encoding="utf-8") as fh:
            json.dump(layout, fh, indent=1)
        print()
        print("cleared %d object(s) off the roads." % cleared)
        print("lifted %d buried object(s), separated %d overlapping object(s); "
              "re-run this audit to confirm the moves did not create new clashes."
              % (lifted, len(moved)))


if __name__ == "__main__":
    main()
