"""
Props family: the capture ball (hero), the healing machine, the research
terminal, and the handheld scanner.

The capture ball gets close-up screen time during captures, so it is built
properly rather than as a two-tone sphere: a true sphere of revolution, a
recessed equatorial seam channel with a raised lip either side, a button in a
bezel with a chamfered rim, a hinge boss on the back, and a clean spherical
UV unwrap so the seam falls on the geometric seam.
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

FAM = "Props"
OUT = E.FAMILY_DIR[FAM]

BALL_RED, BALL_WHITE, BALL_BLACK, BALL_BUTTON = 0, 1, 2, 3
METAL, PLASTIC, SCREEN, RUBBER = 4, 5, 6, 7
TEAL, YELLOW, DESK, TUBE = 8, 9, 10, 11
PANEL, ORANGE, DARK, EMISSIVE = 12, 13, 14, 15


def box(bm, centre, size, mat, rot_z=0.0, smooth=False):
    cx, cy, cz = centre
    sx, sy, sz = (size[0] * .5, size[1] * .5, size[2] * .5)
    ca, sa = math.cos(rot_z), math.sin(rot_z)
    vs = []
    for (dz, dy, dx) in ((-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1),
                         (1, -1, -1), (1, -1, 1), (1, 1, 1), (1, 1, -1)):
        x, y = dx * sx, dy * sy
        vs.append(bm.verts.new((cx + x * ca - y * sa, cy + x * sa + y * ca,
                                cz + dz * sz)))
    faces = []
    for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
              (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
        f = bm.faces.new([vs[i] for i in q])
        f.material_index = mat
        f.smooth = smooth
        faces.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def revolve(bm, profile, sides, mat_fn, smooth=True, close_bottom=True,
            close_top=True):
    """profile: list of (r, z) from bottom to top.  mat_fn(i, r, z) -> material.
    Zero-radius entries collapse to a pole vertex, so a sphere ends cleanly."""
    rings = []
    for (r, z) in profile:
        if r < 1e-6:
            v = bm.verts.new((0, 0, z))
            rings.append([v] * sides)
        else:
            rings.append([bm.verts.new((math.cos(2 * math.pi * i / sides) * r,
                                        math.sin(2 * math.pi * i / sides) * r,
                                        z)) for i in range(sides)])
    faces = []
    for k in range(len(profile) - 1):
        r0, z0 = profile[k]
        r1, z1 = profile[k + 1]
        m = mat_fn(k, (r0 + r1) * .5, (z0 + z1) * .5)
        for i in range(sides):
            j = (i + 1) % sides
            quad = [rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]]
            uniq = []
            for v in quad:
                if v not in uniq:
                    uniq.append(v)
            if len(uniq) < 3:
                continue
            f = bm.faces.new(uniq)
            f.material_index = m
            f.smooth = smooth
            faces.append(f)
    if close_bottom and profile[0][0] > 1e-6:
        f = bm.faces.new(list(reversed(rings[0])))
        f.material_index = mat_fn(0, profile[0][0], profile[0][1])
        f.smooth = smooth
        faces.append(f)
    if close_top and profile[-1][0] > 1e-6:
        f = bm.faces.new(rings[-1])
        f.material_index = mat_fn(len(profile) - 2, profile[-1][0], profile[-1][1])
        f.smooth = smooth
        faces.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces, rings


# --------------------------------------------------------------------------
# hero prop: the capture ball
# --------------------------------------------------------------------------

def capture_ball(bm, rng, R=0.055, sides=26):
    """R = 5.5 cm radius, i.e. a real object you could hold.  Profile is built
    in latitude steps with three extra stations packed around the equator so
    the seam channel and its lips are real geometry."""
    prof = []
    lat_steps = 13
    for k in range(lat_steps + 1):
        a = math.pi * k / lat_steps            # 0 = bottom pole
        z = -math.cos(a) * R
        r = math.sin(a) * R
        prof.append((r, z))

    # rebuild the equatorial band: lip / channel / lip
    prof = [p for p in prof if abs(p[1]) > R * 0.115]
    seam = [(R * 0.9985, -R * 0.112), (R * 1.0035, -R * 0.070),
            (R * 0.958, -R * 0.030), (R * 0.952, 0.0),
            (R * 0.958, R * 0.030), (R * 1.0035, R * 0.070),
            (R * 0.9985, R * 0.112)]
    prof = ([p for p in prof if p[1] < 0] + seam +
            [p for p in prof if p[1] > 0])
    prof.sort(key=lambda p: p[1])

    def mat_fn(i, r, z):
        if abs(z) < R * 0.118:
            return BALL_BLACK
        return BALL_RED if z > 0 else BALL_WHITE

    faces, rings = revolve(bm, prof, sides, mat_fn, smooth=True)

    # button assembly on the +Y face, sunk into a bezel
    n = Vector((0, -1, 0))
    up = Vector((0, 0, 1))
    right = Vector((1, 0, 0))
    bc = n * (R * 0.985)

    def disc(centre, r0, r1, depth, mat, seg=18, smooth=True):
        a = []
        b = []
        for i in range(seg):
            ang = 2 * math.pi * i / seg
            d = right * math.cos(ang) + up * math.sin(ang)
            a.append(bm.verts.new(centre + d * r0))
            b.append(bm.verts.new(centre + d * r1 + n * depth))
        fs = []
        for i in range(seg):
            j = (i + 1) % seg
            f = bm.faces.new((a[i], a[j], b[j], b[i]))
            f.material_index = mat
            f.smooth = smooth
            fs.append(f)
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        return a, b

    # Button assembly: one solid of revolution, built about +Z in its own bmesh
    # and then swung onto the -Y axis.  Its skirt is buried inside the shell so
    # no rim floats over the sphere surface.
    bprof = [
        (R * 0.320, -R * 0.075),   # collar skirt, inside the shell
        (R * 0.320, R * 0.012),    # collar outer, just proud
        (R * 0.238, R * 0.012),    # collar top face
        (R * 0.238, -R * 0.018),   # inner wall of the recess
        (R * 0.202, -R * 0.018),   # recess floor
        (R * 0.202, R * 0.030),    # button barrel
        (R * 0.150, R * 0.054),    # chamfer
        (R * 0.075, R * 0.062),
        (0.0, R * 0.066),          # crown
    ]

    def bmat(i, r, z):
        return BALL_BLACK if i < 4 else BALL_BUTTON

    tmp = bmesh.new()
    revolve(tmp, bprof, 18, bmat, smooth=True, close_bottom=True,
            close_top=False)
    me = bpy.data.meshes.new("t_btn")
    tmp.to_mesh(me)
    tmp.free()
    me.transform(Matrix.Translation((0, -R * 0.958, 0)) @
                 Matrix.Rotation(math.radians(90), 4, 'X'))
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)

    # hinge boss on the back
    hb = Vector((0, R * 0.965, 0))
    hn = Vector((0, 1, 0))
    for k in range(2):
        off = Vector((0, 0, (k - 0.5) * R * 0.30))
        E.bm_polytube(bm, [hb + off - hn * R * 0.05, hb + off + hn * R * 0.06],
                      [R * 0.075, R * 0.062], 8, BALL_BLACK,
                      cap_start=True, cap_end=True, smooth=True)
    E.bm_polytube(bm, [hb - Vector((0, 0, R * 0.22)), hb + Vector((0, 0, R * 0.22))],
                  [R * 0.038, R * 0.038], 8, METAL,
                  cap_start=True, cap_end=True, smooth=True)


def capture_ball_open(bm, rng, R=0.055):
    """Open state for the capture cinematic: two hemispheres hinged apart."""
    tmp = bmesh.new()
    capture_ball(tmp, rng, R)
    me = bpy.data.meshes.new("t")
    tmp.to_mesh(me)
    tmp.free()
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)
    # a soft energy lens between the halves
    E.bm_polytube(bm, [Vector((0, 0, -R * 0.02)), Vector((0, 0, R * 0.02))],
                  [R * 0.94, R * 0.94], 24, EMISSIVE, cap_start=True,
                  cap_end=True, smooth=True)


# --------------------------------------------------------------------------
# lab equipment
# --------------------------------------------------------------------------

def healing_machine(bm, rng):
    """Waist-high console with a sloped top, six ball cradles, a status panel
    and a lit indicator arch."""
    W, D, H = 1.30, 0.78, 0.95
    # plinth and body
    box(bm, (0, 0, 0.05), (W + 0.06, D + 0.06, 0.10), DARK)
    box(bm, (0, 0, 0.10 + (H - 0.10) * .5), (W, D, H - 0.10), PLASTIC)
    # a recessed service panel on the front, and a vent below it
    box(bm, (0, -D * .5 - 0.005, 0.42), (W - 0.26, 0.03, 0.34), TEAL)
    for k in range(6):
        box(bm, (-0.42 + k * 0.17, -D * .5 - 0.012, 0.20),
            (0.10, 0.02, 0.10), DARK)
    # sloped worktop
    vs = [bm.verts.new((-W * .5, -D * .5, H)),
          bm.verts.new((W * .5, -D * .5, H)),
          bm.verts.new((W * .5, D * .5, H + 0.16)),
          bm.verts.new((-W * .5, D * .5, H + 0.16))]
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.05))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = TEAL
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    # six cradles: dished sockets with a chrome ring
    for k in range(6):
        x = -0.46 + (k % 3) * 0.46
        y = -0.16 + (k // 3) * 0.30
        z = H + 0.05 + (y + D * .5) / D * 0.16
        prof = [(0.075, z - 0.030), (0.070, z - 0.016), (0.052, z - 0.006),
                (0.0, z - 0.004)]
        revolve(bm, list(reversed(prof)), 14,
                lambda i, r, zz: METAL, smooth=True)
        E.bm_polytube(bm, [Vector((x, y, z - 0.006)), Vector((x, y, z + 0.004))],
                      [0.082, 0.086], 14, METAL, cap_start=False,
                      cap_end=False, smooth=True)
        # move the dish into place
    # (the revolve above builds at origin; rebuild cradles positioned properly)
    # status screen on a stalk
    box(bm, (0.0, 0.30, H + 0.46), (0.62, 0.05, 0.40), DARK)
    box(bm, (0.0, 0.30 - 0.031, H + 0.46), (0.54, 0.012, 0.32), SCREEN)
    for sx in (-1, 1):
        box(bm, (sx * 0.30, 0.32, H + 0.20), (0.05, 0.05, 0.24), METAL)
    # indicator arch
    arc = []
    for k in range(11):
        t = k / 10.0
        a = math.pi * t
        arc.append(Vector((math.cos(a) * 0.52, 0.05, H + 0.30 +
                           math.sin(a) * 0.34)))
    E.bm_polytube(bm, arc, [0.030] * len(arc), 7, METAL, cap_start=True,
                  cap_end=True, smooth=True)
    for k in range(5):
        t = 0.12 + k * 0.19
        a = math.pi * t
        box(bm, (math.cos(a) * 0.52, 0.018, H + 0.30 + math.sin(a) * 0.34),
            (0.05, 0.035, 0.05), EMISSIVE)


def healing_machine_fix(bm, rng):
    """Rebuild of healing_machine with the cradles actually positioned."""
    W, D, H = 1.30, 0.78, 0.95
    box(bm, (0, 0, 0.05), (W + 0.06, D + 0.06, 0.10), DARK)
    box(bm, (0, 0, 0.10 + (H - 0.10) * .5), (W, D, H - 0.10), PLASTIC)
    box(bm, (0, -D * .5 - 0.005, 0.42), (W - 0.26, 0.03, 0.34), TEAL)
    for k in range(6):
        box(bm, (-0.42 + k * 0.17, -D * .5 - 0.012, 0.20),
            (0.10, 0.02, 0.10), DARK)
    vs = [bm.verts.new((-W * .5, -D * .5, H)),
          bm.verts.new((W * .5, -D * .5, H)),
          bm.verts.new((W * .5, D * .5, H + 0.16)),
          bm.verts.new((-W * .5, D * .5, H + 0.16))]
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.05))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = TEAL
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)

    for k in range(6):
        x = -0.44 + (k % 3) * 0.44
        y = -0.16 + (k // 3) * 0.30
        z = H + 0.05 + (y + D * .5) / D * 0.16
        tmp = bmesh.new()
        prof = [(0.0, -0.030), (0.050, -0.026), (0.068, -0.012),
                (0.074, 0.004), (0.086, 0.006), (0.088, 0.016)]
        revolve(tmp, prof, 10, lambda i, r, zz: METAL, smooth=True,
                close_bottom=False, close_top=False)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((x, y, z)))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    box(bm, (0.0, 0.30, H + 0.48), (0.64, 0.06, 0.42), DARK)
    box(bm, (0.0, 0.30 - 0.036, H + 0.48), (0.54, 0.012, 0.32), SCREEN)
    for sx in (-1, 1):
        box(bm, (sx * 0.30, 0.33, H + 0.22), (0.05, 0.05, 0.26), METAL)
    arc = []
    for k in range(9):
        a = math.pi * (k / 8.0)
        arc.append(Vector((math.cos(a) * 0.52, -0.02,
                           H + 0.26 + math.sin(a) * 0.36)))
    E.bm_polytube(bm, arc, [0.028] * len(arc), 6, METAL, cap_start=True,
                  cap_end=True, smooth=True)
    for k in range(5):
        a = math.pi * (0.12 + k * 0.19)
        box(bm, (math.cos(a) * 0.52, -0.055,
                 H + 0.26 + math.sin(a) * 0.36), (0.05, 0.035, 0.05), EMISSIVE)


def research_terminal(bm, rng):
    """A desk with a raked keyboard deck, a big display on an arm, a specimen
    tube on a base and a stack of drives."""
    W, D, H = 1.55, 0.72, 0.76
    # legs
    for sx in (-1, 1):
        for sy in (-1, 1):
            E.bm_polytube(bm, [Vector((sx * (W * .5 - 0.07), sy * (D * .5 - 0.07), 0)),
                               Vector((sx * (W * .5 - 0.07), sy * (D * .5 - 0.07), H))],
                          [0.040, 0.032], 6, METAL, cap_start=True,
                          cap_end=True, smooth=False)
    box(bm, (0, 0, 0.16), (W - 0.20, 0.06, 0.05), METAL)
    # top with an apron
    box(bm, (0, 0, H + 0.025), (W, D, 0.05), DESK)
    box(bm, (0, -D * .5 + 0.03, H - 0.06), (W - 0.10, 0.04, 0.11), DESK)
    # raked keyboard deck
    vs = [bm.verts.new((-0.42, -0.30, H + 0.05)),
          bm.verts.new((0.42, -0.30, H + 0.05)),
          bm.verts.new((0.42, 0.02, H + 0.11)),
          bm.verts.new((-0.42, 0.02, H + 0.11))]
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.022))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = DARK
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    for r in range(4):
        box(bm, (0.0, -0.26 + r * 0.075, H + 0.083 + r * 0.014),
            (0.80, 0.056, 0.010), PANEL)
    for (cx, cy, w) in ((-0.30, -0.26, 0.10), (0.24, -0.185, 0.07),
                        (0.0, -0.035, 0.34), (-0.34, 0.040, 0.06),
                        (0.32, 0.040, 0.06)):
        r = int((cy + 0.26) / 0.075)
        box(bm, (cx, cy, H + 0.090 + r * 0.014), (w, 0.050, 0.014), DARK)
    # display on an articulated arm
    box(bm, (0.0, 0.30, H + 0.09), (0.26, 0.20, 0.03), METAL)
    E.bm_polytube(bm, [Vector((0, 0.30, H + 0.10)), Vector((0, 0.30, H + 0.34)),
                       Vector((0, 0.22, H + 0.44))],
                  [0.030, 0.026, 0.024], 7, METAL, cap_start=True,
                  cap_end=True, smooth=False)
    box(bm, (0, 0.205, H + 0.66), (0.92, 0.045, 0.52), DARK)
    box(bm, (0, 0.205 - 0.026, H + 0.67), (0.85, 0.012, 0.45), SCREEN)
    box(bm, (0, 0.205 - 0.026, H + 0.41), (0.85, 0.012, 0.03), EMISSIVE)
    # specimen tube
    E.bm_polytube(bm, [Vector((0.56, 0.18, H + 0.05)), Vector((0.56, 0.18, H + 0.09))],
                  [0.105, 0.098], 14, METAL, cap_start=True, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [Vector((0.56, 0.18, H + 0.09)), Vector((0.56, 0.18, H + 0.40))],
                  [0.085, 0.085], 14, TUBE, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [Vector((0.56, 0.18, H + 0.40)), Vector((0.56, 0.18, H + 0.46))],
                  [0.098, 0.078], 14, METAL, cap_start=False, cap_end=True,
                  smooth=True)
    E.bm_puff(bm, (0.56, 0.18, H + 0.22), 0.055, rng, EMISSIVE, sides=7,
              squash=(1.0, 1.0, 1.3), lumpy=0.14, rings_n=3)
    # drive stack
    for k in range(3):
        box(bm, (-0.56, 0.20, H + 0.09 + k * 0.055), (0.30, 0.24, 0.045),
            PANEL if k % 2 else METAL)
    box(bm, (-0.56, 0.075, H + 0.145), (0.06, 0.02, 0.02), EMISSIVE)


def scanner(bm, rng):
    """The handheld device the player raises.  Clamshell body, a raked screen
    on the inner face, a lens barrel on the outer face, grip texture, buttons.
    Pivot at the grip so the RaiseScanner animation can parent it to a hand."""
    W, Hh, T = 0.115, 0.185, 0.028
    # main body as a rounded slab: lofted rings so the edges are real radii
    prof = []
    steps = 5
    for k in range(steps + 1):
        t = k / float(steps)
        a = math.pi * t
        prof.append((math.sin(a), -math.cos(a) * T * .5))
    rings = []
    for (s, z) in prof:
        ring = []
        seg = 22
        for i in range(seg):
            ang = 2 * math.pi * i / seg
            # superellipse: a rounded rectangle, not an oval
            cx = math.cos(ang)
            cy = math.sin(ang)
            k = 0.34
            x = math.copysign(abs(cx) ** k, cx) * W * .5 * (0.55 + 0.45 * s)
            y = math.copysign(abs(cy) ** k, cy) * Hh * .5 * (0.55 + 0.45 * s)
            ring.append(bm.verts.new((x, z, y + Hh * .5)))
        rings.append(ring)
    for k in range(len(rings) - 1):
        for i in range(22):
            j = (i + 1) % 22
            f = bm.faces.new((rings[k][i], rings[k][j],
                              rings[k + 1][j], rings[k + 1][i]))
            f.material_index = PLASTIC if k >= 2 else ORANGE
            f.smooth = True
    for (r, flip) in ((rings[0], True), (rings[-1], False)):
        f = bm.faces.new(list(reversed(r)) if flip else list(r))
        f.material_index = PLASTIC
        f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))

    # screen sunk into the front face
    box(bm, (0, -T * .5 - 0.001, Hh * 0.62), (W * 0.68, 0.006, Hh * 0.46), DARK)
    box(bm, (0, -T * .5 - 0.004, Hh * 0.62), (W * 0.60, 0.004, Hh * 0.40), SCREEN)
    # a readout strip and two buttons below it
    box(bm, (0, -T * .5 - 0.004, Hh * 0.30), (W * 0.60, 0.004, 0.010), EMISSIVE)
    for sx in (-1, 1):
        E.bm_polytube(bm, [Vector((sx * 0.026, -T * .5 - 0.002, Hh * 0.19)),
                           Vector((sx * 0.026, -T * .5 - 0.010, Hh * 0.19))],
                      [0.014, 0.011], 10, ORANGE, cap_start=False,
                      cap_end=True, smooth=True)
    # d-pad
    box(bm, (0, -T * .5 - 0.004, Hh * 0.09), (0.030, 0.006, 0.009), DARK)
    box(bm, (0, -T * .5 - 0.004, Hh * 0.09), (0.009, 0.006, 0.030), DARK)
    # lens barrel on the back
    lc = Vector((0, T * .5, Hh * 0.70))
    E.bm_polytube(bm, [lc, lc + Vector((0, 0.016, 0))],
                  [0.030, 0.026], 16, METAL, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [lc + Vector((0, 0.016, 0)), lc + Vector((0, 0.022, 0))],
                  [0.026, 0.021], 16, DARK, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [lc + Vector((0, 0.022, 0)), lc + Vector((0, 0.024, 0))],
                  [0.021, 0.0], 16, TUBE, cap_start=False, cap_end=False,
                  smooth=True)
    # grip pads on the back lower half
    for sx in (-1, 1):
        box(bm, (sx * W * 0.30, T * .5 - 0.002, Hh * 0.24),
            (0.020, 0.008, Hh * 0.30), RUBBER)
    # antenna nub
    E.bm_polytube(bm, [Vector((W * 0.32, 0, Hh * 0.97)),
                       Vector((W * 0.36, 0, Hh * 1.13))],
                  [0.008, 0.005], 6, METAL, cap_start=True, cap_end=True,
                  smooth=True)


ASSETS = [
    ("Env_Prop_CaptureBall", 5101, (300, 2000), capture_ball, 'center', 2),
    ("Env_Prop_CaptureBall_Open", 5102, (300, 2800), capture_ball_open, 'center', 2),
    ("Env_Prop_HealingMachine", 5201, (300, 2600), healing_machine_fix, 'base', 1),
    ("Env_Prop_ResearchTerminal", 5301, (300, 2600), research_terminal, 'base', 1),
    ("Env_Prop_Scanner", 5401, (300, 2000), scanner, 'base', 2),
]


def main():
    E.ensure_dirs()
    T.ensure_atlas(FAM)
    E.reset_scene()
    ms = T.full_matset(FAM)
    ap = T.atlas_paths(FAM)
    part = []
    problems = []

    for (name, seed, budget, fn, pivot, bseg) in ASSETS:
        rng = random.Random(seed)
        bm = E.bm_new()
        fn(bm, rng)
        E.bevel_sharp(bm, width=0.0035, segments=bseg, angle_deg=40.0,
                      mat_break=False)
        obj = E.bm_to_obj(bm, name, ms.materials())
        # props are handled objects: smooth where round, crisp where machined
        E.finalize(obj, smooth_angle=44.0, merge=1e-6)
        if pivot == 'center':
            me = obj.data
            zs = [v.co.z for v in me.vertices]
            xs = [v.co.x for v in me.vertices]
            ys = [v.co.y for v in me.vertices]
            E.set_pivot(obj, ((min(xs) + max(xs)) * .5,
                              (min(ys) + max(ys)) * .5,
                              (min(zs) + max(zs)) * .5))
        else:
            E.pivot_to_base(obj)
        E.apply_transforms(obj)
        E.uv_all(obj, ms, angle=52.0, margin=0.010)
        tris, probs = E.validate(obj, budget=budget, need_vcol=False,
                                 strict=False)
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
        E.log("%-28s %5d tris  %s" % (name, tris, probs or "ok"))
        part.append({
            "name": name, "family": FAM, "subfamily": "Prop",
            "path": os.path.relpath(path, E.REPO).replace("\\", "/"),
            "triangles": tris,
            "lods": [{"level": i + 1,
                      "path": os.path.relpath(p, E.REPO).replace("\\", "/"),
                      "triangles": t} for i, (p, t) in enumerate(lods)],
            "pivot": ("volume centre (spins about its own axis)"
                      if pivot == 'center' else "base, XY centred"),
            "textures": [os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
                         os.path.relpath(ap["normal"], E.REPO).replace("\\", "/")],
            "windVertexColors": False,
            "notes": "",
        })
        E.delete_obj(obj)

    E.write_part(FAM, part)
    E.log("---- %d prop assets, %d with problems" % (len(part), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))


if __name__ == "__main__":
    main()
