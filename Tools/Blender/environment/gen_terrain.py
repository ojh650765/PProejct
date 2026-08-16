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

CAVE_HW = 8.6        # headwall width -- the gorge pinches to 9 m at the mouth,
                     # and the talus fan has to fit inside that too
CAVE_HH = 6.3        # headwall height at the crown of its broken skyline
CAVE_OPEN_W = 5.9    # profile width; the *measured* clear span comes out
                     # ~0.5 m under this once the jamb relief and the bore's
                     # roughness have eaten into it, and the level declares a
                     # 5.4 m trigger, so the profile is cut wide to land on it
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
    # Deliberately NOT wet_rock at the lip, tempting though it is: keeping
    # WET_ROCK exclusive to the bore is what lets cave_fix_bore_normals and
    # cave_occlusion_vcol identify the throat by material alone, with no plane to
    # keep in sync.  Moss on the damp lip says the same thing anyway.
    # Moss is kept low and near the lip on purpose.  The wall's faces get bigger
    # the further out they are, and a big face box-mapped into the moss cell
    # samples a large flat patch of it -- which reads as a bright green rectangle
    # painted on the rock, not as moss.  Down at the foot the faces are small
    # enough for the blotching in the swatch to do its job.
    if u < 0.12:
        return MOSS_ROCK if z < 1.4 else ROCK_GREY
    if u < 0.28 and z < 1.5 and _hash2(int(x * 3.8), int(z * 3.8),
                                       int(seed)) < 0.34:
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
    sx = 1.0 - 0.22 * f
    sz = 1.0 - 0.34 * f
    bend = 2.60 * (f ** 1.6)
    floor = 0.62 * (f ** 1.4)
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
        # same reasoning as Env_Rock_Boulder_D: a block on the ground has no
        # bedding to be banded against, so it takes plain rock, not cliff_strata
        (-3.45, -0.45, 0.86, 2, ROCK_WARM, 0.72),
        (3.60, -0.30, 0.74, 2, ROCK_WARM, 0.66),
        (-2.95, -1.75, 0.62, 2, ROCK_GREY, 0.58),
        (1.55, -1.90, 0.55, 2, ROCK_WARM, 0.62),
        (3.85, -1.35, 0.56, 2, MOSS_ROCK, 0.60),
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
        if abs(x) < 1.7 and y > -1.6:
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
            # ...but the brow curve is analytic and the blend to it is smooth, so
            # without this the two or three rows nearest the opening come out as
            # one wide unbroken ribbon sweeping over the arch -- which rendered
            # as a poured concrete band laid over the rock.  Roughen the whole
            # annulus, hardest at the lip where the blend is doing the most work.
            y -= ((_plate(x * 1.4 + 5.0, z * 1.25, int(seed) + 11) - 0.5) * 0.26 +
                  0.10 * math.sin(x * 3.1 + z * 2.3 + seed) +
                  0.06 * math.sin(x * 5.7 - z * 4.1)) * (1.0 - 0.45 * w)
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

    # ---- the mass behind the face --------------------------------------
    # Not a plain loft from the front skyline to a scaled copy of it: two arched
    # rings lofted together give a smooth half-cylinder, and the first build of
    # this read as a Nissen hut sitting in the gorge.  The site the mouth has
    # been moved to only rises +0.6 m at 3.5 m in, so this mass is *not* going to
    # be politely buried -- it is the cliff, and it has to be faceted like one.
    # Two intermediate rings, each with its own crest noise and its own plate
    # relief, break the silhouette into shoulders.
    depth_rings = [(0.34, 0.97, 0.93), (0.68, 0.95, 0.85), (1.00, 0.92, 0.74)]
    prev = [grid[i][rows][0] for i in range(segs + 1)]
    for (k, (fy, fx, fz)) in enumerate(depth_rings):
        y = CAVE_BACK * fy    # still authored throat-into-+Y; mirrored at the end
        ring = []
        for i in range(segs + 1):
            t = i / float(segs)
            ox, oz = _wall_point(t, seed + 3.1 * (k + 1))
            ox *= fx
            oz *= fz
            # shoulders: the mass steps in and out as it goes back, so the
            # skyline never reads as one swept curve
            oz += (0.34 * math.sin(t * 11.0 + k * 2.1 + seed) +
                   0.19 * math.sin(t * 19.0 + k * 4.7)) * min(1.0, oz / 2.0)
            ox += 0.22 * math.sin(t * 13.0 + k * 3.3 + seed)
            ring.append(bm.verts.new((ox, y, max(oz, 0.0))))
        for i in range(segs):
            f = bm.faces.new((prev[i], prev[i + 1], ring[i + 1], ring[i]))
            f.material_index = ROCK_GREY if k else STRATA
            f.smooth = False
        prev = ring
    plate = bm.faces.new(tuple(reversed(prev)))
    plate.material_index = ROCK_GREY
    plate.smooth = False

    # ---- broken lintel blocks sitting on the brow ----------------------
    # z is set so the underside of each block clears CAVE_OPEN_H: they sit ON the
    # brow, they do not hang across the top of the opening.  The level wires a
    # LevelTransition trigger at the declared 5.4 x 4.3, and nothing may cross it.
    for (x, y, z, r, sq) in ((-1.25, -0.62, 4.86, 0.54, 0.42),
                             (1.05, -0.76, 5.02, 0.62, 0.38)):
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


def cave_fix_bore_normals(obj):
    """Point the throat's faces back into the throat.

    E.finalize -> cleanup ends with recalc_face_normals, which orients each
    *connected shell* outward by its own volume.  The bore is a tube, and once
    the ground clamp has dissolved the degenerate band that joined it to the
    face it is a shell of its own -- so "outward" comes out meaning *away from
    the tube axis*, i.e. into the surrounding rock.  Backface culling then makes
    the whole throat invisible and the player sees straight through the mountain
    to the skybox.  This was not theoretical: it is what the first build did.

    Fixed from the geometry rather than from connectivity.  The bore's own cross
    section at each depth gives the axis, and a face is right way round when its
    normal points at that axis.
    """
    bm = E.obj_bm(obj)
    inner = {CAVE_ROCK, WET_ROCK}      # bore-only materials, see _wall_mat
    faces = [f for f in bm.faces if f.material_index in inner]
    if not faces:
        E.bm_write(bm, obj)
        return 0
    # axis point per half-metre slice of depth
    bins = {}
    for f in faces:
        c = f.calc_center_median()
        b = bins.setdefault(round(c.y * 2.0), [Vector((0, 0, 0)), 0])
        b[0] += c
        b[1] += 1
    wrong = []
    for f in faces:
        c = f.calc_center_median()
        s, n = bins[round(c.y * 2.0)]
        if f.normal.dot((s / n) - c) < 0.0:
            wrong.append(f)
    # all or nothing: a handful disagreeing is the end cap and the lip, which sit
    # across the axis rather than around it, and flipping those would tear it
    if len(wrong) > len(faces) * 0.5:
        bmesh.ops.reverse_faces(bm, faces=faces)
        E.bm_write(bm, obj)
        return len(faces)
    E.bm_write(bm, obj)
    return 0


def cave_occlusion_vcol(obj):
    """Paint the shader's vertex-occlusion channel so the throat kills its own
    ambient light.

    PokeLabPropGroundBlend reads vertex colour BLUE and nothing else:

        occlusion = lerp(1.0, input.colour.b, _OcclusionStrength)
        colour   += PL_Ambient(normalWS) * albedo * occlusion

    Geometry and shadow maps get the throat most of the way to dark; ambient was
    what was still lighting it, and this is the only lever that removes ambient
    without a second material or an atlas cell that does not exist.  The ask was
    for "a black plane inside the opening" -- this is that, except the plane is
    the real end wall 5.8 m back, so it reads as depth rather than as a lid.

    Keyed off material rather than a hard-coded plane: CAVE_ROCK and WET_ROCK
    exist nowhere on this asset except the bore and its lip.
    """
    me = obj.data
    inner = {CAVE_ROCK, WET_ROCK}
    ys = []
    for p in me.polygons:
        if p.material_index in inner:
            for li in p.loop_indices:
                ys.append(me.vertices[me.loops[li].vertex_index].co.y)
    if not ys:
        return None
    # the mouth faces +Y by the time this runs, so the throat runs toward -Y
    y_lip, y_end = max(ys), min(ys)
    span = max(y_lip - y_end, 1e-3)

    def paint(co, li, pi):
        if me.polygons[pi].material_index not in inner:
            return (1.0, 1.0, 1.0, 1.0)
        t = min(max((y_lip - co.y) / span, 0.0), 1.0)
        return (1.0, 1.0, 0.55 * (1.0 - t) ** 2 + 0.03, 1.0)

    return E.add_vcol(obj, paint, name="Col")


# --------------------------------------------------------------------------
# Env_Bridge_Wood -- a 9 m timber trestle
#
# WHY A TRESTLE AND NOT A LONGER PLANK
#
# The crossing at (13.6, 18.2) is not a 3.4 m stream in a shallow dip.  The
# conform cuts a channel of half width 1.7 m with a 2.6 m shoulder, so the
# ground sits below road level across about 8.6 m, and it is roughly 1.2 m
# down for the whole middle of that.  The 5 m asset this replaces stood
# +1.17 m clear of the ground at one end and +1.32 m at the other: BOTH its
# abutments were over open channel, which is the floating plank rectangle the
# user photographed.  Length, not detail, was the defect.
#
# At 9 m the two ends reach |x| = 4.5, past the 4.3 m point where the ground
# comes back to road level, so both abutments land in real bank with a little
# to spare.
#
# 9 m then has to be carried, and there are only two honest ways to do it:
#
#   arch    a 9 m arch needs roughly a metre of rise to read as an arch, and
#           the road cannot climb a metre and come back down inside the
#           crossing.  Flatten it to fit and it stops reading as an arch and
#           starts reading as a sagging plank -- the exact failure we are
#           replacing.
#   trestle four braced timber bents standing on footings, two of them in the
#           water.  The channel is shallow, so the bents are buildable in
#           fiction; the deck stays level so the road agrees at both ends; and
#           the structure is all in the low three quarter view the player gets
#           walking the bank, which is where the old one read as empty.
#
# So: trestle.  Bents at |x| = 0.85 (in the stream) and |x| = 2.55 (on the
# channel floor), cut stone abutments from |x| = 3.85 out to the ends, five
# spans of about 1.7 m, and a level deck.
#
# THE NUMBER THE PLACEMENT NEEDS
#
# BR_DECK_Z is the height of the walking surface above the pivot.  The pivot
# is at the base (min z == 0, XY centred) exactly as before, so the object is
# seated at  Y = road surface level - BR_DECK_Z.
# --------------------------------------------------------------------------

BR_HALF = 4.5000         # half length -> 9.000 m overall
BR_Y = 0.8000            # outer face of the kerb -> 1.600 m overall
BR_DECK_Y = 0.7000       # deck slab half width; kerb and posts sit outboard
BR_DECK_Z = 1.2500       # >>> walking surface above the pivot <<<
BR_CAMBER = 0.0500       # crown rise, so the deck is not a dead flat plane
BR_TH = 0.0600           # deck slab thickness
BR_STR_D = 0.2000        # stringer depth
BR_CAP_D = 0.1700        # bent cap depth
BR_FOOT_Z = 0.1200       # top of the footing pads; their base is z = 0
BR_RAIL_H = 0.8800       # handrail height above the deck
BR_BENTS = (0.85, 2.55)  # bent centrelines (mirrored)
BR_ABUT_X = 3.8500       # inner face of the stone abutments


def br_deck_top(x):
    """The walking surface: level at BR_DECK_Z at both ends so the road agrees
    there, with a 50 mm parabolic crown in between."""
    t = min(1.0, abs(x) / BR_HALF)
    return BR_DECK_Z + BR_CAMBER * (1.0 - t * t)


def _member(bm, a, b, w, h, mat, cap=True):
    """A square-sectioned timber between two points, in any orientation.
    w is measured across the member horizontally, h across it the other way."""
    a = Vector(a)
    b = Vector(b)
    t = b - a
    if t.length < 1e-9:
        return []
    t = t.normalized()
    ref = Vector((0.0, 0.0, 1.0))
    if abs(t.dot(ref)) > 0.94:
        ref = Vector((1.0, 0.0, 0.0))
    u = t.cross(ref).normalized()
    v = u.cross(t).normalized()
    faces = []
    rings = []
    for p in (a, b):
        rings.append([bm.verts.new(p - u * w * .5 - v * h * .5),
                      bm.verts.new(p + u * w * .5 - v * h * .5),
                      bm.verts.new(p + u * w * .5 + v * h * .5),
                      bm.verts.new(p - u * w * .5 + v * h * .5)])
    for j in range(4):
        k = (j + 1) % 4
        faces.append(bm.faces.new((rings[0][j], rings[0][k],
                                   rings[1][k], rings[1][j])))
    if cap:
        faces.append(bm.faces.new(list(reversed(rings[0]))))
        faces.append(bm.faces.new(rings[1]))
    for f in faces:
        f.material_index = mat
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def _run(bm, path, hw, ht, mat, cap=True):
    """A member swept along a polyline in the XZ plane, section offset
    vertically so that a sloped run's top face still lies on the surface it
    is carrying.  hw / ht may be per-station lists."""
    n = len(path)
    hw = hw if isinstance(hw, (list, tuple)) else [hw] * n
    ht = ht if isinstance(ht, (list, tuple)) else [ht] * n
    rings = []
    for i, p in enumerate(path):
        c = Vector(p)
        z = Vector((0.0, 0.0, ht[i]))
        rings.append([bm.verts.new(c - z + Vector((0, -hw[i], 0))),
                      bm.verts.new(c - z + Vector((0, hw[i], 0))),
                      bm.verts.new(c + z + Vector((0, hw[i], 0))),
                      bm.verts.new(c + z + Vector((0, -hw[i], 0)))])
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


def _rail(bm, xa, za, xb, zb, y, hw, ht, mat):
    """A handrail chord between two post faces.  The ends are pulled back
    along the chord by ht*|dz/dx| so a SLOPED rail's square end finishes at
    the given x instead of burying ht*sin(theta) of itself in the post."""
    dx, dz = xb - xa, zb - za
    L = math.hypot(dx, dz)
    if abs(dx) > 1e-6 and L > 1e-9:
        f = (ht * abs(dz) / abs(dx)) / L
        if f < 0.45:
            xa, za = xa + dx * f, za + dz * f
            xb, zb = xb - dx * f, zb - dz * f
    return _run(bm, [Vector((xa, y, za)), Vector((xb, y, zb))], hw, ht, mat)


def a_bridge(bm, rng, seed):
    """A trestle footbridge that is actually built: a continuous planked deck
    with no daylight through it, four braced bents standing on stone footings
    (two of them in the water), stringers and sway bracing under the deck
    where the player sees them from the bank, and cut stone abutments at both
    ends that reach real ground."""

    cap_top = br_deck_top(0.0) - BR_TH - BR_STR_D       # top of the bent caps
    # The bents' batter.  leg_base_y is set so the spread footing pad's outer
    # face lands exactly on BR_Y -- otherwise the pads, not the kerb, decide
    # the asset's width and it comes out at an accidental 1.72 m.
    leg_top_y, leg_base_y = 0.500, 0.650

    # ---- deck: ONE watertight lofted slab -------------------------------
    # The 5 m asset this replaces was 9 loose plank boxes at 82-92 % of their
    # bay length, and the 40-80 mm gaps between them were holes: 6 of 83
    # sample rays straight down the centreline hit nothing at all.  Here the
    # planks share their edges, and the plank line comes from a 32 mm x 10 mm
    # groove plus a 6 mm step rather than from a void.  A 5 mm step on its own
    # was tried first and was invisible at the game camera.
    nplank = 34
    GW, GD = 0.016, 0.010
    lifts = [(0.0030 if (k % 2) else -0.0030) + rng.uniform(-0.0012, 0.0012)
             for k in range(nplank)]
    stations = []
    for k in range(nplank):
        x0 = -BR_HALF + 2.0 * BR_HALF * k / nplank
        x1 = -BR_HALF + 2.0 * BR_HALF * (k + 1) / nplank
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

    # ---- stringers: three runs from abutment to abutment ----------------
    # They stop at the abutment's bearing shelf (4.40) rather than running on
    # to 4.50: past the shelf the abutment steps up to carry the deck itself,
    # and a stringer that continued would be buried inside the masonry.
    str_end = BR_ABUT_X + 0.55
    xs = [-str_end + 2.0 * str_end * i / 24.0 for i in range(25)]
    for sy in (0.0, -0.50, 0.50):
        path = [Vector((x, sy, br_deck_top(x) - BR_TH - BR_STR_D * 0.5))
                for x in xs]
        _run(bm, path, 0.055, BR_STR_D * 0.5, WOOD)

    # ---- the bents ------------------------------------------------------
    for sx in (-1, 1):
        for bx in BR_BENTS:
            x = sx * bx
            zc = br_deck_top(x) - BR_TH - BR_STR_D          # cap top
            zcb = zc - BR_CAP_D                              # cap underside
            _member(bm, (x, -0.720, zc - BR_CAP_D * .5),
                    (x, 0.720, zc - BR_CAP_D * .5), BR_CAP_D, BR_CAP_D, WOOD)
            for sy in (-1, 1):
                top = Vector((x, sy * leg_top_y, zcb))
                base = Vector((x, sy * leg_base_y, BR_FOOT_Z))
                _member(bm, base, top, 0.150, 0.150, WOOD)
                # stone footing pad, base at z = 0 -- the lowest thing here
                # ROCK_GREY, not the pale cut stone: at 160 mm of
                # STEPPING these read as bright concrete slabs dropped on the
                # grass, and they are the first thing the eye finds
                E.bm_box(bm, (x, sy * leg_base_y, BR_FOOT_Z * .5),
                         (0.360, 2.0 * (BR_Y - leg_base_y), BR_FOOT_Z),
                         ROCK_GREY)

            def leg_y(z, sy):
                t = (z - BR_FOOT_Z) / max(1e-6, zcb - BR_FOOT_Z)
                return sy * (leg_base_y + (leg_top_y - leg_base_y) * t)

            # X brace and ledger between the legs, landing on their inner faces
            zlo = BR_FOOT_Z + (zcb - BR_FOOT_Z) * 0.22
            zhi = BR_FOOT_Z + (zcb - BR_FOOT_Z) * 0.86
            for sy in (-1, 1):
                _member(bm, (x, leg_y(zlo, sy) - sy * 0.105, zlo),
                        (x, leg_y(zhi, -sy) + sy * 0.105, zhi),
                        0.070, 0.062, WOOD)
            zm = BR_FOOT_Z + (zcb - BR_FOOT_Z) * 0.52
            _member(bm, (x, leg_y(zm, -1) + 0.105, zm),
                    (x, leg_y(zm, 1) - 0.105, zm), 0.090, 0.080, WOOD)

    # ---- longitudinal sway bracing between the two stream bents ---------
    zc0 = br_deck_top(0.85) - BR_TH - BR_STR_D - BR_CAP_D
    zlo = BR_FOOT_Z + (zc0 - BR_FOOT_Z) * 0.30
    zhi = zc0 - 0.16
    for sy in (-1, 1):
        # ride just inboard of the legs at those heights, so both ends land on
        # timber rather than in it
        ylo = sy * (leg_base_y - (leg_base_y - leg_top_y) * 0.30 - 0.105)
        yhi = sy * (leg_base_y - (leg_base_y - leg_top_y) *
                    ((zhi - BR_FOOT_Z) / (zc0 - BR_FOOT_Z)) - 0.105)
        _member(bm, (-0.85, ylo, zlo), (0.85, yhi, zhi), 0.070, 0.062, WOOD)
        _member(bm, (-0.85, yhi, zhi), (0.85, ylo, zlo), 0.070, 0.062, WOOD)

    # ---- abutments: cut stone, founded at z = 0 and buried in the bank ---
    for sx in (-1, 1):
        # The shelf's top follows the stringer soffit and the back wall's top
        # follows the deck soffit, station by station -- a flat top taken from
        # one x buries 11 mm of timber in the masonry at the other end of it,
        # because the deck is cambered.
        def zs(x):
            return br_deck_top(x) - BR_TH - BR_STR_D
        def zb(x):
            return br_deck_top(x) - BR_TH
        prof = [(BR_ABUT_X, zs(BR_ABUT_X)), (BR_ABUT_X + 0.55, zs(str_end)),
                (BR_ABUT_X + 0.57, zb(BR_ABUT_X + 0.57)), (BR_HALF, zb(BR_HALF))]
        _run(bm, [Vector((sx * x, 0, z * .5)) for (x, z) in prof],
             BR_DECK_Y, [z * .5 for (_x, z) in prof], STEPPING)
        # a coping course standing a little proud, so the masonry reads as
        # courses rather than one slab, and rough stones bedded at the toe
        cop = [BR_ABUT_X - 0.05, BR_ABUT_X + 0.18]
        _run(bm, [Vector((sx * x, 0, zs(x) * .5)) for x in cop],
             BR_DECK_Y + 0.035, [zs(x) * .5 for x in cop], STEPPING)
        for (k, ys) in enumerate((-0.46, 0.0, 0.46)):
            tmp = bmesh.new()
            boulder(tmp, rng, rng.uniform(0.13, 0.17), 1,
                    seed + k * 7 + (0 if sx > 0 else 40), ROCK_GREY,
                    squash=0.58)
            me = bpy.data.meshes.new("t")
            tmp.to_mesh(me)
            tmp.free()
            zlo = min(v.co.z for v in me.vertices)
            me.transform(Matrix.Translation(
                (sx * rng.uniform(3.70, 3.86), ys + rng.uniform(-0.03, 0.03),
                 rng.uniform(0.30, 0.46) - zlo)))
            bm.from_mesh(me)
            bpy.data.meshes.remove(me)

    # ---- kerb, posts and handrail ---------------------------------------
    # All of it lives in the 100 mm band OUTBOARD of the deck slab, between
    # |y| = BR_DECK_Y and |y| = BR_Y.  The deck's edge plane and this band's
    # inner plane are the same plane, so the frame butts the deck instead of
    # hovering beside it, and nothing shares volume with it.
    post_xs = [0.0]
    for v in (0.85, 1.70, 2.55, 3.40, 4.30):
        post_xs = [-v] + post_xs + [v]
    pw = 0.048
    yc = (BR_DECK_Y + BR_Y) * 0.5
    yh = (BR_Y - BR_DECK_Y) * 0.5
    for sy in (-1, 1):
        tops = []
        for px in post_xs:
            zt = br_deck_top(px)
            zbase = zt - BR_TH - BR_STR_D - 0.02
            E.bm_box(bm, (px, sy * yc, (zt + BR_RAIL_H + zbase) * 0.5),
                     (pw * 2.0, yh * 2.0, zt + BR_RAIL_H - zbase), WOOD)
            tops.append((px, zt + BR_RAIL_H))
            # knee brace: post inner face to the outer stringer's outer face
            xk = px - math.copysign(0.36, px) if px else 0.36
            _member(bm, (px, sy * (BR_DECK_Y - 0.030), zbase + 0.030),
                    (xk, sy * 0.585,
                     br_deck_top(xk) - BR_TH - BR_STR_D + 0.055),
                    0.058, 0.050, WOOD)
        for k in range(len(post_xs) - 1):
            xa, xb = post_xs[k] + pw, post_xs[k + 1] - pw
            steps = max(2, int((xb - xa) / 0.30) + 1)
            path = [Vector((xa + (xb - xa) * i / steps, sy * yc,
                            br_deck_top(xa + (xb - xa) * i / steps)))
                    for i in range(steps + 1)]
            _run(bm, path, yh, 0.058, WOOD)
        for k in range(len(tops) - 1):
            (xa, za), (xb, zbb) = tops[k], tops[k + 1]
            _rail(bm, xa + pw, za - 0.040, xb - pw, zbb - 0.040,
                  sy * yc, yh, 0.040, WOOD)
            _rail(bm, xa + pw, za - 0.040 - BR_RAIL_H * 0.44,
                  xb - pw, zbb - 0.040 - BR_RAIL_H * 0.44,
                  sy * yc, yh * 0.70, 0.028, WOOD)


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
    # ROCK_WARM, not STRATA.  cliff_strata is a *banded* swatch -- it earns its
    # keep as one stripe up a cliff face, where the bedding planes have something
    # to be layered relative to; wrapped around a free-standing boulder the bands
    # are just noise.  It is also the darkest brown in the atlas (0.349, 0.307,
    # 0.253) against rock_grey's cool (0.368, 0.377, 0.409), so D -- the largest
    # boulder at 1.60 -- was the one dark object in every grey clump.  Warm rather
    # than grey because A and C are already grey and B is already warm: warm keeps
    # the 2/2 split that reads as variety, where a third grey would leave B as the
    # new odd one out, and warm still sits in the same hue family as the strata
    # cliffs D is usually found beneath.
    ("Env_Rock_Boulder_D", 3204, (300, 2000), "base",
     lambda bm, rng: a_rock_scatter(bm, rng, 1, 0.0, 1.60, 1.60, 404,
                                    (ROCK_WARM,))),
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

# Assets big enough that a per-face unique unwrap starves them of texels.
# See the comment in finish().
UV_MODE = {
    "Env_Cave_Arch": "box",
}

ASSET_NOTES = {
    "Env_Cave_Arch":
        "Directional: the mouth faces the model's +Z in Unity, so the level's "
        "facingYaw points it at the player. Pivot is base / XY-centred as usual, "
        "which puts the mouth plane 1.95 m in FRONT of the pivot along +Z -- set "
        "the pivot 1.95 m behind the declared mouth position or the stone sits "
        "that far out into the gorge. Carries a Col vertex-colour attribute: "
        "blue is the vertex-occlusion mask PokeLabPropGroundBlend multiplies "
        "ambient by, near zero down the throat. Import vertex colours on this "
        "mesh; discarding them puts the light back into the cave.",
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
    if UV_MODE.get(name) == "box":
        # Smart-project gives every face its own island, and uv_pack_into_cells
        # then squeezes a material's whole island layout into one 1/4 x 1/4 atlas
        # cell.  Those two together set texel density by *face count*: measured on
        # the cave mouth, 512 ROCK_WARM faces came out at a median UV footprint of
        # 1.6 px of a 512 px cell -- one texel per face, i.e. a flat colour per
        # face, which is the second half of the mosaic.  A box map instead keeps
        # neighbouring faces neighbours in UV, so the rock texture runs across the
        # wall continuously.  Only worth it on the big, mostly-slab pieces; the
        # 250-tri boulders have enough room per face already.
        E.uv_box_walls(obj, margin=0.006)
        E.uv_pack_into_cells(obj, ms)
    else:
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
        if name == "Env_Cave_Arch":
            # order matters: the normal fix round-trips through bmesh, which
            # would drop a colour attribute added before it
            cave_fix_bore_normals(obj)
            obj.data.use_auto_smooth = True
            obj.data.auto_smooth_angle = math.radians(SPLIT_ANGLE)
            cave_occlusion_vcol(obj)
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
            # build_manifest classifies every Terrain subfamily in ROCKY --
            # "Bridge" included -- as a rock, and holds it to 300-2000 tris.
            # The 9 m trestle is a built structure, not a boulder, and its own
            # entry in ASSETS above budgets it at 900-6000 like the buildings.
            # Declaring the class here is the documented way to say so; it
            # does not move any budget, it puts the asset in the right one.
            **({"budgetClass": "building"}
               if name == "Env_Bridge_Wood" else {}),
            "notes": ASSET_NOTES.get(name, ""),
        })
        E.delete_obj(obj)

    E.write_part(FAM, part)
    E.log("---- %d terrain assets, %d with problems" % (len(part), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))


if __name__ == "__main__":
    main()
