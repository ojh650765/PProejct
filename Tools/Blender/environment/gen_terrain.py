"""
Terrain family: seam-tiling cliff modules, eroded boulders, cave entrance and
dressing, riverbank edging, waterfall shelf, stepping stones and a wooden
bridge.

Two ideas carry this family:

* Seamless tiling.  A cliff module's end profile is a pure function of height,
  evaluated from a fixed set of harmonics whose fundamental period is the
  0.5 m snap grid.  Any two modules therefore share an identical cross section
  at their join, whatever their length, and the seam disappears.
* Erosion, not faceting.  Boulders start as a subdivided icosphere, get layered
  fbm displacement, then a *directional* pass that flattens the top, undercuts
  one flank and sags the base -- the shape a rock ends up with after weather,
  rather than a noisy ball.
"""

import sys
import os
import math
import random

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Matrix

import envlib as E
import textures as T

FAM = "Terrain"
OUT = E.FAMILY_DIR[FAM]

ROCK_GREY, ROCK_WARM, STRATA, CAVE_ROCK = 0, 1, 2, 3
DIRT, SAND, GRAVEL, STALACTITE = 4, 5, 6, 7
MOSS_ROCK, WET_ROCK, WOOD, ROPE = 8, 9, 10, 11
WATERFALL, STEPPING, RUBBLE, EARTH = 12, 13, 14, 15

GRID = 0.5   # modular snap


# --------------------------------------------------------------------------
# the tiling seam profile
# --------------------------------------------------------------------------

SEAM_HARMONICS = [(1, 0.085, 0.7), (2, 0.052, 2.1), (3, 0.031, 4.3),
                  (5, 0.018, 1.2), (8, 0.011, 5.5)]


def seam_offset(z, height):
    """Horizontal displacement of the cliff face at height z, at a module end.
    Depends on z only, so every module end matches every other."""
    t = z / max(height, 1e-5)
    o = 0.0
    for (k, amp, ph) in SEAM_HARMONICS:
        o += amp * math.sin(2.0 * math.pi * k * t + ph)
    return o


PERIOD = GRID * 4.0        # 2 m: the tiling period of every cliff module
PLATE_W = GRID * 0.5       # 0.25 m rock plates across the face
PLATE_H = 0.42


def _hash2(i, j, salt=0):
    h = (i * 73856093) ^ (j * 19349663) ^ (salt * 83492791)
    h = (h ^ (h >> 13)) * 1274126177
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0


def _plate(u, z, salt):
    """Jointed rock plates.  Column index wraps on the tiling period so the
    value at u=0 equals the value at u=PERIOD; the blend keeps every vertex
    single-valued while still reading as a hard step."""
    ncol = int(round(PERIOD / PLATE_W))
    fu = u / PLATE_W
    ci = int(math.floor(fu))
    fr = fu - ci
    row = int(math.floor(z / PLATE_H))
    a = _hash2(ci % ncol, row, salt)
    b = _hash2((ci + 1) % ncol, row, salt)
    # sharp 12% transition -> a joint, not a ripple
    t = min(1.0, max(0.0, (fr - 0.88) / 0.12))
    t = t * t * (3 - 2 * t)
    v = a * (1 - t) + b * t
    # rows also step, blended over the top eighth of each course
    fz = z / PLATE_H - row
    a2 = _hash2(ci % ncol, row + 1, salt + 5)
    tz = min(1.0, max(0.0, (fz - 0.86) / 0.14))
    tz = tz * tz * (3 - 2 * tz)
    return v * (1 - tz * 0.45) + a2 * (tz * 0.45)


def cliff_face_offset(u, z, length, height, seed):
    """Offset of the cliff face at (u along the module, z up).  Periodic in u
    with period PERIOD, so the value at u=0 equals the value at u=length for
    any length that is a multiple of it -- that is what makes the seam vanish.

    Three tiers, per the hard-surface detail hierarchy: a large form, jointed
    plates, and sedimentary ledges.  No fine tier -- the atlas carries that."""
    base = seam_offset(z, height)
    o = base
    ph0 = seed * 0.7139
    # tier 1: large form, big bulges and hollows
    for (k, amp, ph) in ((1, 0.42, 0.0), (2, 0.24, 1.9), (3, 0.13, 3.4)):
        w = 2.0 * math.pi * k * u / PERIOD
        a = amp * (0.50 + 0.50 * math.sin(2.4 * z / max(height, 1e-5) *
                                          math.pi + ph0 + k))
        o += a * math.sin(w + ph + ph0 * k) - a * 0.5
    # tier 2: jointed plates stepping in and out
    o += (_plate(u, z, int(seed) & 255) - 0.5) * 0.30
    # tier 3: sedimentary ledges that catch the light along the whole face
    t = z / max(height, 1e-5)
    for (lz, lw, ld) in ((0.22, 0.045, 0.26), (0.53, 0.038, 0.19),
                         (0.78, 0.032, 0.13)):
        o += ld * math.exp(-((t - lz) / lw) ** 2)
        o -= ld * 0.55 * math.exp(-((t - lz - 0.07) / (lw * 1.6)) ** 2)
    # base skirt: the lowest course always sits proud of everything above it,
    # so a module never leaves a notch for the ground to show through
    skirt = max(0.0, 1.0 - t / 0.10)
    o = o * (1.0 - 0.45 * skirt) + (0.34 + base * 0.3) * skirt
    return o


def cliff_module(bm, length, height, seed, mat=STRATA, mat_top=EARTH,
                 vsegs=None, back=0.9, corner=False):
    """A wall panel with a real eroded face, a grass/earth cap and a flat back.
    Pivot ends up at the module corner so it snaps on the 0.5 m grid."""
    usegs = max(4, int(round(length / PLATE_W)))
    if vsegs is None:
        vsegs = max(6, int(round(height / (PLATE_H * 0.5))))
    rng = random.Random(seed)
    grid = []
    for i in range(usegs + 1):
        u = length * i / usegs
        col = []
        for j in range(vsegs + 1):
            t = j / float(vsegs)
            z = height * (t ** 1.06)
            y = -cliff_face_offset(u, z, length, height, seed)
            # batter: cliffs lean back as they rise
            y -= 0.10 * height * (t ** 1.7)
            col.append(bm.verts.new((u, y, z)))
        grid.append(col)

    faces = []
    for i in range(usegs):
        for j in range(vsegs):
            f = bm.faces.new((grid[i][j], grid[i + 1][j],
                              grid[i + 1][j + 1], grid[i][j + 1]))
            f.material_index = mat
            f.smooth = False
            faces.append(f)

    # back slab so the module is a closed solid the integrator can butt against
    back_cols = []
    for i in range(usegs + 1):
        u = length * i / usegs
        col = []
        for j in range(vsegs + 1):
            t = j / float(vsegs)
            z = height * (t ** 1.06)
            col.append(bm.verts.new((u, back, z)))
        back_cols.append(col)
    for i in range(usegs):
        for j in range(vsegs):
            f = bm.faces.new((back_cols[i][j], back_cols[i][j + 1],
                              back_cols[i + 1][j + 1], back_cols[i + 1][j]))
            f.material_index = ROCK_GREY
            f.smooth = False
    # cap, floor and the two ends
    for i in range(usegs):
        f = bm.faces.new((grid[i][vsegs], grid[i + 1][vsegs],
                          back_cols[i + 1][vsegs], back_cols[i][vsegs]))
        f.material_index = mat_top
        f.smooth = False
        f2 = bm.faces.new((grid[i][0], back_cols[i][0],
                           back_cols[i + 1][0], grid[i + 1][0]))
        f2.material_index = ROCK_GREY
        f2.smooth = False
    for (cols, flip) in ((0, False), (usegs, True)):
        for j in range(vsegs):
            quad = (grid[cols][j], grid[cols][j + 1],
                    back_cols[cols][j + 1], back_cols[cols][j])
            if flip:
                quad = tuple(reversed(quad))
            f = bm.faces.new(quad)
            f.material_index = ROCK_GREY
            f.smooth = False
    return faces


# --------------------------------------------------------------------------
# eroded rock forms
# --------------------------------------------------------------------------

# --------------------------------------------------------------------------
# bevel that does not repaint the mesh grey
#
# bmesh.ops.bevel gives every face it creates material_index 0 -- it does not
# inherit from the edge's own faces.  Slot 0 in this family is ROCK_GREY, so
# every bevel run quietly repaints its whole chamfer skeleton grey.  Measured on
# one talus stone: 16 RUBBLE faces in, 16 RUBBLE + 64 ROCK_GREY out.  Every
# boulder, scatter and stepping stone in the kit has shipped mostly-grey since
# the bevel pass was added, and nobody saw it because grey rock on grey rock is
# invisible -- until the cave mouth put moss, strata and gravel next to each
# other and the grey skeleton showed up between them as a mosaic.
#
# It is worse than a wrong colour.  uv_pack_into_cells squeezes one material's
# *entire* island layout into one 1/4 x 1/4 atlas cell, so the more faces a
# material collects the smaller every island gets.  Slot 0 collecting 2418 of a
# 3484-face mesh drove its median face UV footprint to 0.3 px of a 512 px cell:
# one texel per face, i.e. a flat colour per face.
#
# The general fix belongs in envlib.bevel(), which would mend every family at
# once; that is a wide change to make while other workstreams are live, so this
# is fixed here, for the Terrain family only, and reported upward.
# --------------------------------------------------------------------------

def bevel_keep_mats(bm, **kw):
    """E.bevel_sharp, with the faces it creates inheriting the material of the
    face they grew out of instead of falling to slot 0."""
    from mathutils.kdtree import KDTree
    lay = bm.faces.layers.int.get("mtag")
    if lay is None:
        lay = bm.faces.layers.int.new("mtag")
    for f in bm.faces:
        f[lay] = f.material_index + 1

    src = [(f.calc_center_median().copy(), f.material_index) for f in bm.faces]
    E.bevel_sharp(bm, **kw)

    if not src:
        return
    kd = KDTree(len(src))
    for i, (c, _) in enumerate(src):
        kd.insert(c, i)
    kd.balance()
    for f in bm.faces:
        if f[lay] > 0:
            f.material_index = f[lay] - 1
        else:
            _, i, _ = kd.find(f.calc_center_median())
            f.material_index = src[i][1]
    bm.faces.layers.int.remove(lay)


def erode(bm, verts, seed, amount=0.30, freq=1.5, flatten_top=0.35,
          undercut=0.30, sag=0.20, sharpness=0.55):
    """Weathering pass.  Layered fbm for the mass, then directional terms:
    the top wears flat, one flank gets undercut, the base spreads."""
    rng = random.Random(seed)
    axis = Vector((math.cos(seed * 1.7), math.sin(seed * 2.3), 0)).normalized()
    zs = [v.co.z for v in verts]
    lo, hi = min(zs), max(zs)
    span = max(hi - lo, 1e-5)
    for v in verts:
        n = v.co.normalized() if v.co.length > 1e-6 else Vector((0, 0, 1))
        p = v.co * freq
        d = E.fbm(p, 4, seed=seed) * amount
        d += E.fbm(p * 3.1, 3, seed=seed + 7.7) * amount * 0.34
        # ridged component gives crisp facet breaks instead of lumpy noise
        d += (E.ridged(p * 1.9, 3, seed=seed + 3.3) - 0.5) * amount * sharpness
        v.co += n * d
        t = (v.co.z - lo) / span
        if t > 0.62:
            v.co.z -= (t - 0.62) * span * flatten_top
        s = v.co.dot(axis)
        if s > 0 and 0.25 < t < 0.75:
            v.co -= axis * s * undercut * math.sin((t - 0.25) * math.pi / 0.5)
        if t < 0.25:
            k = (0.25 - t) / 0.25
            v.co.x *= 1.0 + sag * k
            v.co.y *= 1.0 + sag * k
            v.co.z += k * span * 0.05


def boulder(bm, rng, radius=0.8, subd=2, seed=1, mat=ROCK_GREY, squash=0.78,
            amount=0.30, sharpness=0.55, bury=0.18):
    tmp = bmesh.new()
    bmesh.ops.create_icosphere(tmp, subdivisions=subd, radius=radius)
    for v in tmp.verts:
        v.co.z *= squash
    erode(tmp, list(tmp.verts), seed, amount * radius, 1.9 / max(radius, 0.2),
          sharpness=sharpness)
    # sink so it sits in the ground rather than resting on it like a ball
    zs = [v.co.z for v in tmp.verts]
    lo = min(zs)
    for v in tmp.verts:
        v.co.z -= lo + (max(zs) - lo) * bury
    keep = [f for f in tmp.faces if f.calc_center_median().z > -0.001]
    if len(keep) > 8:
        bmesh.ops.delete(tmp, geom=[f for f in tmp.faces if f not in keep],
                         context='FACES')
        # close the bottom
        bound = [e for e in tmp.edges if len(e.link_faces) == 1]
        if bound:
            bmesh.ops.holes_fill(tmp, edges=bound)
    for f in tmp.faces:
        f.material_index = mat
        f.smooth = False
    bevel_keep_mats(tmp, width=radius * 0.030, segments=2, angle_deg=44.0,
                    mat_break=False)
    me = bpy.data.meshes.new("tmp_b")
    tmp.to_mesh(me)
    tmp.free()
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)


def stone_spike(bm, rng, height, radius, seed, mat=STALACTITE, down=False,
                sides=7):
    """Stalactite / stalagmite: a bumpy tapered cone with drip rings."""
    n = 7
    pts = []
    radii = []
    for i in range(n):
        t = i / float(n - 1)
        z = height * t
        wob = Vector((math.sin(t * 5.0 + seed) * radius * 0.30,
                      math.cos(t * 4.1 + seed * 1.7) * radius * 0.30, 0))
        pts.append(Vector((wob.x, wob.y, -z if down else z)))
        r = radius * ((1.0 - t) ** 1.55)
        r *= 1.0 + 0.26 * math.sin(t * 11.0 + seed * 3.0)   # drip rings
        radii.append(max(r, radius * 0.035))
    faces, rings = E.bm_polytube(bm, pts, radii, sides, mat,
                                 cap_start=True, cap_end=True, smooth=False)
    return faces


# --------------------------------------------------------------------------
# assets
# --------------------------------------------------------------------------

def a_cliff(bm, rng, length, height, seed, corner=False):
    if not corner:
        cliff_module(bm, length, height, seed)
        return
    # corner: two panels meeting at 90 degrees, both ends carrying the profile
    cliff_module(bm, length, height, seed)
    tmp = bmesh.new()
    cliff_module(tmp, length, height, seed + 31)
    me = bpy.data.meshes.new("tmp_c")
    tmp.to_mesh(me)
    tmp.free()
    rot = Matrix.Rotation(math.radians(90), 4, 'Z')
    me.transform(rot)
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)


def a_rock_scatter(bm, rng, n, spread, rlo, rhi, seed, mats):
    for i in range(n):
        a = rng.uniform(0, 6.2832)
        d = spread * math.sqrt(rng.random())
        r = rng.uniform(rlo, rhi)
        tmp = bmesh.new()
        boulder(tmp, rng, r, 1 if r < 0.16 else (2 if r < 0.5 else 3),
                seed + i * 17,
                mats[rng.randrange(len(mats))],
                squash=rng.uniform(0.58, 0.95), amount=rng.uniform(0.30, 0.46),
                sharpness=rng.uniform(0.40, 0.95))
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        m = (Matrix.Translation((math.cos(a) * d, math.sin(a) * d, 0)) @
             Matrix.Rotation(rng.uniform(0, 6.2832), 4, 'Z') @
             Matrix.Rotation(rng.uniform(-0.22, 0.22), 4, 'X'))
        me.transform(m)
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)


# --------------------------------------------------------------------------
# the cave mouth
#
# The first version of this asset was a freestanding arch: a 1.8 m thick ring of
# rock with a hole in it, standing on grass.  Rendered at the game camera it read
# exactly as what it was -- a croissant of stone dropped on a hillside, with
# daylight visible straight through the opening.  Nothing about it said "you can
# walk into a mountain here".
#
# Four things fix that, and all four have to be present at once:
#
#   scale      the clear opening -- what is left after the brow overhangs -- has
#              to clear a 1.7 m trainer with obvious headroom, and hold that
#              headroom across the 3 m path, not only on the centre line.  The
#              exponent on the arch profile (_mouth_point) is what buys that.
#   bedding    the mouth is cut out of a headwall that is part of the hillside:
#              strata ledges running past the opening and off both edges, jambs
#              thickened either side, and the two ends of the wall swept forward
#              so the player walks into a shallow re-entrant before they reach
#              the stone.  Talus and fallen blocks pile at the feet.
#   depth      a flat dark plane across an arch reads as a painted door from a
#              38 degree camera.  The bore runs CAVE_THROAT metres back, and the
#              sight line that grazes the brow at that pitch lands on the throat
#              floor well before the end wall -- so the back of the hole is never
#              in frame and the interior is only ever falloff.
#   invitation moss and wet rock on the lower jamb where water tracks out, a worn
#              gravel threshold where the path meets stone, and a notched lip the
#              level can hang Env_Vine_Hanging_A/B from.
#
# Blender axes here: +X across the mouth, +Y into the hill, +Z up.  The player
# stands at -Y.
# --------------------------------------------------------------------------

CAVE_HW = 8.1        # headwall width -- the gorge pinches to 9 m at the mouth,
                     # and the talus fan has to fit inside that too
CAVE_HH = 6.3        # headwall height at the crown of its broken skyline
CAVE_OPEN_W = 5.4    # clear opening at its widest
CAVE_OPEN_H = 4.35   # clear opening at the crown
CAVE_THROAT = 5.8    # how far the bore runs back into the rock.  Measured, not
                     # guessed: the sight line that grazes the brow at the
                     # camera's 38 degrees lands on the throat floor about 4 m
                     # in, so 5.8 keeps the end wall out of frame with margin.
CAVE_BACK = 6.4      # where the buried block is closed off behind the bore


def _mouth_point(t, seed):
    """A point (x, z) on the clear opening outline, t running 0..1 from the left
    foot up over the crown to the right foot.

    The 0.72 exponent on sin() is the whole trick.  A semicircle loses its
    headroom the instant you step off the centre line: at a third of the way out
    a 4.3 m semicircular arch is down to 3.1 m and dropping fast, and a trainer
    walking the left-hand side of a 3 m path ducks.  At 0.72 the crown height
    holds across the middle third and only then falls away, which is the
    difference between a doorway and a keyhole."""
    a = math.pi * t
    rx = CAVE_OPEN_W * 0.5 * (1.0 + 0.045 * math.sin(a * 3.0 + seed))
    x = -math.cos(a) * rx
    z = (math.sin(a) ** 0.72) * CAVE_OPEN_H
    # asymmetry: the left haunch has weathered back further than the right
    z *= 1.0 - 0.055 * math.cos(a * 2.0 + 0.6) - 0.03 * math.sin(a * 5.0 + seed)
    return x, max(z, 0.0)


def _brow(t):
    """How far the mouth flares forward (-Y) at parameter t.  Over a metre at the
    crown, nothing at the feet: the overhanging brow is what drops the throat
    into shadow instead of leaving a lit hole in a wall."""
    a = math.pi * t
    return 0.98 * (math.sin(a) ** 1.5) + 0.20 * math.sin(a)


# Outer edge of the headwall, as (t, x/CAVE_HW, z/CAVE_HH).  Not a rectangle:
# the skyline is broken and both ends drop away, so the block reads as a piece of
# hillside rather than a panel someone leaned against the hill.
_WALL_KEYS = [
    (0.00, -0.500, 0.000), (0.06, -0.500, 0.190), (0.13, -0.496, 0.480),
    (0.21, -0.470, 0.700), (0.30, -0.418, 0.860), (0.40, -0.318, 0.955),
    (0.50, -0.030, 1.000), (0.60, 0.245, 0.962), (0.70, 0.392, 0.868),
    (0.79, 0.462, 0.716), (0.87, 0.500, 0.492), (0.94, 0.500, 0.208),
    (1.00, 0.500, 0.000),
]


def _wall_point(t, seed):
    for k in range(len(_WALL_KEYS) - 1):
        t0, x0, z0 = _WALL_KEYS[k]
        t1, x1, z1 = _WALL_KEYS[k + 1]
        if t <= t1 or k == len(_WALL_KEYS) - 2:
            u = 0.0 if t1 <= t0 else (t - t0) / (t1 - t0)
            u = min(max(u, 0.0), 1.0)
            x = (x0 + (x1 - x0) * u) * CAVE_HW
            z = (z0 + (z1 - z0) * u) * CAVE_HH
            break
    # broken skyline: only where the edge is actually the top, never at the feet
    rise = min(1.0, z / (CAVE_HH * 0.35))
    z += (0.22 * math.sin(t * 17.0 + seed) +
          0.12 * math.sin(t * 31.0 + seed * 2.3)) * rise
    x += 0.09 * math.sin(t * 23.0 + seed * 1.7) * rise
    return x, max(z, 0.0)


def _wall_relief(x, z, seed):
    """Y of the headwall face at (x, z).  More negative is further toward the
    player.  Same three-tier vocabulary as the cliff modules -- large form,
    jointed plates, sedimentary ledges -- so the mouth and the gorge walls that
    squeeze in beside it are speaking the same language."""
    hx = abs(x) / (CAVE_HW * 0.5)
    o = 0.0
    # the two ends sweep forward, so the player walks into a shallow re-entrant
    # before they reach the stone.  This is most of what makes the mouth an
    # arrival: the rock closes around you first, then the hole appears.
    o -= 2.05 * E.smoothstep(0.33, 1.0, hx) ** 1.3
    # batter: rock faces lean back as they rise
    o += 0.085 * z
    # jambs: thickened piers either side of the opening, foot to shoulder
    for sx in (-1.0, 1.0):
        d = (x - sx * (CAVE_OPEN_W * 0.5 + 0.50)) / 0.95
        o -= 0.44 * math.exp(-d * d) * math.exp(-((z - 1.45) / 2.2) ** 2)
    # sedimentary ledges, unbroken across the whole face and off both edges --
    # the single clearest cue that the opening was cut out of this rock rather
    # than parked in front of it
    for (lz, lw, ld) in ((1.15, 0.34, 0.25), (2.62, 0.29, 0.19),
                         (4.30, 0.26, 0.15), (5.50, 0.22, 0.11)):
        o -= ld * math.exp(-((z - lz) / lw) ** 2)
        o += ld * 0.52 * math.exp(-((z - lz - 0.42) / (lw * 1.7)) ** 2)
    # jointed plates
    o -= (_plate(x + CAVE_HW, z, int(seed) & 255) - 0.5) * 0.32
    # large form
    for (k, amp, ph) in ((1, 0.30, 0.0), (2, 0.17, 1.9), (3, 0.09, 3.4)):
        o -= amp * math.sin(2.0 * math.pi * k * x / (CAVE_HW * 0.62) + ph + seed)
    return o


def _wall_mat(x, z, u, seed):
    """Material for a point on the headwall face.  u is the radial parameter,
    0 at the lip of the opening and 1 at the outer edge of the wall."""
    if u < 0.12:
        return WET_ROCK          # the lip is permanently damp
    if u < 0.46 and z < 2.5 and _hash2(int(x * 2.6), int(z * 2.6),
                                       int(seed)) < 0.42:
        return MOSS_ROCK         # blotchy moss where water tracks out
    if z < 0.55:
        return RUBBLE            # the face disappearing into its own talus
    zb = z + 0.30 * math.sin(x * 0.42 + seed) + 0.13 * math.sin(x * 1.30)
    for (lo, hi, mat) in ((0.55, 1.40, STRATA), (1.40, 2.30, ROCK_WARM),
                          (2.30, 3.15, STRATA), (3.15, 4.35, ROCK_GREY),
                          (4.35, 5.25, STRATA)):
        if lo <= zb < hi:
            return mat
    return ROCK_WARM


def _bore_ring(d, seed):
    """One cross section of the throat at depth d, as a closed loop of (x, y, z).

    Three things happen with depth and all three exist to stop the player seeing
    the back of the hole: the section shrinks, it drifts sideways so the end wall
    swings off the sight line, and the floor lifts slightly."""
    f = d / CAVE_THROAT
    sx = 1.0 - 0.13 * f
    sz = 1.0 - 0.17 * f
    bend = 1.75 * (f ** 1.7)
    floor = 0.34 * (f ** 1.5)
    arch_n = 22
    floor_n = 8
    pts = []
    for i in range(arch_n):
        t = i / float(arch_n - 1)
        x, z = _mouth_point(t, seed)
        y = d - _brow(t) * (1.0 - min(1.0, d / 1.30))
        # rough the bore up so it is a broken tube, not a extruded profile
        wob = 0.13 * math.sin(t * 9.0 + d * 1.7 + seed) + \
            0.07 * math.sin(t * 21.0 + d * 3.1)
        pts.append((x * sx + bend + wob * 0.6,
                    y,
                    max(z * sz + floor + wob, floor)))
    # back across the floor, right foot to left foot
    xr = pts[-1][0]
    xl = pts[0][0]
    for i in range(1, floor_n):
        u = i / float(floor_n)
        x = xr + (xl - xr) * u
        z = floor + 0.11 * math.sin(u * 7.0 + d * 2.2 + seed) * (
            1.0 - abs(u - 0.5) * 1.4)
        pts.append((x, d, max(z, 0.0)))
    return pts


def _scree(bm, rng, seed):
    """Fallen blocks and talus, in the same vocabulary as Env_Rock_Boulder_*.

    Placed rather than scattered: a heavy block slumped against each jamb, a
    talus fan spilling out of the mouth and thinning downhill, and a handful of
    pieces bedded against the wall away from the opening so the wall's foot is
    never a clean line where it meets the ground."""
    blocks = [
        # (x, y, radius, subd, material, squash)
        (-3.00, -0.45, 0.86, 2, STRATA, 0.72),
        (3.15, -0.30, 0.74, 2, ROCK_WARM, 0.66),
        (-2.25, -1.55, 0.62, 2, ROCK_GREY, 0.58),
        (1.55, -1.90, 0.55, 2, ROCK_WARM, 0.62),
        (3.45, -1.35, 0.56, 2, MOSS_ROCK, 0.60),
    ]
    for (x, y, r, subd, mat, squash) in blocks:
        tmp = bmesh.new()
        boulder(tmp, rng, r, subd, seed + int(abs(x * 31 + y * 17)), mat,
                squash=squash, amount=rng.uniform(0.32, 0.46),
                sharpness=rng.uniform(0.55, 0.95), bury=rng.uniform(0.26, 0.40))
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((x, y, 0)) @
                     Matrix.Rotation(rng.uniform(0, 6.2832), 4, 'Z') @
                     Matrix.Rotation(rng.uniform(-0.26, 0.26), 4, 'X'))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    # talus fan: small stuff, densest at the threshold and thinning outward
    for i in range(12):
        u = rng.random()
        spread = 1.1 + 2.7 * u
        x = rng.uniform(-1.0, 1.0) * spread
        y = -0.25 - u * 2.4 + rng.uniform(-0.35, 0.35)
        if abs(x) < 0.9 and y > -1.5:
            continue                      # keep the threshold itself walkable
        r = rng.uniform(0.10, 0.30) * (1.0 - 0.45 * u)
        tmp = bmesh.new()
        boulder(tmp, rng, r, 1, seed + 40 + i * 13,
                rng.choice((RUBBLE, GRAVEL, ROCK_GREY, ROCK_WARM)),
                squash=rng.uniform(0.45, 0.80), amount=0.38,
                sharpness=0.75, bury=0.42)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((x, y, 0)) @
                     Matrix.Rotation(rng.uniform(0, 6.2832), 4, 'Z'))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)


def _threshold(bm, seed):
    """The worn apron where the gravel path stops being path and becomes stone.
    Low, wide, and it runs a little way into the throat so there is no line on
    the ground for the eye to read as the edge of a prop."""
    nx, ny = 13, 6
    y0, y1 = -2.1, 0.95
    grid = []
    for i in range(nx + 1):
        x = (i / float(nx) - 0.5) * (CAVE_OPEN_W + 1.4)
        col = []
        for j in range(ny + 1):
            t = j / float(ny)
            y = y0 + (y1 - y0) * t
            z = 0.055 + 0.045 * math.sin(x * 1.9 + seed) * math.sin(y * 2.3)
            z *= 1.0 - E.smoothstep(0.62, 1.0, abs(x) / ((CAVE_OPEN_W + 1.4) * 0.5))
            col.append(bm.verts.new((x, y, max(z, 0.006))))
        grid.append(col)
    for i in range(nx):
        for j in range(ny):
            f = bm.faces.new((grid[i][j], grid[i + 1][j],
                              grid[i + 1][j + 1], grid[i][j + 1]))
            f.material_index = GRAVEL
            f.smooth = False


def a_cave_arch(bm, rng, seed):
    """A mouth cut into a headwall, with a throat deep enough that the back of it
    is never in frame.  See the block comment above for why each piece is here."""
    segs = 48
    rows = 6

    # ---- the headwall face, with the opening cut out of it -------------
    grid = []
    for i in range(segs + 1):
        t = i / float(segs)
        ix, iz = _mouth_point(t, seed)
        ox, oz = _wall_point(t, seed)
        iy = -_brow(t)
        col = []
        for r in range(rows + 1):
            u = r / float(rows)
            x = ix + (ox - ix) * u
            z = iz + (oz - iz) * u
            # leave the lip crisp, then settle into the wall's own relief
            w = u ** 0.72
            y = iy * (1.0 - w) + _wall_relief(x, z, seed) * w
            col.append((bm.verts.new((x, y, z)), u))
        grid.append(col)

    for i in range(segs):
        for r in range(rows):
            f = bm.faces.new((grid[i][r][0], grid[i + 1][r][0],
                              grid[i + 1][r + 1][0], grid[i][r + 1][0]))
            c = f.calc_center_median()
            f.material_index = _wall_mat(c.x, c.z, (r + 0.5) / rows, seed)
            f.smooth = False

    # ---- the throat ----------------------------------------------------
    depths = [0.0, 0.55, 1.15, 1.85, 2.65, 3.55, 4.50, 5.45, CAVE_THROAT]
    rings = []
    for d in depths:
        pts = _bore_ring(d, seed)
        if d == 0.0:
            # ring 0 must be the same vertices as the face's inner row, or the
            # lip splits open along a seam the camera looks straight at
            ring = [grid[int(round(i * segs / 21.0))][0][0]
                    for i in range(22)]
            ring += [bm.verts.new(p) for p in pts[22:]]
        else:
            ring = [bm.verts.new(p) for p in pts]
        rings.append(ring)

    n = len(rings[0])
    for k in range(len(rings) - 1):
        for j in range(n):
            j2 = (j + 1) % n
            a, b = rings[k][j], rings[k + 1][j]
            c, d2 = rings[k + 1][j2], rings[k][j2]
            if len({a, b, c, d2}) < 4:
                continue
            f = bm.faces.new((a, b, c, d2))
            # the first band is the wet lip; everything past it is cave rock,
            # the darkest surface in the terrain atlas
            f.material_index = WET_ROCK if k == 0 else CAVE_ROCK
            f.smooth = False
    cap = bm.faces.new(tuple(rings[-1]))
    cap.material_index = CAVE_ROCK
    cap.smooth = False

    # ---- the buried block behind the face ------------------------------
    back = []
    for i in range(segs + 1):
        t = i / float(segs)
        ox, oz = _wall_point(t, seed)
        back.append(bm.verts.new((ox * 0.92, CAVE_BACK, oz * 0.78)))
    for i in range(segs):
        f = bm.faces.new((grid[i][rows][0], grid[i + 1][rows][0],
                          back[i + 1], back[i]))
        f.material_index = ROCK_GREY
        f.smooth = False
    plate = bm.faces.new(tuple(reversed(back)))
    plate.material_index = ROCK_GREY
    plate.smooth = False

    # ---- broken lintel blocks sitting on the brow ----------------------
    for (x, y, z, r, sq) in ((-1.25, -0.62, 4.28, 0.54, 0.42),
                             (1.05, -0.76, 4.48, 0.62, 0.38)):
        tmp = bmesh.new()
        boulder(tmp, rng, r, 2, seed + int(x * 41), STRATA, squash=sq,
                amount=0.30, sharpness=0.85, bury=0.0)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((x, y, z)) @
                     Matrix.Rotation(rng.uniform(0, 6.2832), 4, 'Z') @
                     Matrix.Rotation(rng.uniform(-0.18, 0.18), 4, 'Y'))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    _threshold(bm, seed)
    _scree(bm, rng, seed)

    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-4)
    # Flatten everything at or below the ground plane. The talus boulders are
    # deliberately sunk, and without this the lowest of them sets the base pivot
    # and lifts the entire mouth off the terrain by the depth they were buried.
    for v in bm.verts:
        if v.co.z < 0.02:
            v.co.z = 0.0

    bevel_keep_mats(bm, width=0.020, segments=2, angle_deg=48.0,
                    mat_break=False)

    # Authored throat-into-+Y because that is much easier to reason about, then
    # mirrored once, here, because the shipping convention is the other way
    # round: the export puts Blender +Y on Unity +Z, the manifest's import note
    # says "models face +Z", and Tools/Level/worldgen.py yaw_towards() rotates
    # every prefab as a +Z-facing model.  Cave_AsterGrotto's mouth is placed at
    # facingYaw 190 -- pointing back down the gorge at a player walking up
    # Path_CaveBranch -- so the opening has to leave this function facing +Y.
    # The old asset was front/back symmetric, so nothing here was ever tested;
    # get this backwards and the mouth opens into the hillside.  Winding is left
    # to the recalc_face_normals in E.finalize rather than reversed by hand.
    for v in bm.verts:
        v.co.y = -v.co.y


# --------------------------------------------------------------------------
# Env_Bridge_Wood
#
# The route crosses the stream on this at (13.6, 18.2) yaw 142.25 with the
# object seated at Y = 0.06, and the terrain pass was tuned against the deck
# surface the OLD asset happened to have.  Measured off the shipped FBX, that
# surface is
#
#     z_top(x) = 0.2310 + 0.3350 * sin(pi * (x + 2) / 4)      rms 4.9 mm
#
# with min z = 0 (pivot at base) and max z = 1.1216.  Everything below is
# authored to reproduce that curve and that bounding box exactly, because
# changing any of it un-seats the abutments the placement pass just fixed.
#
# The ground the object sits in, back-computed from the placement report
# (deck -0.023 / +0.051 above the bank at the ends, +1.228 over the streambed
# at the centre, water 0.97 below the deck), is roughly
#
#     bank crest  z = +0.18 .. +0.25     water surface  z = -0.404
#
# in this model's own coordinates -- i.e. the model's z = 0 plane is BELOW the
# bank crest at the ends and well above the water in the middle.  So an
# abutment founded at z = 0 is buried in the bank, which is exactly what the
# old one was not: it was a loose boulder hung in the air under the deck.
# --------------------------------------------------------------------------

BR_SPRING = 2.0000       # arch springing
BR_DECK_END = 2.4400     # end of the timber deck
BR_HALF = 2.4996         # abutment outer face == asset bbox half length
BR_Y = 0.7995            # kerb / post outer face == asset bbox half width
BR_DECK_Y = 0.7095       # deck slab half width; posts and kerb sit outboard
BR_Z0 = 0.2310           # deck top at the springing   (measured)
BR_RISE = 0.3350         # arch rise                   (measured)
BR_TH = 0.0550           # deck slab thickness
BR_TOP = 1.1216          # asset bbox height == handrail post top


def br_deck_top(x):
    """The walking surface.  Flat on the approaches, arched over the water,
    with a 100 mm fillet at the springing so the deck does not kink."""
    ax = abs(x)
    if ax >= BR_SPRING:
        return BR_Z0
    z = BR_Z0 + BR_RISE * math.sin(math.pi * (x + 2.0) / 4.0)
    if ax > 1.90:
        s = (ax - 1.90) / 0.10
        s = s * s * (3.0 - 2.0 * s)
        z = z * (1.0 - s) + BR_Z0 * s
    return z


BR_BEAR_Z = 0.1180       # top of the abutment bearing shelf == rib soffit
BR_CROWN_Z = 0.2110      # rib soffit at the crown (0.300 m deep there)
BR_RIB_Y = 0.5200        # rib centreline
BR_RIB_HW = 0.0650


def br_rib_soffit(x):
    """Underside of the arch ribs.  A smoothstep, so the soffit arrives at the
    abutment bearing FLAT -- the rib and the shelf are coplanar over the whole
    bearing length instead of touching along one line."""
    s = max(0.0, min(1.0, (1.98 - abs(x)) / 1.98))
    return BR_BEAR_Z + (BR_CROWN_Z - BR_BEAR_Z) * s * s * (3.0 - 2.0 * s)


def _beam(bm, path, hw, ht, mat, cap=True, vertical=False):
    """Rectangular member swept along a polyline.  hw is the half width in Y;
    ht is the half thickness, taken across the path in the XZ plane, or
    straight up if `vertical` -- which is what makes a rib's top face exactly
    coincide with the deck soffit it carries instead of falling a millimetre
    away from it on the slopes.  hw and ht may be per-station lists."""
    n = len(path)
    hw = hw if isinstance(hw, (list, tuple)) else [hw] * n
    ht = ht if isinstance(ht, (list, tuple)) else [ht] * n
    rings = []
    for i, p in enumerate(path):
        if vertical:
            nn = Vector((0.0, 0.0, 1.0))
        else:
            if i == 0:
                t = path[1] - path[0]
            elif i == n - 1:
                t = path[-1] - path[-2]
            else:
                t = path[i + 1] - path[i - 1]
            t = Vector((t.x, 0.0, t.z))
            if t.length < 1e-9:
                t = Vector((1.0, 0.0, 0.0))
            t.normalize()
            nn = Vector((-t.z, 0.0, t.x))
        c = Vector(p)
        rings.append([bm.verts.new(c - nn * ht[i] + Vector((0, -hw[i], 0))),
                      bm.verts.new(c - nn * ht[i] + Vector((0, hw[i], 0))),
                      bm.verts.new(c + nn * ht[i] + Vector((0, hw[i], 0))),
                      bm.verts.new(c + nn * ht[i] + Vector((0, -hw[i], 0)))])
    faces = []
    for i in range(n - 1):
        a, b = rings[i], rings[i + 1]
        for j in range(4):
            k = (j + 1) % 4
            faces.append(bm.faces.new((a[j], a[k], b[k], b[j])))
    if cap:
        faces.append(bm.faces.new(list(reversed(rings[0]))))
        faces.append(bm.faces.new(rings[-1]))
    for f in faces:
        f.material_index = mat
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def _obox(bm, a, b, hw, ht, mat, trim=False):
    """A single straight member between two points -- braces and struts.

    `trim` pulls both ends back along the member's own axis by ht*|dz/dx|, so
    that a SLOPED member's square end still finishes exactly at the given x
    rather than poking ht*sin(theta) past it.  Without it a handrail chord
    running post to post buries 8 mm of itself inside each post.
    """
    a = Vector(a)
    b = Vector(b)
    if trim:
        dx, dz = b.x - a.x, b.z - a.z
        L = math.hypot(dx, dz)
        if abs(dx) > 1e-6 and L > 1e-9:
            f = (ht * abs(dz) / abs(dx)) / L
            if f < 0.45:
                a, b = a + (b - a) * f, b - (b - a) * f
    return _beam(bm, [a, b], hw, ht, mat)


def a_bridge(bm, rng, seed):
    """A footbridge that is actually built: a continuous planked deck with no
    daylight through it, tapered arch ribs and cross bracing underneath where
    the player sees them from the water, kerbs and posts framed into the deck
    edge rather than floating beside it, and cut-stone abutments founded below
    the bank crest at both ends."""

    # ---- deck: ONE watertight lofted slab, planks read as 5 mm risers ----
    # The old deck was 9 loose plank boxes at 82-92 % of the bay length; the
    # 40-80 mm gaps between them were holes you could see the stream through
    # (measured: 6 of 83 sample rays down the centreline hit nothing at all).
    # Here the planks share their edges, so the surface is continuous, and the
    # plank line comes from a 5 mm step instead of a void.
    # A 5 mm step between boards turned out to be invisible at the game
    # camera -- the first rebuild's deck read as a brick mosaic.  So each
    # joint gets a real 32 mm x 10 mm groove as well as the step: a genuine
    # shadow line about 3 px wide on screen, and the slab stays continuous
    # because the groove is a dish in the top surface, not a gap.
    nplank = 22
    GW, GD = 0.016, 0.010
    lifts = [(0.0030 if (k % 2) else -0.0030) + rng.uniform(-0.0012, 0.0012)
             for k in range(nplank)]
    stations = []          # (x, top offset from the deck curve)
    for k in range(nplank):
        x0 = -BR_DECK_END + 2.0 * BR_DECK_END * k / nplank
        x1 = -BR_DECK_END + 2.0 * BR_DECK_END * (k + 1) / nplank
        stations.append((x0 if k == 0 else x0 + GW, lifts[k]))
        stations.append((x1 if k == nplank - 1 else x1 - GW, lifts[k]))
        if k < nplank - 1:
            floor = min(lifts[k], lifts[k + 1]) - GD
            stations.append((x1 - GW * 0.40, floor))
            stations.append((x1 + GW * 0.40, floor))
    rings = []
    for (x, lift) in stations:
        zt = br_deck_top(x) + lift
        zb = br_deck_top(x) - BR_TH
        rings.append([bm.verts.new((x, -BR_DECK_Y, zb)),
                      bm.verts.new((x, BR_DECK_Y, zb)),
                      bm.verts.new((x, BR_DECK_Y, zt)),
                      bm.verts.new((x, -BR_DECK_Y, zt))])
    dfaces = []
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for j in range(4):
            k = (j + 1) % 4
            dfaces.append(bm.faces.new((a[j], a[k], b[k], b[j])))
    dfaces.append(bm.faces.new(list(reversed(rings[0]))))
    dfaces.append(bm.faces.new(rings[-1]))
    for f in dfaces:
        f.material_index = WOOD
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=dfaces)

    # ---- arch ribs: top face coincident with the deck soffit ------------
    # Vertical section offsets, so the rib's top plane IS the deck's underside
    # everywhere rather than drifting a millimetre off it on the slopes.
    RIB_END = 2.16
    xs = [-RIB_END + 2.0 * RIB_END * i / 24.0 for i in range(25)]
    for sy in (-1, 1):
        path, ht = [], []
        for x in xs:
            zt = br_deck_top(x) - BR_TH
            zb = br_rib_soffit(x)
            path.append(Vector((x, sy * BR_RIB_Y, (zt + zb) * 0.5)))
            ht.append((zt - zb) * 0.5)
        _beam(bm, path, BR_RIB_HW, ht, WOOD, vertical=True)

    # ---- transverse bearers and plan bracing, seen from the water -------
    BRACE_Y = BR_RIB_Y - BR_RIB_HW          # inner face of the ribs
    for x in (-1.52, -0.76, 0.0, 0.76, 1.52):
        zb = br_rib_soffit(x)
        E.bm_box(bm, (x, 0.0, zb - 0.046),
                 (0.100, 2.0 * (BR_RIB_Y + BR_RIB_HW), 0.092), WOOD)
    bays = ((-1.52, -0.76), (-0.76, 0.0), (0.0, 0.76), (0.76, 1.52))
    for (xa, xb) in bays:
        za = br_rib_soffit(xa) + 0.055
        zb = br_rib_soffit(xb) + 0.055
        _obox(bm, (xa, -BRACE_Y + 0.030, za), (xb, BRACE_Y - 0.030, zb),
              0.030, 0.026, WOOD)
        _obox(bm, (xa, BRACE_Y - 0.030, za), (xb, -BRACE_Y + 0.030, zb),
              0.030, 0.026, WOOD)

    # ---- raking struts off the abutment faces ---------------------------
    # They stop at x = 1.62, short of the 1.55 post's knee brace: at the 1.30
    # they started life at, strut and brace shared 260 mm of the same volume.
    for sx in (-1, 1):
        for sy in (-1, 1):
            xr = sx * 1.62
            _obox(bm, (sx * 1.93, sy * (BR_RIB_Y + BR_RIB_HW + 0.042),
                       BR_BEAR_Z + 0.030),
                  (xr, sy * (BR_RIB_Y + BR_RIB_HW + 0.042),
                   br_rib_soffit(xr) + 0.075), 0.042, 0.040, WOOD, trim=True)

    # ---- edge beam, posts and handrail ----------------------------------
    # All of it lives in the 90 mm band OUTBOARD of the deck slab, between
    # |y| = BR_DECK_Y and |y| = BR_Y.  The deck's edge plane and this band's
    # inner plane are the same plane, so the rail frame butts the deck rather
    # than hovering next to it, and nothing interpenetrates.
    post_xs = [-2.30, -1.55, -0.63, 0.63, 1.55, 2.30]
    hpost = BR_TOP - br_deck_top(0.63)      # so the tallest post == BR_TOP
    pw = 0.045                               # post half size along X
    yc = (BR_DECK_Y + BR_Y) * 0.5
    yh = (BR_Y - BR_DECK_Y) * 0.5
    for sy in (-1, 1):
        tops = []
        for px in post_xs:
            zt = br_deck_top(px)
            # posts over the abutment are founded in the bank; the others hang
            # on the edge beam and are trussed back to the rib by a knee brace
            over_abut = abs(px) > 2.0
            zbase = 0.020 if over_abut else br_rib_soffit(px) + 0.015
            E.bm_box(bm, (px, sy * yc, (zt + hpost + zbase) * 0.5),
                     (pw * 2.0, yh * 2.0, zt + hpost - zbase), WOOD)
            tops.append((px, zt + hpost))
            if not over_abut:
                # the brace lands ON the post's inner face and ON the rib's
                # outer face -- both ends butt, neither end is buried
                xk = px - math.copysign(0.34, px)
                _obox(bm, (px, sy * (BR_DECK_Y - 0.032), zbase + 0.045),
                      (xk, sy * (BR_RIB_Y + BR_RIB_HW + 0.032),
                       br_rib_soffit(xk) + 0.115), 0.032, 0.030, WOOD)
        # edge beam between the posts: flush with the deck soffit below and
        # standing 55 mm proud above it as a rub rail
        for k in range(len(post_xs) - 1):
            xa, xb = post_xs[k] + pw, post_xs[k + 1] - pw
            steps = max(2, int((xb - xa) / 0.24) + 1)
            path, ht = [], []
            for i in range(steps + 1):
                x = xa + (xb - xa) * i / steps
                path.append(Vector((x, sy * yc, br_deck_top(x))))
                ht.append(0.055)
            _beam(bm, path, yh, ht, WOOD, vertical=True)
        # top and mid rail: straight chords post face to post face, so BR_TOP
        # is reached at the posts and never exceeded between them
        for k in range(len(tops) - 1):
            (xa, za), (xb, zb) = tops[k], tops[k + 1]
            _obox(bm, (xa + pw, sy * yc, za - 0.038),
                  (xb - pw, sy * yc, zb - 0.038), yh, 0.038, WOOD, trim=True)
            _obox(bm, (xa + pw, sy * yc, za - 0.038 - hpost * 0.44),
                  (xb - pw, sy * yc, zb - 0.038 - hpost * 0.44),
                  yh * 0.70, 0.026, WOOD, trim=True)

    # ---- abutments: cut stone, founded at z = 0 (below the bank crest) ---
    zback = BR_Z0 - BR_TH
    for sx in (-1, 1):
        # One solid, not two: a bearing shelf whose top is coplanar with the
        # rib soffit for its whole length, stepping up at x = 2.22 to a back
        # wall whose top is coplanar with the deck soffit, out to the bbox
        # face.  Built as a single sweep so the two do not share a duplicated
        # face at the step.
        xs_ab = [(1.930, BR_BEAR_Z), (2.215, BR_BEAR_Z),
                 (2.225, zback), (BR_HALF, zback)]
        _beam(bm, [Vector((sx * x, 0, z * 0.5)) for (x, z) in xs_ab],
              BR_DECK_Y, [z * 0.5 for (_x, z) in xs_ab], STEPPING,
              vertical=True)
        # rough stones bedded at the toe, where the masonry meets the bank.
        # Their own base is measured and zeroed first: boulder() can leave a
        # vertex a centimetre or two below its nominal ground plane, and one
        # of those becoming the model's lowest point lifts the whole bridge
        # off the deck curve the terrain pass was seated against.
        # Fixed y slots 420 mm apart, so no two of them share volume -- the
        # first pass scattered them at random and two pairs overlapped by more
        # than a centimetre.  They DO bed into the abutment face on purpose:
        # a stone half buried in the masonry toe is what stops the join
        # between cut stone and bank reading as a cut line.
        for (k, ys) in enumerate((-0.44, -0.02, 0.42)):
            tmp = bmesh.new()
            boulder(tmp, rng, rng.uniform(0.095, 0.125), 1,
                    seed + k * 7 + (0 if sx > 0 else 40), ROCK_GREY,
                    squash=0.58)
            me = bpy.data.meshes.new("t")
            tmp.to_mesh(me)
            tmp.free()
            zlo = min(v.co.z for v in me.vertices)
            me.transform(Matrix.Translation(
                (sx * rng.uniform(1.88, 1.97), ys + rng.uniform(-0.03, 0.03),
                 rng.uniform(0.012, 0.040) - zlo)))
            bm.from_mesh(me)
            bpy.data.meshes.remove(me)


def a_riverbank(bm, rng, seed, length=4.0, drop=0.55):
    """Bank edging: a grassy lip, an eroded soil face and a gravel toe.
    Tiles along X on the 0.5 m grid using the same seam harmonics."""
    usegs = int(length / GRID)
    rows = [(0.00, 0.00, EARTH), (0.10, -0.16, DIRT), (0.45, -0.34, DIRT),
            (0.78, -0.52, GRAVEL), (1.00, -0.86, GRAVEL)]
    cols = []
    for i in range(usegs + 1):
        u = length * i / usegs
        col = []
        for (t, y, mat) in rows:
            z = -drop * t
            wob = seam_offset(t * 2.0, 2.0) * 0.55
            wob += 0.10 * math.sin(2 * math.pi * u / (GRID * 4) + t * 3.0 + seed)
            wob -= 0.10 * math.sin(t * 3.0 + seed)
            col.append(bm.verts.new((u, y + wob * (0.25 + t), z + wob * 0.30)))
        cols.append(col)
    for i in range(usegs):
        for j in range(len(rows) - 1):
            f = bm.faces.new((cols[i][j], cols[i + 1][j],
                              cols[i + 1][j + 1], cols[i][j + 1]))
            f.material_index = rows[j + 1][2]
            f.smooth = False
    # a few stones bedded into the toe
    for k in range(5):
        u = rng.uniform(0.2, length - 0.2)
        tmp = bmesh.new()
        boulder(tmp, rng, rng.uniform(0.10, 0.20), 1, seed + k * 5, ROCK_GREY,
                squash=0.7)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((u, rng.uniform(-0.80, -0.55),
                                         -drop * rng.uniform(0.82, 0.98))))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)


def a_waterfall_shelf(bm, rng, seed, width=3.0, height=1.7):
    """A stepped lip for water to fall over: worn channel in the middle, dry
    shelves either side, splash boulders at the foot."""
    usegs = 16
    vsegs = 8
    cols = []
    for i in range(usegs + 1):
        u = (i / float(usegs) - 0.5) * width
        chan = math.exp(-(u / (width * 0.17)) ** 2)
        col = []
        for j in range(vsegs + 1):
            t = j / float(vsegs)
            z = height * (1.0 - t) - chan * 0.22 * (1.0 - t)
            y = -0.5 + t * 1.4
            y += 0.16 * math.sin(t * 5.0 + u * 1.7 + seed) * (1 - chan)
            z += 0.10 * math.sin(u * 3.1 + t * 6.0 + seed) * (1 - chan) * 0.7
            col.append(bm.verts.new((u, y, max(z, 0.0))))
        cols.append(col)
    for i in range(usegs):
        for j in range(vsegs):
            f = bm.faces.new((cols[i][j], cols[i + 1][j],
                              cols[i + 1][j + 1], cols[i][j + 1]))
            u = (i / float(usegs) - 0.5) * width
            wet = math.exp(-(u / (width * 0.22)) ** 2) > 0.4
            f.material_index = WATERFALL if wet else ROCK_GREY
            f.smooth = False
    for k in range(6):
        tmp = bmesh.new()
        boulder(tmp, rng, rng.uniform(0.16, 0.34), 2, seed + k * 9,
                WET_ROCK if k % 2 else ROCK_GREY, squash=0.72)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((rng.uniform(-width * 0.45, width * 0.45),
                                         rng.uniform(0.75, 1.05),
                                         rng.uniform(0.0, 0.10))))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)


def a_stepping_stones(bm, rng, seed, n=5, run=3.2):
    for i in range(n):
        t = i / float(max(n - 1, 1))
        x = (t - 0.5) * run
        y = math.sin(t * 3.4 + seed) * 0.28
        tmp = bmesh.new()
        boulder(tmp, rng, rng.uniform(0.30, 0.42), 2, seed + i * 23, STEPPING,
                squash=0.34, amount=0.16, sharpness=0.30, bury=0.30)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((x, y, 0)) @
                     Matrix.Rotation(rng.uniform(0, 6.28), 4, 'Z'))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)


def a_cave_rubble(bm, rng, seed):
    a_rock_scatter(bm, rng, 11, 0.75, 0.07, 0.22, seed,
                   (CAVE_ROCK, RUBBLE, GRAVEL))


ASSETS = [
    # cliffs and walls
    ("Env_Cliff_Wall_2m", 3101, (300, 2600), "corner",
     lambda bm, rng: a_cliff(bm, rng, 2.0, 3.0, 11)),
    ("Env_Cliff_Wall_4m", 3102, (300, 4200), "corner",
     lambda bm, rng: a_cliff(bm, rng, 4.0, 3.0, 11)),
    ("Env_Cliff_Wall_6m", 3103, (300, 6000), "corner",
     lambda bm, rng: a_cliff(bm, rng, 6.0, 3.0, 11)),
    ("Env_Cliff_Wall_Tall_4m", 3104, (300, 6000), "corner",
     lambda bm, rng: a_cliff(bm, rng, 4.0, 5.0, 11)),
    ("Env_Cliff_Corner_Inner", 3105, (300, 5200), "corner",
     lambda bm, rng: a_cliff(bm, rng, 2.0, 3.0, 11, corner=True)),
    ("Env_Cliff_Corner_Outer", 3106, (300, 5200), "corner",
     lambda bm, rng: a_cliff(bm, rng, 2.0, 3.0, 11, corner=True)),
    # boulders
    ("Env_Rock_Boulder_A", 3201, (250, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 0.85, 0.85, 101,
                                    (ROCK_GREY,))),
    ("Env_Rock_Boulder_B", 3202, (300, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 1.25, 1.25, 202,
                                    (ROCK_WARM,))),
    ("Env_Rock_Boulder_C", 3203, (250, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 0.60, 0.60, 303,
                                    (ROCK_GREY,))),
    ("Env_Rock_Boulder_D", 3204, (300, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 1.60, 1.60, 404,
                                    (STRATA,))),
    ("Env_Rock_Boulder_Mossy_E", 3205, (300, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 1.00, 1.00, 505,
                                    (MOSS_ROCK,))),
    ("Env_Rock_Boulder_Wet_F", 3206, (250, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 0.75, 0.75, 606,
                                    (WET_ROCK,))),
    ("Env_Rock_Scatter_A", 3211, (300, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 7, 0.85, 0.09, 0.26, 707,
                                    (ROCK_GREY, ROCK_WARM, GRAVEL))),
    ("Env_Rock_Scatter_B", 3212, (300, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 5, 0.55, 0.12, 0.34, 808,
                                    (ROCK_WARM, RUBBLE))),
    # cave
    ("Env_Cave_Arch", 3301, (700, 6000), "base",
     lambda bm, rng: a_cave_arch(bm, rng, 5)),
    ("Env_Cave_Stalactite_A", 3311, (60, 2000), "top",
     lambda bm, rng: stone_spike(bm, rng, 1.45, 0.30, 3, down=True, sides=8)),
    ("Env_Cave_Stalactite_B", 3312, (60, 2000), "top",
     lambda bm, rng: stone_spike(bm, rng, 0.80, 0.20, 9, down=True, sides=7)),
    ("Env_Cave_Stalagmite_A", 3321, (60, 2000), "base",
     lambda bm, rng: stone_spike(bm, rng, 1.05, 0.34, 17, sides=8)),
    ("Env_Cave_Stalagmite_B", 3322, (60, 2000), "base",
     lambda bm, rng: stone_spike(bm, rng, 0.60, 0.24, 23, sides=7)),
    ("Env_Cave_Rubble", 3331, (300, 2000), "base",
     lambda bm, rng: a_cave_rubble(bm, rng, 909)),
    # water
    ("Env_Riverbank_4m", 3401, (300, 2600), "corner",
     lambda bm, rng: a_riverbank(bm, rng, 3, 4.0, 0.55)),
    ("Env_Riverbank_2m", 3402, (200, 2200), "corner",
     lambda bm, rng: a_riverbank(bm, rng, 3, 2.0, 0.55)),
    ("Env_Waterfall_Shelf", 3411, (300, 3000), "base",
     lambda bm, rng: a_waterfall_shelf(bm, rng, 7)),
    ("Env_Stepping_Stones", 3421, (300, 2600), "base",
     lambda bm, rng: a_stepping_stones(bm, rng, 13)),
    ("Env_Bridge_Wood", 3431, (900, 6000), "base",
     lambda bm, rng: a_bridge(bm, rng, 19)),
]


# --------------------------------------------------------------------------
# Split normals
#
# Once the atlases are palette-quantised and the toon ramp bands at three hard
# steps, smooth-shaded rock stops working. A smooth normal across a boulder
# produces a soft gradient that the flat texture and the hard ramp have nothing
# to say about, and the result reads as mush: a shape with no facets under a
# lighting model that only draws facets. The fix is *harder* normal breaks, not
# softer -- an authoring pass, not a remodel. Geometry is untouched.
#
# Two mechanisms, both wanted:
#
#   angle break     auto smooth at SPLIT_ANGLE instead of the 26 degrees the
#                   family shipped with. 26 degrees smooths across most of an
#                   eroded boulder's relief; 18 leaves the erosion pass's
#                   plateaus and undercuts reading as distinct planes, which is
#                   what the toon ramp then bands.
#   material break  every edge between two different materials is marked sharp
#                   unconditionally. A strata band meeting rock, or moss meeting
#                   wet stone, is a change of surface and should never share an
#                   interpolated normal across the join, whatever the angle is.
#
# Blender's FBX exporter writes per-loop normals from the split-normal result,
# so both of these reach Unity through the existing export path with no exporter
# change. The manifest's "custom split normals are authored; do not recalculate"
# import note is what keeps Unity from throwing them away on the other side.
# --------------------------------------------------------------------------

SPLIT_ANGLE = 18.0        # degrees; was 26 before the HD-2D pivot
SPLIT_ANGLE_BUILT = 24.0  # bridge and stepping stones: milled timber and cut
                          # stone read wrong if every board face separates

SMOOTH_OVERRIDE = {
    "Env_Bridge_Wood": SPLIT_ANGLE_BUILT,
    "Env_Stepping_Stones": SPLIT_ANGLE_BUILT,
}


def split_normals(obj, angle=SPLIT_ANGLE, material_breaks=True):
    """Harden the shading normals in place. No vertices move."""
    me = obj.data

    if material_breaks and len(me.materials) > 1:
        bm = E.obj_bm(obj)
        bm.edges.ensure_lookup_table()
        for e in bm.edges:
            if len(e.link_faces) == 2 and \
                    e.link_faces[0].material_index != e.link_faces[1].material_index:
                e.smooth = False
        E.bm_write(bm, obj)
        me = obj.data

    for p in me.polygons:
        p.use_smooth = True
    me.use_auto_smooth = True
    me.auto_smooth_angle = math.radians(angle)
    return obj


def finish(bm, name, ms, budget, pivot, smooth=SPLIT_ANGLE):
    obj = E.bm_to_obj(bm, name, ms.materials())
    E.finalize(obj, smooth_angle=smooth)
    if pivot == 'corner':
        E.pivot_to_base(obj, xy='corner')
    elif pivot == 'top':
        me = obj.data
        E.set_pivot(obj, (0, 0, max(v.co.z for v in me.vertices)))
    else:
        E.pivot_to_base(obj)
    E.apply_transforms(obj)
    E.uv_all(obj, ms, angle=62.0, margin=0.012)
    # Last, deliberately. UV unwrapping goes through bmesh and operators that
    # rewrite the mesh datablock, and edge sharpness set before that point is not
    # guaranteed to survive the round trip.
    split_normals(obj, angle=smooth)
    tris, probs = E.validate(obj, budget=budget, need_vcol=False, strict=False)
    return obj, tris, probs


def main():
    E.ensure_dirs()
    T.ensure_atlas(FAM)
    E.reset_scene()
    ms = T.full_matset(FAM)
    ap = T.atlas_paths(FAM)
    part = []
    problems = []

    for (name, seed, budget, pivot, fn) in ASSETS:
        rng = random.Random(seed)
        bm = E.bm_new()
        fn(bm, rng)
        obj, tris, probs = finish(bm, name, ms, budget, pivot,
                                  smooth=SMOOTH_OVERRIDE.get(name, SPLIT_ANGLE))
        path = os.path.join(OUT, name + ".fbx")
        lods = []
        if tris > 2000:
            for lo in E.make_lods(obj, (0.40, 0.15)):
                lp = os.path.join(OUT, lo.name + ".fbx")
                E.export_fbx([lo], lp)
                lods.append((lp, E.tri_count(lo)))
                E.delete_obj(lo)
        E.export_fbx([obj], path)
        if probs:
            problems.append((name, probs))
        E.log("%-26s %5d tris  %s" % (name, tris, probs or "ok"))
        part.append({
            "name": name, "family": FAM,
            "subfamily": name.split("_")[1],
            "path": os.path.relpath(path, E.REPO).replace("\\", "/"),
            "triangles": tris,
            "lods": [{"level": i + 1,
                      "path": os.path.relpath(p, E.REPO).replace("\\", "/"),
                      "triangles": t} for i, (p, t) in enumerate(lods)],
            "pivot": {"corner": "module corner at origin, snaps on 0.5 m grid",
                      "top": "top anchor (hangs downward)",
                      "base": "base, XY centred"}[pivot],
            "textures": [os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
                         os.path.relpath(ap["normal"], E.REPO).replace("\\", "/")],
            "windVertexColors": False,
            "notes": "",
        })
        E.delete_obj(obj)

    E.write_part(FAM, part)
    E.log("---- %d terrain assets, %d with problems" % (len(part), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))


if __name__ == "__main__":
    main()
