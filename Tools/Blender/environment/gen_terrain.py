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
    E.bevel_sharp(tmp, width=radius * 0.030, segments=2, angle_deg=44.0,
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


def a_cave_arch(bm, rng, seed, width=3.6, height=3.4, depth=1.6):
    """A rock mass with a real arched mouth cut through it, then eroded so the
    opening looks collapsed rather than drilled."""
    segs = 22
    inner = []
    outer = []
    for i in range(segs + 1):
        t = i / float(segs)
        a = math.pi * t
        # a pointed, asymmetric arch reads better than a semicircle
        rx = width * 0.5 * (1.0 + 0.10 * math.sin(a * 2.0))
        rz = height * (0.92 + 0.10 * math.sin(a + 0.7))
        x = -math.cos(a) * rx
        z = math.sin(a) * rz
        z *= 1.0 - 0.14 * math.cos(a * 3.0)
        inner.append((x, z))
        ox = x * (1.0 + 0.52 + 0.16 * math.sin(a * 4.0 + seed))
        oz = z * (1.0 + 0.40 + 0.14 * math.cos(a * 3.0 + seed))
        outer.append((ox, min(oz, height * 1.62)))
    for y, mat in ((-depth * 0.5, CAVE_ROCK), (depth * 0.5, CAVE_ROCK)):
        pass
    front = []
    back = []
    for (i, ((ix, iz), (ox, oz))) in enumerate(zip(inner, outer)):
        front.append((bm.verts.new((ix, -depth * 0.5, iz)),
                      bm.verts.new((ox, -depth * 0.5, oz))))
        back.append((bm.verts.new((ix, depth * 0.5, iz)),
                     bm.verts.new((ox, depth * 0.5, oz))))
    for i in range(segs):
        # front and back faces of the arch ring
        f = bm.faces.new((front[i][0], front[i][1],
                          front[i + 1][1], front[i + 1][0]))
        f.material_index = ROCK_WARM
        f.smooth = False
        f = bm.faces.new((back[i + 1][0], back[i + 1][1],
                          back[i][1], back[i][0]))
        f.material_index = ROCK_WARM
        f.smooth = False
        # the tunnel soffit
        f = bm.faces.new((front[i + 1][0], back[i + 1][0],
                          back[i][0], front[i][0]))
        f.material_index = CAVE_ROCK
        f.smooth = False
        # the outer shell
        f = bm.faces.new((front[i][1], back[i][1],
                          back[i + 1][1], front[i + 1][1]))
        f.material_index = ROCK_WARM
        f.smooth = False
    # close the two feet
    for (side, flip) in ((0, False), (segs, True)):
        quad = (front[side][0], front[side][1], back[side][1], back[side][0])
        if flip:
            quad = tuple(reversed(quad))
        f = bm.faces.new(quad)
        f.material_index = ROCK_WARM
        f.smooth = False

    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-5)
    erode(bm, list(bm.verts), seed, amount=0.13, freq=1.1, flatten_top=0.10,
          undercut=0.05, sag=0.06, sharpness=0.75)
    for v in bm.verts:
        if v.co.z < 0.02:
            v.co.z = 0.0
    # zoned greeble: broken rock only on the outer shell, tunnel left clean
    outer_faces = [f for f in bm.faces
                   if f.material_index == ROCK_WARM and
                   f.calc_center_median().length > width * 0.34]
    E.greeble(bm, outer_faces, rng, count=int(len(outer_faces) * 0.40),
              lo=0.16, hi=0.34, depth=0.055)
    E.bevel_sharp(bm, width=0.022, segments=2, angle_deg=46.0, mat_break=False)


def a_bridge(bm, rng, seed, span=4.0, width=1.6):
    """Planked footbridge with a real arch, stringers, posts and rope rails."""
    n = 9
    rise = 0.34
    centres = []
    for i in range(n + 1):
        t = i / float(n)
        x = (t - 0.5) * span
        z = rise * math.sin(math.pi * t) + 0.16
        centres.append(Vector((x, 0, z)))

    # stringers
    for sy in (-1, 1):
        pts = [c + Vector((0, sy * (width * 0.5 - 0.07), -0.055))
               for c in centres]
        E.bm_polytube(bm, pts, [0.055] * len(pts), 5, WOOD,
                      cap_start=True, cap_end=True, smooth=False)

    # planks -- each one slightly different, gapped, a few warped
    for i in range(n):
        t0 = i / float(n)
        c = (centres[i] + centres[i + 1]) * 0.5
        d = (centres[i + 1] - centres[i]).normalized()
        ln = (centres[i + 1] - centres[i]).length * rng.uniform(0.82, 0.92)
        w = width * rng.uniform(0.94, 1.0)
        th = 0.045
        u = d
        v = Vector((0, 1, 0))
        nrm = u.cross(v).normalized()
        tilt = rng.uniform(-0.02, 0.02)
        for sx in (-1, 1):
            for sy in (-1, 1):
                pass
        vs = []
        for (a, b, cc) in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                           (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            p = (c + u * (a * ln * 0.5) + v * (b * w * 0.5) +
                 nrm * (cc * th * 0.5) + Vector((0, 0, b * tilt)))
            vs.append(bm.verts.new(p))
        for q in ((0, 1, 2, 3), (7, 6, 5, 4), (4, 5, 1, 0),
                  (6, 7, 3, 2), (5, 6, 2, 1), (7, 4, 0, 3)):
            f = bm.faces.new([vs[k] for k in q])
            f.material_index = WOOD
            f.smooth = False

    # posts and rope handrails
    posts = []
    for sy in (-1, 1):
        rope_pts = []
        for i in (0, 3, 6, 9):
            c = centres[i]
            base = c + Vector((0, sy * (width * 0.5 - 0.05), -0.02))
            top = base + Vector((0, 0, 0.62 + rng.uniform(-0.03, 0.03)))
            E.bm_polytube(bm, [base, top], [0.052, 0.042], 5, WOOD,
                          cap_start=True, cap_end=True, smooth=False)
            rope_pts.append(top + Vector((0, 0, -0.05)))
        # sagging rope between the posts
        for k in range(len(rope_pts) - 1):
            a, b = rope_pts[k], rope_pts[k + 1]
            seg = [a.lerp(b, s / 4.0) - Vector((0, 0, 0.07 *
                   math.sin(math.pi * (s / 4.0)))) for s in range(5)]
            E.bm_polytube(bm, seg, [0.020] * 5, 4, ROPE,
                          cap_start=False, cap_end=False, smooth=True)

    # abutment stones at each end
    for sx in (-1, 1):
        tmp = bmesh.new()
        boulder(tmp, rng, 0.44, 2, seed + (7 if sx > 0 else 13), ROCK_GREY,
                squash=0.7, amount=0.26)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((sx * span * 0.52, 0, 0.02)))
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


def finish(bm, name, ms, budget, pivot, smooth=26.0):
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
        obj, tris, probs = finish(bm, name, ms, budget, pivot)
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
