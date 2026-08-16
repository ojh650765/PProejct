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
    # The Town band's south edge follows the world box, which moved from z=-34 to z=-50
    # when the town was rebuilt: every metre the town grew is inside this band and none
    # of it is loaded by the Field scene.
    ("Town",  -50.0,  8.0, "Zone_Town"),
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
# Town paving goes to DIRT, not ROCK, and that is a compromise rather than a fix.
# ROCK is the right *idea* — paving is stone — but it is the one layer sampled
# triplanar and it tiles at 6.67 m, a figure chosen so a 20 m cliff face shows three
# repeats instead of five. At that size the plaza reads as a floor of natural boulder
# plates, which is worse than a road. DIRT tiles at 2.0 m with a granular crust and
# reads as a worn, compacted surface, which is nearer the truth. Paving that actually
# looks like paving needs a fifth layer or a control map; this is the better of the
# two answers available today.
MATERIAL_LAYER = {
    "road_flagstone": DIRT,
    "road_cobble_town": DIRT,
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


# How far past its own half width the stream's ribbon may widen, and the same distance
# the lake trace is masked back along the channel. One number on purpose: the mask is
# the ground the lake surface gives up to the stream, so a ribbon narrower than the mask
# leaves a dry notch between the two waters where the stream meets the lake, and a wider
# one puts the two surfaces back on top of each other, which is the z-fighting patchwork
# `stop_polygon` exists to prevent.
STREAM_BANK_REACH = 1.2


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
            if d <= half_width + STREAM_BANK_REACH and pt[1] > lake_y + 0.05:
                out[iz][ix] = 99.0
    return out

def build_stream(body, grid, stop_polygon=None, stop_y=None, crossings=()):
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

    That march is not enough on its own, and this function claimed it was for as long
    as it went uncalled. Measured against the baked grid, the terrain rises back over
    the waterline 2.5 to 3.1 m out from the centreline — but the march was capped at
    1.1 m past the authored 1.7 m half width, so on 25 of the stream's 71 rungs it ran
    out of reach before it found the bank and left the rim hanging in the air anyway,
    by up to 0.60 m in the open run and 2.25 m at the lake mouth. The cap cannot simply
    be raised: for a 3 m stretch below the bridge the land beside the channel is
    genuinely 0.7 m *lower* than the water, and there is no bank out there to find at
    any distance. So each rung carries a rim vertex outside the flat channel, and the
    rim drops onto the terrain wherever the terrain never came up to meet it. The sheet
    then ends on the ground in every case: buried in the bank where there is one,
    lipped over the ground where there is not, and flat where the lake takes over.

    UV.y runs in metres along the flow so the water shader's scroll moves downstream
    at a constant speed regardless of how the rungs happen to be spaced.
    """
    line = [tuple(float(v) for v in p) for p in body["centreline"]]
    if len(line) < 2:
        return []
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
            return []
    step = 0.2

    # Only the crossings that actually stand in this channel. The test below measures
    # along the flow, and without this a bridge over some other water at the same
    # offset upstream would punch a hole here.
    near = [c for c in crossings
            if closest_on_polyline(c[0], c[1], line)[0] <= half + STREAM_BANK_REACH]

    def crossed(x, z, tx, tz, span):
        """Is this rung under something the player walks across?"""
        for cx, cz, ex, ez, _hx, hz in near:
            # How far along the flow the crossing reaches *while it is over the water*.
            # Not its full footprint: the bridge is 9 m long and only the middle of it
            # spans the channel, and the wall this cut is dodging exists only where the
            # ribbon does. Two terms -- the deck's own width along the flow, and how far
            # the deck drifts along the flow over the ribbon's width, which for a deck
            # 15 degrees off square to the channel is most of it.
            reach = abs(hz * (tz * ex - tx * ez)) + span * abs(tx * ex + tz * ez)
            if abs((x - cx) * tx + (z - cz) * tz) < reach + BODY_RADIUS:
                return True
        return False

    def edge(x, z, nx, nz, y):
        d = half
        while d < half + STREAM_BANK_REACH:
            d += step
            if grid.at(x + nx * d, z + nz * d) > y:
                break
        return d

    def rim_height(x, z, y):
        """Where the ribbon's outer vertex sits, once it has stopped marching."""
        ground = grid.at(x, z)
        if ground >= y:
            return y            # the bank came up: the seam is inside it, hold the sheet flat
        if stop_y is not None and ground <= stop_y:
            # Lake bed. The ground here is below the lake's own waterline and the lake
            # surface is what covers it, so a rim laid on the ground would dive under
            # the neighbouring water instead of meeting it. This is the notch
            # `mask_stream_channel` cut out of the lake for the stream to fill.
            return y
        return ground           # dry land below the waterline: lay the rim on it

    rungs = []
    run = 0.0
    for i, (x, y, z) in enumerate(line):
        if i > 0:
            run += math.hypot(x - line[i - 1][0], z - line[i - 1][2])
        # Tangent from the neighbours, so the ribbon does not pinch on bends.
        a = line[max(0, i - 1)]
        b = line[min(len(line) - 1, i + 1)]
        tx, tz = b[0] - a[0], b[2] - a[2]
        n = math.hypot(tx, tz) or 1.0
        tx, tz = tx / n, tz / n
        nx, nz = -tz, tx

        left = edge(x, z, nx, nz, y)
        right = edge(x, z, -nx, -nz, y)
        lx, lz = x + nx * left, z + nz * left
        rx, rz = x - nx * right, z - nz * right
        total = left + right
        # Four across, not two. The rim is the only vertex allowed to leave the
        # waterline, so the channel between the authored half widths stays a flat
        # sheet -- moving a two-vertex rung's edge would tilt the whole width of the
        # water instead of turning down its fringe.
        rungs.append({
            "run": run,
            "cut": crossed(x, z, tx, tz, max(left, right)),
            "width": total,
            "verts": [(lx, rim_height(lx, lz, y), lz),
                      (x + nx * half, y, z + nz * half),
                      (x - nx * half, y, z - nz * half),
                      (rx, rim_height(rx, rz, y), rz)],
            "us": [0.0, (left - half) / total, (left + half) / total, 1.0],
        })

    # Split at the crossings. The emitted water mesh grows an invisible 3 m wall along
    # every boundary edge it has -- LevelLayoutBuilder.BuildShoreline, which is what
    # stops the player wading into the lake -- and a continuous ribbon under the bridge
    # would run that wall straight across the deck, closing the route's one gate. Cut
    # clear of each crossing and the two end caps stand upstream and downstream of it
    # instead, with the walking line between them.
    pieces, current = [], []
    for rung in rungs:
        if rung["cut"]:
            if len(current) >= 2:
                pieces.append(current)
            current = []
        else:
            current.append(rung)
    if len(current) >= 2:
        pieces.append(current)

    out = []
    for number, piece in enumerate(pieces):
        verts, uvs, tris = [], [], []
        for k, rung in enumerate(piece):
            for (vx, vy, vz), u in zip(rung["verts"], rung["us"]):
                verts.extend((vx, vy, vz))
                uvs.extend((u, rung["run"]))
            if k > 0:
                p0, c0 = (k - 1) * 4, k * 4
                for m in range(3):
                    tris.extend((p0 + m, c0 + m, p0 + m + 1,
                                 p0 + m + 1, c0 + m, c0 + m + 1))
        ys = verts[1::3]
        out.append({
            "name": (body["name"] if len(pieces) == 1
                     else "%s_%02d" % (body["name"], number + 1)),
            "kind": body.get("kind", "flowing"),
            "surfaceY": round(sum(ys) / len(ys), 4),
            "vertices": [round(v, 4) for v in verts],
            "uvs": [round(v, 3) for v in uvs],
            "triangles": tris,
            "widthMin": round(min(r["width"] for r in piece), 2),
            "widthMax": round(max(r["width"] for r in piece), 2),
        })
    return out


# What the player crosses a watercourse on. Their footprints are the holes the ribbon
# has to leave, so that the shoreline wall the builder raises along the water's edge
# does not close the crossing the level is gated on.
CROSSING_ASSETS = ("Env_Bridge_", "Env_Stepping_")


def stream_crossings(layout, bounds):
    """Footprints of the bridge and the stepping stones, as (x, z, ex, ez, hx, hz)."""
    out = []
    for o in layout.get("objects", []):
        asset = o["prefab"].split("/")[-1].replace(".fbx", "")
        if not asset.startswith(CROSSING_ASSETS):
            continue
        size = (bounds.get(asset) or {}).get("size")
        if not size:
            continue
        scale = float((o.get("scale") or [1.0])[0])
        x, _, z = [float(v) for v in o["position"]]
        yaw = math.radians(float((o.get("rotation") or [0, 0, 0])[1]))
        out.append((x, z, math.cos(yaw), -math.sin(yaw),
                    size[0] * 0.5 * scale, size[2] * 0.5 * scale))
    return out


def build_water(layout, grid, bounds):
    """Flat surfaces at each body's authored waterline, plus the flowing stream.

    Subdivided even though they are flat, because the water shader displaces vertices
    for its swell and a two-triangle lake cannot show a wave.
    """
    out = []
    crossings = stream_crossings(layout, bounds)

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
    receiving_y = next((float(b["surfaceY"]) for b in layout["terrain"].get("water", [])
                        if "centreline" not in b and "surfaceY" in b), None)

    for body in layout["terrain"].get("water", []):
        # Discriminated on the centreline, not on the absence of a polygon: the
        # stream ships both, and its polygon is only the ribbon's flat outline —
        # using it would hold a watercourse that falls 1.7 m at a single height.
        if "centreline" in body:
            # Built here and scoped at the scene split, not dropped. It was dropped for
            # a while, and the note it left behind said "by design" without saying whose
            # design: the ask was "Water_Stream 이건 없어도 되자나", and the correction is
            # that it meant the overworld only -- "이건 overworld에서 빼도 된다는 거였고.
            # Water Stream은 field에서 필요함". FLOWING_WATER_SCENES is where that lands.
            out.extend(build_stream(body, grid, receiving, receiving_y, crossings))
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


# Barrier modules. A riverbank stands at the water's edge and a ledge lip stands on a
# steep face -- that is what each is for -- so the rules that keep scenery out of the
# lake and off the cliffs would delete exactly the pieces that make the level's gates
# real. Every one of them was dropped before this exemption existed: 20 riverbank
# modules and 1 of 2 ledge modules.
BARRIER_ASSETS = ("Env_Riverbank_", "Env_Ledge_")


def is_barrier(prefab):
    return any(k in prefab for k in BARRIER_ASSETS)


# Jamb boxes, in the same shape and to the same numbers as the town's boundary
# barriers: 4 m tall, sunk 1 m so they cannot be stepped on to.
ARCH_JAMB_HEIGHT = 4.0
ARCH_JAMB_SINK = 1.0


def arch_barriers(layout, grid, bounds):
    """The solid parts of the cave mouth, now that its mesh is not collided against.

    Two boxes, one on each jamb, running the arch's full depth: everything from the
    edge of the declared opening out to the edge of the asset is rock, and everything
    between them is the doorway. That is the whole of what the mesh collider was
    usefully doing -- the rest of what it did was making the arch's own scree solid,
    which is what closed the door.

    No lintel box over the opening. The headwall above it is 4.3 m up and there is no
    jump in this project, so a box there would be collision the player cannot reach.
    """
    out = []
    caves = [c for c in layout["terrain"].get("caves", []) if c.get("mouth")]
    for o in layout.get("objects", []):
        asset = o["prefab"].split("/")[-1].replace(".fbx", "")
        if not asset.startswith(COLLIDER_PORTAL):
            continue
        size = (bounds.get(asset) or {}).get("size")
        if not size:
            continue
        x, _, z = [float(v) for v in o["position"]]
        scale = float((o.get("scale") or [1.0])[0])
        yaw = float((o.get("rotation") or [0, 0, 0])[1])
        rad = math.radians(yaw)

        # The opening is the mouth the level declares, not a guess at the mesh: the
        # trigger, the barrier and the modelled jambs then agree on where the door is.
        # Measured on the asset, the modelled clear span is 5.4 m at the threshold, so
        # a box whose inner face stands at half of that lands on the stone.
        cave = min(caves, key=lambda c: math.hypot(float(c["mouth"]["position"][0]) - x,
                                                   float(c["mouth"]["position"][2]) - z),
                   default=None)
        opening = float((cave or {}).get("mouth", {}).get("widthMetres", 4.0))
        outer = size[0] * 0.5 * scale
        inner = opening * 0.5
        if inner >= outer:
            continue
        for side, label in ((-1.0, "West"), (1.0, "East")):
            local = side * (inner + outer) * 0.5
            bx = x + local * math.cos(rad)
            bz = z - local * math.sin(rad)
            out.append({
                "name": "Barrier_%s_Jamb%s" % (o["name"], label),
                "centre": [round(bx, 3),
                           round(grid.at(bx, bz) - ARCH_JAMB_SINK
                                 + ARCH_JAMB_HEIGHT * 0.5, 3),
                           round(bz, 3)],
                "size": [round(outer - inner, 3), ARCH_JAMB_HEIGHT,
                         round(size[2] * scale, 3)],
                "yawDegrees": round(yaw, 2) % 360.0,
                "layer": "Environment",
            })
    return out


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

# The cave mouth, which is a doorway and was collided against as though it were a rock.
#
# It fell through to "mesh" -- there was nothing else for it to match -- so every
# triangle of Env_Cave_Arch became solid, and the arch's mesh is not only the headwall.
# It carries its own scree: five blocks slumped around the threshold and a talus fan,
# and two of those blocks stand inside the 5.4 m opening the level declares. The nearer
# one is a metre across and sits 1.5 m in front of the doorway on the line the approach
# path walks in on. Collided against, that is a boulder lying across the door of the
# player's own cave, which is what the user circled.
#
# "none" here does not mean the mouth is not solid. It means the arch's *mesh* stops
# deciding what is solid, because a mesh collider cannot say "this stone is a wall and
# that stone is a threshold" -- the collider policy is keyed per prefab and the FBX is
# one object. The headwall comes back as two barrier boxes on the jambs, `arch_barriers`,
# which is the same answer the boundary wood already uses for the same reason.
COLLIDER_PORTAL = ("Env_Cave_Arch",)

# Every character in this level is a 0.28 m capsule: LevelLayoutBuilder.BuildPeople
# gives one to every NPC and trainer, and PlayerRigSetup gives the player the same.
BODY_RADIUS = 0.28

# NavMeshSurface.voxelSize, as LevelLayoutBuilder sets it before baking. Clearances are
# rounded out by one of these so the bake has a whole voxel of floor to call walkable
# rather than a sliver that rounds away.
NAVMESH_VOXEL = 0.12


def collider_for(prefab):
    name = prefab.split("/")[-1]
    if name.startswith(COLLIDER_TRUNK):
        return "trunk"
    if name.startswith(COLLIDER_PORTAL):
        return "none"
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


class SolidProps:
    """Where a foliage field is not allowed to plant, because something solid is there.

    Placed objects reject each other -- Builder.blocked -- but foliage fields never
    consulted them at all, so a field polygon drawn over a house planted trees through
    its roof and a field drawn along the terrace planted them through the retaining
    wall. That was survivable while the fields were ankle-high meadow inside open
    ground. It stops being survivable the moment a boundary wood is authored as a
    dense field over the whole edge of the town, which is what closes it in.

    Trees are deliberately not solid here. Their collider is a trunk capsule, their
    canopies are supposed to interpenetrate, and treating them as obstacles would
    make a wood unable to be a wood.
    """

    #  asset prefix -> clearance in metres beyond its own half-footprint
    CLEARANCE = [
        (("Env_Building_", "Env_House_"), 1.0),
        (("Env_Wall_Stone_", "Env_Ledge_", "Env_Riverbank_"), 0.5),
        (("Env_Market_", "Env_Well", "Env_Cart", "Env_Notice_", "Env_Signpost",
          "Env_Lamp_Post", "Env_Bench", "Env_Trough", "Env_Bridge_"), 0.4),
        (("Env_Fence_", "Env_Planter", "Env_Crate", "Env_Barrel"), 0.25),
        # An item ball is 0.28 m across and the tall-grass clusters it is hidden
        # among are 1.28 m, planted eight to the square metre. Without a clearing
        # the one at the back of Field_Route_NorthGrass is not merely hard to spot,
        # it is behind two ranks of cover from the only yaw the camera has -- the
        # reward for wading through an encounter field would be an item the player
        # never learns is there. The clearing is generous for the ball's size on
        # purpose: it has to open a hole from the south-west, not just around the
        # pivot.
        (("Env_Prop_CaptureBall",), 0.9),
    ]
    CELL = 4.0

    def __init__(self, layout, bounds):
        self.cells = {}
        for o in layout.get("objects", []):
            asset = o["prefab"].split("/")[-1].replace(".fbx", "")
            pad = None
            for prefixes, clearance in self.CLEARANCE:
                if asset.startswith(prefixes):
                    pad = clearance
                    break
            if pad is None:
                continue
            size = (bounds.get(asset) or {}).get("size")
            if not size:
                continue
            scale = float((o.get("scale") or [1.0])[0])
            x, _, z = [float(v) for v in o["position"]]
            yaw = math.radians(float((o.get("rotation") or [0, 0, 0])[1]))
            # The building's OWN rectangle, tested in its own frame -- not the
            # axis-aligned box around it. On the lab, which is 9.4 x 10.0 m at 222
            # degrees, the axis-aligned box is 13.7 x 13.7: nearly twice the area, and
            # it reached 4.6 m past the lab's real north-east corner. That is the whole
            # of the shelf behind the lab, and it is why no boundary wood would grow
            # there and the town's rim was visible over the lab's shoulder.
            hx = size[0] * 0.5 * scale + pad
            hz = size[2] * 0.5 * scale + pad
            box = (x, z, math.cos(-yaw), math.sin(-yaw), hx, hz)
            # Cell coverage still uses the conservative circumscribed radius, or a
            # rotated box could straddle a cell it was never registered in.
            r = math.hypot(hx, hz)
            for i in range(int((x - r) // self.CELL), int((x + r) // self.CELL) + 1):
                for j in range(int((z - r) // self.CELL), int((z + r) // self.CELL) + 1):
                    self.cells.setdefault((i, j), []).append(box)

    def blocked(self, x, z):
        for bx, bz, c, s, hx, hz in self.cells.get(
                (int(x // self.CELL), int(z // self.CELL)), ()):
            dx, dz = x - bx, z - bz
            if abs(dx * c - dz * s) <= hx and abs(dx * s + dz * c) <= hz:
                return True
        return False


# --- people, as the other constraint on what can be planted -------------------------

def planting_reach(prefab, bounds, scale):
    """How far from its own pivot an instance of this asset stands in somebody's way.

    A tree is measured on its stem and not on its box, for the same reason its collider
    is a trunk capsule: the canopy is *meant* to overhang, and measuring the 5.5 m box
    of Env_Tree_Broadleaf_C would clear a 4 m circle of the wood around every waypoint
    and read as a bald patch. The same formula the builder uses for the capsule is used
    here so the two agree on what the stem is.

    Everything else is measured on its box, because a bush is a 1.65 m body standing at
    exactly the height the character sprite occupies.
    """
    asset = prefab.split("/")[-1].replace(".fbx", "")
    size = (bounds.get(asset) or {}).get("size")
    if not size:
        return 0.0
    if asset.startswith(COLLIDER_TRUNK):
        return max(0.18, min(size[0], size[2]) * 0.13) * scale
    return max(size[0], size[2]) * 0.5 * scale


class PeopleClearance:
    """Where a foliage field is not allowed to plant, because somebody stands there.

    `SolidProps` is the other half of this and was the only half that existed. It keeps
    foliage out of things; nothing kept foliage off people. A field is drawn as a
    polygon over a region and the town's residents live inside those regions, so the
    boundary wood grew through the gatekeeper and the gardener -- "npc가 나무 사이에 막
    파묻히고 들어가 있어".

    Colliders would not have helped and are not the fix: fields are drawn through
    Graphics.DrawMeshInstanced, which submits geometry and nothing else, so the navmesh
    bake cannot see a single trunk however solid it looks. What the bake can see is
    ground, so the ground each person uses is kept clear instead -- their whole wander
    circle, not just the waypoint at the middle of it, because the circle is the part
    the agent actually walks.

    Ten anchors and four trainers on this map, so this is a list and a loop. A grid
    would be more code than it saves.
    """

    def __init__(self, layout):
        self.discs = []
        gameplay = layout.get("gameplay", {})
        for npc in gameplay.get("npcs", []):
            self._add(npc["position"], 0.0)
            for entry in npc.get("schedule", []):
                self._add(entry["waypoint"], float(entry.get("wanderRadius", 0.0)))
        # Trainers have no schedule. They stand where they are put and turn on the
        # spot, so their own body is the whole of the circle.
        for trainer in gameplay.get("trainers", []):
            self._add(trainer["position"], 0.0)

    def _add(self, position, wander):
        x, _, z = [float(v) for v in position]
        self.discs.append((x, z, wander))

    def blocked(self, x, z, clearance):
        for cx, cz, wander in self.discs:
            r = wander + clearance
            if (x - cx) ** 2 + (z - cz) ** 2 < r * r:
                return True
        return False


# --- foliage ----------------------------------------------------------------------

def scatter_foliage(layout, grid, caves, painter, water, solids, people, bounds):
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

        # How much room this particular field has to leave around a person, taken from
        # its own palette at its own largest scale. One number for every field would be
        # wrong in both directions: the wood's 1.65 m bushes need 1.5 m and the town
        # lawn's flowers need 0.8, and clearing 1.5 m of lawn around a stallholder
        # standing in it would put a bald ring on the plaza.
        clearance = BODY_RADIUS + NAVMESH_VOXEL + max(
            [planting_reach(p["prefab"], bounds, hi) for p in prefabs] or [0.0])

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
            if solids.blocked(px, pz):
                continue
            if people.blocked(px, pz, clearance):
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
        rejected_solid = 0
        rejected_water = 0
        rejected_slope = 0
        rejected_people = 0
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

            if solids.blocked(x, z):
                rejected_solid += 1
                continue

            if people.blocked(x, z, clearance):
                rejected_people += 1
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
            "rejectedOnProps": rejected_solid,
            "rejectedInWater": rejected_water,
            "rejectedOnSlope": rejected_slope,
            "rejectedOnPeople": rejected_people,
            "personClearance": round(clearance, 2),
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

# Which scenes draw flowing water. The stream's surface is a Field fixture and nothing
# else's: the ask was to take it out of the overworld -- "이건 overworld에서 빼도 된다는
# 거였고. Water Stream은 field에서 필요함" -- and LevelLayoutBuilder loads slice_town for
# both the Town and the Overworld scene, so leaving it out of that one file is what
# leaving it out of the overworld means. Filtering by band alone would look like it did
# the job today (the channel runs z=10..34, clear of the town's band) and would quietly
# start drawing it the day the town's edge moves.
FLOWING_WATER_SCENES = {"Field"}


ENTERABLE = {
    "Env_Building_PokeLab": ("Lab", "Prof's lab"),
    "Env_Building_PokeCentre": ("PokeCentre", "the Pokemon Centre"),
    "Env_House_Cottage_A": ("House", "a house"),
    "Env_House_Townhouse_B": ("House", "a house"),
    "Env_House_Farmhouse_C": ("House", "a house"),
}

# Checked before ENTERABLE, and matched on the object's name rather than on its mesh.
#
# The Pokemon Centre's own asset is still being modelled, so its plot is held by a
# townhouse. Keyed on the asset alone that placeholder would emit a "House" door into
# Interior_House, and the Centre would quietly not exist -- which is the same failure as
# the healing machine standing in the street, one layer down. Keyed on the name, the
# door is a Centre door today and stays one the moment the mesh is swapped, whichever
# way round the swap happens.
ENTERABLE_BY_NAME = {
    "Town_PokeCentre": ("PokeCentre", "the Pokemon Centre"),
}


def enterable_entry(obj):
    for prefix, entry in ENTERABLE_BY_NAME.items():
        if obj["name"].startswith(prefix):
            return entry
    return ENTERABLE.get(obj["prefab"].split("/")[-1].replace(".fbx", ""))


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
        entry = enterable_entry(o)
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
    water = build_water(layout, grid, bounds)
    solids = SolidProps(layout, bounds)
    people = PeopleClearance(layout)
    foliage = scatter_foliage(layout, grid, caves, painter, water_test, solids,
                              people, bounds)

    # Vegetation that would be rooted in a rock face. The steep ground is read as
    # rock by both the vertex weights and the shader, and a tree standing out of a
    # cliff at 40 degrees looks pinned on rather than grown.
    PLANTED = ("Env_Tree_", "Env_Bush_", "Env_Fern_", "Env_Flower_",
               "Env_Grass_", "Env_TallGrass_", "Env_Reed_")

    objects, interior, cliffside, drowned, indoors, underfoot = [], 0, 0, 0, 0, 0
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
        if is_barrier(o.get("prefab", "")):
            o = dict(o)
            o["collider"] = collider_for(o.get("prefab", ""))
            objects.append(o)
            continue
        if asset.startswith(PLANTED) and slope > MAX_PLANT_SLOPE:
            cliffside += 1
            continue
        # Planted objects standing where somebody works. The scatter now keeps fields
        # off people, but the orchard behind the west lane is not a field -- it is a
        # `clump` of twelve placed trees, and the Builder's occupancy has never known
        # anything about the residents, so Town_Orchard_03 landed 0.91 m from the
        # gardener whose own note says she stands *behind* the orchard. With a 2.2 m
        # wander radius she spends her day inside that trunk. Only plants: a market
        # stall or a lamp inside a resident's circle is the stall she works at, and
        # dropping the furniture people are placed against would be the same fault
        # with the sign reversed.
        if asset.startswith(PLANTED) and people.blocked(
                x, z, BODY_RADIUS + NAVMESH_VOXEL + planting_reach(
                    o.get("prefab", ""), bounds,
                    float((o.get("scale") or [1.0])[0]))):
            underfoot += 1
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
        # People. Nothing was building these: the layout has authored six residents and
        # four trainers with positions, schedules and sight cones, and the scene came up
        # empty every time. Two separate workstreams reported it independently -- the
        # sprite pipeline finished sixteen characters that had nowhere to stand.
        # Invisible collision for the boundary wood. The trees are drawn through
        # Graphics.DrawMeshInstanced, which carries no colliders, so the wall that
        # actually stops the player is these boxes: one per straight run, no renderer.
        # They are not objects because the object collider policy is keyed on a prefab
        # and these have no mesh. The cave mouth's jambs join them for the same reason
        # from the other end: there the mesh exists and must *not* be the collider.
        "barrierVolumes": (layout.get("barrierVolumes", [])
                           + arch_barriers(layout, grid, bounds)),
        "npcs": gameplay.get("npcs", []),
        "trainers": gameplay.get("trainers", []),
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
    # Names already emitted into an earlier band, so nobody is emitted twice.
    _people_claimed = set()

    def claim(seen, key):
        if key in seen:
            return False
        seen.add(key)
        return True

    for name, lo, hi, _zone in SCENES:
        scene_ground = [c for c in ground if chunk_overlaps(c, lo, hi)]
        scene_water = [w for w in water if surface_overlaps(w, lo, hi)
                       and (w["kind"] != "flowing" or name in FLOWING_WATER_SCENES)]
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
        doc["barrierVolumes"] = [v for v in out["barrierVolumes"]
                                 if in_scene(float(v["centre"][2]), lo, hi)]
        # People are claimed, not shared. The bands overlap by ten metres on purpose --
        # ground and props must exist in both so the two meshes meet and the player can
        # walk across the join without anything popping. That is right for scenery and
        # wrong for a person: NPC_GateKeeper stands at z = 0.4, inside both ranges, and
        # with both scenes loaded the player meets two of him standing in each other.
        #
        # First band wins, and the order in SCENES is the answer: the gatekeeper is the
        # town's gatekeeper, and Town is listed first.
        doc["npcs"] = [n for n in out["npcs"]
                       if in_scene(float(n["position"][2]), lo, hi)
                       and claim(_people_claimed, n["name"])]
        doc["trainers"] = [t for t in out["trainers"]
                           if in_scene(float(t["position"][2]), lo, hi)
                           and claim(_people_claimed, t["name"])]
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
        print("  water      %-22s %5d verts  %5d tris%s"
              % (w["name"], len(w["vertices"]) // 3, len(w["triangles"]) // 3,
                 "  %.1f-%.1f m wide" % (w["widthMin"], w["widthMax"])
                 if "widthMin" in w else ""))
    total_inst = 0
    for f in foliage:
        total_inst += f["placed"]
        print("  foliage    %-26s %5d instances, %d group(s), rejected: %d paved / %d props / %d water / %d slope / %d people (%.2f m)"
              % (f["name"], f["placed"], len(f["groups"]), f["rejectedOnPaving"],
                 f["rejectedOnProps"], f["rejectedInWater"], f["rejectedOnSlope"],
                 f["rejectedOnPeople"], f["personClearance"]))
    print("  foliage total %d instances" % total_inst)
    print("  objects %d  (%d for cave scenes, %d for interiors, %d off rock faces, "
          "%d out of the water, %d off somebody's feet)"
          % (len(objects), interior, indoors, cliffside, drowned, underfoot))
    print("  anchors %d  grass triggers %d  cave entrances %d  spawn %s"
          % (len(out["ambientAnchors"]), len(out["tallGrass"]),
             len(out["caveEntrances"]), spawn_pos))
    print()
    for name, path, doc in written:
        print("  scene %-6s %2d ground, %d water, %3d objects, %4d foliage, %d barriers, "
              "%d links, %d doors, %d npc, %d trainer, %d ambient, spawn %s"
              % (name, len(doc["ground"]), len(doc["water"]), len(doc["objects"]),
                 sum(f["placed"] for f in doc["foliage"]), len(doc["barrierVolumes"]),
                 len(doc["sceneLinks"]), len(doc["buildingDoors"]), len(doc["npcs"]),
                 len(doc["trainers"]), len(doc["ambientAnchors"]),
                 "yes" if doc["playerSpawn"] else "-"))


if __name__ == "__main__":
    main()
