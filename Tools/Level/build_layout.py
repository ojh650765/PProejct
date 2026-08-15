"""Generate Assets/Game/Data/Levels/slice_layout.json -- the vertical slice's placement data.

Design intent lives in this file. The JSON is an artefact; tune the numbers here and re-run.

    python Tools/Level/build_layout.py

World conventions (from Assets/Game/Art/Environment/environment_manifest.json):
  * 1 unit = 1 metre, Y up, models face +Z, pivots at the base, modular pieces on a 0.5 m grid.
  * FBX bounds are read from Tools/Level/asset_bounds.json, which stores Blender-space
    (X, Y=depth, Z=up) sizes. Unity footprint = (size[0], size[1]) and height = size[2].

Height plan -- six depth planes, which is what makes the shot read as a diorama:
    -2.0  lake surface        -1.6..-1.2  shore band       0.0  route floor
    +1.5  cave mouth/interior +3.0  town terrace           +4.5/+9.5  backdrop cliff tiers
"""

from __future__ import annotations

import json
import math
import os
import random

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
BOUNDS_PATH = os.path.join(HERE, "asset_bounds.json")
OUT_DIR = os.path.join(REPO, "Assets", "Game", "Data", "Levels")
OUT_PATH = os.path.join(OUT_DIR, "slice_layout.json")

SEED = 20260816
ENV = "Assets/Game/Art/Environment"

# ---------------------------------------------------------------------------
# Height plane constants
# ---------------------------------------------------------------------------
Y_WATER = -2.0
Y_SHORE = -1.5
Y_ROUTE = 0.0
Y_CAVE = 1.5
Y_TOWN = 3.0
Y_CAVE_CEIL = 6.5
Y_TIER1 = 4.5      # top of the massif's outer face (and the waterfall lip)
Y_TIER2 = 11.5     # base of the skyline tier; the chamber roof at 6.5 passes underneath
Y_SKYLINE = 16.5   # ridge top -- pure background silhouette, never walkable

# The camera this layout is composed for. Pitch band and rest distance come from
# Docs/ART_DIRECTION_SPRITE_PIVOT.md section 3.2 and OverworldCameraRig defaults.
CAMERA = {
    "kind": "fixed three-quarter, 8-step yaw snapping, limited orbit",
    "pitchDegrees": 42.0,
    "pitchBandDegrees": [35.0, 50.0],
    "yawStepDegrees": 45.0,
    "restDistanceMetres": 5.5,
    "minDistanceMetres": 1.4,
    "maxDistanceMetres": 9.0,
    "verticalFovDegrees": 40.0,
    "screenfulWidthMetres": 14.0,
    "note": (
        "At 42 deg pitch and 5.5 m the camera sits 3.68 m above and 4.09 m behind the player, "
        "and one screenful of ground is about 14 m across. Every 14 m of walkable path in this "
        "layout is given its own landmark and its own near-lens framing element, because that is "
        "the interval at which the player gets a new picture."
    ),
    "depthOfField": {
        "mode": "Bokeh",
        "apertureFStops": [2.8, 4.0],
        "focusMetres": 5.5,
        "foregroundFringeMetres": [1.5, 3.5],
        "sharpMidgroundMetres": [3.5, 14.0],
        "blurredBackgroundMetres": [22.0, 120.0],
        "note": (
            "DOF only pays off if there is geometry at each depth. Foliage is deliberately planted "
            "1.5-3.5 m off the path edge on BOTH sides so that whichever of the 8 yaw steps the "
            "camera snaps to, something sits in the foreground fringe."
        ),
    },
}

# ---------------------------------------------------------------------------
# Asset shorthand
# ---------------------------------------------------------------------------
TREE_BROADLEAF = ["Env_Tree_Broadleaf_A", "Env_Tree_Broadleaf_B", "Env_Tree_Broadleaf_C"]
TREE_CONIFER = ["Env_Tree_Conifer_A", "Env_Tree_Conifer_B", "Env_Tree_Conifer_C"]
TREE_BIRCH = ["Env_Tree_Birch_A", "Env_Tree_Birch_B", "Env_Tree_Birch_C"]
TREE_WILLOW = ["Env_Tree_Willow_A", "Env_Tree_Willow_B", "Env_Tree_Willow_C"]
BUSHES = ["Env_Bush_A", "Env_Bush_B", "Env_Bush_C"]
FERNS = ["Env_Fern_A", "Env_Fern_B"]
GRASS = ["Env_Grass_Clump_A", "Env_Grass_Clump_B", "Env_Grass_Clump_C", "Env_Grass_Clump_D"]
GRASS_TALL = ["Env_Grass_Clump_C", "Env_Grass_Clump_A"]  # the two upright reads
FLOWERS = ["Env_Flower_Red", "Env_Flower_Yellow", "Env_Flower_Purple", "Env_Flower_White"]
REEDS = ["Env_Reed_A", "Env_Reed_B"]
LILYPADS = ["Env_Lilypad_A", "Env_Lilypad_B"]
MOSS = ["Env_Moss_Cave_A", "Env_Moss_Cave_B"]
VINES = ["Env_Vine_Hanging_A", "Env_Vine_Hanging_B"]
BOULDERS = ["Env_Rock_Boulder_A", "Env_Rock_Boulder_B", "Env_Rock_Boulder_C", "Env_Rock_Boulder_D"]
BOULDERS_SMALL = ["Env_Rock_Boulder_A", "Env_Rock_Boulder_C", "Env_Rock_Boulder_Wet_F"]
BOULDERS_MOSSY = ["Env_Rock_Boulder_Mossy_E", "Env_Rock_Boulder_C"]
BOULDERS_WET = ["Env_Rock_Boulder_Wet_F", "Env_Rock_Boulder_C", "Env_Rock_Boulder_A"]
SCATTER = ["Env_Rock_Scatter_A", "Env_Rock_Scatter_B"]
STALACTITES = ["Env_Cave_Stalactite_A", "Env_Cave_Stalactite_B"]
STALAGMITES = ["Env_Cave_Stalagmite_A", "Env_Cave_Stalagmite_B"]

# Which layer each family lands on. Names come from OverworldNames in
# Assets/Game/Scripts/Overworld/OverworldContracts.cs -- there is no "Foliage" layer in this
# project, so foliage goes on Environment.
LAYER_BY_FAMILY = {"Foliage": "Environment", "Terrain": "Environment", "Town": "Environment",
                   "Props": "Interactable"}

# How much of an asset's footprint counts as solid for overlap rejection. Canopies are
# *supposed* to interpenetrate -- trunks are not -- so trees reject on a trunk-sized radius.
SOLID_FRACTION = {
    "Tree": 0.22, "Bush": 0.30, "Fern": 0.30, "Grass": 0.26, "Flower": 0.26,
    "Reed": 0.30, "Lilypad": 0.42, "Moss": 0.30, "Vine": 0.25,
    "Rock": 0.46, "Cliff": 0.50, "Cave": 0.42, "Riverbank": 0.50, "Waterfall": 0.50,
    "Stepping": 0.50, "Bridge": 0.50,
    "House": 0.52, "Building": 0.52, "Fence": 0.40, "Lamp": 0.35, "Signpost": 0.35,
    "Bench": 0.45, "Crate": 0.45, "Barrel": 0.45, "Market": 0.50, "Well": 0.50,
    "Planter": 0.45, "Path": 0.0, "Prop": 0.45,
}
# Subfamilies light enough to sit inside a walkable corridor's outer margin.
GROUND_COVER = {"Grass", "Flower", "Moss", "Lilypad", "Fern"}


class Builder:
    """Accumulates placements, rejecting overlaps and anything that blocks a walkable path."""

    def __init__(self, bounds):
        self.bounds = bounds
        self.rng = random.Random(SEED)
        self.objects = []
        self.occupancy = []          # (x, y, z, radius, is_ground_cover)
        self.paths = []              # dict(points, half_width, name)
        self.footprints = []         # (x, z, yaw, half_w, half_d) -- no-build rectangles
        self.counters = {}
        self.footprint_y = {}
        self.rejected = 0

    # -- geometry helpers ---------------------------------------------------
    def size(self, name):
        """Unity-space (width_x, depth_z, height_y) for an asset."""
        s = self.bounds[name]["size"]
        return s[0], s[1], s[2]

    def footprint_radius(self, name, scale):
        w, d, _ = self.size(name)
        return 0.5 * max(w, d) * scale

    def solid_radius(self, name, scale):
        sub = self.bounds[name]["subfamily"]
        return self.footprint_radius(name, scale) * SOLID_FRACTION.get(sub, 0.45) * 2.0

    def add_footprint(self, asset, x, y, z, yaw):
        """Mark a building's oriented rectangle as no-build.

        The circular occupancy radius is both too generous on the long axis and too mean
        on the short one for a 7 x 5.5 m farmhouse, which let scatter land inside the lab.
        """
        w, d, _ = self.size(asset)
        self.footprints.append((x, z, yaw, w / 2, d / 2))
        self.footprint_y[(x, z)] = y

    def add_path(self, name, points, half_width):
        self.paths.append({"name": name, "points": points, "halfWidth": half_width})

    def path_distance(self, x, z):
        """Distance to the nearest walkable centreline, minus that path's half width.

        Negative means inside the corridor.
        """
        best = 1e9
        for path in self.paths:
            pts = path["points"]
            for i in range(len(pts) - 1):
                ax, az = pts[i][0], pts[i][2]
                bx, bz = pts[i + 1][0], pts[i + 1][2]
                dx, dz = bx - ax, bz - az
                length_sq = dx * dx + dz * dz
                t = 0.0 if length_sq == 0 else ((x - ax) * dx + (z - az) * dz) / length_sq
                t = max(0.0, min(1.0, t))
                px, pz = ax + t * dx, az + t * dz
                d = math.hypot(x - px, z - pz) - path["halfWidth"]
                best = min(best, d)
        return best

    # -- placement ----------------------------------------------------------
    def place(self, asset, name, parent, pos, yaw=None, scale=None, tilt=0.0,
              static=True, tag="Untagged", layer=None, force=False, notes=None,
              ignore_paths=False):
        """Try to place one object. Returns the record, or None if it was rejected.

        Overlap is tested in XZ but *banded in Y*: two objects more than 3 m apart vertically
        never collide, so the town terrace at +3 does not veto the route floor at 0 beneath it,
        and a ceiling stalactite at +6.5 does not veto the stalagmite under it.
        """
        if scale is None:
            scale = self.rng.uniform(0.85, 1.15)      # +/-15%: texel density stays coherent
        if yaw is None:
            yaw = self.rng.uniform(0.0, 360.0)
        info = self.bounds[asset]
        sub = info["subfamily"]
        radius = self.solid_radius(asset, scale)
        x, y, z = pos

        if not force:
            # Never block the walk. Ground cover may creep into the corridor's outer third.
            if not ignore_paths:
                clearance = self.path_distance(x, z)
                allowance = -0.35 * 2.0 if sub in GROUND_COVER else radius * 0.5
                if clearance < allowance:
                    self.rejected += 1
                    return None
            for fx, fz, fyaw, fhw, fhd in self.footprints:
                if abs(y - self.footprint_y.get((fx, fz), y)) > 3.0:
                    continue
                a = math.radians(fyaw)
                dx, dz = x - fx, z - fz
                lx = dx * math.cos(a) - dz * math.sin(a)
                lz = dx * math.sin(a) + dz * math.cos(a)
                if abs(lx) < fhw + radius and abs(lz) < fhd + radius:
                    self.rejected += 1
                    return None
            for ox, oy, oz, orad, o_cover in self.occupancy:
                if abs(y - oy) > 3.0:
                    continue                            # different height plane
                if sub in GROUND_COVER and o_cover:
                    continue                            # tufts are allowed to interleave
                if (x - ox) ** 2 + (z - oz) ** 2 < (radius + orad) ** 2:
                    self.rejected += 1
                    return None

        self.occupancy.append((x, y, z, radius, sub in GROUND_COVER))
        n = self.counters.get(name, 0) + 1
        self.counters[name] = n
        rot_x = self.rng.uniform(-tilt, tilt) if tilt else 0.0
        rot_z = self.rng.uniform(-tilt, tilt) if tilt else 0.0
        record = {
            "prefab": info["path"],
            "name": "%s_%02d" % (name, n),
            "parent": parent,
            "position": [round(x, 3), round(y, 3), round(z, 3)],
            "rotation": [round(rot_x, 2), round(yaw % 360.0, 2), round(rot_z, 2)],
            "scale": [round(scale, 4)] * 3,
            "layer": layer or LAYER_BY_FAMILY.get(info["family"], "Environment"),
            "tag": tag,
            "static": static,
        }
        if notes:
            record["notes"] = notes
        self.objects.append(record)
        return record


    def push_off(self, x, z, clearance):
        """Slide a point away from the nearest walkable corridor until it clears it.

        Street furniture is authored where it looks right, then nudged out of the walk here,
        which is far more reliable than my recomputing every offset against a bent polyline.
        """
        for _ in range(48):
            d = self.path_distance(x, z)
            if d >= clearance:
                return x, z
            best = None
            for path in self.paths:
                pts = path["points"]
                for i in range(len(pts) - 1):
                    ax, az = pts[i][0], pts[i][2]
                    bx, bz = pts[i + 1][0], pts[i + 1][2]
                    dx, dz = bx - ax, bz - az
                    length_sq = dx * dx + dz * dz
                    t = 0.0 if length_sq == 0 else ((x - ax) * dx + (z - az) * dz) / length_sq
                    t = max(0.0, min(1.0, t))
                    px, pz = ax + t * dx, az + t * dz
                    dd = math.hypot(x - px, z - pz) - path["halfWidth"]
                    if best is None or dd < best[0]:
                        best = (dd, px, pz)
            _, px, pz = best
            ux, uz = x - px, z - pz
            length = math.hypot(ux, uz) or 1.0
            x += ux / length * 0.25
            z += uz / length * 0.25
        return x, z


    def place_near(self, asset, name, parent, x, z, y_fn, clearance, **kw):
        """Place authored street furniture at its anchor, or at the nearest free slot.

        Benches and planters are composed against a specific wall or plaza edge, so silently
        dropping them when the anchor happens to be taken loses the composition. Spiral out a
        little instead: still overlap-checked, still pushed clear of the walk.
        """
        for radius in (0.0, 0.8, 1.6, 2.4, 3.2):
            for k in range(1 if radius == 0 else 8):
                a = math.tau * k / 8
                tx, tz = x + radius * math.cos(a), z + radius * math.sin(a)
                tx, tz = self.push_off(tx, tz, clearance)
                rec = self.place(asset, name, parent, (tx, y_fn(tx, tz), tz),
                                 ignore_paths=True, **kw)
                if rec:
                    return rec, tx, tz
        return None, x, z

    def assert_clear(self, buildings, label=""):
        """Report any building whose footprint reaches a walkable centreline, or that
        overlaps another building. Printed rather than raised so a tuning run still emits."""
        problems = []
        for asset, x, z, _yaw, _note in buildings:
            w, d, _ = self.size(asset)
            half = max(w, d) / 2
            gap = self.path_distance(x, z) - half
            if gap < 0:
                problems.append("%s at (%.1f, %.1f) overlaps a walkable corridor by %.2f m"
                                % (asset, x, z, -gap))
        for i in range(len(buildings)):
            a_asset, ax, az, _, _ = buildings[i]
            aw, ad, _ = self.size(a_asset)
            for j in range(i + 1, len(buildings)):
                b_asset, bx, bz, _, _ = buildings[j]
                bw, bd, _ = self.size(b_asset)
                need = (min(aw, ad) + min(bw, bd)) / 2
                got = math.hypot(ax - bx, az - bz)
                if got < need:
                    problems.append("%s and %s are %.2f m apart, need %.2f"
                                    % (a_asset, b_asset, got, need))
        if problems:
            print("[%s] building clearance:" % label)
            for p in problems:
                print("    " + p)
        return problems

    # -- composition primitives --------------------------------------------
    def cluster(self, assets, name, parent, centre, radius, count, y=None, ground=None,
                scale_range=(0.85, 1.15), tilt=0.0, falloff=1.6, weights=None):
        """A clump: density highest at the centre, species mixed, rotations free.

        `falloff` > 1 biases samples toward the middle, which is what stops a "cluster" from
        looking like a disc of evenly spaced props.
        """
        cx, cy, cz = centre
        placed = 0
        for _ in range(count * 4):
            if placed >= count:
                break
            r = radius * (self.rng.random() ** falloff)
            a = self.rng.uniform(0, math.tau)
            x, z = cx + r * math.cos(a), cz + r * math.sin(a)
            asset = self.rng.choices(assets, weights=weights)[0] if weights else self.rng.choice(assets)
            yy = ground(x, z) if ground else (cy if y is None else y)
            if self.place(asset, name, parent, (x, yy, z),
                          scale=self.rng.uniform(*scale_range), tilt=tilt):
                placed += 1
        return placed

    def drift(self, assets, name, parent, a, b, count, spread, y=None, ground=None,
              scale_range=(0.85, 1.15), tilt=0.0, side=0):
        """Scatter along a line -- a hedge, a verge, a shoreline band.

        `side` 0 = both sides, +1/-1 = one side only (used to keep a verge asymmetric).
        """
        ax, _, az = a
        bx, _, bz = b
        placed = 0
        for _ in range(count * 4):
            if placed >= count:
                break
            t = self.rng.random()
            x, z = ax + (bx - ax) * t, az + (bz - az) * t
            dx, dz = bx - ax, bz - az
            length = math.hypot(dx, dz) or 1.0
            nx, nz = -dz / length, dx / length
            off = self.rng.uniform(0.0, spread) if side else self.rng.uniform(-spread, spread)
            if side < 0:
                off = -off
            x += nx * off
            z += nz * off
            asset = self.rng.choice(assets)
            yy = ground(x, z) if ground else y
            if self.place(asset, name, parent, (x, yy, z),
                          scale=self.rng.uniform(*scale_range), tilt=tilt):
                placed += 1
        return placed

    def treeline(self, assets, name, parent, points, count, spread, y=None, ground=None,
                 scale_range=(0.85, 1.15), side=0):
        """Trees along a polyline. Segments are weighted by length so spacing stays even-ish
        without becoming a rank of soldiers -- the spread does the irregularity."""
        segs = []
        total = 0.0
        for i in range(len(points) - 1):
            length = math.dist(points[i][::2], points[i + 1][::2])
            segs.append((points[i], points[i + 1], length))
            total += length
        for seg_a, seg_b, length in segs:
            n = max(1, int(round(count * length / total)))
            self.drift(assets, name, parent, seg_a, seg_b, n, spread, y=y, ground=ground,
                       scale_range=scale_range, side=side)

    def cliff_run(self, points, base_y, parent, name, modules=("Env_Cliff_Wall_4m",
                                                               "Env_Cliff_Wall_2m",
                                                               "Env_Cliff_Wall_6m"),
                  corners=True):
        """Walk a polyline laying 3 m-tall cliff modules, continuously.

        The exposed rock face ends up on the LEFT of travel, because a module's face normal is
        +Z and its length axis is +X: yaw = atan2(-u.z, u.x) rotates the face to u turned -90
        about Y. So an outward-facing boundary is walked clockwise (X right, Z up) and an
        inward-facing one counter-clockwise. Order the points accordingly.

        Distance is accumulated over the WHOLE polyline rather than per segment. Walking each
        segment separately meant a finely sampled curve -- the lake's riverbank arcs, at 1.6 m
        a segment -- could never fit a 2 m module and silently placed nothing at all.
        """
        segs = []
        total = 0.0
        for i in range(len(points) - 1):
            ax, az = points[i]
            bx, bz = points[i + 1]
            length = math.hypot(bx - ax, bz - az)
            if length < 1e-4:
                continue
            segs.append((ax, az, (bx - ax) / length, (bz - az) / length, length, total))
            total += length
        if not segs:
            return 0

        def at(dist):
            for ax, az, ux, uz, length, start in segs:
                if dist <= start + length or (ax, az) == segs[-1][:2]:
                    t = min(max(dist - start, 0.0), length)
                    return ax + ux * t, az + uz * t, ux, uz
            ax, az, ux, uz, length, start = segs[-1]
            return ax + ux * length, az + uz * length, ux, uz

        placed = 0
        travelled = 0.0
        while travelled < total - 0.4:
            remaining = total - travelled
            choices = [m for m in modules if self.size(m)[0] <= remaining + 0.01]
            if not choices:
                choices = [min(modules, key=lambda m: self.size(m)[0])]
            module = self.rng.choice(choices)
            seg_len = self.size(module)[0]
            x, z, ux, uz = at(travelled)
            yaw = math.degrees(math.atan2(-uz, ux))
            # Modular pieces snap to the 0.5 m grid.
            x, z = round(x * 2) / 2, round(z * 2) / 2
            if self.place(module, name, parent, (x, base_y, z), yaw=yaw, scale=1.0, force=True):
                placed += 1
            travelled += seg_len

        if corners:
            # Drop a corner block wherever the line turns hard, so a bend is a corner piece
            # rather than two flat walls meeting in a visible seam.
            run = 0.0
            for i in range(1, len(points) - 1):
                run += math.hypot(points[i][0] - points[i - 1][0], points[i][1] - points[i - 1][1])
                ax, az = points[i - 1]
                bx, bz = points[i]
                cx, cz = points[i + 1]
                v1 = (bx - ax, bz - az)
                v2 = (cx - bx, cz - bz)
                n1 = math.hypot(*v1) or 1.0
                n2 = math.hypot(*v2) or 1.0
                cross = (v1[0] * v2[1] - v1[1] * v2[0]) / (n1 * n2)
                dot = (v1[0] * v2[0] + v1[1] * v2[1]) / (n1 * n2)
                if abs(math.degrees(math.atan2(cross, dot))) < 40.0:
                    continue
                piece = "Env_Cliff_Corner_Inner" if cross > 0 else "Env_Cliff_Corner_Outer"
                yaw = math.degrees(math.atan2(-v1[1] / n1, v1[0] / n1))
                if self.place(piece, name + "_Corner", parent,
                              (round(bx * 2) / 2, base_y, round(bz * 2) / 2),
                              yaw=yaw, scale=1.0, force=True):
                    placed += 1
        return placed

    def fence_run(self, points, y, parent, name, gap_chance=0.22, ground=None):
        """A fence with holes in it. An unbroken 20 m picket run is the single loudest
        regularity tell in dressed_town.png; this drops roughly one module in five."""
        placed = 0
        for i in range(len(points) - 1):
            ax, az = points[i][0], points[i][2]
            bx, bz = points[i + 1][0], points[i + 1][2]
            dx, dz = bx - ax, bz - az
            length = math.hypot(dx, dz)
            if length < 1.0:
                continue
            ux, uz = dx / length, dz / length
            yaw = math.degrees(math.atan2(ux, uz)) + 90.0   # fence panel spans its local X
            travelled = 0.0
            while travelled < length - 0.9:
                module = "Env_Fence_Picket_2m" if (length - travelled) > 2.4 and self.rng.random() > 0.3 \
                    else "Env_Fence_Picket_1m"
                seg = self.size(module)[0]
                if self.rng.random() > gap_chance:
                    x = ax + ux * (travelled + seg * 0.5)
                    z = az + uz * (travelled + seg * 0.5)
                    yy = ground(x, z) if ground else y
                    if self.place(module, name, parent, (x, yy, z), yaw=yaw + self.rng.uniform(-2.5, 2.5),
                                  scale=1.0, force=True):
                        placed += 1
                travelled += seg
        return placed

    def paved(self, points, y, parent, name, width=2, ground=None):
        """Lay a paved street down a polyline as 2 m tiles, `width` tiles across, with the
        outer lane thinned so the street edge frays instead of ending on a ruled line."""
        placed = 0
        for i in range(len(points) - 1):
            ax, az = points[i][0], points[i][2]
            bx, bz = points[i + 1][0], points[i + 1][2]
            dx, dz = bx - ax, bz - az
            length = math.hypot(dx, dz)
            if length < 0.5:
                continue
            ux, uz = dx / length, dz / length
            nx, nz = -uz, ux
            yaw = math.degrees(math.atan2(ux, uz))
            if i > 0:
                # a real corner tile where the street turns, not two straights butted together
                self.place("Env_Path_Paved_Corner", name + "_Corner", parent,
                           (round(ax * 2) / 2, (ground(ax, az) if ground else y),
                            round(az * 2) / 2),
                           yaw=yaw, scale=1.0, force=True)
            steps = max(1, int(round(length / 2.0)))
            for s in range(steps):
                t = (s + 0.5) * length / steps
                for lane in range(width):
                    off = (lane - (width - 1) * 0.5) * 2.0
                    outer = abs(lane - (width - 1) * 0.5) >= (width - 1) * 0.5 and width > 1
                    if outer and self.rng.random() < 0.28:
                        continue                       # worn edge
                    x = round((ax + ux * t + nx * off) * 2) / 2
                    z = round((az + uz * t + nz * off) * 2) / 2
                    yy = ground(x, z) if ground else y
                    tile = "Env_Path_Paved_2m" if self.rng.random() < 0.82 else "Env_Path_Paved_1m"
                    if self.place(tile, name, parent, (x, yy, z), yaw=yaw, scale=1.0, force=True):
                        placed += 1
        return placed

    def stepping_path(self, points, y, parent, name, spacing=1.6, ground=None):
        """Worn stepping tiles for a dirt track -- used sparingly, and never as the main road.
        dressed_route.png's failure was making this dotted line the *only* road."""
        placed = 0
        for i in range(len(points) - 1):
            ax, az = points[i][0], points[i][2]
            bx, bz = points[i + 1][0], points[i + 1][2]
            length = math.hypot(bx - ax, bz - az)
            steps = max(1, int(length / spacing))
            for s in range(steps):
                t = (s + 0.5) / steps
                x = ax + (bx - ax) * t + self.rng.uniform(-0.35, 0.35)
                z = az + (bz - az) * t + self.rng.uniform(-0.35, 0.35)
                yy = ground(x, z) if ground else y
                if self.place("Env_Path_Paved_1m", name, parent, (x, yy, z),
                              yaw=self.rng.uniform(0, 360), scale=self.rng.uniform(0.9, 1.15),
                              force=True):
                    placed += 1
        return placed

    def edge_break(self, points, y, parent, name, count, spread=1.4, ground=None):
        """Rubble, tufts and scatter rock laid over a material boundary so grass->dirt->stone
        never reads as a drawn line. This is the cheapest 'it looks finished' trick there is."""
        mix = SCATTER + SCATTER + GRASS + ["Env_Bush_B", "Env_Fern_B", "Env_Rock_Boulder_C"]
        total = sum(math.dist(points[i][::2], points[i + 1][::2]) for i in range(len(points) - 1))
        for i in range(len(points) - 1):
            length = math.dist(points[i][::2], points[i + 1][::2])
            n = max(1, int(round(count * length / max(total, 0.01))))
            self.drift(mix, name, parent, points[i], points[i + 1], n, spread,
                       y=y, ground=ground, scale_range=(0.75, 1.15), tilt=6.0)


# ---------------------------------------------------------------------------
# Ground height field -- the decks the integrator will build (the kit has no terrain)
# ---------------------------------------------------------------------------
def poly_y(points, fallback=0.0):
    """Height field that follows a sloped polyline (ramps, graded spurs, shore walks).

    Returns the Y of the nearest point on the line, so props dressing a ramp sit on the ramp
    instead of hovering at the level it started from.
    """
    def sample(x, z):
        best_d, best_y = 1e9, fallback
        for i in range(len(points) - 1):
            ax, ay, az = points[i]
            bx, by, bz = points[i + 1]
            dx, dz = bx - ax, bz - az
            length_sq = dx * dx + dz * dz
            t = 0.0 if length_sq == 0 else ((x - ax) * dx + (z - az) * dz) / length_sq
            t = max(0.0, min(1.0, t))
            px, pz = ax + t * dx, az + t * dz
            d = (x - px) ** 2 + (z - pz) ** 2
            if d < best_d:
                best_d, best_y = d, ay + t * (by - ay)
        return best_y
    return sample


def ground_route(x, z):
    """Route floor with a gentle roll so nothing is a billiard table."""
    return Y_ROUTE + 0.28 * math.sin(x * 0.075) + 0.22 * math.cos(z * 0.09)


def ground_shore(x, z):
    """Shore band grading from the waterline up to the route floor."""
    d = math.hypot(x + 6.0, (z + 2.0) * 1.15)
    t = max(0.0, min(1.0, (d - 21.0) / 8.0))
    return Y_SHORE + t * (Y_ROUTE - Y_SHORE) + 0.12 * math.sin(x * 0.3)


def ground_town(x, z):
    return Y_TOWN + 0.10 * math.sin(x * 0.16) + 0.08 * math.cos(z * 0.19)


def ground_cave(x, z):
    return Y_CAVE + 0.13 * math.sin(x * 0.22) + 0.11 * math.cos(z * 0.26)


def build():
    bounds = json.load(open(BOUNDS_PATH, encoding="utf-8"))
    b = Builder(bounds)
    rng = b.rng

    # -----------------------------------------------------------------------
    # Walkable spine. Declared before anything is planted so every later call
    # is automatically forbidden from blocking the walk.
    # -----------------------------------------------------------------------
    route_spine = [(30, 0, 42), (28, 0, 38.5), (20, 0, 36.5), (12, 0, 34.5),
                   (4, 0, 32.5), (-4, 0, 31), (-12, 0, 30.5), (-20, 0, 31.5), (-26, 0, 32)]
    # The lake is an ellipse centred (-4, -3) with radii 20 x 17. The shore walk is generated
    # at 1.18x those radii so it is guaranteed to stay on the shore band -- the first pass
    # hand-authored it and ran the footpath straight across open water.
    LAKE_C, LAKE_RX, LAKE_RZ = (-4.0, -3.0), 20.0, 17.0

    def shore_point(deg, k=1.24):
        a = math.radians(deg)
        return (LAKE_C[0] + LAKE_RX * k * math.cos(a), LAKE_C[1] + LAKE_RZ * k * math.sin(a))

    lake_spur = [(4, 0, 32.5), (3, -0.4, 26), (1.5, -0.9, 21), (0.4, -1.4, 17.5)]
    shore_walk = [(0.4, -1.4, 17.5)]
    for deg in (86, 118, 150, 182, 214, 246, 278, 310, 338):
        sx, sz = shore_point(deg)
        shore_walk.append((round(sx, 2), -1.4, round(sz, 2)))
    shore_walk.append((21.0, -1.2, -7.2))
    # ...over the bridge, up the east bank and back onto Route 1. The slice is a loop, not a
    # dead end: town -> route -> lake -> bridge -> east bank -> route.
    east_bank = [(21.0, -1.2, -7.2), (26.0, -1.1, -7.8), (29.5, -0.8, -1.0), (30.5, -0.4, 8.0),
                 (28.5, 0.0, 18.0), (24.0, 0.0, 28.0), (20.0, 0.0, 36.5)]
    town_street = [(38, 3, 44), (46, 3, 38), (54, 3, 30), (60, 3, 24),
                   (66, 3, 18), (72, 3, 10)]
    town_ramp = [(30, 0, 42), (34, 1.5, 43), (38, 3, 44)]
    cave_ramp = [(-26, 0, 32), (-28.5, 0.75, 32), (-31, 1.5, 32)]
    cave_walk = [(-31, 1.5, 32), (-38, 1.5, 33), (-44, 1.5, 34), (-50, 1.5, 33),
                 (-55, 1.5, 34)]
    plaza_link = [(57.8, 3, 22.6), (52.5, 3, 19.4), (48.5, 3, 16.5)]
    lab_approach = [(61.0, 3, 21.0), (63.4, 3, 23.6), (65.0, 3, 27.4)]

    b.add_path("Route_Spine", route_spine, 2.6)
    b.add_path("Route_LakeSpur", lake_spur, 2.1)
    b.add_path("Lakeside_ShoreWalk", shore_walk, 2.1)
    b.add_path("Lakeside_EastBank", east_bank, 2.3)
    b.add_path("Town_Street", town_street, 3.1)
    b.add_path("Town_Ramp", town_ramp, 2.6)
    b.add_path("Town_PlazaLane", plaza_link, 2.4)
    b.add_path("Town_LabApproach", lab_approach, 1.6)
    b.add_path("Cave_Ramp", cave_ramp, 2.4)
    b.add_path("Cave_Walk", cave_walk, 2.6)

    y_town_ramp = poly_y(town_ramp, 1.5)
    y_lake_spur = poly_y(lake_spur, -1.0)
    y_shore_walk = poly_y(shore_walk, -1.4)
    y_east_bank = poly_y(east_bank, -0.5)
    y_cave_ramp = poly_y(cave_ramp, 0.75)

    # =======================================================================
    # TOWN -- terrace at Y=3, cliff-edged on its west and south.
    #
    # Buildings sit in four knots, each knot a pair with a 1.2-3 m alley between them, and
    # every centre is held clear of the street centreline by (street half-width + its own
    # half-depth). That clearance is asserted below rather than eyeballed -- the first pass
    # of this layout ran the street straight through a cottage.
    # =======================================================================
    P = "Town"
    b.paved(town_street, None, P + "/Ground", "Town_Street", width=3, ground=ground_town)
    b.paved(plaza_link, None, P + "/Ground", "Town_PlazaLane", width=2, ground=ground_town)
    b.paved(lab_approach, None, P + "/Ground", "Town_LabApproach", width=2, ground=ground_town)
    b.paved(town_ramp, None, P + "/Ground", "Town_Ramp", width=3, ground=y_town_ramp)

    PLAZA = (57.8, 19.6)
    for ring_r, ring_n in ((3.0, 9), (5.2, 15), (7.4, 21)):
        for k in range(ring_n):
            a = math.tau * k / ring_n + rng.uniform(-0.09, 0.09)
            r = ring_r + rng.uniform(-0.5, 0.5)
            x, z = PLAZA[0] + r * math.cos(a), PLAZA[1] + r * math.sin(a)
            if rng.random() < 0.86:
                b.place("Env_Path_Paved_2m", "Town_PlazaApron", P + "/Ground",
                        (round(x * 2) / 2, ground_town(x, z), round(z * 2) / 2),
                        yaw=rng.choice([0, 90, 180, 270]), scale=1.0, force=True)

    # -- the landmark. Tallest object in the slice by 1.4 m; door turned toward the plaza.
    b.place("Env_Building_PokeLab", "Town_PokeLab", P + "/Buildings", (68, ground_town(68, 34), 34),
            yaw=208, scale=1.0, force=True, tag="Landmark",
            notes="Landmark. 8.7 x 9.3 footprint, 8.76 m tall -- nothing else in Town clears "
                  "6.8 m. Door faces 208 deg, down the approach toward the plaza.")
    b.add_footprint("Env_Building_PokeLab", 68.0, ground_town(68, 34), 34.0, 208)
    b.place("Env_Prop_HealingMachine", "Town_HealingMachine", P + "/Props",
            (63.6, ground_town(63.6, 27.2), 27.2), yaw=208, scale=1.0, force=True,
            tag="Interactable", layer="Interactable")
    b.place("Env_Prop_ResearchTerminal", "Town_ResearchTerminal", P + "/Props",
            (65.9, ground_town(65.9, 25.9), 25.9), yaw=214, scale=1.0, force=True,
            tag="Interactable", layer="Interactable")
    b.place("Env_Prop_Scanner", "Town_Scanner", P + "/Props",
            (65.8, ground_town(65.8, 25.9) + 1.05, 25.7), yaw=200, scale=1.0, force=True,
            tag="Interactable", layer="Interactable",
            notes="Deliberately stacked ON the research terminal deck (terminal is 1.68 m tall).")

    # -- four knots of housing. (asset, x, z, yaw, note)
    houses = [
        ("Env_House_Cottage_A", 55.5, 39.5, 225, "north knot; 1.4 m alley to the townhouse"),
        ("Env_House_Townhouse_B", 51.0, 44.8, 200, "north knot"),
        ("Env_House_Farmhouse_C", 41.0, 26.5, 100, "west knot, long side to the terrace edge"),
        ("Env_House_Cottage_A", 40.0, 33.5, 140, "west knot; 1.9 m alley to the farmhouse"),
        ("Env_House_Townhouse_B", 47.0, 10.0, 40, "south knot"),
        ("Env_House_Cottage_A", 56.0, 8.0, 350, "south knot"),
        ("Env_House_Farmhouse_C", 73.5, 23.0, 262, "east knot, backs onto the lab"),
        ("Env_House_Cottage_A", 77.0, 13.0, 300, "east knot"),
    ]
    for asset, x, z, yaw, note in houses:
        b.place(asset, "Town_House", P + "/Buildings", (x, ground_town(x, z), z),
                yaw=yaw, scale=1.0, force=True, notes=note)
        b.add_footprint(asset, x, ground_town(x, z), z, yaw)
    b.assert_clear(houses + [("Env_Building_PokeLab", 68.0, 34.0, 208, "lab")], "Town")

    # -- market row fronting the plaza lane, fanned rather than aligned
    for x, z, yaw in [(47.4, 24.2, 104), (46.6, 21.2, 96), (47.0, 18.0, 86)]:
        b.place("Env_Market_Stall", "Town_MarketStall", P + "/Props", (x, ground_town(x, z), z),
                yaw=yaw, scale=1.0, force=True)
    b.place("Env_Prop_CaptureBall", "Town_LabBallDisplay", P + "/Props",
            (65.1, ground_town(65.9, 25.9) + 1.11, 26.6), yaw=0, scale=1.0, force=True,
            layer="Interactable",
            notes="Sits on the research terminal deck beside the scanner; spins about its own "
                  "axis (pivot is the volume centre, so Y already includes the 0.055 m radius).")
    b.place("Env_Prop_CaptureBall_Open", "Town_MarketBallDisplay", P + "/Props",
            (48.3, ground_town(47.4, 24.2) + 1.02, 23.3), yaw=140, scale=1.0, force=True,
            layer="Interactable", notes="On the market stall counter.")
    b.place("Env_Well", "Town_Well", P + "/Props", (58.5, ground_town(58.5, 19.0), 19.0),
            yaw=24, scale=1.0, force=True, tag="Interactable",
            notes="Plaza centrepiece; the paved apron rings it.")

    # -- lamps along the street, alternating sides, spacing deliberately uneven
    for i, t in enumerate([0.07, 0.21, 0.36, 0.52, 0.67, 0.83, 0.95]):
        pt = _along(town_street, t)
        nx, nz = _normal(town_street, t)
        side = 1 if i % 2 == 0 else -1
        x = pt[0] + nx * side * (4.1 + rng.uniform(-0.3, 0.3))
        z = pt[2] + nz * side * (4.1 + rng.uniform(-0.3, 0.3))
        x, z = b.push_off(x, z, 0.7)
        b.place("Env_Lamp_Post", "Town_Lamp", P + "/Props", (x, ground_town(x, z), z),
                yaw=math.degrees(math.atan2(-nx * side, -nz * side)), scale=1.0,
                ignore_paths=True)
    for x, z in [(54.6, 23.4), (62.4, 16.4), (62.6, 24.4)]:
        x, z = b.push_off(x, z, 0.7)
        b.place("Env_Lamp_Post", "Town_Lamp", P + "/Props", (x, ground_town(x, z), z),
                yaw=rng.uniform(0, 360), scale=1.0, ignore_paths=True)

    sx, sz = b.push_off(37.6, 46.4, 1.9)
    b.place("Env_Signpost", "Town_Signpost", P + "/Props", (sx, ground_town(sx, sz), sz),
            yaw=52, scale=1.0, force=True, tag="Interactable",
            notes="At the ramp head: Route 1 west, town centre east.")
    sx, sz = b.push_off(69.0, 12.6, 1.9)
    b.place("Env_Signpost", "Town_Signpost", P + "/Props", (sx, ground_town(sx, sz), sz),
            yaw=240, scale=1.0, force=True, tag="Interactable")

    for x, z, yaw in [(56.2, 24.6, 210), (61.8, 17.2, 30), (46.2, 32.4, 120), (63.0, 22.0, 250)]:
        b.place_near("Env_Bench", "Town_Bench", P + "/Props", x, z, ground_town, 0.9,
                     yaw=yaw, scale=1.0)

    # -- planters, each seeded with flowers so none reads as an empty tub. push_off slides
    #    them clear of the street rather than my guessing every offset by hand.
    for x, z in [(54.2, 27.8), (57.2, 26.2), (62.0, 21.0), (64.4, 18.0), (48.8, 31.2),
                 (67.6, 14.2), (45.2, 21.6), (71.6, 27.4)]:
        rec, x, z = b.place_near("Env_Planter", "Town_Planter", P + "/Props", x, z,
                                 ground_town, 0.8, yaw=rng.uniform(0, 360), scale=1.0)
        if rec:
            for _ in range(3):
                b.place(rng.choice(FLOWERS), "Town_PlanterFlower", P + "/Foliage",
                        (x + rng.uniform(-0.25, 0.25), ground_town(x, z) + 0.72,
                         z + rng.uniform(-0.25, 0.25)),
                        scale=rng.uniform(0.85, 1.1), force=True)

    # -- crate piles tight against walls; anchors validated against the footprints above
    for cx, cz, n in [(50.0, 27.2, 5), (77.0, 18.6, 4), (44.6, 30.0, 3), (52.4, 10.4, 4),
                      (62.4, 39.6, 3)]:
        cx, cz = b.push_off(cx, cz, 0.9)
        for i in range(n):
            a = math.tau * i / n + rng.uniform(-0.12, 0.12)
            r = 0.70 + 0.30 * (i % 2)
            x, z = cx + r * math.cos(a), cz + r * math.sin(a)
            asset = rng.choice(["Env_Crate", "Env_Barrel", "Env_Crate"])
            y = ground_town(x, z)
            if i and rng.random() < 0.28:
                y += 0.66                                  # a stacked one
            b.place(asset, "Town_Crate", P + "/Props", (x, y, z), scale=rng.uniform(0.92, 1.08),
                    force=True)

    # -- broken fence runs. An unbroken 20 m picket line is the loudest regularity tell in
    #    dressed_town.png, so every run here drops roughly one module in four.
    b.fence_run([(38.0, 3, 24.0), (38.0, 3, 15.0)], None, P + "/Props", "Town_GardenFence",
                gap_chance=0.20, ground=ground_town)
    b.fence_run([(52.0, 3, 48.0), (62.0, 3, 47.0)], None, P + "/Props", "Town_GardenFence",
                gap_chance=0.28, ground=ground_town)
    b.fence_run([(81.0, 3, 18.0), (81.0, 3, 28.0)], None, P + "/Props", "Town_GardenFence",
                gap_chance=0.24, ground=ground_town)
    b.fence_run([(36.0, 3, 36.0), (35.6, 3, 27.0), (37.2, 3, 18.0)], None, P + "/Props",
                "Town_TerraceRail", gap_chance=0.34, ground=ground_town)

    # -- planting: garden corners, plus one big near-lens framer on the west lip
    b.place("Env_Tree_Broadleaf_C", "Town_FramingTree", P + "/Foliage",
            (40.5, ground_town(40.5, 47.0), 47.0), yaw=35, scale=1.05, force=True,
            notes="Foreground framing tree on the terrace's west lip -- a near-lens silhouette "
                  "in every shot looking west across the lake.")
    for x, z in [(58.0, 44.0), (67.5, 41.0), (44.0, 15.5), (64.5, 6.0), (81.5, 32.0),
                 (43.0, 43.0), (74.0, 32.5), (70.0, 20.0), (84.5, 20.0), (84.0, 40.0),
                 (78.0, 44.0), (85.0, 8.0), (68.0, 44.5)]:
        b.cluster(TREE_BROADLEAF + TREE_BIRCH, "Town_Tree", P + "/Foliage",
                  (x, 0, z), 2.8, rng.randint(2, 4), ground=ground_town, scale_range=(0.88, 1.12))
        b.cluster(BUSHES + FERNS, "Town_Shrub", P + "/Foliage", (x, 0, z), 3.6,
                  rng.randint(4, 7), ground=ground_town)
        b.cluster(GRASS + FLOWERS, "Town_GroundCover", P + "/Foliage", (x, 0, z), 4.6,
                  rng.randint(8, 14), ground=ground_town)

    # -- layered planting at every building base: bare ground against a wall reads unfinished
    for asset, x, z, yaw, _ in houses:
        for _ in range(3):
            a = math.radians(yaw + rng.uniform(50, 310))
            r = 3.8 + rng.uniform(0, 1.6)
            sx, sz = x + r * math.cos(a), z + r * math.sin(a)
            b.cluster(BUSHES, "Town_WallShrub", P + "/Foliage", (sx, 0, sz), 1.6, 2,
                      ground=ground_town)
            b.cluster(GRASS + FLOWERS, "Town_WallTuft", P + "/Foliage", (sx, 0, sz), 2.6, 6,
                      ground=ground_town)

    # -- worn edges everywhere a paved boundary meets ground
    b.edge_break(town_street, None, P + "/Detail", "Town_StreetEdge", 110, spread=3.2,
                 ground=ground_town)
    b.edge_break(plaza_link, None, P + "/Detail", "Town_LaneEdge", 34, spread=2.8,
                 ground=ground_town)
    b.edge_break(lab_approach, None, P + "/Detail", "Town_ApproachEdge", 22, spread=2.4,
                 ground=ground_town)
    b.cluster(SCATTER + GRASS, "Town_PlazaEdge", P + "/Detail", (PLAZA[0], 0, PLAZA[1]), 10.5,
              46, ground=ground_town, falloff=0.6)

    # -- fill the terrace's remaining open ground so no screenful is bare
    for _ in range(430):
        x = rng.uniform(35, 88)
        z = rng.uniform(2, 49)
        b.place(rng.choice(GRASS + GRASS + FLOWERS + SCATTER + FERNS), "Town_Scatter",
                P + "/Detail", (x, ground_town(x, z), z), scale=rng.uniform(0.8, 1.15))

    # -- the terrace edge itself. cliff_run puts the rock face on the LEFT of travel, so the
    #    boundary is walked clockwise (X right, Z up) and every face points away from town.
    b.cliff_run([(34.0, 46.0), (44.0, 50.0), (62.0, 50.0), (78.0, 46.0), (88.0, 38.0)], 0.0,
                P + "/Terrain", "Town_NorthCliff")
    # Stops at Z=40: the gap from Z=40 to the north run's start at Z=46 is the ramp mouth,
    # the one break in the terrace wall and the only way up from Route 1.
    b.cliff_run([(88.0, 2.0), (56.0, 0.0), (40.0, 4.0), (34.0, 14.0), (34.0, 40.0)], 0.0,
                P + "/Terrain", "Town_TerraceCliff")
    b.drift(BOULDERS + SCATTER, "Town_CliffCap", P + "/Detail", (35.4, 3, 44), (35.4, 3, 16), 16,
            1.6, ground=ground_town, tilt=8.0)
    b.drift(BUSHES + GRASS + FERNS, "Town_CliffCapGreen", P + "/Foliage",
            (35.4, 3, 45), (35.4, 3, 15), 38, 2.2, ground=ground_town)
    for k in range(9):
        z = 37 - k * 2.4 + rng.uniform(-0.6, 0.6)
        b.place(rng.choice(VINES), "Town_CliffVine", P + "/Foliage", (34.1, 2.85, z),
                yaw=270 + rng.uniform(-12, 12), scale=rng.uniform(0.85, 1.15), force=True,
                notes="Top-anchored; hangs down the terrace cliff face.")

    # =======================================================================
    # ROUTE -- the connective walk. Encounter territory, framing, ledges.
    # =======================================================================
    P = "Route"
    b.stepping_path(route_spine, None, P + "/Ground", "Route_Track", spacing=1.9,
                    ground=ground_route)
    b.stepping_path(lake_spur, None, P + "/Ground", "Route_SpurTrack", spacing=2.1,
                    ground=y_lake_spur)

    # -- north side: a dense mass that closes the map edge and gives the skyline its silhouette
    north_edge = [(38, 0, 50), (28, 0, 48), (18, 0, 46), (8, 0, 44), (-2, 0, 42),
                  (-12, 0, 41), (-22, 0, 42)]
    b.treeline(TREE_CONIFER * 3 + TREE_BROADLEAF * 2, "Route_NorthWall", P + "/Foliage",
               north_edge, 74, 5.5, ground=ground_route, scale_range=(0.9, 1.15))
    b.treeline(TREE_CONIFER * 2 + TREE_BIRCH, "Route_NorthWallBack", P + "/Foliage",
               [(40, 0, 58), (20, 0, 55), (0, 0, 52), (-20, 0, 50)], 58, 6.0,
               ground=ground_route, scale_range=(0.95, 1.15))
    # -- south side: lighter birch groups, deliberately gappy so the lake shows through
    for cx, cz, r, n in [(26, 30, 4.2, 7), (14, 27, 4.8, 8), (0, 25, 4.4, 7), (-14, 24, 5.2, 8)]:
        b.cluster(TREE_BIRCH + TREE_BROADLEAF, "Route_SouthCopse", P + "/Foliage",
                  (cx, 0, cz), r, n, ground=ground_route, scale_range=(0.88, 1.14))
        b.cluster(BUSHES + FERNS, "Route_CopseShrub", P + "/Foliage", (cx, 0, cz), r + 1.8,
                  rng.randint(6, 10), ground=ground_route)
        b.cluster(GRASS + FLOWERS, "Route_CopseCover", P + "/Foliage", (cx, 0, cz), r + 3.0,
                  rng.randint(12, 18), ground=ground_route)

    # -- near-lens framing: big trees planted 2.5-4 m off the path edge, alternating sides,
    #    so the DOF foreground fringe always has an occupant
    framers = [(33, 44.0, 1), (24.5, 33.0, -1), (17.5, 41.5, 1), (9.0, 29.5, -1),
               (1.5, 37.5, 1), (-7.5, 26.5, -1), (-16.0, 36.0, 1), (-23.0, 27.0, -1)]
    for x, z, _side in framers:
        asset = rng.choice(["Env_Tree_Broadleaf_C", "Env_Tree_Birch_C", "Env_Tree_Willow_C",
                            "Env_Tree_Broadleaf_B"])
        b.place(asset, "Route_Framer", P + "/Foliage", (x, ground_route(x, z), z),
                yaw=rng.uniform(0, 360), scale=rng.uniform(1.0, 1.15),
                notes="Near-lens framing element, 2.5-4 m off the path edge.")
        b.cluster(BUSHES + FERNS, "Route_FramerSkirt", P + "/Foliage", (x, 0, z), 2.8, 5,
                  ground=ground_route)
        b.cluster(GRASS, "Route_FramerTuft", P + "/Foliage", (x, 0, z), 3.4, 8,
                  ground=ground_route)

    # -- tall grass: encounter territory. Placed in bays formed by the tree lines so the
    #    player reads "this is where things live" before stepping in.
    grass_patches = [
        ("Route_TallGrass_A", 17.0, 0.0, 44.0, 5.4, 4.2, 96),
        ("Route_TallGrass_B", -2.0, 0.0, 24.0, 6.4, 4.8, 120),
        ("Route_TallGrass_C", -15.0, 0.0, 39.0, 4.8, 4.0, 82),
        ("Route_TallGrass_D", 26.5, 0.0, 31.5, 3.4, 2.8, 44),
        ("Route_TallGrass_E", 23.5, 0.0, 45.5, 3.2, 2.6, 42),
    ]
    tall_grass_records = []
    for name, cx, _cy, cz, rx, rz, n in grass_patches:
        placed = 0
        for _ in range(n * 3):
            if placed >= n:
                break
            a = rng.uniform(0, math.tau)
            rr = rng.random() ** 0.55
            x = cx + math.cos(a) * rx * rr
            z = cz + math.sin(a) * rz * rr
            if b.place(rng.choice(GRASS_TALL), name, P + "/TallGrass",
                       (x, ground_route(x, z), z), scale=rng.uniform(1.02, 1.15)):
                placed += 1
        # 10-tri single blades packed between the clumps. This is what the asset is for,
        # and it is the cheapest density in the kit: 90 blades cost 900 triangles.
        for _ in range(90):
            a = rng.uniform(0, math.tau)
            rr = rng.random() ** 0.5
            x = cx + math.cos(a) * rx * rr
            z = cz + math.sin(a) * rz * rr
            b.place("Env_Grass_Blade", name + "_Blade", P + "/TallGrass",
                    (x, ground_route(x, z), z), scale=rng.uniform(0.9, 1.15), force=True)
        # a ring of shorter tufts and flowers so the patch has a soft edge, not a stencil
        b.cluster(GRASS + FLOWERS + FERNS, name + "_Fringe", P + "/Foliage",
                  (cx, 0, cz), max(rx, rz) + 2.4, int(n * 0.4), ground=ground_route,
                  falloff=0.45)
        tall_grass_records.append({
            "name": name, "centre": [cx, ground_route(cx, cz), cz],
            "size": [rx * 2 + 1.2, 2.0, rz * 2 + 1.2], "clumps": placed,
        })

    # -- rock outcrops, each one a real cluster with a big anchor stone
    for cx, cz, r in [(22.0, 42.0, 3.2), (6.0, 39.5, 2.8), (-9.5, 36.5, 3.4),
                      (-21.0, 27.0, 3.0), (12.5, 22.5, 3.6)]:
        anchor = rng.choice(["Env_Rock_Boulder_D", "Env_Rock_Boulder_B"])
        b.place(anchor, "Route_Outcrop", P + "/Terrain", (cx, ground_route(cx, cz), cz),
                yaw=rng.uniform(0, 360), scale=rng.uniform(0.95, 1.15), tilt=5.0, force=True)
        b.cluster(BOULDERS_SMALL, "Route_OutcropRock", P + "/Terrain", (cx, 0, cz), r,
                  rng.randint(4, 7), ground=ground_route, tilt=7.0)
        b.cluster(SCATTER, "Route_OutcropScatter", P + "/Detail", (cx, 0, cz), r + 1.6,
                  rng.randint(4, 7), ground=ground_route, tilt=9.0)
        b.cluster(GRASS + FERNS + BUSHES, "Route_OutcropGreen", P + "/Foliage", (cx, 0, cz),
                  r + 2.4, rng.randint(7, 12), ground=ground_route)

    # -- ledges to hop: the lake spur drops through two one-way ledges
    # A cliff module is 3.0 m tall, so a 1 m hop-down means burying 2 m of it: base at -3.0
    # puts the cap at 0.0 (route floor) and shows 1.0 m of face to the spur below at -1.0.
    # These deliberately cross the spur -- that is what a Pokemon ledge is.
    b.cliff_run([(9.0, 26.0), (2.0, 27.0)], -3.0, P + "/Terrain", "Route_Ledge",
                modules=("Env_Cliff_Wall_2m",))
    b.cliff_run([(11.0, 18.0), (3.5, 19.0)], -3.0, P + "/Terrain", "Route_Ledge",
                modules=("Env_Cliff_Wall_2m",))
    for x, z in [(2.0, 27.0), (9.0, 26.0), (3.5, 19.0), (11.0, 18.0)]:
        b.cluster(GRASS + SCATTER, "Route_LedgeLip", P + "/Detail", (x, 0, z), 1.8, 6,
                  ground=ground_route)

    # -- fence line and signage
    b.fence_run([(33, 0, 45.5), (24, 0, 44.0), (16, 0, 42.5)], None, P + "/Props",
                "Route_FenceLine", gap_chance=0.30, ground=ground_route)
    b.fence_run([(-6, 0, 36.0), (-13, 0, 35.5)], None, P + "/Props", "Route_FenceLine",
                gap_chance=0.26, ground=ground_route)
    b.place("Env_Signpost", "Route_Signpost", P + "/Props", (4.5, ground_route(4.5, 34.5), 34.5),
            yaw=118, scale=1.0, force=True, tag="Interactable",
            notes="At the fork: lake south, cave west, town east.")
    b.place("Env_Signpost", "Route_Signpost", P + "/Props",
            (-24.5, ground_route(-24.5, 33.8), 33.8), yaw=205, scale=1.0, force=True,
            tag="Interactable")

    # -- verges: flowers on the sunny south side, ferns on the shaded north
    b.treeline(FLOWERS, "Route_VergeFlower", P + "/Foliage", route_spine, 130, 4.5,
               ground=ground_route, side=-1)
    b.treeline(FERNS + BUSHES, "Route_VergeFern", P + "/Foliage", route_spine, 90, 4.0,
               ground=ground_route, side=1)
    b.treeline(GRASS, "Route_VergeGrass", P + "/Foliage", route_spine, 210, 5.5,
               ground=ground_route)
    b.edge_break(route_spine, None, P + "/Detail", "Route_TrackEdge", 130, spread=2.6,
                 ground=ground_route)
    b.edge_break(lake_spur, None, P + "/Detail", "Route_SpurEdge", 55, spread=2.4,
                 ground=y_lake_spur)

    # -- fill the remaining bare route floor: no square metre of visible ground is empty
    for _ in range(340):
        x = rng.uniform(-30, 38)
        z = rng.uniform(20, 50)
        b.place(rng.choice(GRASS + GRASS + FLOWERS + SCATTER + FERNS), "Route_Scatter",
                P + "/Detail", (x, ground_route(x, z), z), scale=rng.uniform(0.8, 1.15))

    # =======================================================================
    # LAKESIDE -- water at -2.0, a graded shore band, bridge, stepping stones, waterfall.
    # The shoreline is the one place in the slice where the tree line is allowed to open
    # right up: the view east across the water to the lab dome on its terrace is the
    # postcard shot this layout is built around, so nothing tall stands in it.
    # =======================================================================
    P = "Lakeside"
    b.stepping_path(shore_walk, None, P + "/Ground", "Shore_Track", spacing=2.2,
                    ground=y_shore_walk)
    b.stepping_path(east_bank, None, P + "/Ground", "EastBank_Track", spacing=2.4,
                    ground=y_east_bank)

    def lake_edge_point(a, inset=0.0):
        """Wobbled ellipse so the waterline is never a drawn oval."""
        return (LAKE_C[0] + (LAKE_RX - inset) * math.cos(a) * (1 + 0.10 * math.sin(3 * a)),
                LAKE_C[1] + (LAKE_RZ - inset) * math.sin(a) * (1 + 0.08 * math.cos(2 * a)))

    # -- riverbank modules line the water on the arcs the player can reach
    for arc_start, arc_end in [(0.30, 1.45), (2.10, 3.25), (3.70, 4.95), (5.30, 6.10)]:
        pts = [lake_edge_point(arc_start + (arc_end - arc_start) * k / 14, inset=0.3)
               for k in range(15)]
        b.cliff_run(pts, -2.0, P + "/Terrain", "Lake_Riverbank",
                    modules=("Env_Riverbank_4m", "Env_Riverbank_2m"))

    # -- reeds: heavy and clustered at the waterline. This is what sells a shoreline.
    for a in [0.35, 0.62, 0.95, 1.25, 1.6, 2.2, 2.55, 2.9, 3.35, 3.9, 4.25, 4.6, 4.95, 5.4,
              5.75, 6.1]:
        ex, ez = lake_edge_point(a, inset=0.6)
        b.cluster(REEDS, "Lake_Reeds", P + "/Foliage", (ex, 0, ez), 2.6, rng.randint(6, 11),
                  y=Y_WATER + 0.05, scale_range=(0.9, 1.15))
        b.cluster(GRASS + REEDS, "Lake_ShoreTuft", P + "/Foliage", (ex, 0, ez), 4.0,
                  rng.randint(6, 10), ground=ground_shore)

    # -- lily pads in rafts, never sprinkled
    for a, r_in in [(0.5, 4.0), (2.4, 5.0), (3.6, 3.5), (5.2, 5.5), (1.1, 3.0), (4.4, 6.5)]:
        ex, ez = lake_edge_point(a, inset=r_in)
        b.cluster(LILYPADS, "Lake_Lilypads", P + "/Water", (ex, 0, ez), 3.4,
                  rng.randint(9, 16), y=Y_WATER + 0.02, scale_range=(0.85, 1.15))

    # -- willows own this shoreline; birch and broadleaf back them. The arc facing the town
    #    (roughly -60 to +40 degrees) is deliberately left low so the postcard view is open.
    for k in range(18):
        deg = 360.0 * k / 18
        ex, ez = lake_edge_point(math.radians(deg), inset=-4.5)
        open_view = (deg < 40 or deg > 320)
        if open_view:
            b.cluster(BUSHES + FERNS + GRASS, "Lake_OpenBank", P + "/Foliage", (ex, 0, ez), 5.0,
                      rng.randint(10, 16), ground=ground_shore)
            b.cluster(SCATTER + BOULDERS_WET, "Lake_OpenRock", P + "/Detail", (ex, 0, ez), 4.5,
                      rng.randint(3, 6), ground=ground_shore, tilt=9.0)
            continue
        b.cluster(TREE_WILLOW * 3 + TREE_BROADLEAF + TREE_BIRCH, "Lake_ShoreTree",
                  P + "/Foliage", (ex, 0, ez), 3.8, rng.randint(3, 5),
                  ground=ground_shore, scale_range=(0.9, 1.15))
        b.cluster(BUSHES + FERNS, "Lake_ShoreShrub", P + "/Foliage", (ex, 0, ez), 4.6,
                  rng.randint(5, 9), ground=ground_shore)
        b.cluster(GRASS + FLOWERS, "Lake_ShoreCover", P + "/Foliage", (ex, 0, ez), 5.8,
                  rng.randint(10, 16), ground=ground_shore)
        b.cluster(SCATTER + BOULDERS_WET, "Lake_ShoreRock", P + "/Detail", (ex, 0, ez), 4.2,
                  rng.randint(2, 5), ground=ground_shore, tilt=8.0)

    # -- the bridge over the outflow, on the shore-walk-to-east-bank crossing
    b.place("Env_Bridge_Wood", "Lake_Bridge", P + "/Terrain", (23.4, -1.05, -7.5),
            yaw=272, scale=1.0, force=True, tag="Interactable",
            notes="5 m span across the outflow. Carries the loop from the shore walk onto the "
                  "east bank; deck top sits ~0.17 m above the abutments.")
    for ax, az in [(20.6, -7.2), (26.4, -7.9)]:
        b.cluster(BOULDERS_WET + SCATTER, "Lake_BridgeAbutment", P + "/Detail", (ax, 0, az),
                  2.4, 7, ground=ground_shore, tilt=9.0)
    b.cluster(REEDS, "Lake_BridgeReeds", P + "/Foliage", (23.0, 0, -4.6), 2.4, 9, y=Y_WATER + 0.05)
    b.cluster(REEDS, "Lake_BridgeReeds", P + "/Foliage", (24.5, 0, -10.5), 2.2, 7, y=Y_WATER + 0.05)

    # -- stepping stones across a southern shallow, just off the shore walk
    for x, z in [(-6.0, -20.6), (-9.4, -21.4), (-12.8, -21.8)]:
        b.place("Env_Stepping_Stones", "Lake_SteppingStones", P + "/Terrain",
                (x, Y_WATER + 0.06, z), yaw=190 + rng.uniform(-8, 8), scale=1.0, force=True)

    # -- waterfall off the massif's south shoulder into the lake's north-west
    b.place("Env_Waterfall_Shelf", "Lake_WaterfallShelf", P + "/Terrain", (-26.0, Y_TIER1, 6.0),
            yaw=134, scale=1.15, force=True, tag="Landmark",
            notes="Lip of a 6.5 m fall: water leaves at Y=4.5 and lands in the plunge pool at "
                  "Y=-2.0. The falling sheet, plunge ring and foam are shader/VFX work -- the "
                  "kit has no falling-water mesh. See terrain.waterfalls.")
    b.cluster(BOULDERS_WET, "Lake_PlungeRock", P + "/Terrain", (-24.6, 0, 2.6), 4.0, 7,
              y=Y_WATER + 0.1, tilt=10.0)
    b.cluster(SCATTER + BOULDERS_WET, "Lake_PlungeScatter", P + "/Detail", (-23.5, 0, 4.5), 6.0,
              14, ground=ground_shore, tilt=12.0)
    b.cluster(MOSS, "Lake_PlungeMoss", P + "/Detail", (-24.0, 0, 3.5), 6.0, 16,
              ground=ground_shore)
    b.cluster(FERNS + BUSHES, "Lake_PlungeGreen", P + "/Foliage", (-22.5, 0, 5.5), 6.5, 18,
              ground=ground_shore)

    # -- shore band fill: the sand-to-grass transition broken with scatter all the way round
    for _ in range(300):
        a = rng.uniform(0, math.tau)
        r = rng.uniform(0.6, 8.0)
        ex, ez = lake_edge_point(a, inset=-r)
        b.place(rng.choice(GRASS + GRASS + SCATTER + FLOWERS + REEDS), "Lake_ShoreScatter",
                P + "/Detail", (ex, ground_shore(ex, ez), ez), scale=rng.uniform(0.8, 1.15))
    b.edge_break(shore_walk, None, P + "/Detail", "Lake_WalkEdge", 90, spread=2.6,
                 ground=y_shore_walk)
    b.edge_break(east_bank, None, P + "/Detail", "Lake_EastBankEdge", 60, spread=2.6,
                 ground=y_east_bank)
    b.treeline(TREE_BROADLEAF + TREE_BIRCH, "Lake_EastBankTree", P + "/Foliage", east_bank,
               22, 5.0, ground=y_east_bank, scale_range=(0.9, 1.15))
    b.treeline(BUSHES + FERNS + GRASS, "Lake_EastBankGreen", P + "/Foliage", east_bank,
               80, 5.5, ground=y_east_bank)

    # =======================================================================
    # CAVE -- a mountain with a hole in it, then an enclosed chamber.
    #
    # dressed_cave.png put the arch on flat grass with a cliff standing beside it, so daylight
    # showed straight through and it read as a folly. Here the massif is built as three
    # CLOSED, stacked rings that step inward as they rise, the outer face is split so the
    # mouth is a genuine gap in it, and the chamber is carved inside the footprint.
    # cliff_run puts the face on the LEFT of travel, so outward-facing rings run clockwise
    # (X right, Z up) and the inward-facing chamber wall runs counter-clockwise.
    # =======================================================================
    P = "Cave"
    b.cliff_run([(-30.0, 52.0), (-32.0, 40.0), (-32.0, 35.5)], Y_CAVE,
                P + "/Terrain", "Cave_MassifFace")
    b.cliff_run([(-32.0, 28.5), (-32.0, 22.0), (-30.0, 10.0), (-28.0, 2.0)], Y_CAVE,
                P + "/Terrain", "Cave_MassifFace")

    massif_ring = [(-30.0, 50.0), (-31.0, 34.0), (-30.0, 20.0), (-28.0, 4.0), (-44.0, 0.0),
                   (-58.0, 8.0), (-64.0, 26.0), (-62.0, 44.0), (-52.0, 56.0), (-38.0, 58.0),
                   (-30.0, 50.0)]
    tier_ring = [(-34.0, 46.0), (-35.0, 32.0), (-34.0, 18.0), (-46.0, 12.0), (-56.0, 20.0),
                 (-58.0, 36.0), (-52.0, 50.0), (-42.0, 52.0), (-34.0, 46.0)]
    ridge_ring = [(-40.0, 40.0), (-41.0, 28.0), (-48.0, 24.0), (-54.0, 32.0), (-52.0, 44.0),
                  (-44.0, 46.0), (-40.0, 40.0)]
    # Bases start at the chamber ceiling and above, so the mountain rises OVER the tunnel
    # instead of through it.
    b.cliff_run(massif_ring, Y_CAVE_CEIL, P + "/Terrain", "Cave_MassifTier2",
                modules=("Env_Cliff_Wall_Tall_4m", "Env_Cliff_Wall_6m", "Env_Cliff_Wall_4m"))
    b.cliff_run(tier_ring, Y_TIER2, P + "/Terrain", "Cave_Skyline",
                modules=("Env_Cliff_Wall_Tall_4m", "Env_Cliff_Wall_6m"))
    b.cliff_run(ridge_ring, Y_SKYLINE, P + "/Terrain", "Cave_Ridge",
                modules=("Env_Cliff_Wall_Tall_4m", "Env_Cliff_Wall_4m"))

    b.place("Env_Cave_Arch", "Cave_Arch", P + "/Terrain", (-31.5, Y_CAVE, 32.0),
            yaw=90, scale=1.15, force=True, tag="Landmark",
            notes="Entrance landmark: 6.3 x 5.0 m at 1.15 scale, facing +X (east) toward the "
                  "route. The outer face is split either side of it and three cliff rings stack "
                  "behind and above, so the opening reads as a hole in a mountain. Keep the "
                  "span clear -- no vines or grass inside the arch.")
    for x, z in [(-30.2, 36.6), (-30.4, 27.4)]:
        # A mossy boulder anchors each side of the mouth: it is the one asset that reads as
        # "damp, old, sheltered" at a glance, so it is placed rather than left to the sampler.
        b.place("Env_Rock_Boulder_Mossy_E", "Cave_MouthAnchorRock", P + "/Terrain",
                (x, ground_cave(x, z), z), yaw=rng.uniform(0, 360),
                scale=rng.uniform(0.95, 1.15), tilt=8.0, force=True)
        b.cluster(BOULDERS_MOSSY, "Cave_MouthRock", P + "/Terrain", (x, 0, z), 3.0, 4,
                  ground=ground_cave, tilt=9.0)
        b.cluster(MOSS, "Cave_MouthMoss", P + "/Detail", (x, 0, z), 3.4, 8, ground=ground_cave)
        b.cluster(FERNS + BUSHES, "Cave_MouthGreen", P + "/Foliage", (x, 0, z), 3.6, 7,
                  ground=ground_cave)
    for k in range(10):
        z = 38.0 - k * 1.15
        if 29.5 < z < 34.5:
            continue                                   # never curtain the doorway
        b.place(rng.choice(VINES), "Cave_MouthVine", P + "/Foliage",
                (-31.3, Y_CAVE + 4.9, z), yaw=90 + rng.uniform(-14, 14),
                scale=rng.uniform(0.85, 1.15), force=True,
                notes="Top-anchored; hangs from the massif lip beside the mouth.")

    # -- chamber. Floor 1.5, ceiling 6.5; walls run CCW so the face looks inward.
    interior_walls = [(-34.0, 22.0), (-36.0, 27.0), (-42.0, 31.0), (-48.0, 27.0),
                      (-55.0, 25.0), (-59.0, 29.0), (-59.0, 38.0), (-54.0, 43.0),
                      (-47.0, 41.0), (-40.0, 39.0), (-34.0, 39.0)]
    interior_walls = list(reversed(interior_walls))
    b.cliff_run(interior_walls, Y_CAVE, P + "/Terrain", "Cave_InteriorWall")
    b.cliff_run(interior_walls, Y_TIER1, P + "/Terrain", "Cave_InteriorWallUpper",
                modules=("Env_Cliff_Wall_2m", "Env_Cliff_Wall_4m"))

    for cx, cz in [(-38, 34), (-43, 29), (-43, 37), (-48, 31), (-49, 37), (-53, 29),
                   (-54, 38), (-57, 33), (-36, 36), (-40, 25)]:
        b.cluster(STALAGMITES, "Cave_Stalagmite", P + "/Terrain", (cx, 0, cz), 3.0,
                  rng.randint(3, 6), ground=ground_cave, scale_range=(0.85, 1.15))
        b.cluster(BOULDERS_MOSSY + BOULDERS_WET, "Cave_Rock", P + "/Terrain", (cx, 0, cz), 3.4,
                  rng.randint(2, 4), ground=ground_cave, tilt=8.0)
        b.cluster(["Env_Cave_Rubble"] * 3 + SCATTER, "Cave_Rubble", P + "/Detail", (cx, 0, cz),
                  4.0, rng.randint(4, 7), ground=ground_cave, tilt=6.0)
        b.cluster(MOSS, "Cave_Moss", P + "/Detail", (cx, 0, cz), 4.4, rng.randint(4, 8),
                  ground=ground_cave)
        for _ in range(rng.randint(3, 6)):
            x = cx + rng.uniform(-3.4, 3.4)
            z = cz + rng.uniform(-3.4, 3.4)
            b.place(rng.choice(STALACTITES), "Cave_Stalactite", P + "/Terrain",
                    (x, Y_CAVE_CEIL, z), yaw=rng.uniform(0, 360),
                    scale=rng.uniform(0.85, 1.15),
                    notes="Top-anchored; hangs from the 6.5 m ceiling.")

    # -- the underground pool: the one bright thing in the chamber
    b.cluster(BOULDERS_WET, "Cave_PoolRock", P + "/Terrain", (-51.0, 0, 34.0), 5.0, 8,
              y=Y_CAVE + 0.05, tilt=10.0)
    b.cluster(MOSS, "Cave_PoolMoss", P + "/Detail", (-51.0, 0, 34.0), 6.0, 12, ground=ground_cave)
    for k in range(14):
        a = math.tau * k / 14
        x, z = -51.0 + 5.4 * math.cos(a), 34.0 + 4.2 * math.sin(a)
        b.place(rng.choice(SCATTER + ["Env_Cave_Rubble"]), "Cave_PoolEdge", P + "/Detail",
                (x, ground_cave(x, z), z), yaw=rng.uniform(0, 360),
                scale=rng.uniform(0.8, 1.1), tilt=8.0)

    for _ in range(170):
        x = rng.uniform(-58, -35)
        z = rng.uniform(26, 39)
        b.place(rng.choice(SCATTER + ["Env_Cave_Rubble"] + MOSS), "Cave_FloorScatter",
                P + "/Detail", (x, ground_cave(x, z), z), scale=rng.uniform(0.8, 1.15), tilt=7.0)
    for _ in range(45):
        x = rng.uniform(-57, -36)
        z = rng.uniform(27, 38)
        b.place(rng.choice(STALACTITES), "Cave_CeilingSpike", P + "/Terrain",
                (x, Y_CAVE_CEIL, z), yaw=rng.uniform(0, 360), scale=rng.uniform(0.8, 1.1))

    # -- approach from the route
    b.stepping_path(cave_ramp, None, P + "/Ground", "Cave_RampTrack", spacing=1.5,
                    ground=y_cave_ramp)
    b.edge_break(cave_ramp, None, P + "/Detail", "Cave_RampEdge", 26, spread=2.2,
                 ground=y_cave_ramp)
    b.cluster(TREE_CONIFER, "Cave_ApproachTree", P + "/Foliage", (-27.0, 0, 41.0), 5.4, 9,
              ground=ground_route, scale_range=(0.95, 1.15))
    b.cluster(TREE_CONIFER, "Cave_ApproachTree", P + "/Foliage", (-25.0, 0, 22.0), 5.4, 9,
              ground=ground_route, scale_range=(0.95, 1.15))
    b.cluster(BOULDERS + SCATTER, "Cave_ApproachRock", P + "/Terrain", (-27.5, 0, 37.0), 4.5, 9,
              ground=ground_route, tilt=9.0)
    b.cluster(GRASS + FERNS + BUSHES, "Cave_ApproachGreen", P + "/Foliage", (-27.0, 0, 34.0),
              6.0, 24, ground=ground_route)


    return b, {
        "route_spine": route_spine, "lake_spur": lake_spur, "shore_walk": shore_walk,
        "town_street": town_street, "town_ramp": town_ramp, "cave_ramp": cave_ramp,
        "cave_walk": cave_walk, "plaza_link": plaza_link,
        "tall_grass": tall_grass_records,
        "east_bank": east_bank,
        "massif": {"base": massif_ring, "tier": tier_ring, "ridge": ridge_ring,
                   "interior": interior_walls},
        "lake": {"centre": LAKE_C, "rx": LAKE_RX, "rz": LAKE_RZ,
                 "edge": [lake_edge_point(math.tau * k / 44) for k in range(44)]},
    }


# ---------------------------------------------------------------------------
def _along(points, t):
    total = sum(math.dist(points[i][::2], points[i + 1][::2]) for i in range(len(points) - 1))
    target = total * t
    run = 0.0
    for i in range(len(points) - 1):
        seg = math.dist(points[i][::2], points[i + 1][::2])
        if run + seg >= target:
            f = (target - run) / max(seg, 1e-6)
            return tuple(points[i][k] + (points[i + 1][k] - points[i][k]) * f for k in range(3))
        run += seg
    return points[-1]


def _normal(points, t):
    total = sum(math.dist(points[i][::2], points[i + 1][::2]) for i in range(len(points) - 1))
    target = total * t
    run = 0.0
    for i in range(len(points) - 1):
        seg = math.dist(points[i][::2], points[i + 1][::2])
        if run + seg >= target or i == len(points) - 2:
            dx = points[i + 1][0] - points[i][0]
            dz = points[i + 1][2] - points[i][2]
            length = math.hypot(dx, dz) or 1.0
            return (-dz / length, dx / length)
        run += seg
    return (0.0, 1.0)


# ---------------------------------------------------------------------------
# Zones, gameplay anchors, ambient anchors, terrain spec
# ---------------------------------------------------------------------------
def zones_block():
    return [
        {
            "name": "Zone_Town", "kind": "Town", "biomeId": "town_01",
            "displayName": "Aster Town", "encounterRateMultiplier": 0.0,
            "roamerBudget": 2, "cameraRestDistance": 6.2,
            "ambience": {"isInterior": False, "ambienceKey": "Amb_TownMurmur",
                         "fogDensity": 0.006, "darkness": 0.0},
            "neighbours": ["Zone_Route"],
            "volumes": [
                {"name": "ZV_Town_Main", "centre": [61.0, 5.0, 25.0], "size": [54.0, 12.0, 46.0],
                 "priority": 0},
                {"name": "ZV_Town_Ramp", "centre": [34.0, 3.5, 43.0], "size": [10.0, 9.0, 8.0],
                 "priority": 1},
            ],
        },
        {
            "name": "Zone_Route", "kind": "Route", "biomeId": "route_01",
            "displayName": "Route 1", "encounterRateMultiplier": 1.0,
            "roamerBudget": 7, "cameraRestDistance": 5.5,
            "ambience": {"isInterior": False, "ambienceKey": "Amb_WindGrass",
                         "fogDensity": 0.009, "darkness": 0.0},
            "neighbours": ["Zone_Town", "Zone_Lakeside", "Zone_Cave"],
            "volumes": [
                {"name": "ZV_Route_Main", "centre": [2.0, 3.0, 36.0], "size": [68.0, 14.0, 28.0],
                 "priority": 0},
                {"name": "ZV_Route_Spur", "centre": [2.0, 2.0, 24.0], "size": [14.0, 14.0, 20.0],
                 "priority": 0},
            ],
        },
        {
            "name": "Zone_Lakeside", "kind": "Lakeside", "biomeId": "lakeside_01",
            "displayName": "Mirror Lake", "encounterRateMultiplier": 0.85,
            "roamerBudget": 6, "cameraRestDistance": 6.8,
            "ambience": {"isInterior": False, "ambienceKey": "Amb_WaterLapping",
                         "fogDensity": 0.012, "darkness": 0.0},
            "neighbours": ["Zone_Route"],
            "volumes": [
                {"name": "ZV_Lakeside_Basin", "centre": [-4.0, 1.0, -4.0],
                 "size": [50.0, 14.0, 44.0], "priority": 0},
                {"name": "ZV_Lakeside_Outflow", "centre": [27.0, 1.0, -8.0],
                 "size": [22.0, 12.0, 16.0], "priority": 0},
                {"name": "ZV_Lakeside_Falls", "centre": [-23.0, 1.0, 5.0],
                 "size": [14.0, 16.0, 14.0], "priority": 1},
            ],
        },
        {
            "name": "Zone_Cave", "kind": "Cave", "biomeId": "cave_01",
            "displayName": "Hollow Deep", "encounterRateMultiplier": 1.25,
            "roamerBudget": 6, "cameraRestDistance": 4.6, "suppressWeather": True,
            "ambience": {"isInterior": True, "ambienceKey": "Amb_CaveRumble",
                         "fogDensity": 0.035, "darkness": 0.62,
                         "ambientTint": [0.34, 0.40, 0.52, 1.0]},
            "neighbours": ["Zone_Route"],
            "volumes": [
                {"name": "ZV_Cave_Mouth", "centre": [-31.5, 3.5, 32.0], "size": [8.0, 6.0, 7.0],
                 "priority": 3, "transitionDurationOverride": 1.1,
                 "note": "Higher priority than the route so standing in the doorway does not "
                         "flicker between zones."},
                {"name": "ZV_Cave_Chamber", "centre": [-46.0, 4.0, 33.0],
                 "size": [28.0, 8.0, 18.0], "priority": 2},
            ],
        },
    ]


def terrain_block(meta):
    lake_poly = [[round(x, 2), round(z, 2)] for x, z in meta["lake"]["edge"]]
    return {
        "note": (
            "THE KIT SHIPS NO TERRAIN AND NO WATER MESH. All 85 assets are props, foliage, "
            "modular cliff/riverbank pieces and buildings; there is no ground plane among them. "
            "These decks must be authored by the integrator (Unity Terrain, ProBuilder, or "
            "imported planes) before any placement below will sit on anything. Deck surfaces "
            "carry PokeLabTerrainBlend; water carries PokeLabWater."
        ),
        "heightPlanes": {
            "lakeSurface": Y_WATER, "shoreBand": [Y_SHORE, -1.2], "routeFloor": Y_ROUTE,
            "caveFloor": Y_CAVE, "caveCeiling": Y_CAVE_CEIL, "townTerrace": Y_TOWN,
            "massifFaceTop": Y_TIER1, "skylineTierBase": Y_TIER2, "ridgeBase": Y_SKYLINE,
        },
        "heightFields": {
            "note": "Placement Y values were sampled from these; reproduce them or the props "
                    "will float or sink. Implemented in Tools/Level/build_layout.py.",
            "route": "y = 0.0 + 0.28*sin(0.075x) + 0.22*cos(0.09z)",
            "town": "y = 3.0 + 0.10*sin(0.16x) + 0.08*cos(0.19z)",
            "cave": "y = 1.5 + 0.13*sin(0.22x) + 0.11*cos(0.26z)",
            "shore": "grades from -1.5 at the waterline to 0.0 over an 8 m band; "
                     "y = -1.5 + t*(1.5) + 0.12*sin(0.3x), t = clamp((d-21)/8), "
                     "d = hypot(x+6, 1.15*(z+2))",
        },
        "decks": [
            {"name": "Deck_TownTerrace", "y": Y_TOWN, "material": "terrain_grass_dirt_paving",
             "polygon": [[34, 46], [44, 50], [62, 50], [78, 46], [88, 38], [88, 2], [56, 0],
                         [40, 4], [34, 14]],
             "note": "Cliff-edged on west and south; the cliff runs under Town/Terrain match it."},
            {"name": "Deck_RouteFloor", "y": "heightFields.route",
             "material": "terrain_grass_dirt",
             "polygon": [[34, 54], [-30, 52], [-34, 30], [-28, 16], [-14, 20], [2, 20],
                         [14, 22], [26, 26], [34, 30]],
             "note": "Meets Deck_TownTerrace at the X=34 cliff and Deck_ShoreBand along its "
                     "southern edge."},
            {"name": "Deck_ShoreBand", "y": "heightFields.shore", "material": "terrain_sand_grass",
             "polygon": [[8, 22], [22, 14], [30, 2], [34, -10], [24, -22], [4, -28],
                         [-14, -28], [-27, -18], [-31, -2], [-28, 12], [-14, 20]],
             "note": "A ring around the lake, 6-9 m wide, grading from -1.5 at the waterline to "
                     "0.0 where it meets Deck_RouteFloor. The seam is broken with scatter, not "
                     "butted."},
            {"name": "Deck_CaveFloor", "y": "heightFields.cave", "material": "terrain_cave_rock",
             "polygon": [[round(x, 1), round(z, 1)] for x, z in meta["massif"]["interior"]],
             "note": "The chamber floor, a void carved out of the massif mass. Ceiling deck at "
                     "y=6.5 over the same polygon, facing down."},
            {"name": "Deck_MassifTier1Top", "y": Y_CAVE_CEIL, "material": "terrain_cliff_rock",
             "polygon": [[round(x, 1), round(z, 1)] for x, z in meta["massif"]["base"]],
             "walkable": False,
             "note": "Top of the massif's first ring. NOT walkable and NOT on the NavMesh -- "
                     "it exists so the cliff modules based at 6.5 have ground under them and "
                     "the mountain reads as a solid mass rather than a floating wall."},
            {"name": "Deck_MassifTier2Top", "y": Y_TIER2, "material": "terrain_cliff_rock",
             "polygon": [[round(x, 1), round(z, 1)] for x, z in meta["massif"]["tier"]],
             "walkable": False},
            {"name": "Deck_MassifRidgeTop", "y": Y_SKYLINE, "material": "terrain_cliff_rock",
             "polygon": [[round(x, 1), round(z, 1)] for x, z in meta["massif"]["ridge"]],
             "walkable": False,
             "note": "The skyline. Pure background silhouette; the DOF far band starts here."},
            {"name": "Deck_CaveCeiling", "y": Y_CAVE_CEIL, "material": "terrain_cave_rock",
             "polygon": [[round(x, 1), round(z, 1)] for x, z in meta["massif"]["interior"]],
             "facesDown": True},
        ],
        "ramps": [
            {"name": "Ramp_TownFromRoute", "from": [30.0, 0.0, 42.0], "to": [38.0, 3.0, 44.0],
             "width": 5.0, "grade": "20 deg -- inside the 48 deg slope limit"},
            {"name": "Ramp_CaveFromRoute", "from": [-26.0, 0.0, 32.0], "to": [-31.0, 1.5, 32.0],
             "width": 5.0, "grade": "17 deg"},
            {"name": "Ramp_LakeSpur", "from": [4.0, 0.0, 32.5], "to": [8.5, -1.5, 10.0],
             "width": 4.0, "grade": "graded bank, under 8 deg, punctuated by two 1 m ledges"},
        ],
        "ledges": [
            {"name": "Ledge_Spur_Upper", "from": [2.0, -1.0, 27.0], "to": [9.0, -1.0, 26.0],
             "drop": 1.0, "oneWay": True,
             "note": "Hop south only. PlayerLocomotion max step is 0.45 m, so this is a real "
                     "barrier northbound."},
            {"name": "Ledge_Spur_Lower", "from": [3.5, -1.0, 19.0], "to": [11.0, -1.0, 18.0],
             "drop": 1.0, "oneWay": True},
        ],
        "water": [
            {"name": "Water_Lake", "surfaceY": Y_WATER, "bedY": -3.8,
             "material": "PokeLabWater", "layer": "Water", "polygon": lake_poly,
             "note": "Needs the opaque texture and depth texture on; see PokeLabWater."},
            {"name": "Water_Outflow", "surfaceY": -2.0, "bedY": -3.2, "material": "PokeLabWater",
             "layer": "Water",
             "polygon": [[16.8, -1.9], [36.8, -9.9], [35.2, -14.1], [15.2, -6.1]],
             "note": "The lake's outflow, ~4.5 m wide, running east off the map. Env_Bridge_Wood "
                     "crosses it at (23.4, -7.5); that crossing carries the loop from the shore "
                     "walk onto the east bank."},
            {"name": "Water_CavePool", "surfaceY": 1.05, "bedY": 0.3, "material": "PokeLabWater",
             "layer": "Water",
             "polygon": [[-56, 34], [-53, 30], [-48, 30], [-45.5, 34], [-48, 38], [-54, 38]],
             "note": "Still, dark, no wind. The one bright thing in the chamber."},
        ],
        "waterfalls": [
            {"name": "Waterfall_Main", "topY": Y_TIER1, "bottomY": Y_WATER,
             "top": [-26.0, 4.5, 6.0], "bottom": [-24.5, -2.0, 3.0], "width": 3.2,
             "dropMetres": 6.5,
             "note": "Env_Waterfall_Shelf provides the lip only. The falling sheet, the plunge "
                     "ring and the foam are shader/VFX work -- not in the kit."},
        ],
    }


def gameplay_block(meta):
    return {
        "playerSpawn": {
            "name": "Spawn_Player", "position": [64.4, 3.05, 24.6], "rotation": [0, 21, 0],
            "note": "On the lab forecourt, facing the lab door. First shot the player sees is "
                    "the dome filling the upper frame with the plaza falling away behind.",
        },
        "spawnPoints": [
            {"name": "Spawn_TownRampHead", "position": [38.0, 3.05, 44.0], "rotation": [0, 250, 0]},
            {"name": "Spawn_RouteFork", "position": [4.0, 0.14, 32.5], "rotation": [0, 270, 0]},
            {"name": "Spawn_LakeShore", "position": [0.4, -1.4, 17.5], "rotation": [0, 200, 0]},
            {"name": "Spawn_Bridge", "position": [23.4, -1.0, -7.5], "rotation": [0, 92, 0]},
            {"name": "Spawn_CaveMouth", "position": [-30.0, 1.55, 32.0], "rotation": [0, 270, 0]},
            {"name": "Spawn_CaveChamber", "position": [-40.0, 1.58, 33.0], "rotation": [0, 270, 0]},
        ],
        "healingMachine": {
            "name": "Anchor_HealingMachine", "prefab": ENV + "/Props/Env_Prop_HealingMachine.fbx",
            "position": [63.6, 3.06, 27.2], "rotation": [0, 208, 0],
            "interactAnchor": [63.2, 3.06, 26.2],
            "note": "Lab forecourt under the awning. The lab has no interior in this slice, so "
                    "healing and the scanner terminal are staged outdoors at the door.",
        },
        "researchTerminal": {
            "name": "Anchor_ResearchTerminal", "position": [65.9, 3.06, 25.9],
            "rotation": [0, 214, 0], "interactAnchor": [65.4, 3.06, 25.0],
        },
        "npcs": [
            {"name": "NPC_Townsfolk_Marketeer", "npcId": "npc_market_01",
             "displayName": "Stallholder", "position": [53.6, 3.05, 22.4], "rotation": [0, 276, 0],
             "schedule": [
                 {"startHour": 6.0, "activity": "Work", "waypoint": [53.6, 3.05, 22.4],
                  "wanderRadius": 0.8},
                 {"startHour": 19.0, "activity": "Walk", "waypoint": [58.5, 3.05, 20.0],
                  "wanderRadius": 2.5},
                 {"startHour": 22.0, "activity": "Idle", "waypoint": [49.5, 3.05, 15.5],
                  "wanderRadius": 1.0}]},
            {"name": "NPC_Townsfolk_WellKeeper", "npcId": "npc_well_01",
             "displayName": "Villager", "position": [60.8, 3.06, 19.6], "rotation": [0, 40, 0],
             "schedule": [
                 {"startHour": 8.0, "activity": "Idle", "waypoint": [60.8, 3.06, 19.6],
                  "wanderRadius": 2.0},
                 {"startHour": 13.0, "activity": "Walk", "waypoint": [46.5, 3.05, 30.0],
                  "wanderRadius": 3.0},
                 {"startHour": 18.0, "activity": "Idle", "waypoint": [57.5, 3.05, 25.5],
                  "wanderRadius": 1.2}]},
            {"name": "NPC_Townsfolk_LabAssistant", "npcId": "npc_lab_01",
             "displayName": "Lab Assistant", "position": [65.0, 3.06, 26.8], "rotation": [0, 30, 0],
             "schedule": [
                 {"startHour": 7.0, "activity": "Work", "waypoint": [65.9, 3.06, 25.9],
                  "wanderRadius": 1.2},
                 {"startHour": 12.0, "activity": "Walk", "waypoint": [60.0, 3.05, 24.0],
                  "wanderRadius": 2.0},
                 {"startHour": 20.0, "activity": "Idle", "waypoint": [68.0, 3.05, 30.0],
                  "wanderRadius": 1.0}]},
            {"name": "NPC_Townsfolk_Gardener", "npcId": "npc_garden_01",
             "displayName": "Gardener", "position": [39.8, 3.05, 24.0], "rotation": [0, 96, 0],
             "schedule": [
                 {"startHour": 6.5, "activity": "Work", "waypoint": [39.8, 3.05, 24.0],
                  "wanderRadius": 2.5},
                 {"startHour": 16.0, "activity": "Walk", "waypoint": [50.5, 3.05, 31.0],
                  "wanderRadius": 2.0}]},
            {"name": "NPC_Rival_RampHead", "npcId": "npc_rival_01", "displayName": "Rival",
             "position": [37.2, 3.05, 42.6], "rotation": [0, 232, 0],
             "schedule": [
                 {"startHour": 0.0, "activity": "Idle", "waypoint": [37.2, 3.05, 42.6],
                  "wanderRadius": 1.5}],
             "note": "Stands at the ramp head, the last thing the player passes leaving town."},
            {"name": "NPC_Fisher_Lakeside", "npcId": "npc_fisher_01", "displayName": "Angler",
             "position": [-2.0, -1.45, 15.6], "rotation": [0, 190, 0],
             "schedule": [
                 {"startHour": 5.0, "activity": "Work", "waypoint": [-2.0, -1.45, 15.6],
                  "wanderRadius": 1.0},
                 {"startHour": 21.0, "activity": "Idle", "waypoint": [3.5, -1.3, 14.0],
                  "wanderRadius": 1.5}]},
        ],
        "trainers": [
            {"name": "Trainer_Route_Youngster", "trainerId": "trainer_route_01",
             "position": [20.5, 0.18, 33.0], "rotation": [0, 20, 0], "sightRange": 11.0,
             "sightHalfAngle": 22.0,
             "note": "Sight cone points north across the path from the south verge; the player "
                     "walks into it between the first two grass patches."},
            {"name": "Trainer_Route_Camper", "trainerId": "trainer_route_02",
             "position": [-6.0, 0.11, 34.5], "rotation": [0, 168, 0], "sightRange": 12.0,
             "sightHalfAngle": 20.0,
             "patrol": [[-6.0, 0.11, 34.5], [-11.0, 0.10, 34.0]]},
            {"name": "Trainer_Lakeside_Swimmer", "trainerId": "trainer_lake_01",
             "position": [-15.0, -1.4, -19.5], "rotation": [0, 55, 0], "sightRange": 10.0,
             "sightHalfAngle": 24.0},
            {"name": "Trainer_Cave_Hiker", "trainerId": "trainer_cave_01",
             "position": [-41.0, 1.60, 33.5], "rotation": [0, 90, 0], "sightRange": 9.0,
             "sightHalfAngle": 26.0,
             "note": "Blocks the chamber's throat; unavoidable on the way west."},
        ],
        "tallGrassPatches": [
            {"name": p["name"], "centre": [round(p["centre"][0], 2), round(p["centre"][1], 2),
                                           round(p["centre"][2], 2)],
             "triggerSize": [round(p["size"][0], 2), 2.0, round(p["size"][2], 2)],
             "clumpCount": p["clumps"], "generatesEncounters": True, "setsTraversalState": True,
             "layer": "ZoneTrigger", "component": "TallGrassPatch"}
            for p in meta["tall_grass"]
        ],
        "waterBodies": [
            {"name": "WB_Lake", "component": "WaterBody", "surfaceHeight": Y_WATER,
             "overrideSurfaceHeight": True, "rideDepth": 0.35, "requiredItemId": "hm_surf",
             "triggerCentre": [-4.0, -2.6, -3.0], "triggerSize": [42.0, 2.4, 36.0],
             "layer": "ZoneTrigger"},
            {"name": "WB_Outflow", "component": "WaterBody", "surfaceHeight": -2.0,
             "overrideSurfaceHeight": True, "rideDepth": 0.35, "requiredItemId": "hm_surf",
             "triggerCentre": [26.0, -2.6, -8.0], "triggerSize": [22.0, 2.4, 12.0],
             "layer": "ZoneTrigger"},
            {"name": "WB_CavePool", "component": "WaterBody", "surfaceHeight": 1.05,
             "overrideSurfaceHeight": True, "rideDepth": 0.3, "requiredItemId": "hm_surf",
             "triggerCentre": [-50.5, 0.5, 34.0], "triggerSize": [11.0, 2.0, 8.0],
             "layer": "ZoneTrigger"},
        ],
        "roamerSpawnRegions": [
            {"name": "Roam_Route_North", "zone": "Zone_Route", "centre": [14.0, 0.2, 42.0],
             "size": [40.0, 4.0, 12.0], "species": [21, 25, 31, 49],
             "note": "Pidgey, Rattata, Pikachu, Oddish. Skittish under the tree line."},
            {"name": "Roam_Route_Grass", "zone": "Zone_Route", "centre": [-4.0, 0.2, 27.0],
             "size": [30.0, 4.0, 14.0], "species": [25, 49, 21]},
            {"name": "Roam_Lakeside_WestShore", "zone": "Zone_Lakeside",
             "centre": [-24.0, -1.2, -4.0], "size": [12.0, 4.0, 28.0], "species": [66, 10, 21],
             "note": "Poliwag hugs the waterline. Boxes cover the shore band only -- the "
                     "spawner NavMesh-samples inside them, so open water yields no placement."},
            {"name": "Roam_Lakeside_SouthShore", "zone": "Zone_Lakeside",
             "centre": [-6.0, -1.2, -22.0], "size": [30.0, 4.0, 10.0], "species": [66, 21, 25]},
            {"name": "Roam_Lakeside_EastBank", "zone": "Zone_Lakeside",
             "centre": [28.0, -0.6, 2.0], "size": [10.0, 4.0, 26.0], "species": [21, 25, 31]},
            {"name": "Roam_Lakeside_Falls", "zone": "Zone_Lakeside", "centre": [-23.0, -1.0, 5.0],
             "size": [12.0, 4.0, 12.0], "species": [66, 73]},
            {"name": "Roam_Cave_Chamber", "zone": "Zone_Cave", "centre": [-46.0, 1.7, 33.0],
             "size": [24.0, 4.0, 14.0], "species": [47, 81, 73, 100],
             "note": "Zubat, Geodude, Machop, Gastly."},
            {"name": "Roam_Cave_Mouth", "zone": "Zone_Cave", "centre": [-34.0, 1.7, 32.0],
             "size": [8.0, 4.0, 10.0], "species": [47, 81]},
            {"name": "Roam_Town_Fringe", "zone": "Zone_Town", "centre": [50.0, 3.2, 44.0],
             "size": [22.0, 4.0, 8.0], "species": [21, 25],
             "note": "Only the town's green fringe -- nothing roams the plaza."},
        ],
    }


def ambient_block():
    """Position and type for every VFX and audio emitter."""
    a = []

    def add(name, kind, pos, **kw):
        rec = {"name": name, "type": kind, "position": [round(v, 2) for v in pos]}
        rec.update(kw)
        a.append(rec)

    # -- waterfall
    add("Amb_Waterfall_Mist", "vfx.waterfall_mist", (-24.8, -1.6, 3.4), radius=6.0,
        intensity=1.0)
    add("Amb_Waterfall_Spray", "vfx.waterfall_spray", (-25.6, 1.2, 4.6), radius=3.0)
    add("Amb_Waterfall_Audio", "audio.loop", (-25.2, -1.0, 4.0), clip="Amb_Waterfall",
        minDistance=6.0, maxDistance=44.0, volume=0.9)
    add("Amb_Waterfall_Rainbow", "vfx.light_shaft", (-23.6, 0.5, 5.0), radius=4.0,
        note="Sun angle at midday throws a bow through the mist.")

    # -- lake
    for i, (x, z) in enumerate([(2.0, 15.5), (-15.0, 12.0), (-24.0, 0.0), (-16.0, -17.0),
                                (0.0, -21.5), (14.0, -15.0), (20.0, -6.0), (28.0, -9.0)]):
        add("Amb_Lake_Lapping_%02d" % (i + 1), "audio.loop", (x, -1.8, z),
            clip="Amb_WaterLapping", minDistance=4.0, maxDistance=22.0, volume=0.55)
    for i, (x, z) in enumerate([(-6.0, -9.0), (-15.0, -3.0), (2.0, -12.0), (-19.0, 7.0),
                                (8.0, 8.0)]):
        add("Amb_Lake_Firefly_%02d" % (i + 1), "vfx.fireflies", (x, -1.2, z), radius=7.0,
            activeTimes="Dusk|Night")
    for i, (x, z) in enumerate([(4.0, 4.0), (-10.0, -14.0), (-20.0, -8.0)]):
        add("Amb_Lake_Ripple_%02d" % (i + 1), "vfx.water_ripple", (x, -1.95, z), radius=5.0)
    add("Amb_Lake_SteppingRipple", "vfx.water_ripple", (-9.4, -1.94, -21.4), radius=3.0)
    add("Amb_Lake_Dragonflies", "vfx.insect_swarm", (-4.0, -1.3, -10.0), radius=8.0,
        activeTimes="Day")
    add("Amb_Lake_BridgeCreak", "audio.oneshot_zone", (23.4, -0.85, -7.5), clip="SFX_Foot_Wood_01",
        note="Triggered by footfall on the bridge deck, not looped.")

    # -- route
    for i, (x, z) in enumerate([(30.0, 40.0), (18.0, 36.0), (6.0, 33.0), (-8.0, 31.0),
                                (-20.0, 32.0)]):
        add("Amb_Route_Pollen_%02d" % (i + 1), "vfx.pollen_drift", (x, 1.2, z), radius=8.0,
            activeTimes="Dawn|Day")
    for i, (x, z, r) in enumerate([(17.0, 44.0, 6.5), (-2.0, 24.0, 7.5), (-15.0, 39.0, 5.5),
                                   (26.5, 31.5, 4.5), (30.0, 44.5, 4.0)]):
        add("Amb_Route_GrassWind_%02d" % (i + 1), "audio.loop", (x, 0.6, z), clip="Amb_WindGrass",
            minDistance=r, maxDistance=r * 3.2, volume=0.5)
        add("Amb_Route_GrassGust_%02d" % (i + 1), "vfx.wind_gust", (x, 0.4, z), radius=r,
            note="Ripples the foliage wind mask over the encounter patch -- the visual cue that "
                 "this ground is live.")
    for i, (x, z) in enumerate([(24.0, 30.0), (0.0, 25.0), (-14.0, 24.0), (12.0, 22.5)]):
        add("Amb_Route_Butterfly_%02d" % (i + 1), "vfx.butterflies", (x, 0.9, z), radius=5.0,
            activeTimes="Day", note="Over the flower clusters.")
    for i, (x, z) in enumerate([(28.0, 48.0), (8.0, 45.0), (-12.0, 42.0), (-24.0, 40.0)]):
        add("Amb_Route_Birdsong_%02d" % (i + 1), "audio.loop", (x, 4.0, z), clip="Amb_Birdsong",
            minDistance=8.0, maxDistance=34.0, volume=0.45, activeTimes="Dawn|Day")
        add("Amb_Route_CanopyShaft_%02d" % (i + 1), "vfx.light_shaft", (x, 3.0, z), radius=4.5,
            note="Dust in a gap in the canopy -- gives the DOF a mid-depth target.")
    for i, (x, z) in enumerate([(20.0, 34.0), (-6.0, 30.0)]):
        add("Amb_Route_NightInsects_%02d" % (i + 1), "audio.loop", (x, 1.0, z),
            clip="Amb_NightInsects", minDistance=10.0, maxDistance=40.0, volume=0.5,
            activeTimes="Dusk|Night")
    for i, (x, z) in enumerate([(22.0, 42.0), (-9.5, 36.5), (12.5, 22.5)]):
        add("Amb_Route_LeafFall_%02d" % (i + 1), "vfx.falling_leaves", (x, 4.5, z), radius=5.0)

    # -- town
    add("Amb_Town_Murmur", "audio.loop", (59.0, 4.0, 21.0), clip="Amb_TownMurmur",
        minDistance=10.0, maxDistance=42.0, volume=0.55, activeTimes="Day|Dusk")
    chimneys = [(48.0, 34.0), (56.5, 40.5), (44.5, 25.0), (49.5, 14.0), (58.0, 9.0),
                (74.5, 22.5), (78.0, 12.0)]
    for i, (x, z) in enumerate(chimneys):
        add("Amb_Town_ChimneySmoke_%02d" % (i + 1), "vfx.chimney_smoke", (x + 1.2, 8.4, z + 0.8),
            radius=1.2, activeTimes="Dawn|Dusk|Night",
            note="Approximate chimney head; nudge to the roof stack when the prefab is in scene.")
    lamps = [(41.0, 42.0), (48.5, 37.5), (55.0, 31.0), (58.5, 24.5), (64.0, 20.0),
             (69.5, 13.5), (73.0, 9.0), (56.5, 24.5), (62.0, 17.5)]
    for i, (x, z) in enumerate(lamps):
        add("Amb_Town_LampGlow_%02d" % (i + 1), "light.point", (x, 5.9, z), range=9.0,
            intensity=2.4, colour=[1.0, 0.86, 0.62], activeTimes="Dusk|Night",
            note="On the lamp head, 3.12 m post + terrace y.")
        add("Amb_Town_LampMoths_%02d" % (i + 1), "vfx.moths", (x, 5.6, z), radius=1.8,
            activeTimes="Night")
    add("Amb_Town_MarketChatter", "audio.loop", (52.6, 4.0, 22.0), clip="Amb_TownMurmur",
        minDistance=4.0, maxDistance=16.0, volume=0.4, activeTimes="Day")
    add("Amb_Town_WellDrip", "audio.loop", (59.0, 3.4, 21.0), clip="Amb_CaveDrips",
        minDistance=1.5, maxDistance=6.0, volume=0.3)
    add("Amb_Town_LabHum", "audio.loop", (68.0, 4.5, 34.0), clip="SFX_Scanner_ScanLoop",
        minDistance=3.0, maxDistance=14.0, volume=0.3,
        note="The lab is the only building with a machine note under it.")
    add("Amb_Town_TerraceWind", "audio.loop", (35.0, 4.5, 30.0), clip="Amb_WindHigh",
        minDistance=6.0, maxDistance=26.0, volume=0.4)
    for i, (x, z) in enumerate([(50.5, 43.0), (69.5, 8.5), (44.0, 18.5)]):
        add("Amb_Town_Butterfly_%02d" % (i + 1), "vfx.butterflies", (x, 3.8, z), radius=3.5,
            activeTimes="Day")

    # -- cave
    for i, (x, z) in enumerate([(-36.0, 34.0), (-41.0, 30.0), (-47.0, 37.0), (-52.0, 31.0),
                                (-56.0, 34.0), (-43.0, 38.0)]):
        add("Amb_Cave_Drip_%02d" % (i + 1), "vfx.cave_drip", (x, 6.3, z), radius=2.5)
        add("Amb_Cave_DripAudio_%02d" % (i + 1), "audio.loop", (x, 2.4, z), clip="Amb_CaveDrips",
            minDistance=3.0, maxDistance=14.0, volume=0.45)
    add("Amb_Cave_Rumble", "audio.loop", (-46.0, 3.0, 33.0), clip="Amb_CaveRumble",
        minDistance=8.0, maxDistance=40.0, volume=0.5)
    add("Amb_Cave_MouthShaft", "vfx.light_shaft", (-33.0, 4.0, 32.0), radius=5.0, intensity=1.4,
        note="The one strong light in the cave: daylight through the arch. Everything inside "
             "reads against this.")
    for i, (x, z) in enumerate([(-38.0, 34.0), (-44.0, 34.0)]):
        add("Amb_Cave_DustShaft_%02d" % (i + 1), "vfx.light_shaft", (x, 5.0, z), radius=3.0,
            intensity=0.5, note="Crack in the ceiling; keeps the deep chamber from going flat.")
    add("Amb_Cave_PoolGlow", "light.point", (-51.0, 1.8, 34.0), range=10.0, intensity=1.1,
        colour=[0.45, 0.78, 0.95], note="Cold bounce off the pool.")
    add("Amb_Cave_PoolRipple", "vfx.water_ripple", (-51.0, 1.06, 34.0), radius=4.5)
    add("Amb_Cave_BatFlutter", "vfx.bat_swarm", (-48.0, 5.4, 33.0), radius=6.0,
        note="Silhouette motion near the ceiling; sells the volume of the chamber.")
    add("Amb_Cave_Spores", "vfx.spore_drift", (-45.0, 2.4, 34.0), radius=8.0)

    # -- skyline
    for i, (x, z) in enumerate([(-44.0, 44.0), (-46.0, 24.0), (34.0, 50.0), (-40.0, 12.0)]):
        add("Amb_Cliff_WindHigh_%02d" % (i + 1), "audio.loop", (x, 14.0, z), clip="Amb_WindHigh",
            minDistance=10.0, maxDistance=48.0, volume=0.4)
    return a


def main():
    b, meta = build()
    os.makedirs(OUT_DIR, exist_ok=True)

    families = {}
    parents = {}
    for o in b.objects:
        fam = b.bounds[os.path.splitext(os.path.basename(o["prefab"]))[0]]["subfamily"]
        families[fam] = families.get(fam, 0) + 1
        parents[o["parent"]] = parents.get(o["parent"], 0) + 1

    ambient = ambient_block()
    doc = {
        "schema": "pokelab.level.layout/1",
        "generatedBy": "Tools/Level/build_layout.py",
        "seed": SEED,
        "units": "1 unit = 1 metre, Y up, models face +Z, pivots at the base, "
                 "modular pieces snapped to 0.5 m",
        "extents": {"minX": -64, "maxX": 90, "minZ": -28, "maxZ": 58,
                    "note": "154 x 86 m. Deliberately small; density beats footprint."},
        "layerContract": {
            "source": "Assets/Game/Scripts/Overworld/OverworldContracts.cs (OverworldNames)",
            "layers": ["Ground", "Environment", "Interactable", "Creature", "Water",
                       "ZoneTrigger"],
            "note": "There is no 'Foliage' layer in this project. All foliage, cliff and town "
                    "geometry goes on Environment; walkable decks on Ground; props the player "
                    "can talk to on Interactable; zone/grass/water triggers on ZoneTrigger.",
        },
        "camera": CAMERA,
        "terrain": terrain_block(meta),
        "zones": zones_block(),
        "gameplay": gameplay_block(meta),
        "ambientAnchors": ambient,
        "paths": [{"name": p["name"], "halfWidth": p["halfWidth"],
                   "points": [[round(v, 2) for v in pt] for pt in p["points"]]}
                  for p in b.paths],
        "counts": {
            "objects": len(b.objects),
            "bySubfamily": dict(sorted(families.items(), key=lambda kv: -kv[1])),
            "byParent": dict(sorted(parents.items())),
            "ambientAnchors": len(ambient),
            "rejectedPlacements": b.rejected,
        },
        "objects": b.objects,
    }
    with open(OUT_PATH, "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=1)
    print("wrote %s" % OUT_PATH)
    print("objects=%d  ambient=%d  rejected=%d" % (len(b.objects), len(ambient), b.rejected))
    for k, v in sorted(families.items(), key=lambda kv: -kv[1]):
        print("   %-12s %d" % (k, v))
    return doc


if __name__ == "__main__":
    main()
