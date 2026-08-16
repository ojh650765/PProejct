"""Flatten the designed level into meshes Unity can upload directly.

The previous version of this script emitted the terrain as `decks`: flat polygonal
plates, one per area, each held at a constant height or at a named analytic formula
transcribed into this file by hand. That is why the level looked like planes laid on
top of each other. The design has not been made of plates for some time — it is one
continuous height field, and `slice_layout.json` already ships the baked 115 x 105
grid that every object's Y was sampled from.

So this script no longer computes terrain height. It reads the baked grid and
interpolates it exactly the way `worldgen.BakedField.at` does, which is the function
the placement pass used. A prop and the ground beneath it therefore cannot disagree:
they are reading the same numbers through the same interpolation.

Two things are deliberately *not* separate geometry:

  Roads. The height field already conforms the terrain to each road's own elevation
  inside its width and feathers out over `edgeBlend + 0.7`, so the road surface *is*
  the terrain there. Laying a ribbon mesh on top would z-fight along 191 m of road.
  Roads are painted into the ground's vertex colours instead, which is what
  PokeLab/TerrainBlend's four weight channels exist for.

  Cliffs. The shader steers weight into its rock layer by slope on its own, and rock
  is the one layer it samples triplanar, so steep ground gets rock without stretching
  and without a second mesh to keep aligned with the first.

    python Tools/Level/emit_unity_layout.py
"""

import json
import math
import os
import random
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)

from worldgen import (chaikin, clamp, closest_on_polyline, contour, lerp,
                      point_in_poly, poly_area, poly_bounds, poly_sdf, resample)

SOURCE = os.path.join(ROOT, "Assets", "Game", "Data", "Levels", "slice_layout.json")
OUT_DIR = os.path.join(ROOT, "Assets", "Game", "Data", "Levels")
OUT = os.path.join(OUT_DIR, "slice_layout_unity.json")

# The slice is emitted as separate scenes rather than one map.
#
# The zones already have different rules -- the town has an encounter rate of zero and
# its own ambience, the route has trainers and grass -- and keeping them in one scene
# means one height field where the town's building pads and the route's cliffs fight
# over the same grid, one lighting rig for two moods, and every object in both loaded
# at once. The cut is the gate ramp, which the design already treats as the town's
# threshold: "the cut through the town's retaining cliff".
#
# The bands overlap by 4 m on purpose. The seam has to be crossed inside a transition
# volume, and terrain that stops exactly at the boundary shows the edge of the world
# from the other side.
SCENES = [
    ("Town",  -34.0,  8.0, "Zone_Town"),
    ("Field",  -2.0, 70.0, "Zone_Route"),
]

# Ground is split into chunks so the camera can frustum-cull it and so no single mesh
# carries the whole world. 32 m is a little over one screenful at the exploration
# camera's framing, which is the size at which culling starts paying for itself.
CHUNK_METRES = 32.0

# Vertex colour channels, matching PokeLab/TerrainBlend's documented convention.
GRASS, DIRT, SAND, ROCK = 0, 1, 2, 3

# Which weight channel each authored material name drives. Every surface material in
# the layout has to appear here; an unknown name is a hard error rather than a silent
# fallback to grass, because a road that quietly renders as lawn is exactly the kind
# of thing that survives review.
MATERIAL_LAYER = {
    "road_flagstone": ROCK,
    "road_cobble_town": ROCK,
    "road_dirt_town": DIRT,
    "road_dirt_route": DIRT,
    "trail_worn": DIRT,
    "shore_sand": SAND,
    "cave_gravel": DIRT,
    "terrain_grass": GRASS,
}


# --- reading the baked height grid ---------------------------------------------

class Grid:
    """Bilinear sampler over the baked height grid shipped in the layout.

    This mirrors `worldgen.BakedField.at` deliberately and exactly. It is not an
    independent implementation of the terrain: the grid it reads was produced by the
    same build that placed every object, so any difference here would show up as
    props floating or sunk.
    """

    def __init__(self, block):
        self.x0 = float(block["originX"])
        self.z0 = float(block["originZ"])
        self.step = float(block["step"])
        self.nx = int(block["countX"])
        self.nz = int(block["countZ"])
        self.rows = block["rows"]
        if len(self.rows) != self.nz or len(self.rows[0]) != self.nx:
            raise ValueError("height grid rows do not match countX/countZ")

    def at(self, x, z):
        fx = clamp((x - self.x0) / self.step, 0.0, self.nx - 1.001)
        fz = clamp((z - self.z0) / self.step, 0.0, self.nz - 1.001)
        ix, iz = int(fx), int(fz)
        tx, tz = fx - ix, fz - iz
        g = self.rows
        a = lerp(g[iz][ix], g[iz][ix + 1], tx)
        b = lerp(g[iz + 1][ix], g[iz + 1][ix + 1], tx)
        return lerp(a, b, tz)

    def normal(self, x, z):
        """Central-difference normal. Computed here, not by Unity, so that adjacent
        chunks agree along their shared edge — per-mesh RecalculateNormals would give
        each chunk a one-sided derivative there and leave a visible lighting seam."""
        h = self.step
        dx = (self.at(x + h, z) - self.at(x - h, z)) / (2.0 * h)
        dz = (self.at(x, z + h) - self.at(x, z - h)) / (2.0 * h)
        n = (-dx, 1.0, -dz)
        m = math.sqrt(n[0] * n[0] + 1.0 + n[2] * n[2]) or 1.0
        return (n[0] / m, n[1] / m, n[2] / m)

    def slope_degrees(self, x, z):
        h = self.step
        dx = (self.at(x + h, z) - self.at(x - h, z)) / (2.0 * h)
        dz = (self.at(x, z + h) - self.at(x, z - h)) / (2.0 * h)
        return math.degrees(math.atan(math.hypot(dx, dz)))


# --- surface weights ------------------------------------------------------------

def smoothstep(t):
    t = clamp(t)
    return t * t * (3.0 - 2.0 * t)


class SurfacePainter:
    """Resolves the four terrain weights at any point.

    Paints in the order the world was built: grass everywhere, then the broad
    surface patches, then the roads over the top of them. A road crossing the plaza
    is still a road, and the plaza is a widening of the street rather than a
    separate square, so that order is the one the design describes.
    """

    def __init__(self, layout):
        self.patches = []
        for p in layout["terrain"].get("surfacePatches", []):
            self.patches.append((
                [(float(a[0]), float(a[1])) for a in p["polygon"]],
                float(p.get("edgeBlend", 1.0)),
                layer_of(p["material"], p["name"]),
            ))

        self.roads = []
        for p in layout.get("paths", []):
            # Interior paths belong to a different scene and never touched this
            # terrain — `build_height_field` skips them when adding conforms. Painting
            # them here would run a gravel stripe across the mountainside directly
            # above the cave, following a floor the player cannot see from outside.
            if not p.get("conformsTerrain", True):
                continue
            # Resampled and smoothed exactly as build_height_field did, so the paint
            # lands on the corridor the terrain was actually flattened along. Using
            # the raw control points instead would drift off the road on every bend.
            pts = resample(chaikin([tuple(float(v) for v in c)
                                    for c in p["controlPoints"]], 3), 0.75)
            self.roads.append((
                pts,
                float(p["width"]) * 0.5,
                float(p.get("edgeBlend", 1.0)) + 0.7,
                layer_of(p["material"], p["name"]),
            ))

    def paved_clearance(self, x, z):
        """How far inside a paved surface this point is, in metres, or 0 if it is not.

        Foliage is scattered inside hand-drawn field polygons, and those polygons were
        drawn as regions — they overlap the roads that cross them. Nothing rejected the
        overlap, so grass grew out of the road surface. The road corridor is known here
        exactly (it is the same centreline the terrain was flattened along), so the
        scatter can just ask.
        """
        # Starts far outside, not at zero: this is a signed depth, and clamping the
        # floor to zero would report every point in the world as being on the kerb.
        best = -1e9
        for pts, half, _, _ in self.roads:
            d, _, _ = closest_on_polyline(x, z, pts)
            best = max(best, half - d)
        for poly, _, layer in self.patches:
            if layer in (ROCK, DIRT):
                best = max(best, -poly_sdf(x, z, poly))
        return best

    def at(self, x, z):
        w = [0.0, 0.0, 0.0, 0.0]
        w[GRASS] = 1.0

        for poly, blend, layer in self.patches:
            d = poly_sdf(x, z, poly)
            if d >= blend:
                continue
            t = 1.0 if d <= 0.0 else smoothstep(1.0 - d / blend)
            if t > 0.0:
                self._mix(w, layer, t)

        for pts, half, blend, layer in self.roads:
            d, _, _ = closest_on_polyline(x, z, pts)
            if d >= half + blend:
                continue
            t = 1.0 if d <= half else smoothstep(1.0 - (d - half) / blend)
            if t > 0.0:
                self._mix(w, layer, t)

        return w

    @staticmethod
    def _mix(w, layer, t):
        for i in range(4):
            w[i] *= (1.0 - t)
        w[layer] += t


def layer_of(material, owner):
    if material not in MATERIAL_LAYER:
        raise ValueError(
            "%s uses surface material %r, which is not mapped to a terrain weight "
            "channel. Add it to MATERIAL_LAYER — defaulting it to grass would render "
            "the surface as lawn and look like a placement bug." % (owner, material))
    return MATERIAL_LAYER[material]


# --- ground -----------------------------------------------------------------------

def build_ground(grid, painter, extents):
    """One continuous surface over the whole map, split into chunks.

    Vertices land exactly on the baked grid points, so the mesh passes through the
    same values `Grid.at` returns and props sit on it to the millimetre.
    """
    per = int(round(CHUNK_METRES / grid.step))
    chunks = []

    for cz in range(0, grid.nz - 1, per):
        for cx in range(0, grid.nx - 1, per):
            x1 = min(cx + per, grid.nx - 1)
            z1 = min(cz + per, grid.nz - 1)
            verts, norms, cols, uvs, tris = [], [], [], [], []
            index = {}

            def vertex(ix, iz):
                key = (ix, iz)
                got = index.get(key)
                if got is not None:
                    return got
                x = grid.x0 + ix * grid.step
                z = grid.z0 + iz * grid.step
                y = grid.rows[iz][ix]
                n = grid.normal(x, z)
                w = painter.at(x, z)

                # Rock on the steep faces, baked in rather than left entirely to the
                # shader's own slope term. The shader steers by the interpolated pixel
                # normal, which on a 1 m grid still leaves grass weight sitting on a
                # near-vertical cliff and shows as green mixed into the rock. Baking
                # it at the vertex settles what the surface *is*; the shader's term
                # then only refines the transition.
                slope = grid.slope_degrees(x, z)
                rock = smoothstep((slope - 34.0) / 20.0)
                if rock > 0.0:
                    for i in range(4):
                        w[i] *= (1.0 - rock)
                    w[ROCK] += rock
                index[key] = len(verts) // 3
                verts.extend((x, y, z))
                norms.extend(n)
                cols.extend(w)
                # World-space planar UVs. The shader projects its own layers from
                # world XZ, but Unity still needs a UV set to derive tangents from
                # for the normal maps.
                uvs.extend((x, z))
                return index[key]

            for iz in range(cz, z1):
                for ix in range(cx, x1):
                    a = vertex(ix, iz)
                    b = vertex(ix + 1, iz)
                    c = vertex(ix + 1, iz + 1)
                    d = vertex(ix, iz + 1)

                    # Which way to split the quad. Splitting every quad the same way
                    # puts a corduroy grain across open ground under a low sun, so
                    # the tie-break alternates — but alternating *unconditionally*,
                    # which this did, turns a cliff into a row of sawteeth: on an
                    # 80 degree face adjacent columns differ by metres, and swapping
                    # the diagonal every quad zigzags the fold line up and down the
                    # face instead of running it along the cliff.
                    #
                    # So the diagonal follows the terrain: join the pair of opposite
                    # corners whose heights are closest, which is the fold that lies
                    # in the surface rather than cutting across it. On level ground
                    # both diagonals are equal and the checkerboard tie-break stands.
                    ha, hb = grid.rows[iz][ix], grid.rows[iz][ix + 1]
                    hc, hd = grid.rows[iz + 1][ix + 1], grid.rows[iz + 1][ix]
                    ac = abs(ha - hc)
                    bd = abs(hb - hd)
                    if abs(ac - bd) < 1e-4:
                        split_ac = bool((ix + iz) & 1)
                    else:
                        split_ac = ac < bd

                    if split_ac:
                        tris.extend((a, c, b, a, d, c))
                    else:
                        tris.extend((a, d, b, b, d, c))

            if tris:
                chunks.append({
                    "name": "Ground_%02d_%02d" % (cx // per, cz // per),
                    "vertices": [round(v, 4) for v in verts],
                    "normals": [round(v, 4) for v in norms],
                    "colors": [round(v, 4) for v in cols],
                    "uvs": [round(v, 3) for v in uvs],
                    "triangles": tris,
                })
    return chunks


# --- water ------------------------------------------------------------------------

def triangulate(poly):
    """Ear clipping, which handles the concave lake and stream outlines.

    Clipping grid cells against the outline was tried and is wrong: Sutherland-Hodgman
    is only valid for a convex clip region, and on the concave cave outline it silently
    discarded 94% of the floor.
    """
    pts = list(poly)
    area = 0.0
    for i in range(len(pts)):
        x1, z1 = pts[i]
        x2, z2 = pts[(i + 1) % len(pts)]
        area += x1 * z2 - x2 * z1
    if area < 0:
        pts.reverse()

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    def inside(p, a, b, c):
        d1, d2, d3 = cross(a, b, p), cross(b, c, p), cross(c, a, p)
        neg = d1 < -1e-12 or d2 < -1e-12 or d3 < -1e-12
        pos = d1 > 1e-12 or d2 > 1e-12 or d3 > 1e-12
        return not (neg and pos)

    idx = list(range(len(pts)))
    tris = []
    guard = 0
    while len(idx) > 3 and guard < 20000:
        guard += 1
        clipped = False
        for k in range(len(idx)):
            i0, i1, i2 = idx[(k - 1) % len(idx)], idx[k], idx[(k + 1) % len(idx)]
            a, b, c = pts[i0], pts[i1], pts[i2]
            if cross(a, b, c) <= 1e-12:
                continue
            if any(inside(pts[m], a, b, c) for m in idx if m not in (i0, i1, i2)):
                continue
            tris.append((a, b, c))
            idx.pop(k)
            clipped = True
            break
        if not clipped:
            break
    if len(idx) == 3:
        tris.append((pts[idx[0]], pts[idx[1]], pts[idx[2]]))
    return tris


def subdivide(tri, max_edge):
    out, stack, guard = [], [tri], 0
    while stack and guard < 400000:
        guard += 1
        a, b, c = stack.pop()
        ab, bc, ca = math.dist(a, b), math.dist(b, c), math.dist(c, a)
        longest = max(ab, bc, ca)
        if longest <= max_edge:
            out.append((a, b, c))
            continue
        if longest == ab:
            m = ((a[0] + b[0]) * .5, (a[1] + b[1]) * .5)
            stack += [(a, m, c), (m, b, c)]
        elif longest == bc:
            m = ((b[0] + c[0]) * .5, (b[1] + c[1]) * .5)
            stack += [(b, m, a), (m, c, a)]
        else:
            m = ((c[0] + a[0]) * .5, (c[1] + a[1]) * .5)
            stack += [(c, m, b), (m, a, b)]
    return out


def mask_stream_channel(rows, x0, z0, step, line, half_width, lake_y):
    """Copy of the height grid with the stream's own channel raised out of reach.

    The lake outline is traced from wherever the terrain crosses the waterline, which
    is right — except that the stream's bed is cut 0.5 m below the stream's *own*
    surface, so upstream of the lake it is still below the lake's level. The trace
    followed it up the channel and claimed 20 of the stream's 77 points, leaving two
    water surfaces stacked over the same ground with the stream floating up to 0.49 m
    above the lake. Masking the channel where the stream still runs above lake level
    stops that without touching the honest shoreline anywhere else.
    """
    out = [list(r) for r in rows]
    for iz in range(len(out)):
        for ix in range(len(out[0])):
            x, z = x0 + ix * step, z0 + iz * step
            d, pt, _ = closest_on_polyline(x, z, line)
            if d <= half_width + 1.2 and pt[1] > lake_y + 0.05:
                out[iz][ix] = 99.0
    return out

def build_stream(body, grid, stop_polygon=None):
    """A flowing ribbon whose surface falls along its length.

    The stream has no single waterline: it drops 1.70 m over 66 m, so holding it at
    one Y would either bury it at the top or float it at the bottom. Each rung takes
    the Y authored at that point of the centreline.

    Each rung is also widened until its edge is *under* the bank. The channel is cut
    0.5 m below the water surface across the authored half width and only then
    feathers up, so a ribbon that stops at exactly that half width ends half a metre
    above the ground beneath it — the water and the land visibly come apart at any
    low angle. Marching outward until the terrain has risen past the waterline puts
    the seam inside the bank instead, where it cannot be seen.

    UV.y runs in metres along the flow so the water shader's scroll moves downstream
    at a constant speed regardless of how the rungs happen to be spaced.
    """
    line = [tuple(float(v) for v in p) for p in body["centreline"]]
    if len(line) < 2:
        return None
    half = float(body.get("halfWidth", 1.5))

    # Stop where the lake takes over. The stream's last rungs sit inside the lake
    # polygon at the same height, so both surfaces were drawn over the same water and
    # z-fought -- visible as a patchwork of slightly different tones near the mouth.
    # One overlapping rung is kept so the two meshes meet rather than leaving a seam.
    if stop_polygon:
        cut = len(line)
        for i, (x, _, z) in enumerate(line):
            if point_in_poly(x, z, stop_polygon):
                cut = min(len(line), i + 2)
                break
        line = line[:cut]
        if len(line) < 2:
            return None
    # Cap the search so a rung crossing an unusually flat bank cannot run away across
    # the meadow; past this the bank is too shallow for the trick to help anyway.
    # Kept short. The point is to bury the seam inside the bank, not to flood it: at
    # 3.5 m the ribbon marched out to 10.4 m wide wherever the bank was shallow, which
    # is wider than the bridge that has to span it.
    reach, step = 1.1, 0.2

    def edge(x, z, nx, nz, y):
        d = half
        while d < half + reach:
            d += step
            if grid.at(x + nx * d, z + nz * d) > y:
                break
        return d

    verts, uvs, tris = [], [], []
    run = 0.0
    widths = []
    for i, (x, y, z) in enumerate(line):
        if i > 0:
            run += math.hypot(x - line[i - 1][0], z - line[i - 1][2])
        # Tangent from the neighbours, so the ribbon does not pinch on bends.
        a = line[max(0, i - 1)]
        b = line[min(len(line) - 1, i + 1)]
        tx, tz = b[0] - a[0], b[2] - a[2]
        n = math.hypot(tx, tz) or 1.0
        nx, nz = -tz / n, tx / n

        left = edge(x, z, nx, nz, y)
        right = edge(x, z, -nx, -nz, y)
        widths.append(left + right)
        verts.extend((x + nx * left, y, z + nz * left,
                      x - nx * right, y, z - nz * right))
        uvs.extend((0.0, run, 1.0, run))
        if i > 0:
            k = (i - 1) * 2
            tris.extend((k, k + 2, k + 1, k + 1, k + 2, k + 3))

    return {
        "name": body["name"],
        "kind": body.get("kind", "flowing"),
        "surfaceY": round(sum(p[1] for p in line) / len(line), 4),
        "vertices": [round(v, 4) for v in verts],
        "uvs": [round(v, 3) for v in uvs],
        "triangles": tris,
        "widthMin": round(min(widths), 2),
        "widthMax": round(max(widths), 2),
    }


def build_water(layout, grid):
    """Flat surfaces at each body's authored waterline, plus the flowing stream.

    Subdivided even though they are flat, because the water shader displaces vertices
    for its swell and a two-triangle lake cannot show a wave.
    """
    out = []

    # Still bodies are traced first, so a flowing one can be told where to stop. The
    # stream's last rungs sit inside the lake at the same height, and drawing both
    # left two surfaces over the same water: a patchwork of slightly different tones
    # near the mouth, and holes wherever the two z-fought.
    still = {}
    for body in layout["terrain"].get("water", []):
        if "centreline" in body or "surfaceY" not in body:
            continue
        y = float(body["surfaceY"])
        rows = grid.rows
        for other in layout["terrain"].get("water", []):
            if "centreline" not in other:
                continue
            line = [tuple(float(v) for v in p) for p in other["centreline"]]
            rows = mask_stream_channel(rows, grid.x0, grid.z0, grid.step, line,
                                       float(other.get("halfWidth", 1.5)), y)
        still[body["name"]] = contour(rows, grid.x0, grid.z0, grid.step, y)

    receiving = next(iter(still.values()), None)

    for body in layout["terrain"].get("water", []):
        # Discriminated on the centreline, not on the absence of a polygon: the
        # stream ships both, and its polygon is only the ribbon's flat outline —
        # using it would hold a watercourse that falls 1.7 m at a single height.
        if "centreline" in body:
            strip = build_stream(body, grid, receiving)
            if strip:
                out.append(strip)
            continue

        y = float(body["surfaceY"])

        # Re-trace the shoreline from the baked height grid rather than trusting the
        # authored outline. The authored one was traced from the same isoline but then
        # masked to the lake bowl's own footprint, which cut it off part-way: about
        # 131 m2 of ground genuinely below the waterline sat outside it, so the water
        # surface ended in mid-slope with a dry pit beside it and its own edge standing
        # proud of the ground. The waterline is not a design choice — it is wherever the
        # terrain crosses this height — so it is derived here.
        traced = still.get(body["name"], [])
        poly = traced if len(traced) >= 3 else [
            (float(p[0]), float(p[1])) for p in body.get("polygon", [])]
        if len(poly) < 3:
            continue
        index, verts, uvs, tris = {}, [], [], []

        def vertex(x, z):
            key = (round(x, 3), round(z, 3))
            got = index.get(key)
            if got is not None:
                return got
            index[key] = len(verts) // 3
            verts.extend((x, y, z))
            uvs.extend((x, z))
            return index[key]

        for tri in triangulate(poly):
            for a, b, c in subdivide(tri, 1.5):
                tris.extend((vertex(*a), vertex(*c), vertex(*b)))

        if tris:
            out.append({
                "name": body["name"],
                "kind": body.get("kind", "still"),
                "surfaceY": y,
                "vertices": [round(v, 4) for v in verts],
                "uvs": [round(v, 3) for v in uvs],
                "triangles": tris,
            })
    return out


# --- the cave, which is a different scene ------------------------------------------

def in_scene(z, lo, hi):
    return lo <= z <= hi


def scene_of_parent(parent):
    """Which scene a layout parent path belongs to. Town is its own; everything else
    outdoors shares the field."""
    root = (parent or "").split("/")[0]
    return "Town" if root == "Town" else "Field"


def cave_interiors(layout):
    """Footprints of anything that lives inside a cave rather than on this terrain.

    Caves are their own scenes, entered through a trigger at the mouth, so nothing
    inside one belongs in the overworld build. This is not merely tidiness: the height
    grid describes the mountain's *outside*, so a cave prop emitted here is placed on
    the surface above the cave. `Grass_Cave_Grass` was landing at y = 20.15 on the
    massif's summit while its floor sits at y = 3.3.

    Filtering is geometric rather than by zone name because the gorge approach is
    tagged Zone_Cave too and is genuinely outdoors — it is the walk up to the mouth,
    and dropping it would delete the level's whole approach sequence.
    """
    return [([(float(p[0]), float(p[1])) for p in c["floorPolygon"]], c)
            for c in layout["terrain"].get("caves", [])]


def inside_any(x, z, polys, margin=0.0):
    """Inside a cave, optionally with a skirt around it.

    Foliage needs the skirt. A field drawn over the cave chamber spills a little
    past the floor polygon, and those stragglers land on the *outside* of the
    mountain — the height grid describes the massif's surface, so they end up on the
    hillside twenty metres above the chamber they belong to.
    """
    if margin <= 0.0:
        return any(point_in_poly(x, z, poly) for poly, _ in polys)
    return any(poly_sdf(x, z, poly) < margin for poly, _ in polys)


# Assets that stand at the threshold and belong to the *outside*. The cave filter
# would otherwise take them with the interior, and it did: Env_Cave_Arch is the
# doorway the player sees from the gorge, and holding it back left the level with no
# visible way onward at all — the transition trigger was there, but nothing to walk
# toward.
ENTRANCE_ASSETS = ("Env_Cave_Arch", "Env_Ramp_CaveFromRoute")

# Fixtures that belong indoors. They were placed on the plaza because there were no
# interiors to put them in -- the layout's own note on the healing machine says "on
# the plaza's east edge, in the same shot as the lab door" -- and a healing machine
# standing in the open street is a Pokemon Centre with no Centre around it. They are
# held back for the interior scenes the same way cave props are.
INTERIOR_PROPS = ("Env_Prop_HealingMachine", "Env_Prop_ResearchTerminal")


def is_interior_prop(prefab):
    return any(k in prefab for k in INTERIOR_PROPS)


def is_entrance(prefab):
    return any(k in prefab for k in ENTRANCE_ASSETS)


def cave_entrances(layout, grid):
    """Where the overworld hands off to a cave scene."""
    out = []
    for c in layout["terrain"].get("caves", []):
        mouth = c.get("mouth")
        if not mouth:
            continue
        mx, _, mz = [float(v) for v in mouth["position"]]
        out.append({
            "name": c["name"],
            "scene": "Cave_" + c["name"].replace("Cave_", ""),
            # Sat on the outside surface, not on the authored floor Y: the trigger
            # lives in the overworld and the player walks into it from the gorge.
            "position": [round(mx, 3), round(grid.at(mx, mz), 3), round(mz, 3)],
            "facingYaw": float(mouth.get("facingYaw", 0.0)),
            "size": [float(mouth.get("widthMetres", 4.0)),
                     float(mouth.get("heightMetres", 4.0)), 1.0],
        })
    return out


# --- collision ---------------------------------------------------------------------
#
# Nothing in the level had a collider except the ground, the water and the trigger
# volumes: every tree, house, rock and the bridge could be walked straight through.
# The decision of what blocks the player belongs with the rest of the placement
# policy rather than in the builder, so it is emitted per object.
#
#   "mesh"  a solid you walk around, collided against its real geometry
#   "trunk" a tree: a capsule on the stem only, so branches overhang a path
#           without blocking it, which a canopy-sized collider would
#   "none"  decoration you walk through

COLLIDER_TRUNK = ("Env_Tree_",)
COLLIDER_NONE = ("Env_Flower_", "Env_Grass_", "Env_TallGrass_", "Env_Fern_",
                 "Env_Moss_", "Env_Lilypad_", "Env_Reed_", "Env_Vine_",
                 "Env_Path_", "Env_Rock_Scatter_")


def collider_for(prefab):
    name = prefab.split("/")[-1]
    if name.startswith(COLLIDER_TRUNK):
        return "trunk"
    if name.startswith(COLLIDER_NONE):
        return "none"
    return "mesh"


# --- water, as a constraint on what can be planted ---------------------------------

class WaterTest:
    """How deep the water is at a point, and where its surface sits.

    Foliage fields are hand-drawn regions and they overlap the water they border,
    exactly as they overlapped the roads. Nothing checked, so grass and whole trees
    were standing submerged in the lake. The waterline is known here — it is the
    same surface the water mesh is built from — so the scatter can just ask.
    """

    def __init__(self, layout):
        self.bodies = []
        for body in layout["terrain"].get("water", []):
            if "centreline" in body:
                line = [tuple(float(v) for v in p) for p in body["centreline"]]
                half = float(body.get("halfWidth", 1.5))
                self.bodies.append(("line", line, half))
            elif "polygon" in body:
                poly = [(float(p[0]), float(p[1])) for p in body["polygon"]]
                self.bodies.append(("poly", poly, float(body["surfaceY"])))

    def surface_at(self, x, z):
        """Waterline height at this point, or None if it is dry land."""
        best = None
        for kind, shape, param in self.bodies:
            if kind == "poly":
                if point_in_poly(x, z, shape):
                    best = param if best is None else max(best, param)
            else:
                d, pt, _ = closest_on_polyline(x, z, shape)
                # A little wider than the channel, because the bank is what the
                # ribbon was widened into and plants belong above it, not in it.
                if d <= param + 0.6:
                    best = pt[1] if best is None else max(best, pt[1])
        return best

    def depth_at(self, x, z, ground):
        surface = self.surface_at(x, z)
        return None if surface is None else surface - ground


# How wet each kind of planting is allowed to get, in metres of water over its roots.
# Reeds stand in the shallows — that is what a reed bed is — while grass and trees do
# not stand in a lake at all.
MAX_WADE = {
    "tall_grass": -0.05,
    "meadow": -0.05,
    "moss": -0.05,
    "reeds": 0.35,
    "lilypads": 99.0,     # floats; handled separately
}
DEFAULT_WADE = -0.05

# Steepest ground anything is planted on. Above this the terrain is being read as
# rock (the vertex weights cross over at 34 degrees and the shader's own slope term
# starts at 30), and a tuft of grass standing perpendicular to a cliff face looks
# like it was stapled on -- which is exactly what it was.
MAX_PLANT_SLOPE = 32.0


# --- foliage ----------------------------------------------------------------------

def scatter_foliage(layout, grid, caves, painter, water):
    """Turn each field into concrete instance transforms.

    Scattering happens here rather than in C# so the Y of every blade comes from the
    same grid the props were placed against, and so the result is reproducible: the
    RNG is seeded from the field's name, so rebuilding the level does not reshuffle
    the grass.

    A jittered grid rather than uniform random. Uniform random on a small budget
    clumps and leaves bald patches, which on an encounter field reads as holes in
    the cover the player is supposed to be walking through.
    """
    fields = []
    for f in layout.get("foliageFields", []):
        poly = [(float(p[0]), float(p[1])) for p in f["polygon"]]
        budget = int(f.get("instanceBudget", 0))
        if budget <= 0 or len(poly) < 3:
            continue

        prefabs = f.get("prefabs", [])
        total_weight = sum(float(p.get("weight", 1)) for p in prefabs) or 1.0
        lo, hi = [float(v) for v in f.get("scaleRange", [1.0, 1.0])]
        yaw_jitter = float(f.get("yawJitterDegrees", 360.0))

        kind = f.get("kind", "")
        wade = MAX_WADE.get(kind, DEFAULT_WADE)
        floats_on_water = kind == "lilypads"

        minx, maxx, minz, maxz = poly_bounds(poly)
        area = poly_area(poly)
        # The grid covers the bounding box while the budget is defined over the
        # polygon, and every cell whose jittered point lands outside is discarded, so
        # it has to hold comfortably more candidates than the budget.
        #
        # Measure how much of the polygon is actually plantable before choosing the
        # cell size. These fields are hand-drawn regions and several of them lie mostly
        # over the lake or across a road -- Field_Lake_ShoreGrass loses 99% of its
        # candidates to water. Sizing the grid to the *whole* polygon then leaves the
        # surviving strip of land nearly bare, which looks like a bug in the scatter
        # rather than in the polygon. Sampling the acceptance rate first puts the
        # authored density on the ground that can hold it.
        probe = random.Random(f["name"] + ":probe")
        pminx, pmaxx, pminz, pmaxz = poly_bounds(poly)
        usable = tries = 0
        for _ in range(400):
            px = probe.uniform(pminx, pmaxx)
            pz = probe.uniform(pminz, pmaxz)
            if not point_in_poly(px, pz, poly):
                continue
            tries += 1
            if inside_any(px, pz, caves, margin=4.0):
                continue
            if painter.paved_clearance(px, pz) > -0.25:
                continue
            if grid.slope_degrees(px, pz) > MAX_PLANT_SLOPE:
                continue
            d = water.depth_at(px, pz, grid.at(px, pz))
            if floats_on_water:
                if d is None or d < 0.25:
                    continue
            elif d is not None and d > wade:
                continue
            usable += 1
        acceptance = max(usable / tries, 0.04) if tries else 1.0

        cell = math.sqrt(max(area, 0.01) / (budget * 2.0 / acceptance))
        rng = random.Random(f["name"])

        buckets = {p["prefab"]: [] for p in prefabs}
        placed = 0
        rejected_paved = 0
        rejected_water = 0
        rejected_slope = 0
        nx = max(1, int(math.ceil((maxx - minx) / cell)))
        nz = max(1, int(math.ceil((maxz - minz) / cell)))
        cells = [(i, j) for j in range(nz) for i in range(nx)]
        rng.shuffle(cells)

        for i, j in cells:
            if placed >= budget:
                break
            x = minx + (i + rng.random()) * cell
            z = minz + (j + rng.random()) * cell
            if not point_in_poly(x, z, poly):
                continue
            if inside_any(x, z, caves, margin=4.0):
                continue
            # Keep a hand's breadth off the paving as well as off it: a blade rooted
            # exactly on the kerb line still reads as growing out of the road.
            if painter.paved_clearance(x, z) > -0.25:
                rejected_paved += 1
                continue

            if grid.slope_degrees(x, z) > MAX_PLANT_SLOPE:
                rejected_slope += 1
                continue

            ground = grid.at(x, z)
            depth = water.depth_at(x, z, ground)
            if floats_on_water:
                # A lily pad floats. Sampling it onto the terrain put it on the lake
                # bed, several metres under the surface it is supposed to lie on.
                if depth is None or depth < 0.25:
                    rejected_water += 1
                    continue
                y = ground + depth
            else:
                if depth is not None and depth > wade:
                    rejected_water += 1
                    continue
                y = ground

            roll = rng.random() * total_weight
            chosen = prefabs[-1]["prefab"]
            for p in prefabs:
                roll -= float(p.get("weight", 1))
                if roll <= 0.0:
                    chosen = p["prefab"]
                    break

            buckets[chosen].append([
                round(x, 3), round(y, 3), round(z, 3),
                round(rng.uniform(0.0, yaw_jitter), 1),
                round(rng.uniform(lo, hi), 3),
            ])
            placed += 1

        fields.append({
            "name": f["name"],
            "kind": f.get("kind", ""),
            "parent": f.get("parent", ""),
            "generatesEncounters": bool(f.get("generatesEncounters", False)),
            "groups": [{"prefab": k, "instances": [v for inst in vs for v in inst]}
                       for k, vs in buckets.items() if vs],
            "placed": placed,
            "rejectedOnPaving": rejected_paved,
            "rejectedInWater": rejected_water,
            "rejectedOnSlope": rejected_slope,
        })
    return fields


# --- main --------------------------------------------------------------------------

# --- scene splitting ----------------------------------------------------------

def chunk_overlaps(chunk, lo, hi):
    zs = chunk["vertices"][2::3]
    return min(zs) <= hi and max(zs) >= lo


def surface_overlaps(surface, lo, hi):
    zs = surface["vertices"][2::3]
    return min(zs) <= hi and max(zs) >= lo


def clip_group(group, lo, hi):
    """Keep only the instances inside the band. Five floats an instance: x y z yaw scale."""
    src = group["instances"]
    kept = []
    for i in range(0, len(src), 5):
        if lo <= src[i + 2] <= hi:
            kept.extend(src[i:i + 5])
    return {"prefab": group["prefab"], "instances": kept}


# Where one scene hands over to another. The gate ramp is the town's own threshold --
# the design calls it "the cut through the town's retaining cliff" -- so the seam goes
# where the level already puts its door.
SCENE_LINKS = [
    ("Town",  "Field", -6.6, 5.6, 20.0),
    ("Field", "Town",  -7.4, 2.4, 200.0),
]


ENTERABLE = {
    "Env_Building_PokeLab": ("Lab", "Prof's lab"),
    "Env_House_Cottage_A": ("House", "a house"),
    "Env_House_Townhouse_B": ("House", "a house"),
    "Env_House_Farmhouse_C": ("House", "a house"),
}


def building_doors(objects, bounds, grid):
    """A door volume at the front of every building you can go into.

    Same mechanism as the cave mouth and the scene seam: a trigger you walk into that
    loads an interior. The door sits on the building's front face, which for this kit
    is local +Z -- the openings are cut in wall 0 and the manifest's convention is that
    models face +Z.
    """
    out = []
    for o in objects:
        asset = o["prefab"].split("/")[-1].replace(".fbx", "")
        entry = ENTERABLE.get(asset)
        if entry is None:
            continue
        size = (bounds.get(asset) or {}).get("size")
        if not size:
            continue
        kind, label = entry
        x, _, z = [float(v) for v in o["position"]]
        yaw = float((o.get("rotation") or [0, 0, 0])[1])
        r = math.radians(yaw)
        fx, fz = math.sin(r), math.cos(r)
        # Just outside the front wall, so the player triggers it standing at the step
        # rather than inside the mesh.
        depth = size[2] * 0.5 + 0.55
        dx, dz = x + fx * depth, z + fz * depth
        out.append({
            "name": "Door_%s" % o["name"],
            "scene": "Interior_%s" % kind,
            "arrivalSpawn": "Spawn_FromDoor",
            "label": label,
            "position": [round(dx, 3), round(grid.at(dx, dz), 3), round(dz, 3)],
            "facingYaw": yaw,
            "size": [1.6, 2.4, 1.0],
        })
    return out


def scene_links(scene, grid):
    out = []
    for src, dst, x, z, yaw in SCENE_LINKS:
        if src != scene:
            continue
        out.append({
            "name": "To_%s" % dst,
            "scene": dst,
            "arrivalSpawn": "Spawn_From%s" % src,
            "position": [x, round(grid.at(x, z), 3), z],
            "facingYaw": yaw,
            "size": [5.0, 3.0, 1.6],
        })
    return out


def main():
    with open(SOURCE, encoding="utf-8") as fh:
        layout = json.load(fh)

    with open(os.path.join(HERE, "asset_bounds.json"), encoding="utf-8") as fh:
        bounds = json.load(fh)

    grid = Grid(layout["terrain"]["heightField"]["grid"])
    painter = SurfacePainter(layout)
    caves = cave_interiors(layout)
    water_test = WaterTest(layout)

    ground = build_ground(grid, painter, layout["extents"])
    water = build_water(layout, grid)
    foliage = scatter_foliage(layout, grid, caves, painter, water_test)

    # Vegetation that would be rooted in a rock face. The steep ground is read as
    # rock by both the vertex weights and the shader, and a tree standing out of a
    # cliff at 40 degrees looks pinned on rather than grown.
    PLANTED = ("Env_Tree_", "Env_Bush_", "Env_Fern_", "Env_Flower_",
               "Env_Grass_", "Env_TallGrass_", "Env_Reed_")

    objects, interior, cliffside, drowned, indoors = [], 0, 0, 0, 0
    for o in layout.get("objects", []):
        pos = o.get("position") or [0.0, 0.0, 0.0]
        x, y, z = float(pos[0]), float(pos[1]), float(pos[2])
        # Two tests, because neither alone is enough. The floor polygon catches the
        # chamber, but the entry passage and back alcove reach outside it — that is how
        # Cave_Rocks_01/02 and Cave_ItemBall_01 were surviving the filter and landing
        # 14 to 17 m under the hillside. Anything buried that far below the outside
        # surface is inside the massif by definition, whatever polygon it falls in.
        if not is_entrance(o.get("prefab", "")) and (
                inside_any(x, z, caves) or (y - grid.at(x, z)) < -2.0):
            interior += 1
            continue
        if is_interior_prop(o.get("prefab", "")):
            indoors += 1
            continue
        asset = o.get("prefab", "").split("/")[-1]
        slope = grid.slope_degrees(x, z)
        if asset.startswith(PLANTED) and slope > MAX_PLANT_SLOPE:
            cliffside += 1
            continue
        # Nothing at all stands on a face this steep. A bench on 60 degrees is not a
        # seating error to be corrected downward, it is a bench on a cliff.
        if slope > 40.0 and not is_entrance(o.get("prefab", "")):
            cliffside += 1
            continue
        # Props standing in the lake. Same rule as the foliage: the waterline is known,
        # so a tree with its trunk under it is a placement fault, not scenery.
        depth = water_test.depth_at(x, z, grid.at(x, z))
        if depth is not None and depth > 0.05 and not is_entrance(o.get("prefab", ""))                 and not any(k in asset for k in ("Bridge", "Stepping", "Lilypad", "Reed")):
            drowned += 1
            continue

        o = dict(o)
        o["collider"] = collider_for(o.get("prefab", ""))
        objects.append(o)

    gameplay = layout.get("gameplay", {})
    spawn = gameplay.get("playerSpawn", {})
    spawn_pos = [float(v) for v in spawn["position"]] if "position" in spawn else [0.0, 0.0, 0.0]

    camera = layout.get("camera", {})
    out = {
        "schema": "pokelab-level-unity/2",
        "ground": ground,
        "water": water,
        "foliage": foliage,
        "objects": objects,
        "ambientAnchors": layout.get("ambientAnchors", []),
        "caveEntrances": cave_entrances(layout, grid),
        "tallGrass": [g for g in gameplay.get("tallGrassPatches", [])
                      if not inside_any(float(g["centre"][0]), float(g["centre"][2]), caves)],
        "playerSpawn": spawn_pos,
        "cameraYaw": float(camera.get("yawDegrees", 45.0)),
        "cameraPitch": float(camera.get("pitchDegrees", 38.0)),
        "cameraDistance": float(camera.get("restDistanceMetres", 5.5)),
        "cameraFov": float(camera.get("verticalFovDegrees", 40.0)),
        "heightGrid": {
            "originX": grid.x0, "originZ": grid.z0, "step": grid.step,
            "countX": grid.nx, "countZ": grid.nz,
            "heights": [round(v, 3) for row in grid.rows for v in row],
        },
    }

    # --- split into scenes ----------------------------------------------------
    #
    # Each scene gets the ground, water, foliage and objects inside its own band,
    # plus the transition volumes that lead out of it. The bands overlap, so a chunk
    # or a tree near the seam is emitted into both: the alternative is the edge of
    # the world visible from the other side of the gate.
    written = []
    for name, lo, hi, _zone in SCENES:
        scene_ground = [c for c in ground if chunk_overlaps(c, lo, hi)]
        scene_water = [w for w in water if surface_overlaps(w, lo, hi)]
        scene_foliage = []
        for f in foliage:
            groups = [clip_group(g, lo, hi) for g in f["groups"]]
            groups = [g for g in groups if g["instances"]]
            if groups:
                scene_foliage.append(dict(f, groups=groups,
                                          placed=sum(len(g["instances"]) // 5 for g in groups)))
        scene_objects = [o for o in objects
                         if in_scene(float(o["position"][2]), lo, hi)
                         and scene_of_parent(o.get("parent", "")) == name]

        doc = dict(out)
        doc["scene"] = name
        doc["ground"] = scene_ground
        doc["water"] = scene_water
        doc["foliage"] = scene_foliage
        doc["objects"] = scene_objects
        doc["ambientAnchors"] = [a for a in out["ambientAnchors"]
                                 if in_scene(float(a["position"][2]), lo, hi)]
        doc["tallGrass"] = [g for g in out["tallGrass"]
                            if in_scene(float(g["centre"][2]), lo, hi)]
        doc["caveEntrances"] = [c for c in out["caveEntrances"]
                                if in_scene(float(c["position"][2]), lo, hi)]
        doc["sceneLinks"] = scene_links(name, grid)
        doc["buildingDoors"] = building_doors(scene_objects, bounds, grid)
        # The player only starts where the story starts.
        doc["playerSpawn"] = spawn_pos if in_scene(spawn_pos[2], lo, hi) else []

        path = os.path.join(OUT_DIR, "slice_%s_unity.json" % name.lower())
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(doc, fh)
        written.append((name, path, doc))

    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(out, fh)

    verts = sum(len(c["vertices"]) // 3 for c in ground)
    tris = sum(len(c["triangles"]) // 3 for c in ground)
    print("wrote %s" % OUT)
    print("  ground     %d chunks, %d verts, %d tris" % (len(ground), verts, tris))
    for w in water:
        print("  water      %-22s %5d verts  %5d tris"
              % (w["name"], len(w["vertices"]) // 3, len(w["triangles"]) // 3))
    total_inst = 0
    for f in foliage:
        total_inst += f["placed"]
        print("  foliage    %-24s %5d instances, %d group(s), rejected: %d paved / %d water / %d slope"
              % (f["name"], f["placed"], len(f["groups"]), f["rejectedOnPaving"], f["rejectedInWater"], f["rejectedOnSlope"]))
    print("  foliage total %d instances" % total_inst)
    print("  objects %d  (%d for cave scenes, %d for interiors, %d off rock faces, "
          "%d out of the water)"
          % (len(objects), interior, indoors, cliffside, drowned))
    print("  anchors %d  grass triggers %d  cave entrances %d  spawn %s"
          % (len(out["ambientAnchors"]), len(out["tallGrass"]),
             len(out["caveEntrances"]), spawn_pos))
    print()
    for name, path, doc in written:
        print("  scene %-6s %2d ground, %d water, %3d objects, %4d foliage, "
              "%d links, %d doors, spawn %s"
              % (name, len(doc["ground"]), len(doc["water"]), len(doc["objects"]),
                 sum(f["placed"] for f in doc["foliage"]), len(doc["sceneLinks"]),
                 len(doc["buildingDoors"]), "yes" if doc["playerSpawn"] else "-"))


if __name__ == "__main__":
    main()
