"""
Ground family: the terrain decks, water surfaces, ramps, ledges and the
waterfall sheet that the shipped kit did not have.

Why this exists
---------------
`Assets/Game/Data/Levels/slice_layout.json` places 4,682 objects at Y values
sampled from four analytic height fields, and then notes, in its own terrain
block, that "THE KIT SHIPS NO TERRAIN AND NO WATER MESH".  Every one of those
placements is floating.  This module closes that gap by building the decks the
layout assumed, from the layout's own polygons and height fields, so the
surface a prop stands on is the surface its Y was sampled from.

The height fields are therefore not re-derived or improved here.  They are
transcribed verbatim from Tools/Level/build_layout.py and asserted against the
layout's own object positions at the end of the run (see `verify_heights`).
Any drift is a bug, not a style choice.

Axis note
---------
Everything here is authored in Blender's Z-up space and the export bakes the
conversion (see envlib.export_fbx): Blender +Z becomes Unity +Y and Blender +Y
becomes Unity +Z.  So a layout world position (X, Y, Z) is authored at Blender
(X, Z, Y) -- the layout's Z is Blender's Y, and the layout's height Y is
Blender's Z.  Every function in this file takes (x, z) in LAYOUT space and
returns a height, and the mesher is the only place the swap happens.

Materials
---------
Decks carry PokeLabTerrainBlend, not the family atlas, so their vertex colours
are blend weights, not wind:  R grass, G dirt, B sand, A rock, exactly as the
shader's header states.  Water carries PokeLabWater.  The Blender-side
materials in this file exist only so the contact sheets are readable.
"""

import sys
import os
import math
import json
import random

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector

import envlib as E
import textures as T

FAM = "Terrain"
PART = "ground"
OUT = E.FAMILY_DIR[FAM]

LAYOUT = os.path.join(E.REPO, "Assets", "Game", "Data", "Levels",
                      "slice_layout.json")

# --------------------------------------------------------------------------
# height planes, transcribed from Tools/Level/build_layout.py
# --------------------------------------------------------------------------

Y_WATER = -2.0
Y_SHORE = -1.5
Y_ROUTE = 0.0
Y_CAVE = 1.5
Y_TOWN = 3.0
Y_CAVE_CEIL = 6.5
Y_TIER1 = 4.5
Y_TIER2 = 11.5
Y_SKYLINE = 16.5


def clamp(t, lo=0.0, hi=1.0):
    return lo if t < lo else (hi if t > hi else t)


def smoothstep(t):
    t = clamp(t)
    return t * t * (3.0 - 2.0 * t)


def ground_route(x, z):
    return Y_ROUTE + 0.28 * math.sin(x * 0.075) + 0.22 * math.cos(z * 0.09)


def ground_shore(x, z):
    d = math.hypot(x + 6.0, (z + 2.0) * 1.15)
    t = clamp((d - 21.0) / 8.0)
    return Y_SHORE + t * (Y_ROUTE - Y_SHORE) + 0.12 * math.sin(x * 0.3)


def ground_town(x, z):
    return Y_TOWN + 0.10 * math.sin(x * 0.16) + 0.08 * math.cos(z * 0.19)


def ground_cave(x, z):
    return Y_CAVE + 0.13 * math.sin(x * 0.22) + 0.11 * math.cos(z * 0.26)


HEIGHT_FIELDS = {
    "heightFields.route": ground_route,
    "heightFields.town": ground_town,
    "heightFields.cave": ground_cave,
    "heightFields.shore": ground_shore,
}


# --------------------------------------------------------------------------
# 2D polygon maths.  Everything below works in LAYOUT (X, Z) space.
# --------------------------------------------------------------------------

def poly_bbox(poly):
    xs = [p[0] for p in poly]
    zs = [p[1] for p in poly]
    return min(xs), min(zs), max(xs), max(zs)


def point_in_poly(x, z, poly):
    inside = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        xi, zi = poly[i]
        xj, zj = poly[j]
        if (zi > z) != (zj > z):
            xc = xi + (z - zi) * (xj - xi) / (zj - zi)
            if x < xc:
                inside = not inside
        j = i
    return inside


def dist_to_poly(x, z, poly):
    """Unsigned distance from (x, z) to the polygon boundary."""
    best = 1e18
    n = len(poly)
    for i in range(n):
        ax, az = poly[i]
        bx, bz = poly[(i + 1) % n]
        dx, dz = bx - ax, bz - az
        L2 = dx * dx + dz * dz
        t = 0.0 if L2 < 1e-12 else clamp(((x - ax) * dx + (z - az) * dz) / L2)
        px, pz = ax + t * dx, az + t * dz
        d = (x - px) ** 2 + (z - pz) ** 2
        if d < best:
            best = d
    return math.sqrt(best)


def depth_inside(x, z, poly):
    """Distance inside the polygon; 0 anywhere outside it."""
    if not point_in_poly(x, z, poly):
        return 0.0
    return dist_to_poly(x, z, poly)


def ccw(poly):
    return poly if poly_area_xz(poly) > 0 else list(reversed(poly))


def poly_area_xz(pts):
    a = 0.0
    n = len(pts)
    for i in range(n):
        x0, z0 = pts[i]
        x1, z1 = pts[(i + 1) % n]
        a += x0 * z1 - x1 * z0
    return 0.5 * a


def offset_poly(poly, d):
    """Offset every edge outward by d metres and re-intersect the corners.

    Decks are grown by roughly a metre where they meet another deck.  The
    layout says so itself -- "the seam is broken with scatter, not butted" --
    and the placement data proves it: several hundred props sit a few
    centimetres to a metre outside the polygon they were height-sampled
    against, because the scatter was allowed to spill over the join.  Butting
    the decks edge to edge leaves those props standing on nothing; overlapping
    them by a metre costs a handful of hidden triangles and nothing else.
    """
    if abs(d) < 1e-9:
        return list(poly)
    p = ccw(poly)
    n = len(p)
    lines = []
    for i in range(n):
        ax, az = p[i]
        bx, bz = p[(i + 1) % n]
        dx, dz = bx - ax, bz - az
        L = math.hypot(dx, dz) or 1.0
        nx, nz = dz / L, -dx / L        # outward normal of a CCW polygon
        lines.append((ax + nx * d, az + nz * d, dx / L, dz / L))
    out = []
    for i in range(n):
        px, pz, ux, uz = lines[i - 1]
        qx, qz, vx, vz = lines[i]
        den = ux * vz - uz * vx
        if abs(den) < 1e-6:             # near-parallel: keep the shifted point
            out.append((qx, qz))
            continue
        t = ((qx - px) * vz - (qz - pz) * vx) / den
        out.append((px + ux * t, pz + uz * t))
    return out


def dist_to_polyline(x, z, pts):
    best = 1e18
    for i in range(len(pts) - 1):
        ax, az = pts[i][0], pts[i][1]
        bx, bz = pts[i + 1][0], pts[i + 1][1]
        dx, dz = bx - ax, bz - az
        L2 = dx * dx + dz * dz
        t = 0.0 if L2 < 1e-12 else clamp(((x - ax) * dx + (z - az) * dz) / L2)
        px, pz = ax + t * dx, az + t * dz
        d = (x - px) ** 2 + (z - pz) ** 2
        if d < best:
            best = d
    return math.sqrt(best)


# --------------------------------------------------------------------------
# Sutherland-Hodgman: clip the deck polygon against one grid cell.
#
# The cell is the CLIP window and it is convex, which is the only thing S-H
# requires; the deck polygon being concave is fine as the subject.  Cells are
# 1-2 m and the deck edges are tens of metres long, so a cell is crossed at most
# once and the result is exact rather than approximate.  The alternative --
# keeping whole cells whose centre is inside -- leaves a staircase boundary
# along every cliff edge, which is exactly where the eye is.
# --------------------------------------------------------------------------

def _clip_halfplane(subject, keep):
    """keep(pt) -> (inside, intersect(a, b))"""
    inside_fn, inter_fn = keep
    out = []
    n = len(subject)
    if n == 0:
        return out
    for i in range(n):
        cur = subject[i]
        prv = subject[i - 1]
        ci, pi = inside_fn(cur), inside_fn(prv)
        if ci:
            if not pi:
                out.append(inter_fn(prv, cur))
            out.append(cur)
        elif pi:
            out.append(inter_fn(prv, cur))
    return out


def clip_to_cell(poly, x0, z0, x1, z1):
    def mk(axis, value, sign):
        def inside(p):
            return (p[axis] >= value) if sign > 0 else (p[axis] <= value)

        def inter(a, b):
            da = a[axis] - value
            db = b[axis] - value
            t = 0.0 if abs(db - da) < 1e-12 else da / (da - db)
            t = clamp(t)
            return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)
        return (inside, inter)

    res = list(poly)
    for spec in ((0, x0, +1), (0, x1, -1), (1, z0, +1), (1, z1, -1)):
        res = _clip_halfplane(res, mk(*spec))
        if not res:
            return []
    # drop duplicated points introduced by clipping through a vertex
    out = []
    for p in res:
        if not out or (abs(p[0] - out[-1][0]) > 1e-7 or
                       abs(p[1] - out[-1][1]) > 1e-7):
            out.append(p)
    if len(out) > 2 and abs(out[0][0] - out[-1][0]) < 1e-7 and \
            abs(out[0][1] - out[-1][1]) < 1e-7:
        out.pop()
    return out if len(out) >= 3 else []


def poly_area(pts):
    a = 0.0
    n = len(pts)
    for i in range(n):
        x0, z0 = pts[i]
        x1, z1 = pts[(i + 1) % n]
        a += x0 * z1 - x1 * z0
    return 0.5 * a


# --------------------------------------------------------------------------
# the deck mesher
# --------------------------------------------------------------------------

QUANT = 1e-4     # snap grid for vertex welding, 0.1 mm
PROUD = 0.02     # how far a ramp sits above the deck it blends into


class DeckMesher:
    """Builds a height-field surface over an arbitrary polygon on a regular
    grid, with the polygon boundary reproduced exactly."""

    def __init__(self, bm, height_fn, mat=0):
        self.bm = bm
        self.height = height_fn
        self.mat = mat
        self.cache = {}
        self.faces = []

    def vert(self, x, z):
        kx = round(x / QUANT) * QUANT
        kz = round(z / QUANT) * QUANT
        key = (round(kx, 4), round(kz, 4))
        v = self.cache.get(key)
        if v is None:
            v = self.bm.verts.new((kx, kz, self.height(kx, kz)))
            self.cache[key] = v
        return v

    def face(self, pts):
        if len(pts) < 3:
            return
        if abs(poly_area(pts)) < 1e-6:
            return
        if poly_area(pts) < 0:
            pts = list(reversed(pts))
        vs = [self.vert(p[0], p[1]) for p in pts]
        # a clipped cell can collapse two corners onto one welded vertex
        uniq = []
        for v in vs:
            if not uniq or uniq[-1] is not v:
                uniq.append(v)
        if len(uniq) > 2 and uniq[0] is uniq[-1]:
            uniq.pop()
        if len(uniq) < 3:
            return
        try:
            f = self.bm.faces.new(uniq)
        except ValueError:
            return
        f.material_index = self.mat
        f.smooth = True
        self.faces.append(f)

    # The layout's polygons are drawn on whole and half metres, so an aligned
    # grid puts polygon vertices exactly on grid lines and the clip returns
    # zero-width slivers on both sides of the line.  Those weld together in
    # finalize() and leave a non-manifold edge.  Phasing the grid by an
    # irrational-ish fraction of a cell guarantees no vertex ever lands on one.
    PHASE = 0.1372

    def run(self, poly, cell):
        x0, z0, x1, z1 = poly_bbox(poly)
        gx0 = math.floor(x0 / cell) * cell + self.PHASE * cell
        gz0 = math.floor(z0 / cell) * cell + self.PHASE * cell
        if gx0 > x0:
            gx0 -= cell
        if gz0 > z0:
            gz0 -= cell
        nx = int(math.ceil((x1 - gx0) / cell)) + 1
        nz = int(math.ceil((z1 - gz0) / cell)) + 1
        # cheap reject: per-cell bbox test against the polygon's edge bboxes
        edges = []
        for i in range(len(poly)):
            ax, az = poly[i]
            bx, bz = poly[(i + 1) % len(poly)]
            edges.append((min(ax, bx), min(az, bz), max(ax, bx), max(az, bz)))
        for i in range(nx):
            cx0 = gx0 + i * cell
            cx1 = cx0 + cell
            for j in range(nz):
                cz0 = gz0 + j * cell
                cz1 = cz0 + cell
                touched = any(not (e[2] < cx0 or e[0] > cx1 or
                                   e[3] < cz0 or e[1] > cz1) for e in edges)
                if not touched:
                    # fully inside or fully outside; one point decides
                    if point_in_poly(cx0 + cell * 0.5, cz0 + cell * 0.5, poly):
                        self.face([(cx0, cz0), (cx1, cz0),
                                   (cx1, cz1), (cx0, cz1)])
                    continue
                clipped = clip_to_cell(poly, cx0, cz0, cx1, cz1)
                if len(clipped) == 4 and abs(abs(poly_area(clipped)) -
                                             cell * cell) < 1e-6:
                    self.face([(cx0, cz0), (cx1, cz0), (cx1, cz1), (cx0, cz1)])
                elif clipped:
                    # fan from the centroid: keeps every triangle well shaped
                    # even when the clip leaves a long thin sliver
                    ccx = sum(p[0] for p in clipped) / len(clipped)
                    ccz = sum(p[1] for p in clipped) / len(clipped)
                    n = len(clipped)
                    for k in range(n):
                        a = clipped[k]
                        b = clipped[(k + 1) % n]
                        self.face([(ccx, ccz), a, b])
        return self.faces


def build_deck(bm, poly, height_fn, cell, mat=0):
    return DeckMesher(bm, height_fn, mat).run(poly, cell)


def build_solid_deck(bm, poly, height_fn, floor_z, cell, mat=0):
    """Top surface, matching underside, and the wall that joins them.

    The first version of this used bmesh's triangle_fill for the underside and
    it was wrong in a way that only a render caught: triangle_fill has no idea
    the boundary is concave, so it happily spanned the route deck's notch with
    one enormous triangle floating across the level.  Meshing the underside with
    the same polygon mesher as the top removes the guesswork -- both surfaces
    have identical XZ vertices in identical order, so the wall is a plain quad
    strip between paired boundary vertices and the result is closed by
    construction rather than by hope.
    """
    top = DeckMesher(bm, height_fn, mat)
    top.run(poly, cell)
    bot = DeckMesher(bm, flat(floor_z), mat)
    bot.run(poly, cell)

    bm.edges.ensure_lookup_table()
    top_verts = set(top.cache.values())
    walls = []
    for e in list(bm.edges):
        if len(e.link_faces) != 1:
            continue
        v0, v1 = e.verts
        if v0 not in top_verts or v1 not in top_verts:
            continue
        k0 = (round(v0.co.x, 4), round(v0.co.y, 4))
        k1 = (round(v1.co.x, 4), round(v1.co.y, 4))
        b0 = bot.cache.get(k0)
        b1 = bot.cache.get(k1)
        if b0 is None or b1 is None:
            continue
        try:
            f = bm.faces.new((v0, v1, b1, b0))
        except ValueError:
            continue
        f.material_index = mat
        f.smooth = False
        walls.append(f)
    return walls


# --------------------------------------------------------------------------
# blend-weight painting.  R grass, G dirt, B sand, A rock -- PokeLabTerrainBlend
# --------------------------------------------------------------------------

def norm4(g, d, s, r):
    t = g + d + s + r
    if t < 1e-5:
        return (0.0, 0.0, 0.0, 1.0)
    return (g / t, d / t, s / t, r / t)


def hash_noise(x, z, seed, freq):
    """Cheap smooth value noise so the blend weights are not flat fields."""
    return (0.5 + 0.5 * math.sin(x * freq + seed) *
            math.cos(z * freq * 0.83 + seed * 1.7)
            + 0.25 * math.sin((x + z) * freq * 2.3 + seed * 2.9)) * 0.66


def paint_weights(obj, fn):
    """fn(layout_x, layout_z, height, normal_z) -> (g, d, s, r)"""
    me = obj.data
    nz_by_vert = {}
    for p in me.polygons:
        for vi in p.vertices:
            nz_by_vert[vi] = max(nz_by_vert.get(vi, -1.0), abs(p.normal.z))

    def f(co, li, pi):
        vi = me.loops[li].vertex_index
        return norm4(*fn(co.x, co.y, co.z, nz_by_vert.get(vi, 1.0)))
    return E.add_vcol(obj, f)


# --------------------------------------------------------------------------
# Blender-side preview materials.  Unity uses PokeLabTerrainBlend / PokeLabWater;
# these only have to make the contact sheets legible.
# --------------------------------------------------------------------------

LAYER_RGB = {
    "grass": (0.30, 0.46, 0.17),
    "dirt": (0.35, 0.26, 0.16),
    "sand": (0.72, 0.65, 0.46),
    "rock": (0.40, 0.41, 0.44),
}


def blend_preview_material(name):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Roughness"].default_value = 0.86
    attr = nt.nodes.new("ShaderNodeVertexColor")
    attr.layer_name = "Col"
    attr.location = (-900, 120)
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    sep.location = (-700, 120)
    nt.links.new(attr.outputs["Color"], sep.inputs["Color"])
    prev = None
    order = [("grass", sep.outputs[0]), ("dirt", sep.outputs[1]),
             ("sand", sep.outputs[2]), ("rock", attr.outputs["Alpha"])]
    x = -500
    for (layer, sock) in order:
        mixn = nt.nodes.new("ShaderNodeMix")
        mixn.data_type = 'RGBA'
        mixn.location = (x, 120)
        x += 190
        if prev is None:
            mixn.inputs[6].default_value = (0.2, 0.2, 0.2, 1.0)
        else:
            nt.links.new(prev, mixn.inputs[6])
        c = LAYER_RGB[layer]
        mixn.inputs[7].default_value = (c[0], c[1], c[2], 1.0)
        nt.links.new(sock, mixn.inputs[0])
        prev = mixn.outputs[2]
    nt.links.new(prev, bsdf.inputs["Base Color"])
    mat["pokelab_shader"] = "PokeLab/TerrainBlend"
    return mat


def water_preview_material(name, rgb=(0.10, 0.42, 0.52), alpha=0.72):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    bsdf.inputs["Roughness"].default_value = 0.06
    if "Transmission" in bsdf.inputs:
        bsdf.inputs["Transmission"].default_value = 0.35
    bsdf.inputs["Alpha"].default_value = alpha
    mat.blend_method = 'BLEND'
    mat["pokelab_shader"] = "PokeLab/Water"
    return mat


def flat_material(name, rgb, rough=0.8):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    return mat


# --------------------------------------------------------------------------
# layout data
# --------------------------------------------------------------------------

_LAYOUT_CACHE = {}


def layout():
    if "d" not in _LAYOUT_CACHE:
        with open(LAYOUT, "r", encoding="utf-8") as f:
            _LAYOUT_CACHE["d"] = json.load(f)
    return _LAYOUT_CACHE["d"]


def deck_spec(name):
    for d in layout()["terrain"]["decks"]:
        if d["name"] == name:
            return d
    raise KeyError(name)


def water_spec(name):
    for w in layout()["terrain"]["water"]:
        if w["name"] == name:
            return w
    raise KeyError(name)


def poly_of(spec):
    """Polygon as (x, z) pairs, with a repeated closing vertex dropped.

    Deck_MassifTier1Top and Deck_MassifTier2Top in the layout repeat their first
    point at the end. Left in, that becomes a zero-length edge and a
    non-manifold seam once the deck is closed into a solid."""
    pts = [(float(p[0]), float(p[1])) for p in spec["polygon"]]
    out = []
    for p in pts:
        if out and abs(p[0] - out[-1][0]) < 1e-6 and abs(p[1] - out[-1][1]) < 1e-6:
            continue
        out.append(p)
    if len(out) > 2 and abs(out[0][0] - out[-1][0]) < 1e-6 and             abs(out[0][1] - out[-1][1]) < 1e-6:
        out.pop()
    return out


LAKE = None
OUTFLOW = None
CAVEPOOL = None


def load_water_polys():
    global LAKE, OUTFLOW, CAVEPOOL
    LAKE = poly_of(water_spec("Water_Lake"))
    OUTFLOW = poly_of(water_spec("Water_Outflow"))
    CAVEPOOL = poly_of(water_spec("Water_CavePool"))


# --------------------------------------------------------------------------
# composite height fields: the spec field with the water beds carved into it
# --------------------------------------------------------------------------

LAKE_BED = -3.8
LAKE_RAMP = 5.0
OUTFLOW_BED = -3.2
OUTFLOW_RAMP = 2.4
POOL_BED = 0.3
POOL_RAMP = 1.8


def shore_with_beds(x, z):
    """Shore band, with the lake and the outflow channel carved into it.

    The spec height field is left untouched everywhere the layout sampled it --
    the reeds and shore scatter at the waterline sit at ground_shore exactly.
    The descent to the bed starts at the water polygon boundary and runs
    inward, so the visible waterline lands a metre or so inside the water mesh
    and the water's own edge is buried in the bank.  That is deliberate: a
    water plane whose rim is coplanar with the ground z-fights along its entire
    perimeter, and a gap there is the single most visible terrain defect.
    """
    y = ground_shore(x, z)
    dl = depth_inside(x, z, LAKE)
    if dl > 0.0:
        k = smoothstep(dl / LAKE_RAMP)
        y = y * (1.0 - k) + LAKE_BED * k
    do = depth_inside(x, z, OUTFLOW)
    if do > 0.0:
        k = smoothstep(do / OUTFLOW_RAMP)
        y = min(y, y * (1.0 - k) + OUTFLOW_BED * k)
    return y


def cave_with_pool(x, z):
    y = ground_cave(x, z)
    dp = depth_inside(x, z, CAVEPOOL)
    if dp > 0.0:
        k = smoothstep(dp / POOL_RAMP)
        y = y * (1.0 - k) + POOL_BED * k
    return y


def river_channel(x, z):
    """East bank of the outflow, beyond the shore deck."""
    y = Y_ROUTE - 0.9 + 0.16 * math.sin(x * 0.21) + 0.13 * math.cos(z * 0.24)
    do = depth_inside(x, z, OUTFLOW)
    if do > 0.0:
        k = smoothstep(do / OUTFLOW_RAMP)
        y = y * (1.0 - k) + OUTFLOW_BED * k
    else:
        d = dist_to_poly(x, z, OUTFLOW)
        k = smoothstep(d / 6.0)
        y = y * k + (Y_WATER - 0.05) * (1.0 - k)
    return y


def with_ramp_notch(base_fn, specs, feather=2.2, clearance=0.06):
    """Cut the ramp corridors out of a deck's height field.

    Without this the ramps are simply buried: Ramp_TownFromRoute climbs to
    (38, 3, 44) and the town terrace covers everything west of x=32.8, so the
    top four metres of the ramp run *inside* the terrace and emerge through it
    as a sliver.  Lowering the deck to the ramp surface inside the corridor
    gives the terrace a real cut-in, which is what a ramp up a terrace edge
    looks like anyway, and leaves the ramp mesh as the only visible surface
    there rather than two meshes fighting for the same pixels.
    """
    prepared = []
    for spec in specs:
        ax, ay, az = spec["from"]
        bx, by, bz = spec["to"]
        dx, dz = bx - ax, bz - az
        L = math.hypot(dx, dz) or 1.0
        prepared.append((ax, ay, az, bx, by, bz, dx / L, dz / L, L,
                         spec["width"] * 0.5))

    def f(x, z):
        y = base_fn(x, z)
        for (ax, ay, az, bx, by, bz, ux, uz, L, hw) in prepared:
            t = ((x - ax) * ux + (z - az) * uz) / L
            if t < -0.06 or t > 1.06:
                continue
            tc = clamp(t)
            px, pz = ax + ux * L * tc, az + uz * L * tc
            dl = math.hypot(x - px, z - pz)
            k = 1.0 - smoothstep((dl - hw) / feather)
            # taper the notch out at both ends so it does not leave a step
            k *= (1.0 - smoothstep((abs(t - 0.5) - 0.5) / 0.09))
            if k <= 0.0:
                continue
            ry = ay + (by - ay) * tc - clearance
            y = y * (1.0 - k) + min(y, ry) * k
        return y
    return f


def flat(h, amp=0.0, seed=0.0):
    def f(x, z):
        if amp <= 0.0:
            return h
        return h + amp * (hash_noise(x, z, seed, 0.11) - 0.5) * 2.0
    return f


def cave_ceiling_field(x, z):
    """Chamber roof: bumpy, and it dips at the edges so the ceiling meets the
    wall instead of stopping in mid air."""
    d = dist_to_poly(x, z, CAVE_POLY)
    lip = 1.5 * (1.0 - smoothstep(d / 4.0))
    return Y_CAVE_CEIL - lip + 0.26 * math.sin(x * 0.31 + 1.1) * \
        math.cos(z * 0.27) + 0.14 * math.sin((x + z) * 0.53)


CAVE_POLY = None


# --------------------------------------------------------------------------
# weight painters per deck
# --------------------------------------------------------------------------

def route_paths():
    """The walkable spine polylines, used to bake worn dirt into the deck."""
    out = []
    for p in layout()["paths"]:
        pts = [(q[0], q[2]) for q in p["points"]]
        out.append((pts, p["halfWidth"]))
    return out


def w_route(paths):
    def f(x, z, y, nz):
        n = hash_noise(x, z, 3.0, 0.09)
        g = 0.80 + 0.35 * n
        d = 0.10 + 0.45 * hash_noise(x, z, 11.0, 0.21)
        s = 0.02
        r = 0.02 + 0.9 * (1.0 - clamp((nz - 0.55) / 0.35))
        for (pts, hw) in paths:
            dd = dist_to_polyline(x, z, pts)
            worn = 1.0 - smoothstep((dd - hw * 0.55) / (hw * 1.1))
            if worn > 0.0:
                d += worn * 2.6
                g *= (1.0 - worn * 0.85)
        return (g, d, s, r)
    return f


def w_town(paths):
    def f(x, z, y, nz):
        n = hash_noise(x, z, 7.0, 0.13)
        g = 0.62 + 0.30 * n
        d = 0.45 + 0.40 * hash_noise(x, z, 21.0, 0.26)
        s = 0.04
        r = 0.03 + 1.2 * (1.0 - clamp((nz - 0.55) / 0.35))
        for (pts, hw) in paths:
            dd = dist_to_polyline(x, z, pts)
            worn = 1.0 - smoothstep((dd - hw * 0.6) / (hw * 1.0))
            if worn > 0.0:
                d += worn * 3.2
                g *= (1.0 - worn * 0.9)
        return (g, d, s, r)
    return f


def w_shore(x, z, y, nz):
    # sand hugs the waterline, grass takes over as the band climbs to the route
    t = clamp((y - Y_SHORE) / (Y_ROUTE - Y_SHORE))
    n = hash_noise(x, z, 5.0, 0.17)
    s = (1.25 - t) * (0.75 + 0.5 * n)
    g = t * t * (1.4 + 0.4 * n)
    d = 0.10 + 0.30 * hash_noise(x, z, 13.0, 0.23)
    r = 0.05 + 1.4 * (1.0 - clamp((nz - 0.5) / 0.4))
    if y < Y_WATER - 0.15:                    # lake bed
        s = 0.9
        g = 0.0
        r += 0.8 * smoothstep((Y_WATER - 0.15 - y) / 1.2)
    return (g, d, s, r)


def w_cave(x, z, y, nz):
    n = hash_noise(x, z, 9.0, 0.19)
    r = 1.6 + 0.5 * n
    d = 0.35 + 0.45 * hash_noise(x, z, 17.0, 0.31)
    s = 0.10 + 0.35 * (1.0 if y < Y_CAVE - 0.6 else 0.0)
    g = 0.02
    return (g, d, s, r)


def w_rock(x, z, y, nz):
    n = hash_noise(x, z, 23.0, 0.07)
    return (0.02 + 0.08 * n, 0.12 + 0.2 * n, 0.02, 1.7 + 0.4 * n)


# --------------------------------------------------------------------------
# assembly helpers
# --------------------------------------------------------------------------

def finish_ground(obj, smooth=30.0, planar_uv=True, budget=None,
                  need_vcol=True):
    E.finalize(obj, smooth_angle=smooth)
    E.apply_transforms(obj)
    if planar_uv:
        E.uv_planar(obj)
    tris, probs = E.validate(obj, budget=budget, need_vcol=need_vcol,
                             strict=False)
    return tris, probs


def make_deck(name, poly, height_fn, cell, weight_fn, floor_z, mat,
              smooth=30.0, skew=0.35, budget=None):
    bm = E.bm_new()
    build_solid_deck(bm, poly, height_fn, floor_z, cell, 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, name, [mat])
    E.finalize(obj, smooth_angle=smooth)
    E.apply_transforms(obj)
    E.uv_planar(obj)
    paint_weights(obj, weight_fn)
    tris, probs = E.validate(obj, budget=budget, need_vcol=True, strict=False,
                             closed=True)
    return obj, tris, probs


def make_water(name, poly, surface_y, cell, mat, budget=None):
    bm = E.bm_new()
    build_deck(bm, poly, flat(surface_y), cell, 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, name, [mat])
    E.finalize(obj, smooth_angle=80.0)
    E.apply_transforms(obj)
    E.uv_planar(obj)
    tris, probs = E.validate(obj, budget=budget, need_vcol=False, strict=False)
    return obj, tris, probs


def ceiling_solid(name, poly, cell, mat, thickness=1.4, budget=None):
    """The chamber roof.  Built as a slab, not a sheet: a single-sided ceiling
    disappears from below the moment the camera clips it, and the massif above
    has to read as solid rock."""
    bm = E.bm_new()
    build_solid_deck(bm, poly, cave_ceiling_field, Y_CAVE_CEIL + thickness,
                     cell, 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, name, [mat])
    E.finalize(obj, smooth_angle=22.0)
    E.apply_transforms(obj)
    E.uv_planar(obj)
    paint_weights(obj, w_rock)
    tris, probs = E.validate(obj, budget=budget, need_vcol=True, strict=False,
                             closed=True)
    return obj, tris, probs


# --------------------------------------------------------------------------
# ramps and ledges
# --------------------------------------------------------------------------

def ramp_strip(bm, a, b, width, blend_a, blend_b, mat=0, along=None,
               across=6, lip=1.2):
    """A graded strip from world point a to world point b.

    The two ends do not simply stop at the given Y: they cross-fade into the
    height field of the deck they land on over `lip` metres, so a ramp meets
    the route floor's roll instead of cutting a step into it.
    """
    ax, ay, az = a
    bx, by, bz = b
    dx, dz = bx - ax, bz - az
    L = math.hypot(dx, dz) or 1.0
    ux, uz = dx / L, dz / L
    px, pz = -uz, ux
    if along is None:
        along = max(4, int(round(L / 1.0)))
    grid = []
    for i in range(along + 1):
        t = i / along
        cx = ax + dx * t
        cz = az + dz * t
        cy = ay + (by - ay) * t
        # blend into the destination fields at the two ends
        wa = 1.0 - smoothstep(t * L / lip)
        wb = 1.0 - smoothstep((1.0 - t) * L / lip)
        row = []
        for j in range(across + 1):
            s = j / across - 0.5
            x = cx + px * width * s
            z = cz + pz * width * s
            y = cy
            # PROUD is why this is not zero: the ramp lands inside the deck it
            # serves, so at the blended ends its surface is the deck's own
            # height field -- coplanar, and coplanar is z-fighting. Two
            # centimetres is invisible at a 6-12 m HD-2D camera and removes the
            # flicker completely.
            if wa > 0.0:
                y = y * (1.0 - wa) + (blend_a(x, z) + PROUD) * wa
            if wb > 0.0:
                y = y * (1.0 - wb) + (blend_b(x, z) + PROUD) * wb
            # crown the surface slightly so it sheds the eye, not water
            y += 0.05 * (1.0 - 4.0 * s * s) * 0.5
            row.append(bm.verts.new((x, z, y)))
        grid.append(row)
    faces = []
    for i in range(along):
        for j in range(across):
            f = bm.faces.new((grid[i][j], grid[i][j + 1],
                              grid[i + 1][j + 1], grid[i + 1][j]))
            f.material_index = mat
            f.smooth = True
            faces.append(f)
    return faces, grid


def close_grid_solid(bm, grid, floor_z, mat=0):
    """Give a rectangular vertex grid an underside and four walls.

    Same reasoning as build_solid_deck: mirror the grid instead of asking
    bmesh to guess a cap.  A ramp with no underside is a ramp you fall
    through from below, and the lake spur is walked past from below.
    """
    rows = len(grid)
    cols = len(grid[0])
    bot = [[bm.verts.new((v.co.x, v.co.y, floor_z)) for v in row]
           for row in grid]
    faces = []

    def quad(a, b, c, d):
        try:
            f = bm.faces.new((a, b, c, d))
        except ValueError:
            return
        f.material_index = mat
        f.smooth = False
        faces.append(f)

    for i in range(rows - 1):
        for j in range(cols - 1):
            quad(bot[i][j], bot[i][j + 1], bot[i + 1][j + 1], bot[i + 1][j])
    for i in range(rows - 1):
        quad(grid[i][0], grid[i + 1][0], bot[i + 1][0], bot[i][0])
        quad(grid[i][-1], grid[i + 1][-1], bot[i + 1][-1], bot[i][-1])
    for j in range(cols - 1):
        quad(grid[0][j], grid[0][j + 1], bot[0][j + 1], bot[0][j])
        quad(grid[-1][j], grid[-1][j + 1], bot[-1][j + 1], bot[-1][j])
    return faces


def make_ramp(name, spec, blend_a, blend_b, mat, floor_drop=1.6, budget=None):
    bm = E.bm_new()
    a = spec["from"]
    b = spec["to"]
    _, grid = ramp_strip(bm, a, b, spec["width"], blend_a, blend_b, 0)
    floor = min(a[1], b[1]) - floor_drop
    close_grid_solid(bm, grid, floor, 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, name, [mat])
    E.finalize(obj, smooth_angle=30.0)
    E.apply_transforms(obj)
    E.uv_box_walls(obj)
    paint_weights(obj, lambda x, z, y, nz: (
        0.55 + 0.3 * hash_noise(x, z, 3.0, 0.12),
        1.3 + 0.4 * hash_noise(x, z, 8.0, 0.3), 0.05,
        0.05 + 1.1 * (1.0 - clamp((nz - 0.5) / 0.4))))
    tris, probs = E.validate(obj, budget=budget, need_vcol=True, strict=False,
                             closed=True)
    return obj, tris, probs


def ledge_solid(bm, a, b, drop, cap_depth=1.1, mat=0, segs=None, seed=0):
    """A one-way hop-down: a grass-capped shelf whose downhill face is a short
    vertical cliff.  The cap overhangs the face slightly, which is what makes a
    ledge read as a ledge from a three-quarter camera rather than as a step."""
    rng = random.Random(seed)
    ax, ay, az = a
    bx, by, bz = b
    dx, dz = bx - ax, bz - az
    L = math.hypot(dx, dz) or 1.0
    ux, uz = dx / L, dz / L
    px, pz = -uz, ux              # +p is the uphill side
    if segs is None:
        segs = max(3, int(round(L / 0.5)))
    top_back, top_front, bot_front = [], [], []
    for i in range(segs + 1):
        t = i / segs
        cx = ax + dx * t
        cz = az + dz * t
        cy = ay + (by - ay) * t
        jag = (rng.random() - 0.5) * 0.10
        over = 0.16 + 0.06 * rng.random()
        top_back.append(bm.verts.new((cx + px * cap_depth,
                                      cz + pz * cap_depth,
                                      cy + 0.04 * rng.random())))
        top_front.append(bm.verts.new((cx - px * over, cz - pz * over,
                                       cy + jag * 0.3)))
        bot_front.append(bm.verts.new((cx - px * (over - 0.22) + jag * 0.2,
                                       cz - pz * (over - 0.22),
                                       cy - drop)))
    bot_back = [bm.verts.new((v.co.x, v.co.y, ay - drop - 0.15))
                for v in top_back]
    faces = []

    def strip(r0, r1, flip=False):
        for i in range(len(r0) - 1):
            q = (r0[i], r0[i + 1], r1[i + 1], r1[i])
            if flip:
                q = tuple(reversed(q))
            try:
                f = bm.faces.new(q)
            except ValueError:
                continue
            f.material_index = mat
            f.smooth = True
            faces.append(f)

    strip(top_back, top_front)      # grass cap
    strip(top_front, bot_front)     # the face
    strip(bot_front, bot_back)      # underside
    strip(bot_back, top_back)       # uphill back wall -- without it the module
                                    # is a shell and you see through it from
                                    # the high side, which is the side the
                                    # player approaches from
    for pair in ((top_back[0], top_front[0], bot_front[0], bot_back[0]),
                 (top_back[-1], top_front[-1], bot_front[-1], bot_back[-1])):
        try:
            f = bm.faces.new(pair)
            f.material_index = mat
            faces.append(f)
        except ValueError:
            pass
    return faces


def make_ledge(name, spec, mat, budget=None, seed=0):
    bm = E.bm_new()
    ledge_solid(bm, spec["from"], spec["to"], spec["drop"], 1.1, 0, seed=seed)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, name, [mat])
    E.finalize(obj, smooth_angle=24.0)
    E.apply_transforms(obj)
    E.uv_box_walls(obj)
    paint_weights(obj, lambda x, z, y, nz: (
        1.5 if nz > 0.6 else 0.02, 0.25, 0.03,
        0.1 + 1.6 * (1.0 - clamp((nz - 0.5) / 0.4))))
    tris, probs = E.validate(obj, budget=budget, need_vcol=True, strict=False,
                             closed=True)
    return obj, tris, probs


# --------------------------------------------------------------------------
# waterfall
# --------------------------------------------------------------------------

def waterfall_sheet(bm, top, bottom, width, mat=0, rows=18, cols=7,
                    bow=0.55, seed=3):
    """The falling sheet.  Not a flat plane: it narrows and accelerates as it
    drops, bows away from the rock, and frays at the lip, so the scrolling
    water shader has silhouette to work with instead of a rectangle."""
    rng = random.Random(seed)
    tx, ty, tz = top
    bx, by, bz = bottom
    dx, dz = bx - tx, bz - tz
    horiz = math.hypot(dx, dz) or 1e-4
    ux, uz = dx / horiz, dz / horiz
    px, pz = -uz, ux
    grid = []
    for i in range(rows + 1):
        t = i / rows
        fall = t * t * 0.72 + t * 0.28          # accelerating
        y = ty + (by - ty) * fall
        adv = horiz * (t * 0.55 + fall * 0.45)
        # bow: the sheet leaves the lip, then is pulled back at the plunge
        push = math.sin(t * math.pi) * bow
        cx = tx + ux * (adv + push)
        cz = tz + uz * (adv + push)
        w = width * (1.0 - 0.22 * t) * (1.0 + 0.10 * math.sin(t * 7.3))
        row = []
        for j in range(cols + 1):
            s = j / cols - 0.5
            ripple = 0.06 * math.sin(s * 9.0 + t * 11.0) * (0.3 + t)
            row.append(bm.verts.new((cx + px * w * s + ux * ripple,
                                     cz + pz * w * s + uz * ripple,
                                     y + (rng.random() - 0.5) * 0.03)))
        grid.append(row)
    faces = []
    for i in range(rows):
        for j in range(cols):
            f = bm.faces.new((grid[i][j], grid[i][j + 1],
                              grid[i + 1][j + 1], grid[i + 1][j]))
            f.material_index = mat
            f.smooth = True
            faces.append(f)
    return faces


def plunge_ring(bm, centre, radius, mat=0, rings=3, seg=20, rise=0.16):
    cx, cy, cz = centre
    loops = []
    for r in range(rings + 1):
        t = r / rings
        rad = radius * (0.45 + 0.55 * t)
        loop = []
        for s in range(seg):
            a = 2.0 * math.pi * s / seg
            h = cy + rise * math.sin(t * math.pi) * (0.6 + 0.4 * math.sin(a * 3))
            loop.append(bm.verts.new((cx + math.cos(a) * rad,
                                      cz + math.sin(a) * rad, h)))
        loops.append(loop)
    faces = []
    for r in range(rings):
        a, b = loops[r], loops[r + 1]
        for s in range(seg):
            s2 = (s + 1) % seg
            f = bm.faces.new((a[s], a[s2], b[s2], b[s]))
            f.material_index = mat
            f.smooth = True
            faces.append(f)
    return faces



# --------------------------------------------------------------------------
# Modular pieces.  The world-placed ledges and waterfall above are one-offs for
# the shipped layout; these are the kit versions -- pivot at the snapping
# corner, lengths on the 0.5 m grid, ready to be run along any edge.
# --------------------------------------------------------------------------

def module_ledge(bm, length, drop=1.0, cap_depth=1.15, mat=0, seed=0):
    """A one-way hop-down module, built along +X from the origin corner.

    The hop-down is core Pokemon vocabulary and the layout currently fakes one
    by burying two metres of a three-metre cliff, which gives the player a 3 m
    visual drop for a 1 m gameplay drop.  A 1 m module with a grass cap says
    what it does.
    """
    return ledge_solid(bm, (0.0, drop, 0.0), (length, drop, 0.0), drop,
                       cap_depth, mat, seed=seed)


def module_ledge_corner(bm, arm=1.5, drop=1.0, cap_depth=1.15, mat=0, seed=0):
    """An outside corner, so a ledge run can turn without leaving a gap."""
    ledge_solid(bm, (0.0, drop, 0.0), (arm, drop, 0.0), drop, cap_depth, mat,
                seed=seed)
    return ledge_solid(bm, (arm, drop, 0.0), (arm, drop, arm), drop, cap_depth,
                       mat, seed=seed + 1)


def module_fall_sheet(bm, width=2.0, height=4.0, mat=0, cols=7, rows=14,
                      seed=5):
    """A tileable falling sheet, built in the XZ plane at y=0.

    Tileable both ways: the left and right edge profiles are the same function
    of u so two sheets butt without a seam, and the top and bottom edges are
    straight so they stack.  The interior is rippled, which is what stops a
    2 x 4 m sheet of water reading as a rectangle of glass.
    """
    def edge(u):
        return (0.055 * math.sin(2.0 * math.pi * u) +
                0.022 * math.sin(6.0 * math.pi * u + 1.3))

    grid = []
    for i in range(rows + 1):
        t = i / rows
        row = []
        for j in range(cols + 1):
            u = j / cols
            x = -width * 0.5 + width * u
            z = height * (1.0 - t)
            y = (edge(u) * (1.0 - 0.45 * t) +
                 0.035 * math.sin(u * 11.0 + t * 9.0) * (0.4 + t))
            row.append(bm.verts.new((x, y, z)))
        grid.append(row)
    faces = []
    for i in range(rows):
        for j in range(cols):
            f = bm.faces.new((grid[i][j], grid[i][j + 1],
                              grid[i + 1][j + 1], grid[i + 1][j]))
            f.material_index = mat
            f.smooth = True
            faces.append(f)
    return faces


def module_plunge(bm, radius=1.9, mat=0, rings=4, seg=22):
    """The foam ring where a fall lands.  Pivot at the centre, sits at y=0."""
    return plunge_ring(bm, (0.0, 0.0, 0.0), radius, mat, rings, seg, rise=0.14)


# --------------------------------------------------------------------------
# height verification -- the handover risk the layout worker flagged
# --------------------------------------------------------------------------

def sample_surface(obj, x, z):
    """Height of `obj`'s upward-facing surface at layout (x, z), or None.

    Reads the exported mesh itself, not the height function, so this catches
    meshing errors as well as field errors.
    """
    me = obj.data
    best = None
    for p in me.polygons:
        # 0.35 rejects the skirt walls. They face outward and, because the
        # skirt is skewed outward as it drops, their normals tilt slightly up,
        # so a looser test lets a point just outside the deck "land" halfway
        # down a wall and report a plausible-looking wrong height.
        if p.normal.z <= 0.35:
            continue
        vs = [me.vertices[i].co for i in p.vertices]
        # fan-triangulate the polygon and barycentric-test each triangle
        for k in range(1, len(vs) - 1):
            a, b, c = vs[0], vs[k], vs[k + 1]
            d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y)
            if abs(d) < 1e-12:
                continue
            l1 = ((b.y - c.y) * (x - c.x) + (c.x - b.x) * (z - c.y)) / d
            l2 = ((c.y - a.y) * (x - c.x) + (a.x - c.x) * (z - c.y)) / d
            l3 = 1.0 - l1 - l2
            if l1 < -1e-4 or l2 < -1e-4 or l3 < -1e-4:
                continue
            y = l1 * a.z + l2 * b.z + l3 * c.z
            if best is None or y > best:
                best = y
    return best


# ramps that cut a notch into their deck; objects standing in the notch are
# reported separately rather than counted as height failures
RAMP_CORRIDORS = []


def in_ramp_corridor(x, z, field, feather=2.2):
    for spec, owner in RAMP_CORRIDORS:
        if owner != field:
            continue
        ax, _, az = spec["from"]
        bx, _, bz = spec["to"]
        dx, dz = bx - ax, bz - az
        L = math.hypot(dx, dz) or 1.0
        t = ((x - ax) * dx / L + (z - az) * dz / L) / L
        if t < -0.1 or t > 1.1:
            continue
        tc = clamp(t)
        px, pz = ax + dx * tc, az + dz * tc
        if math.hypot(x - px, z - pz) < spec["width"] * 0.5 + feather:
            return spec["name"]
    return None


FIELD_OF = {
    "route": ground_route,
    "town": ground_town,
    "cave": ground_cave,
    "shore": ground_shore,
}

# which deck is authoritative for a field-anchored object
DECK_OF_FIELD = {
    "route": "Env_Ground_RouteFloor",
    "town": "Env_Ground_TownTerrace",
    "cave": "Env_Ground_CaveFloor",
    "shore": "Env_Ground_ShoreBand",
}


def anchoring_field(x, y, z, eps=0.004):
    """Which of build_layout.py's height fields this object's Y came from.

    The layout does not record it, but it does not have to: the fields are
    analytic and mutually well separated (they live on different height
    planes), so an exact match to within 4 mm identifies the source
    unambiguously.  Anything that matches none of them was placed some other
    way -- on a ramp polyline, hanging from the ceiling, floating on water, or
    stacked on a cliff module -- and is not this mesh's responsibility.
    """
    for name, fn in FIELD_OF.items():
        if abs(y - fn(x, z)) < eps:
            return name
    return None


def verify_heights(decks, report_path, tol=0.05, grid_samples=3000):
    """The handover check the layout worker asked for, in two independent parts.

    1. FIELD-ANCHORED OBJECTS.  Every object whose placed Y is an exact
       evaluation of one of build_layout.py's four height fields is looked up
       on the deck that owns that field, and the mesh surface under it is
       compared with the Y it was placed at.  This is the check that matters:
       these are the props that float or sink if the deck is wrong.

    2. FREE GRID.  Independently of any object, the deck mesh is sampled at
       random points inside its own polygon and compared with the analytic
       field, excluding the water beds that are deliberately carved below it.
       This catches meshing error -- boundary clipping, welding, interpolation
       across a 1.5 m cell -- rather than field error.
    """
    d = layout()
    by_name = {n: (mesh, poly) for (n, mesh, poly, walk) in decks}
    rows = []
    per_field = {}
    unanchored = {}
    in_water = []
    in_corridor = []

    for spec in d["objects"]:
        x, y, z = spec["position"]
        field = anchoring_field(x, y, z)
        if field is None:
            unanchored[spec.get("parent", "?")] = \
                unanchored.get(spec.get("parent", "?"), 0) + 1
            continue
        # A LAYOUT defect, not a mesh one: the shore and cave height fields are
        # defined across the whole plan, water included, and with no water mesh
        # in the kit nothing stopped the scatter passes from planting shore
        # tufts and plunge moss out in the middle of the lake.  Those objects
        # sit above the water surface with nothing under them.  Carving the bed
        # up to meet them would be modelling around a bug, so they are counted
        # and listed for the level owner instead of being fixed here.
        wet = None
        for wpoly, wname, wsurf in ((LAKE, "Water_Lake", -2.0),
                                    (OUTFLOW, "Water_Outflow", -2.0),
                                    (CAVEPOOL, "Water_CavePool", 1.05)):
            if point_in_poly(x, z, wpoly):
                wet = (wname, wsurf)
                break
        corridor = in_ramp_corridor(x, z, field)
        if corridor is not None:
            in_corridor.append({"object": spec["name"], "ramp": corridor,
                                "parent": spec.get("parent", ""),
                                "x": round(x, 2), "z": round(z, 2),
                                "placedY": y})
            continue
        if wet is not None:
            in_water.append({"object": spec["name"],
                             "parent": spec.get("parent", ""),
                             "prefab": os.path.basename(spec["prefab"]),
                             "water": wet[0], "x": round(x, 2),
                             "z": round(z, 2), "placedY": y,
                             "waterSurfaceY": wet[1]})
            continue
        deck = DECK_OF_FIELD[field]
        mesh, poly = by_name.get(deck, (None, None))
        if mesh is None:
            continue
        s = sample_surface(mesh, x, z)
        if s is None:
            per_field.setdefault(field, {"off": 0, "err": []})["off"] += 1
            continue
        per_field.setdefault(field, {"off": 0, "err": []})["err"].append(s - y)
        rows.append((spec["name"], spec.get("parent", ""), deck, field,
                     x, z, y, s, s - y))

    def stats(errs):
        if not errs:
            return None
        a = sorted(abs(e) for e in errs)
        n = len(a)
        return {"samples": n,
                "meanAbsError": round(sum(a) / n, 5),
                "p99AbsError": round(a[min(n - 1, int(n * 0.99))], 5),
                "maxAbsError": round(a[-1], 5),
                "withinTolerance": int(sum(1 for e in a if e <= tol)),
                "failures": int(sum(1 for e in a if e > tol))}

    objects_summary = {}
    for field, blob in sorted(per_field.items()):
        st = stats(blob["err"]) or {}
        st["outsideDeckPolygon"] = blob["off"]
        st["deck"] = DECK_OF_FIELD[field]
        objects_summary[field] = st

    # ---- free grid ------------------------------------------------------
    rng = random.Random(20260816)
    grid_summary = {}
    for field, deck in DECK_OF_FIELD.items():
        mesh, poly = by_name.get(deck, (None, None))
        if mesh is None:
            continue
        fn = FIELD_OF[field]
        x0, z0, x1, z1 = poly_bbox(poly)
        errs = []
        misses = 0
        tries = 0
        while len(errs) < grid_samples and tries < grid_samples * 60:
            tries += 1
            x = rng.uniform(x0, x1)
            z = rng.uniform(z0, z1)
            if not point_in_poly(x, z, poly):
                continue
            # skip the water beds, which are deliberately carved below the
            # field, and a one-cell margin outside them where the carve bleeds
            # through the grid cells that straddle the waterline
            if field == "shore" and (dist_to_poly(x, z, LAKE) < 2.0 or
                                     dist_to_poly(x, z, OUTFLOW) < 2.0 or
                                     depth_inside(x, z, LAKE) > 0.0 or
                                     depth_inside(x, z, OUTFLOW) > 0.0):
                continue
            if field == "cave" and (dist_to_poly(x, z, CAVEPOOL) < 2.0 or
                                    depth_inside(x, z, CAVEPOOL) > 0.0):
                continue
            if in_ramp_corridor(x, z, field) is not None:
                continue          # deliberately notched for the ramp
            s = sample_surface(mesh, x, z)
            if s is None:
                misses += 1
                continue
            errs.append(s - fn(x, z))
        st = stats(errs) or {}
        st["noSurfaceHit"] = misses
        grid_summary[field] = st

    worst = sorted(rows, key=lambda r: -abs(r[8]))[:40]
    total_fail = sum(v.get("failures", 0) for v in objects_summary.values()) + \
        sum(v.get("failures", 0) for v in grid_summary.values())
    doc = {
        "note": "Two-part ground-height verification. `objects` compares the "
                "authored mesh against the Y of every layout object whose "
                "placement was sampled from one of build_layout.py's four "
                "analytic height fields. `freeGrid` compares the mesh against "
                "the fields themselves at random points, excluding the water "
                "beds this build deliberately carves below them.",
        "tolerance": tol,
        "objects": objects_summary,
        "freeGrid": grid_summary,
        "unanchoredObjectsByParent": dict(sorted(unanchored.items(),
                                                 key=lambda kv: -kv[1])),
        "unanchoredNote": "Objects whose Y matches no height field: placed on "
                          "a ramp/shore-walk polyline (poly_y), hanging from "
                          "the cave ceiling, floating on water, or stacked on "
                          "a cliff module. Not this mesh's responsibility.",
        "objectsInsideRampCorridors": {
            "count": len(in_corridor),
            "note": "Standing where a deck was notched to let a ramp cut in. "
                    "They sit up to the ramp's depth above the deck; the level "
                    "owner should re-sample them against the ramp surface or "
                    "move them clear.",
            "sample": in_corridor[:20],
        },
        "objectsInsideWaterPolygons": {
            "count": len(in_water),
            "note": "LAYOUT DEFECT, for the level owner. Ground scatter placed "
                    "at a height-field Y that falls inside a water polygon, so "
                    "it stands in open water above the surface with nothing "
                    "under it. Placed before any water mesh existed. Needs "
                    "moving or culling in build_layout.py -- no terrain mesh "
                    "can rescue it.",
            "byPrefab": dict(sorted(
                {k: sum(1 for r in in_water if r["prefab"] == k)
                 for k in {r["prefab"] for r in in_water}}.items(),
                key=lambda kv: -kv[1])),
            "sample": in_water[:25],
        },
        "totalFailures": total_fail,
        "worst": [{"object": r[0], "parent": r[1], "deck": r[2],
                   "field": r[3], "x": r[4], "z": r[5], "placedY": r[6],
                   "meshY": round(r[7], 4), "error": round(r[8], 4)}
                  for r in worst],
    }
    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)
    return doc


# --------------------------------------------------------------------------
# build
# --------------------------------------------------------------------------

GROUND_BUDGET = (100, 26000)
WATER_BUDGET = (100, 12000)
PIECE_BUDGET = (30, 6000)


def main():
    E.ensure_dirs()
    E.reset_scene()
    load_water_polys()
    global CAVE_POLY
    CAVE_POLY = poly_of(deck_spec("Deck_CaveFloor"))

    paths = route_paths()
    town_paths = [p for p in paths if True]
    ramps = {r["name"]: r for r in layout()["terrain"]["ramps"]}
    # Only the town ramp gets a notch. It climbs to (38, 3, 44), four metres
    # inside a terrace whose surface is 3.0, so without a cut it runs buried.
    # The cave ramp bridges a real gap between two decks. The lake spur is not
    # a cut ramp at all -- the layout calls it "a graded bank, under 8 deg" and
    # its straight from/to does not even follow the Route_LakeSpur path, so
    # trenching the route floor along it would drop scatter into a slot that is
    # not there in the design.
    route_field = ground_route
    town_field = with_ramp_notch(ground_town, [ramps["Ramp_TownFromRoute"]])
    RAMP_CORRIDORS.append((ramps["Ramp_TownFromRoute"], "town"))

    mat_ground = blend_preview_material("M_Ground_TerrainBlend")
    mat_water = water_preview_material("M_Ground_Water")
    mat_water_dark = water_preview_material("M_Ground_WaterCave",
                                            (0.06, 0.26, 0.34), 0.8)
    mat_fall = water_preview_material("M_Ground_Waterfall",
                                      (0.62, 0.82, 0.90), 0.62)

    part = []
    problems = []
    decks_for_check = []
    made = []

    def emit(obj, tris, probs, subfamily, pivot, notes, material,
             vcolour=None, lods=()):
        path = os.path.join(OUT, obj.name + ".fbx")
        E.export_fbx([obj], path)
        entry = {
            "name": obj.name, "family": FAM, "subfamily": subfamily,
            "path": os.path.relpath(path, E.REPO).replace("\\", "/"),
            "triangles": tris,
            "lods": [{"level": i + 1,
                      "path": os.path.relpath(p, E.REPO).replace("\\", "/"),
                      "triangles": t} for i, (p, t) in enumerate(lods)],
            "pivot": pivot,
            "textures": [],
            "windVertexColors": False,
            "notes": notes,
            "unityMaterial": material,
            "budgetClass": "ground",
        }
        if vcolour:
            entry["vertexColors"] = vcolour
        part.append(entry)
        if probs:
            problems.append((obj.name, probs))
        E.log("%-30s %6d tris  %s" % (obj.name, tris, probs or "ok"))

    BLEND_VC = ("PokeLabTerrainBlend layer weights, normalised: "
                "R grass, G dirt, B sand, A rock")

    # ---- decks ----------------------------------------------------------
    deck_jobs = [
        # name, layout deck, height fn, cell, weights, skirt floor, walkable,
        # smooth angle, seam overlap
        ("Env_Ground_RouteFloor", "Deck_RouteFloor", route_field, 1.5,
         w_route(paths), -3.0, True, 30.0, 1.6),
        ("Env_Ground_TownTerrace", "Deck_TownTerrace", town_field, 1.5,
         w_town(town_paths), -1.4, True, 30.0, 1.2),
        ("Env_Ground_ShoreBand", "Deck_ShoreBand", shore_with_beds, 0.9,
         w_shore, -5.4, True, 30.0, 1.6),
        ("Env_Ground_CaveFloor", "Deck_CaveFloor", cave_with_pool, 1.25,
         w_cave, -1.2, True, 20.0, 1.2),
        ("Env_Ground_MassifTier1Top", "Deck_MassifTier1Top", flat(Y_TIER1 + 2.0, 0.45, 4.0),
         3.0, w_rock, -0.5, False, 18.0, 0.0),
        ("Env_Ground_MassifTier2Top", "Deck_MassifTier2Top", flat(Y_TIER2, 0.9, 9.0),
         3.0, w_rock, Y_TIER1 + 1.0, False, 18.0, 0.0),
        ("Env_Ground_MassifRidgeTop", "Deck_MassifRidgeTop", flat(Y_SKYLINE, 1.4, 15.0),
         3.0, w_rock, Y_TIER2, False, 18.0, 0.0),
    ]
    for (name, deck, fn, cell, wfn, floor, walkable, smooth, grow) in deck_jobs:
        spec = deck_spec(deck)
        poly = offset_poly(poly_of(spec), grow)
        obj, tris, probs = make_deck(name, poly, fn, cell, wfn, floor,
                                     mat_ground, smooth=smooth,
                                     budget=GROUND_BUDGET)
        note = ("%s. Closed slab: the boundary is skirted to y=%.1f and capped, "
                "so a MeshCollider on this mesh is watertight. Authored in "
                "world space -- instantiate at (0,0,0) with identity rotation."
                % (spec.get("note", "").split(".")[0] or deck, floor))
        if not walkable:
            note += " NOT walkable, keep off the NavMesh."
        emit(obj, tris, probs, "Ground", "world origin (0,0,0)", note,
             "PokeLab/TerrainBlend", BLEND_VC)
        decks_for_check.append((name, obj, poly, walkable))
        made.append(obj)

    # cave ceiling, faces down
    cave_poly = poly_of(deck_spec("Deck_CaveFloor"))
    obj, tris, probs = ceiling_solid("Env_Ground_CaveCeiling", cave_poly, 1.5,
                                     mat_ground, budget=GROUND_BUDGET)
    emit(obj, tris, probs, "Ground", "world origin (0,0,0)",
         "Chamber roof at y=6.5, dipping to meet the walls at the rim. Built "
         "as a 1.4 m slab so the massif above reads solid and the ceiling does "
         "not vanish when the camera clips it. Faces down. World space.",
         "PokeLab/TerrainBlend", BLEND_VC)
    made.append(obj)

    # river channel east of the shore deck, so the outflow has a bed
    river_poly = [(30.0, 2.0), (42.0, -4.0), (42.0, -18.0), (28.0, -18.0),
                  (28.0, -8.0)]
    obj, tris, probs = make_deck("Env_Ground_RiverChannel", river_poly,
                                 river_channel, 1.0, w_shore, -5.0,
                                 mat_ground, smooth=30.0,
                                 budget=GROUND_BUDGET)
    emit(obj, tris, probs, "Ground", "world origin (0,0,0)",
         "Not in the layout's deck list, but Water_Outflow runs east past the "
         "shore band's edge at x=34 and had no bed under it. Same channel "
         "profile as the shore deck uses, so the two agree along the seam.",
         "PokeLab/TerrainBlend", BLEND_VC)
    made.append(obj)

    # ---- water ----------------------------------------------------------
    for (name, wname, cell, mat, note) in (
        ("Env_Water_Lake", "Water_Lake", 1.0, mat_water,
         "Lake surface at y=-2.0 over a bed at -3.8 carved into "
         "Env_Ground_ShoreBand. 1 m grid: PokeLabWater's default wavelength is "
         "4.2 m, so this resolves the vertex wave four ways over. Needs Depth "
         "Texture and Opaque Texture ON in the URP renderer."),
        ("Env_Water_Outflow", "Water_Outflow", 0.9, mat_water,
         "The east outflow. Bed at -3.2 is carried by Env_Ground_ShoreBand "
         "and Env_Ground_RiverChannel."),
        ("Env_Water_CavePool", "Water_CavePool", 0.6, mat_water_dark,
         "Still cave pool at y=1.05 over a bed at 0.3 carved into "
         "Env_Ground_CaveFloor. Set wave amplitude near zero on this one -- "
         "the layout calls for still, dark water."),
    ):
        spec = water_spec(wname)
        obj, tris, probs = make_water(name, poly_of(spec), spec["surfaceY"],
                                      cell, mat, budget=WATER_BUDGET)
        emit(obj, tris, probs, "Water", "world origin (0,0,0)", note,
             "PokeLab/Water")
        made.append(obj)

    # ---- ramps ----------------------------------------------------------
    ramp_blend = {
        "Ramp_TownFromRoute": (ground_route, ground_town),
        "Ramp_CaveFromRoute": (ground_route, ground_cave),
        "Ramp_LakeSpur": (ground_route, ground_shore),
    }
    for spec in layout()["terrain"]["ramps"]:
        ba, bb = ramp_blend[spec["name"]]
        name = "Env_Ramp_" + spec["name"].split("_", 1)[1]
        obj, tris, probs = make_ramp(name, spec, ba, bb, mat_ground,
                                     budget=PIECE_BUDGET)
        emit(obj, tris, probs, "Ramp", "world origin (0,0,0)",
             "%s -> %s, %.1f m wide, %s. Both ends cross-fade into the deck "
             "height field over 1.2 m so there is no step at the join."
             % (spec["from"], spec["to"], spec["width"], spec["grade"]),
             "PokeLab/TerrainBlend", BLEND_VC)
        made.append(obj)

    # ---- ledges ---------------------------------------------------------
    for i, spec in enumerate(layout()["terrain"]["ledges"]):
        name = "Env_Ledge_" + spec["name"].replace("Ledge_", "")
        obj, tris, probs = make_ledge(name, spec, mat_ground,
                                      budget=PIECE_BUDGET, seed=41 + i)
        emit(obj, tris, probs, "Ledge", "world origin (0,0,0)",
             "One-way hop-down, %.1f m drop, grass cap overhangs the face. "
             "PlayerLocomotion's 0.45 m max step makes this a real barrier "
             "from below. World space." % spec["drop"],
             "PokeLab/TerrainBlend", BLEND_VC)
        made.append(obj)

    # ---- waterfall ------------------------------------------------------
    wf = layout()["terrain"]["waterfalls"][0]
    bm = E.bm_new()
    waterfall_sheet(bm, wf["top"], wf["bottom"], wf["width"], 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, "Env_Waterfall_Main", [mat_fall])
    E.finalize(obj, smooth_angle=80.0)
    E.apply_transforms(obj)
    E.uv_planar(obj)
    # the falling sheet still wants the foliage wind convention: the shader the
    # integrator picks reads R as a "how free is this vertex" mask either way
    E.wind_vcol_from_height(obj, phase=0.0, power=1.0)
    tris, probs = E.validate(obj, budget=PIECE_BUDGET, need_vcol=True,
                             strict=False)
    emit(obj, tris, probs, "Waterfall", "world origin (0,0,0)",
         "The falling sheet for Waterfall_Main: top %s, bottom %s, %.1f m "
         "wide, %.1f m drop. Narrows and bows as it falls so the silhouette "
         "is not a rectangle. Env_Waterfall_Shelf is the lip; this is the fall."
         % (wf["top"], wf["bottom"], wf["width"], wf["dropMetres"]),
         "PokeLab/Water (scrolling) or a VFX sheet")
    made.append(obj)

    bm = E.bm_new()
    plunge_ring(bm, (wf["bottom"][0], Y_WATER + 0.03, wf["bottom"][2]),
                wf["width"] * 1.5, 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, "Env_Waterfall_Plunge_Main", [mat_fall])
    E.finalize(obj, smooth_angle=80.0)
    E.apply_transforms(obj)
    E.uv_planar(obj)
    E.wind_vcol_from_height(obj, power=1.0)
    tris, probs = E.validate(obj, budget=PIECE_BUDGET, need_vcol=True,
                             strict=False)
    emit(obj, tris, probs, "Waterfall", "world origin (0,0,0)",
         "Foam ring at the plunge pool, sitting 3 cm proud of the lake "
         "surface. World space, matched to Waterfall_Main's bottom point.",
         "PokeLab/Water (foam) or a VFX sheet")
    made.append(obj)

    # ---- modular kit pieces ---------------------------------------------
    def ledge_weights(x, z, y, nz):
        return (1.6 if nz > 0.6 else 0.02, 0.22, 0.03,
                0.1 + 1.6 * (1.0 - clamp((nz - 0.5) / 0.4)))

    for (name, length, seed, note) in (
        ("Env_Ledge_2m", 2.0, 61,
         "2 m one-way hop-down, 1.0 m drop, grass cap overhanging the face."),
        ("Env_Ledge_4m", 4.0, 62,
         "4 m one-way hop-down, 1.0 m drop. Same end profile as Env_Ledge_2m, "
         "so a run of them is seamless."),
    ):
        bm = E.bm_new()
        module_ledge(bm, length, 1.0, 1.15, 0, seed=seed)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        obj = E.bm_to_obj(bm, name, [mat_ground])
        E.finalize(obj, smooth_angle=24.0)
        E.pivot_to_base(obj, xy='corner')
        E.apply_transforms(obj)
        E.uv_box_walls(obj)
        paint_weights(obj, ledge_weights)
        tris, probs = E.validate(obj, budget=PIECE_BUDGET, need_vcol=True,
                                 strict=False, closed=True)
        emit(obj, tris, probs, "Ledge",
             "module corner at origin, snaps on 0.5 m grid, runs along +X",
             note + " The layout fakes its hop-downs by burying 2 m of a 3 m "
             "cliff, which gives a 3 m visual drop for a 1 m gameplay drop; "
             "this is the piece that stops doing that.",
             "PokeLab/TerrainBlend", BLEND_VC)
        made.append(obj)

    bm = E.bm_new()
    module_ledge_corner(bm, 1.5, 1.0, 1.15, 0, seed=63)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, "Env_Ledge_Corner", [mat_ground])
    E.finalize(obj, smooth_angle=24.0)
    E.pivot_to_base(obj, xy='corner')
    E.apply_transforms(obj)
    E.uv_box_walls(obj)
    paint_weights(obj, ledge_weights)
    tris, probs = E.validate(obj, budget=PIECE_BUDGET, need_vcol=True,
                             strict=False)
    emit(obj, tris, probs, "Ledge",
         "module corner at origin, snaps on 0.5 m grid",
         "Outside corner for a ledge run, 1.5 m arms, so a run can turn a "
         "corner without leaving a gap.",
         "PokeLab/TerrainBlend", BLEND_VC)
    made.append(obj)

    for (name, w, h, seed, note) in (
        ("Env_Waterfall_Sheet_2x4", 2.0, 4.0, 71,
         "Tileable falling sheet, 2 m wide by 4 m tall, in the XZ plane at "
         "y=0 with the pivot at the top centre. Left and right edge profiles "
         "are the same function of u so sheets butt seamlessly, and the top "
         "and bottom edges are straight so they stack. Env_Waterfall_Shelf is "
         "the lip; this is the fall."),
        ("Env_Waterfall_Sheet_1x4", 1.0, 4.0, 72,
         "Half-width sheet, for narrower falls and for breaking the seam line "
         "on a wide one."),
    ):
        bm = E.bm_new()
        module_fall_sheet(bm, w, h, 0, seed=seed)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        obj = E.bm_to_obj(bm, name, [mat_fall])
        E.finalize(obj, smooth_angle=80.0)
        E.set_pivot(obj, (0.0, 0.0, max(v.co.z for v in obj.data.vertices)))
        E.apply_transforms(obj)
        E.uv_planar(obj)
        E.wind_vcol_from_height(obj, power=1.0)
        tris, probs = E.validate(obj, budget=PIECE_BUDGET, need_vcol=True,
                                 strict=False)
        emit(obj, tris, probs, "Waterfall",
             "top centre of the sheet (hangs downward), snaps on 0.5 m grid",
             note, "PokeLab/Water (scrolling) or a VFX sheet")
        made.append(obj)

    bm = E.bm_new()
    module_plunge(bm, 1.9, 0)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, "Env_Waterfall_Plunge", [mat_fall])
    E.finalize(obj, smooth_angle=80.0)
    E.apply_transforms(obj)
    E.uv_planar(obj)
    E.wind_vcol_from_height(obj, power=1.0)
    tris, probs = E.validate(obj, budget=PIECE_BUDGET, need_vcol=True,
                             strict=False)
    emit(obj, tris, probs, "Waterfall", "centre, sits on the water surface",
         "Foam ring for the base of any fall. Place it 2-4 cm proud of the "
         "water surface so it is never coplanar with it.",
         "PokeLab/Water (foam) or a VFX sheet")
    made.append(obj)

    # ---- verification ---------------------------------------------------
    report = os.path.join(E.PREVIEWS, "ground_height_check.json")
    doc = verify_heights(decks_for_check, report)
    E.log("---- ground height check (tolerance %.3f m)" % doc["tolerance"])
    for title, blob in (("objects", doc["objects"]),
                        ("freeGrid", doc["freeGrid"])):
        for field, s in sorted(blob.items()):
            E.log("  %-9s %-6s n=%4d  mean %.5f  p99 %.5f  max %.5f  FAIL %d"
                  % (title, field, s.get("samples", 0), s.get("meanAbsError", 0),
                     s.get("p99AbsError", 0), s.get("maxAbsError", 0),
                     s.get("failures", 0)))
    if doc["totalFailures"]:
        E.log("  !! %d height samples outside tolerance" % doc["totalFailures"])

    E.write_part(FAM, part, part=PART)
    E.log("---- %d ground assets, %d with problems" % (len(part), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))
    if "--keep" in sys.argv:
        bpy.ops.wm.save_as_mainfile(
            filepath=os.path.join(E.PREVIEWS, "_ground.blend"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
