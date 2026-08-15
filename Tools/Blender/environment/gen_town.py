"""
Town family: three houses, the Poke Lab civic building, and the street kit.

Buildings are built in the three-tier hierarchy the hard-surface skill argues
for: primary massing first (a footprint lofted to eaves height, with a real
gable/hip roof carcass), then medium divisions (window and door reveals cut
INTO the wall with inset_region, sill and lintel courses, corner boards,
eaves fascia), then a restrained fine tier (roof ridge cap, chimney flashing,
shutters).  Nothing is a box with a texture on it.
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

FAM = "Town"
OUT = E.FAMILY_DIR[FAM]

PLASTER_C, PLASTER_B, PLASTER_R, BEAM = 0, 1, 2, 3
ROOF_RED, ROOF_BLUE, ROOF_GREEN, METAL_ROOF = 4, 5, 6, 7
GLASS, DOOR, PAVING, STONE_WALL = 8, 9, 10, 11
FENCE, LAMP, AWNING, TRIM = 12, 13, 14, 15

GRID = 0.5


# --------------------------------------------------------------------------
# generic hard-surface helpers
# --------------------------------------------------------------------------

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


def prism_wall(bm, poly, height, mat, z0=0.0):
    """Extrude a footprint polygon upward.  Returns (wall_faces, top_ring)."""
    bot = [bm.verts.new((x, y, z0)) for (x, y) in poly]
    top = [bm.verts.new((x, y, z0 + height)) for (x, y) in poly]
    faces = []
    n = len(poly)
    for i in range(n):
        j = (i + 1) % n
        f = bm.faces.new((bot[i], bot[j], top[j], top[i]))
        f.material_index = mat
        f.smooth = False
        faces.append(f)
    f = bm.faces.new(list(reversed(bot)))
    f.material_index = mat
    bmesh.ops.recalc_face_normals(bm, faces=faces + [f])
    return faces, top


def gable_roof(bm, w, d, eave_z, ridge_h, over=0.34, mat=ROOF_RED,
               mat_gable=TRIM, ridge_along_x=True, thickness=0.10):
    """A real roof carcass: two pitched planes with thickness, an overhanging
    eave with a fascia, gable end infill and a ridge cap."""
    W = w * 0.5 + over
    D = d * 0.5 + over
    rz = eave_z + ridge_h
    if ridge_along_x:
        a0 = Vector((-W, -D, eave_z))
        a1 = Vector((W, -D, eave_z))
        b0 = Vector((-W, 0, rz))
        b1 = Vector((W, 0, rz))
        c0 = Vector((-W, D, eave_z))
        c1 = Vector((W, D, eave_z))
        # ridge runs along X at y=0, so the gable walls are the ends at x=+-w/2
        gable_pts = [(-w * .5, -d * .5, eave_z), (-w * .5, d * .5, eave_z),
                     (-w * .5, 0, rz)]
        gable_pts2 = [(w * .5, -d * .5, eave_z), (w * .5, d * .5, eave_z),
                      (w * .5, 0, rz)]
    else:
        a0 = Vector((-W, -D, eave_z))
        a1 = Vector((-W, D, eave_z))
        b0 = Vector((0, -D, rz))
        b1 = Vector((0, D, rz))
        c0 = Vector((W, -D, eave_z))
        c1 = Vector((W, D, eave_z))
        # ridge runs along Y at x=0, so the gable walls are at y=+-d/2
        gable_pts = [(-w * .5, -d * .5, eave_z), (w * .5, -d * .5, eave_z),
                     (0, -d * .5, rz)]
        gable_pts2 = [(-w * .5, d * .5, eave_z), (w * .5, d * .5, eave_z),
                      (0, d * .5, rz)]

    up = Vector((0, 0, thickness))

    def slab(p0, p1, q0, q1, m):
        # winding must go round the rim: p0 -> p1 -> q0 -> q1.  Reordering
        # here produces a bowtie quad, which renders as two crossed triangles.
        vs = [bm.verts.new(p) for p in (p0, p1, q0, q1)]
        vt = [bm.verts.new(p + up) for p in (p0, p1, q0, q1)]
        fs = []
        f = bm.faces.new(vt)
        f.material_index = m
        fs.append(f)
        f = bm.faces.new(list(reversed(vs)))
        f.material_index = TRIM
        fs.append(f)
        for i in range(4):
            j = (i + 1) % 4
            f = bm.faces.new((vs[i], vs[j], vt[j], vt[i]))
            f.material_index = TRIM
            fs.append(f)
        for f in fs:
            f.smooth = False
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        return fs

    slab(a0, a1, b1, b0, mat)
    slab(b0, b1, c1, c0, mat)
    # ridge cap
    ridge_mid = (b0 + b1) * 0.5 + Vector((0, 0, thickness))
    ln = (b1 - b0).length
    ax = (b1 - b0).normalized()
    box(bm, ridge_mid + Vector((0, 0, 0.035)),
        (ln + 0.08 if abs(ax.x) > 0.5 else 0.16,
         0.16 if abs(ax.x) > 0.5 else ln + 0.08, 0.09), mat)
    # gable end infill
    for pts in (gable_pts, gable_pts2):
        vs = [bm.verts.new(p) for p in pts]
        f = bm.faces.new(vs)
        f.material_index = mat_gable
        f.smooth = False
        bmesh.ops.recalc_face_normals(bm, faces=[f])
    return rz


def window(bm, wall_faces, centre, size, normal, mat_glass=GLASS,
           mat_frame=TRIM, mullion=True, sill=True):
    """A window built as a reveal: recessed frame box, glazing set back inside
    it, sill and lintel proud of the wall.  Never a decal, never a stuck-on box."""
    n = Vector(normal).normalized()
    c = Vector(centre)
    w, h = size
    right = n.cross(Vector((0, 0, 1)))
    if right.length < 1e-6:
        right = Vector((1, 0, 0))
    right.normalize()
    up = Vector((0, 0, 1))

    def plate(cc, ww, hh, dd, mat):
        vs = []
        for (a, b, s) in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                          (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            p = cc + right * (a * ww * .5) + up * (b * hh * .5) + n * (s * dd * .5)
            vs.append(bm.verts.new(p))
        fs = []
        for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                  (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
            f = bm.faces.new([vs[i] for i in q])
            f.material_index = mat
            f.smooth = False
            fs.append(f)
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        return fs

    # The frame is a RING of four bars, not a slab -- a slab here quietly
    # covers the glazing and the whole facade reads as blank white boards.
    fw = 0.075                                   # frame bar width
    plate(c - n * 0.055, w, h, 0.04, mat_glass)  # glazing, set back in the reveal
    for sy in (-1, 1):
        plate(c + up * (sy * (h * .5 + fw * .5)) + n * 0.022,
              w + fw * 2.0, fw, 0.075, mat_frame)
    for sx in (-1, 1):
        plate(c + right * (sx * (w * .5 + fw * .5)) + n * 0.022,
              fw, h, 0.075, mat_frame)
    # reveal returns: thin dark jambs between wall face and glass
    for sx in (-1, 1):
        plate(c + right * (sx * (w * .5 - 0.008)) - n * 0.030,
              0.016, h - 0.01, 0.055, mat_frame)
    if mullion:
        plate(c - n * 0.038, 0.034, h - 0.01, 0.030, mat_frame)
        plate(c - n * 0.038, w - 0.01, 0.034, 0.030, mat_frame)
    if sill:
        plate(c - up * (h * .5 + fw + 0.035) + n * 0.062,
              w + 0.26, 0.070, 0.20, mat_frame)
        plate(c + up * (h * .5 + fw + 0.030) + n * 0.048,
              w + 0.22, 0.055, 0.16, mat_frame)


def door(bm, centre, size, normal, mat=DOOR, mat_frame=TRIM, step=True):
    n = Vector(normal).normalized()
    c = Vector(centre)
    w, h = size
    right = n.cross(Vector((0, 0, 1))).normalized()
    up = Vector((0, 0, 1))

    def plate(cc, ww, hh, dd, m):
        vs = []
        for (a, b, s) in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                          (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            vs.append(bm.verts.new(cc + right * (a * ww * .5) +
                                   up * (b * hh * .5) + n * (s * dd * .5)))
        fs = []
        for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                  (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
            f = bm.faces.new([vs[i] for i in q])
            f.material_index = m
            f.smooth = False
            fs.append(f)
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        return fs

    # architrave as a ring of three bars, leaf set back into the opening
    fw = 0.095
    plate(c - n * 0.040, w, h, 0.06, mat)
    plate(c + up * (h * .5 + fw * .5) + n * 0.028,
          w + fw * 2.0, fw, 0.085, mat_frame)
    for sx in (-1, 1):
        plate(c + right * (sx * (w * .5 + fw * .5)) + n * 0.028,
              fw, h + fw, 0.085, mat_frame)
    # two raised door panels, proud of the leaf
    plate(c - n * 0.012 - up * (h * 0.20), w - 0.22, h * 0.32, 0.022, mat)
    plate(c - n * 0.012 + up * (h * 0.22), w - 0.22, h * 0.30, 0.022, mat)
    # handle
    plate(c - n * 0.055 + right * (w * 0.32), 0.05, 0.05, 0.09, mat_frame)
    if step:
        box(bm, (c + n * 0.26 - up * (h * .5 + 0.055)),
            (w + 0.46, 0.52, 0.11), PAVING)
        box(bm, (c + n * 0.20 - up * (h * .5 + 0.145)),
            (w + 0.66, 0.66, 0.08), PAVING)


def chimney(bm, x, y, base_z, top_z, w=0.44, mat=STONE_WALL):
    box(bm, (x, y, (base_z + top_z) * .5), (w, w, top_z - base_z), mat)
    box(bm, (x, y, top_z + 0.055), (w + 0.16, w + 0.16, 0.11), TRIM)
    box(bm, (x, y, top_z + 0.18), (w * 0.42, w * 0.42, 0.16), STONE_WALL)


# --------------------------------------------------------------------------
# buildings
# --------------------------------------------------------------------------

def house_a(bm, rng):
    """Cottage: L-shaped plan, steep gable, dormer, half-timbered gable end."""
    w, d, eave = 4.5, 4.0, 2.5
    poly = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2 - 1.2),
            (w / 2 - 1.5, d / 2 - 1.2), (w / 2 - 1.5, d / 2), (-w / 2, d / 2)]
    walls, top = prism_wall(bm, poly, eave, PLASTER_C)
    # stone plinth course
    for (i, (x, y)) in enumerate(poly):
        pass
    plinth = [(x * 1.02, y * 1.02) for (x, y) in poly]
    prism_wall(bm, plinth, 0.42, STONE_WALL)
    gable_roof(bm, w, d, eave, 1.55, 0.36, ROOF_RED, TRIM, ridge_along_x=True)
    # corner boards -- medium tier
    for (sx, sy) in ((-1, -1), (1, -1), (-1, 1)):
        box(bm, (sx * (w / 2 - 0.02), sy * (d / 2 - 0.02), eave * 0.5),
            (0.16, 0.16, eave), BEAM)
    # half timbering on the front gable, zoned: front only
    for k in range(4):
        z = 0.55 + k * 0.62
        box(bm, (0, -d / 2 - 0.015, z), (w - 0.30, 0.06, 0.11), BEAM)
    for k in (-1, 1):
        box(bm, (k * 1.05, -d / 2 - 0.015, eave * 0.5),
            (0.10, 0.06, eave - 0.2), BEAM)
    window(bm, walls, (-1.15, -d / 2, 1.55), (0.85, 0.95), (0, -1, 0))
    window(bm, walls, (1.30, -d / 2, 1.55), (0.85, 0.95), (0, -1, 0))
    window(bm, walls, (-w / 2, 0.20, 1.60), (0.80, 0.90), (-1, 0, 0))
    door(bm, (0.05, -d / 2, 1.05), (0.95, 2.05), (0, -1, 0))
    # dormer on the rear pitch
    box(bm, (-1.0, 0.55, eave + 0.62), (1.15, 1.0, 1.0), PLASTER_C)
    gable_roof(bm, 1.35, 1.2, eave + 1.12, 0.44, 0.14, ROOF_RED, TRIM,
               ridge_along_x=False)
    window(bm, walls, (-1.0, 0.05, eave + 0.72), (0.60, 0.62), (0, -1, 0),
           sill=False)
    chimney(bm, w / 2 - 0.9, 0.9, 1.0, eave + 2.15)


def house_b(bm, rng):
    """Two-storey townhouse: hip roof, jettied first floor, shopfront awning."""
    w, d, e1, e2 = 4.0, 4.4, 2.45, 4.75
    poly = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]
    prism_wall(bm, poly, e1, PLASTER_B)
    # jetty: upper floor oversails by 22 cm on the street side
    j = 0.22
    poly2 = [(-w / 2 - j, -d / 2 - j), (w / 2 + j, -d / 2 - j),
             (w / 2 + j, d / 2), (-w / 2 - j, d / 2)]
    prism_wall(bm, poly2, e2 - e1, PLASTER_C, z0=e1)
    # jetty bracket beams
    for x in (-1.35, 0.0, 1.35):
        box(bm, (x, -d / 2 - j * 0.5, e1 + 0.04), (0.14, j + 0.10, 0.16), BEAM)
    box(bm, (0, -d / 2 - j, e1 + 0.14), (w + 2 * j + 0.06, 0.10, 0.16), BEAM)
    gable_roof(bm, w + 2 * j, d + j, e2, 1.15, 0.36, ROOF_BLUE, TRIM,
               ridge_along_x=True)
    for (sx, sy) in ((-1, -1), (1, -1), (-1, 1), (1, 1)):
        box(bm, (sx * (w / 2 - 0.02), sy * (d / 2 - 0.02), e1 * 0.5),
            (0.15, 0.15, e1), BEAM)
    # ground floor shopfront
    window(bm, None, (-1.05, -d / 2, 1.45), (1.15, 1.25), (0, -1, 0))
    door(bm, (1.15, -d / 2, 1.05), (1.0, 2.05), (0, -1, 0))
    # awning over the shopfront
    for i in range(6):
        x = -1.85 + i * 0.32
        box(bm, (x, -d / 2 - 0.42, 2.12 - 0.10), (0.30, 0.90, 0.05), AWNING,
            rot_z=0.0)
    box(bm, (0, -d / 2 - 0.86, 1.94), (2.10, 0.06, 0.14), TRIM)
    # upper floor windows
    for x in (-1.15, 0.0, 1.15):
        window(bm, None, (x, -d / 2 - j, e1 + 1.10), (0.68, 0.95), (0, -1, 0))
    window(bm, None, (w / 2, 0.4, e1 + 1.10), (0.68, 0.95), (1, 0, 0))
    chimney(bm, -w / 2 + 0.55, 1.2, e1, e2 + 1.55, 0.40)


def house_c(bm, rng):
    """Long low farmhouse: shallow pitch, porch on posts, shuttered windows."""
    w, d, eave = 6.0, 3.6, 2.35
    poly = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]
    prism_wall(bm, poly, eave, PLASTER_R)
    prism_wall(bm, [(x * 1.015, y * 1.02) for (x, y) in poly], 0.34, STONE_WALL)
    gable_roof(bm, w, d, eave, 1.05, 0.46, ROOF_GREEN, TRIM, ridge_along_x=True)
    # porch: real posts, a beam and its own lean-to roof
    py = -d / 2 - 1.15
    for x in (-1.55, 0.0, 1.55):
        E.bm_polytube(bm, [Vector((x, py, 0.06)), Vector((x, py, 2.20))],
                      [0.085, 0.075], 6, BEAM, cap_start=True, cap_end=True,
                      smooth=False)
        box(bm, (x, py, 2.10), (0.30, 0.30, 0.12), BEAM)
    box(bm, (0, py, 2.28), (3.6, 0.14, 0.16), BEAM)
    vs = []
    for (x, y, z) in ((-2.1, py - 0.30, 2.34), (2.1, py - 0.30, 2.34),
                      (2.1, -d / 2 + 0.05, 2.86), (-2.1, -d / 2 + 0.05, 2.86)):
        vs.append(bm.verts.new((x, y, z)))
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.09))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = ROOF_GREEN
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    for x in (-2.05, -0.6, 0.9, 2.2):
        window(bm, None, (x, -d / 2, 1.50), (0.72, 0.92), (0, -1, 0))
        for sx in (-1, 1):
            box(bm, (x + sx * 0.55, -d / 2 - 0.06, 1.50), (0.30, 0.07, 0.98),
                BEAM)
    door(bm, (0.15, -d / 2, 1.08), (1.0, 2.10), (0, -1, 0))
    for (sx, sy) in ((-1, -1), (1, -1), (-1, 1), (1, 1)):
        box(bm, (sx * (w / 2 - 0.02), sy * (d / 2 - 0.02), eave * 0.5),
            (0.15, 0.15, eave), BEAM)
    chimney(bm, -1.9, 0.5, eave - 0.4, eave + 1.55, 0.46)


def poke_lab(bm, rng):
    """The civic building.  It has to read as the most interesting thing in
    town, so: a wide stone base, a two-storey glazed drum, a shallow domed
    metal roof, a projecting entrance canopy on columns, rooftop instruments."""
    R = 3.3
    sides = 16
    # stepped stone podium
    for (r, z0, h, m) in ((R + 1.05, 0.0, 0.20, PAVING),
                          (R + 0.72, 0.20, 0.20, PAVING),
                          (R + 0.42, 0.40, 0.30, STONE_WALL)):
        poly = [(math.cos(2 * math.pi * i / sides) * r,
                 math.sin(2 * math.pi * i / sides) * r) for i in range(sides)]
        prism_wall(bm, poly, h, m, z0=z0)

    base_z = 0.70
    # ground storey: solid plaster with deep window bays
    poly = [(math.cos(2 * math.pi * i / sides) * R,
             math.sin(2 * math.pi * i / sides) * R) for i in range(sides)]
    prism_wall(bm, poly, 2.65, PLASTER_C, z0=base_z)
    # glazed upper drum, mullions every other facet
    poly2 = [(math.cos(2 * math.pi * i / sides) * (R - 0.22),
              math.sin(2 * math.pi * i / sides) * (R - 0.22))
             for i in range(sides)]
    prism_wall(bm, poly2, 2.05, GLASS, z0=base_z + 2.65)
    for i in range(sides):
        a = 2 * math.pi * (i + 0.5) / sides
        box(bm, (math.cos(a) * (R - 0.18), math.sin(a) * (R - 0.18),
                 base_z + 2.65 + 1.02), (0.13, 0.13, 2.05), TRIM, rot_z=a)
    # floor band between storeys and a cornice
    for (z, r, h, m) in ((base_z + 2.65, R + 0.13, 0.24, TRIM),
                         (base_z + 4.70, R + 0.20, 0.26, TRIM)):
        p = [(math.cos(2 * math.pi * i / sides) * r,
              math.sin(2 * math.pi * i / sides) * r) for i in range(sides)]
        prism_wall(bm, p, h, m, z0=z - h * 0.5)

    # shallow dome, lofted in rings so it is a real surface
    dome_z = base_z + 4.96
    rings = []
    steps = 5
    for k in range(steps + 1):
        t = k / float(steps)
        r = (R + 0.20) * math.cos(t * math.pi * 0.42)
        z = dome_z + math.sin(t * math.pi * 0.42) * 1.30
        rings.append([bm.verts.new((math.cos(2 * math.pi * i / sides) * r,
                                    math.sin(2 * math.pi * i / sides) * r, z))
                      for i in range(sides)])
    for k in range(steps):
        for i in range(sides):
            j = (i + 1) % sides
            f = bm.faces.new((rings[k][i], rings[k][j],
                              rings[k + 1][j], rings[k + 1][i]))
            f.material_index = METAL_ROOF
            f.smooth = False
    f = bm.faces.new(rings[-1])
    f.material_index = METAL_ROOF
    f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=[f])
    # lantern and aerial on top
    box(bm, (0, 0, dome_z + 1.44), (0.72, 0.72, 0.52), TRIM)
    box(bm, (0, 0, dome_z + 1.76), (0.92, 0.92, 0.12), METAL_ROOF)
    E.bm_polytube(bm, [Vector((0, 0, dome_z + 1.80)),
                       Vector((0, 0, dome_z + 3.10))],
                  [0.055, 0.022], 5, LAMP, cap_start=True, cap_end=True,
                  smooth=False)
    for k in range(3):
        z = dome_z + 2.25 + k * 0.28
        box(bm, (0, 0, z), (0.46 - k * 0.11, 0.05, 0.035), LAMP)

    # entrance canopy on the -Y face, projecting forward
    cy = -R - 0.95
    for sx in (-1, 1):
        E.bm_polytube(bm, [Vector((sx * 1.30, cy, 0.20)),
                           Vector((sx * 1.30, cy, 2.95))],
                      [0.16, 0.13], 8, TRIM, cap_start=True, cap_end=True,
                      smooth=False)
        box(bm, (sx * 1.30, cy, 0.32), (0.44, 0.44, 0.24), STONE_WALL)
        box(bm, (sx * 1.30, cy, 2.88), (0.38, 0.38, 0.16), TRIM)
    box(bm, (0, cy + 0.35, 3.06), (3.30, 1.90, 0.20), METAL_ROOF)
    box(bm, (0, cy - 0.55, 3.02), (3.34, 0.14, 0.30), TRIM)
    # sign board over the entrance
    box(bm, (0, cy - 0.62, 2.55), (2.10, 0.10, 0.52), AWNING)
    box(bm, (0, cy - 0.68, 2.55), (1.86, 0.05, 0.34), TRIM)
    # steps up to the doors
    for k in range(3):
        box(bm, (0, cy + 0.55 + k * 0.30, 0.055 + k * 0.11),
            (3.0 - k * 0.22, 0.34, 0.11 + k * 0.0), PAVING)
    door(bm, (0, -R + 0.02, 1.82), (1.9, 2.25), (0, -1, 0), step=False)
    # big ground-floor bays, zoned: three on the front, plain around the back
    for a_deg in (-52, 52, -104, 104, 180):
        a = math.radians(a_deg - 90)
        n = Vector((math.cos(a), math.sin(a), 0))
        window(bm, None, tuple(n * (R - 0.02) + Vector((0, 0, 1.95))),
               (1.10, 1.45), tuple(n))


# --------------------------------------------------------------------------
# street kit
# --------------------------------------------------------------------------

def fence_section(bm, rng, length=2.0, height=1.05, pickets=7):
    for sx in (-1, 1):
        x = sx * length * 0.5
        E.bm_polytube(bm, [Vector((x, 0, 0)), Vector((x, 0, height))],
                      [0.075, 0.062], 5, FENCE, cap_start=True, cap_end=True,
                      smooth=False)
        box(bm, (x, 0, height + 0.045), (0.19, 0.19, 0.09), FENCE)
    for z in (height * 0.30, height * 0.72):
        box(bm, (0, 0, z), (length - 0.10, 0.055, 0.13), FENCE)
    for i in range(pickets):
        t = (i + 0.5) / pickets
        x = (t - 0.5) * (length - 0.30)
        h = height * rng.uniform(0.86, 0.96)
        box(bm, (x, 0, h * 0.5), (0.10, 0.035, h), FENCE)
        # pointed picket top
        vs = [bm.verts.new((x - 0.05, -0.018, h)),
              bm.verts.new((x + 0.05, -0.018, h)),
              bm.verts.new((x + 0.05, 0.018, h)),
              bm.verts.new((x - 0.05, 0.018, h)),
              bm.verts.new((x, 0, h + 0.085))]
        for k in range(4):
            f = bm.faces.new((vs[k], vs[(k + 1) % 4], vs[4]))
            f.material_index = FENCE
            f.smooth = False


def lamp_post(bm, rng, height=3.1):
    base = [Vector((0, 0, 0)), Vector((0, 0, 0.14)), Vector((0, 0, 0.26))]
    E.bm_polytube(bm, base, [0.20, 0.185, 0.115], 8, LAMP, cap_start=True,
                  cap_end=False, smooth=False)
    E.bm_polytube(bm, [Vector((0, 0, 0.26)), Vector((0, 0, height - 0.55))],
                  [0.075, 0.052], 8, LAMP, cap_start=False, cap_end=False,
                  smooth=False)
    # collar rings
    for z in (0.85, 1.9):
        box(bm, (0, 0, z), (0.135, 0.135, 0.055), LAMP)
    # scrolled arm
    arm = [Vector((0, 0, height - 0.55)), Vector((0.16, 0, height - 0.22)),
           Vector((0.42, 0, height - 0.12)), Vector((0.60, 0, height - 0.24))]
    E.bm_polytube(bm, arm, [0.05, 0.042, 0.036, 0.030], 6, LAMP,
                  cap_start=True, cap_end=True, smooth=False)
    # lantern: tapered glass box in a metal cage with a finial
    lc = Vector((0.60, 0, height - 0.60))
    for k in range(4):
        a = math.pi * 0.5 * k + math.pi * 0.25
        box(bm, (lc.x + math.cos(a) * 0.135, math.sin(a) * 0.135, lc.z),
            (0.035, 0.035, 0.46), LAMP, rot_z=a)
    box(bm, (lc.x, 0, lc.z), (0.24, 0.24, 0.40), GLASS)
    box(bm, (lc.x, 0, lc.z + 0.26), (0.34, 0.34, 0.10), LAMP)
    vs = [bm.verts.new((lc.x - 0.15, -0.15, lc.z + 0.31)),
          bm.verts.new((lc.x + 0.15, -0.15, lc.z + 0.31)),
          bm.verts.new((lc.x + 0.15, 0.15, lc.z + 0.31)),
          bm.verts.new((lc.x - 0.15, 0.15, lc.z + 0.31)),
          bm.verts.new((lc.x, 0, lc.z + 0.50))]
    for k in range(4):
        f = bm.faces.new((vs[k], vs[(k + 1) % 4], vs[4]))
        f.material_index = LAMP
        f.smooth = False
    box(bm, (lc.x, 0, lc.z + 0.56), (0.06, 0.06, 0.12), LAMP)


def signpost(bm, rng):
    E.bm_polytube(bm, [Vector((0, 0, 0)), Vector((0, 0, 1.95))],
                  [0.075, 0.062], 6, FENCE, cap_start=True, cap_end=True,
                  smooth=False)
    box(bm, (0, 0, 0.10), (0.28, 0.28, 0.20), STONE_WALL)
    for (z, sx, w) in ((1.70, 1, 0.95), (1.36, -1, 0.80)):
        box(bm, (sx * (w * 0.5 + 0.06), 0, z), (w, 0.06, 0.30), FENCE)
        # pointed end
        x0 = sx * (w + 0.10)
        vs = [bm.verts.new((x0, -0.03, z - 0.15)),
              bm.verts.new((x0, 0.03, z - 0.15)),
              bm.verts.new((x0, 0.03, z + 0.15)),
              bm.verts.new((x0, -0.03, z + 0.15)),
              bm.verts.new((x0 + sx * 0.16, 0, z))]
        for k in range(4):
            f = bm.faces.new((vs[k], vs[(k + 1) % 4], vs[4]))
            f.material_index = FENCE
            f.smooth = False
        box(bm, (sx * (w * 0.5 + 0.06), -0.035, z), (w * 0.82, 0.02, 0.14),
            TRIM)
    box(bm, (0, 0, 1.99), (0.13, 0.13, 0.09), FENCE)


def bench(bm, rng):
    for sx in (-1, 1):
        x = sx * 0.62
        box(bm, (x, -0.16, 0.21), (0.09, 0.09, 0.42), FENCE)
        box(bm, (x, 0.16, 0.21), (0.09, 0.09, 0.42), FENCE)
        box(bm, (x, 0.19, 0.62), (0.08, 0.08, 0.84), FENCE)
        box(bm, (x, 0.0, 0.44), (0.08, 0.46, 0.07), FENCE)
        box(bm, (x, 0.10, 0.12), (0.07, 0.46, 0.06), FENCE)
    for i, y in enumerate((-0.19, -0.02, 0.15)):
        box(bm, (0, y, 0.47), (1.45, 0.145, 0.05), FENCE)
    for i, z in enumerate((0.66, 0.83, 1.00)):
        box(bm, (0, 0.235, z), (1.42, 0.045, 0.135), FENCE)
    for sx in (-1, 1):
        box(bm, (sx * 0.73, 0.02, 0.48), (0.07, 0.50, 0.09), FENCE)


def crate(bm, rng, s=0.62):
    box(bm, (0, 0, s * 0.5), (s, s, s), FENCE)
    # corner battens and face rails -- the difference between a crate and a cube
    for (sx, sy) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        box(bm, (sx * s * 0.5, sy * s * 0.5, s * 0.5),
            (0.075, 0.075, s + 0.012), BEAM)
    for sx in (-1, 1):
        box(bm, (sx * (s * 0.5 + 0.006), 0, s * 0.5), (0.02, s, 0.075), BEAM)
        box(bm, (0, sx * (s * 0.5 + 0.006), s * 0.5), (s, 0.02, 0.075), BEAM)
        box(bm, (sx * (s * 0.5 + 0.006), 0, s * 0.86), (0.02, s, 0.065), BEAM)
    box(bm, (0, 0, s + 0.014), (s + 0.03, s + 0.03, 0.03), BEAM)


def barrel(bm, rng, h=0.86, r=0.30):
    n = 9
    pts = []
    radii = []
    for k in range(n):
        t = k / float(n - 1)
        pts.append(Vector((0, 0, t * h)))
        radii.append(r * (0.78 + 0.22 * math.sin(math.pi * t)))
    E.bm_polytube(bm, pts, radii, 12, FENCE, cap_start=True, cap_end=True,
                  smooth=False)
    for t in (0.10, 0.34, 0.66, 0.90):
        rr = r * (0.78 + 0.22 * math.sin(math.pi * t)) + 0.018
        ring = [Vector((0, 0, t * h - 0.032)), Vector((0, 0, t * h + 0.032))]
        E.bm_polytube(bm, ring, [rr, rr], 12, LAMP, cap_start=False,
                      cap_end=False, smooth=False)
    box(bm, (0, 0, h + 0.012), (r * 1.10, r * 1.10, 0.024), BEAM)


def market_stall(bm, rng):
    w, d, h = 2.4, 1.5, 2.15
    for (sx, sy) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        E.bm_polytube(bm, [Vector((sx * w * .5, sy * d * .5, 0)),
                           Vector((sx * w * .5, sy * d * .5, h))],
                      [0.065, 0.055], 6, BEAM, cap_start=True, cap_end=True,
                      smooth=False)
    box(bm, (0, 0, 0.90), (w - 0.05, d - 0.05, 0.07), BEAM)
    box(bm, (0, -d * .5 + 0.03, 0.55), (w - 0.05, 0.05, 0.62), BEAM)
    # striped canopy: two pitched panels with a scalloped valance
    rz = h + 0.55
    for sy in (-1, 1):
        vs = [bm.verts.new((-w * .5 - 0.22, sy * (d * .5 + 0.28), h)),
              bm.verts.new((w * .5 + 0.22, sy * (d * .5 + 0.28), h)),
              bm.verts.new((w * .5 + 0.22, 0, rz)),
              bm.verts.new((-w * .5 - 0.22, 0, rz))]
        vt = [bm.verts.new(v.co + Vector((0, 0, 0.045))) for v in vs]
        fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
        for i in range(4):
            j = (i + 1) % 4
            fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
        for f in fs:
            f.material_index = AWNING
            f.smooth = False
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        for k in range(7):
            x = -w * .5 - 0.10 + k * (w + 0.20) / 6.0
            box(bm, (x, sy * (d * .5 + 0.27), h - 0.10), (0.22, 0.05, 0.20),
                AWNING)
    # goods on the counter
    for k in range(5):
        crate_r = 0.09 + rng.random() * 0.05
        E.bm_puff(bm, (rng.uniform(-0.9, 0.9), rng.uniform(-0.35, 0.35),
                       0.96 + crate_r * 0.6), crate_r, rng, AWNING, sides=6,
                  squash=(1.0, 1.0, 0.8), lumpy=0.12, rings_n=3)


def well(bm, rng):
    sides = 14
    for (r, z0, h, m) in ((0.86, 0.0, 0.16, PAVING), (0.72, 0.16, 0.78, STONE_WALL)):
        poly = [(math.cos(2 * math.pi * i / sides) * r,
                 math.sin(2 * math.pi * i / sides) * r) for i in range(sides)]
        prism_wall(bm, poly, h, m, z0=z0)
    # coping ring
    poly = [(math.cos(2 * math.pi * i / sides) * 0.80,
             math.sin(2 * math.pi * i / sides) * 0.80) for i in range(sides)]
    prism_wall(bm, poly, 0.11, TRIM, z0=0.94)
    for sx in (-1, 1):
        E.bm_polytube(bm, [Vector((sx * 0.62, 0, 0.94)),
                           Vector((sx * 0.55, 0, 2.02))],
                      [0.075, 0.062], 5, BEAM, cap_start=True, cap_end=True,
                      smooth=False)
    # little pitched roof
    for sy in (-1, 1):
        vs = [bm.verts.new((-0.95, sy * 0.80, 1.90)),
              bm.verts.new((0.95, sy * 0.80, 1.90)),
              bm.verts.new((0.95, 0, 2.28)),
              bm.verts.new((-0.95, 0, 2.28))]
        vt = [bm.verts.new(v.co + Vector((0, 0, 0.07))) for v in vs]
        fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
        for i in range(4):
            j = (i + 1) % 4
            fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
        for f in fs:
            f.material_index = ROOF_GREEN
            f.smooth = False
        bmesh.ops.recalc_face_normals(bm, faces=fs)
    # windlass, crank and bucket
    E.bm_polytube(bm, [Vector((-0.58, 0, 1.62)), Vector((0.58, 0, 1.62))],
                  [0.075, 0.075], 8, BEAM, cap_start=True, cap_end=True,
                  smooth=False)
    E.bm_polytube(bm, [Vector((0.58, 0, 1.62)), Vector((0.74, 0, 1.62)),
                       Vector((0.74, 0.22, 1.62)), Vector((0.74, 0.22, 1.48))],
                  [0.030] * 4, 5, LAMP, cap_start=True, cap_end=True,
                  smooth=False)
    E.bm_polytube(bm, [Vector((0.05, 0, 1.60)), Vector((0.05, 0, 1.18))],
                  [0.014, 0.014], 4, LAMP, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [Vector((0.05, 0, 1.18)), Vector((0.05, 0, 0.92))],
                  [0.16, 0.19], 9, FENCE, cap_start=True, cap_end=True,
                  smooth=False)


def planter(bm, rng):
    sides = 8
    for (r, z0, h, m) in ((0.44, 0.0, 0.08, TRIM), (0.40, 0.08, 0.46, STONE_WALL),
                          (0.46, 0.54, 0.09, TRIM)):
        poly = [(math.cos(2 * math.pi * i / sides + 0.4) * r,
                 math.sin(2 * math.pi * i / sides + 0.4) * r)
                for i in range(sides)]
        prism_wall(bm, poly, h, m, z0=z0)
    for k in range(7):
        a = rng.uniform(0, 6.28)
        d = 0.26 * math.sqrt(rng.random())
        E.bm_puff(bm, (math.cos(a) * d, math.sin(a) * d,
                       0.60 + rng.uniform(0, 0.10)),
                  rng.uniform(0.13, 0.20), rng, AWNING, sides=6,
                  squash=(1.2, 1.2, 0.75), lumpy=0.35, rings_n=3)


def path_tile(bm, rng, w=2.0, d=2.0, worn=True):
    """Paved path module with worn, irregular edges so a run of them does not
    read as a checkerboard.  Tiles on the 0.5 m grid."""
    nx, ny = int(w / 0.25), int(d / 0.25)
    cols = []
    for i in range(nx + 1):
        col = []
        for j in range(ny + 1):
            x = -w * .5 + i * 0.25
            y = -d * .5 + j * 0.25
            ex = min(i, nx - i) / float(nx * 0.5)
            ey = min(j, ny - j) / float(ny * 0.5)
            edge = min(ex, ey)
            z = 0.05
            if worn:
                # edges dip and ravel; interior gets a shallow worn dish
                wob = 0.030 * math.sin(x * 5.1 + y * 3.7) + \
                      0.022 * math.sin(x * 9.3 - y * 7.1)
                z -= (1.0 - min(1.0, edge * 2.2)) * (0.028 + abs(wob))
                z -= 0.012 * math.exp(-((x / (w * 0.35)) ** 2))
                z += wob * 0.35
            col.append(bm.verts.new((x, y, z)))
        cols.append(col)
    for i in range(nx):
        for j in range(ny):
            f = bm.faces.new((cols[i][j], cols[i + 1][j],
                              cols[i + 1][j + 1], cols[i][j + 1]))
            f.material_index = PAVING
            f.smooth = False
    # skirt down to ground so the module has thickness
    ring = ([cols[i][0] for i in range(nx + 1)] +
            [cols[nx][j] for j in range(1, ny + 1)] +
            [cols[i][ny] for i in range(nx - 1, -1, -1)] +
            [cols[0][j] for j in range(ny - 1, 0, -1)])
    low = [bm.verts.new((v.co.x, v.co.y, -0.06)) for v in ring]
    for k in range(len(ring)):
        m = (k + 1) % len(ring)
        f = bm.faces.new((ring[k], low[k], low[m], ring[m]))
        f.material_index = STONE_WALL
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))


ASSETS = [
    ("Env_House_Cottage_A", 4101, (1500, 6000), house_a),
    ("Env_House_Townhouse_B", 4102, (1500, 6000), house_b),
    ("Env_House_Farmhouse_C", 4103, (1500, 6000), house_c),
    ("Env_Building_PokeLab", 4104, (1500, 6000), poke_lab),
    ("Env_Fence_Picket_2m", 4201, (200, 2000),
     lambda bm, rng: fence_section(bm, rng, 2.0, 1.05, 7)),
    ("Env_Fence_Picket_1m", 4202, (150, 2000),
     lambda bm, rng: fence_section(bm, rng, 1.0, 1.05, 3)),
    ("Env_Lamp_Post", 4211, (300, 2000), lamp_post),
    ("Env_Signpost", 4212, (200, 2000), signpost),
    ("Env_Bench", 4221, (300, 2000), bench),
    ("Env_Crate", 4231, (200, 2000), crate),
    ("Env_Barrel", 4232, (200, 2000), barrel),
    ("Env_Market_Stall", 4241, (400, 3000), market_stall),
    ("Env_Well", 4251, (400, 3000), well),
    ("Env_Planter", 4261, (200, 2000), planter),
    ("Env_Path_Paved_2m", 4271, (200, 2000),
     lambda bm, rng: path_tile(bm, rng, 2.0, 2.0)),
    ("Env_Path_Paved_1m", 4272, (100, 2000),
     lambda bm, rng: path_tile(bm, rng, 1.0, 1.0)),
    ("Env_Path_Paved_Corner", 4273, (200, 2000),
     lambda bm, rng: path_tile(bm, rng, 2.0, 1.0)),
]

BUILDINGS = {"Env_House_Cottage_A", "Env_House_Townhouse_B",
             "Env_House_Farmhouse_C", "Env_Building_PokeLab"}


def main():
    E.ensure_dirs()
    T.ensure_atlas(FAM)
    E.reset_scene()
    ms = T.full_matset(FAM)
    ap = T.atlas_paths(FAM)
    part = []
    problems = []

    for (name, seed, budget, fn) in ASSETS:
        rng = random.Random(seed)
        bm = E.bm_new()
        fn(bm, rng)
        # bevel every hard edge -- but only after the mesh is complete, and
        # small enough that a 6 cm trim board does not swallow itself
        E.bevel_sharp(bm, width=0.012, segments=1, angle_deg=38.0,
                      mat_break=False)
        obj = E.bm_to_obj(bm, name, ms.materials())
        E.finalize(obj, smooth_angle=22.0)
        E.pivot_to_base(obj, xy='center' if name not in
                        ("Env_Path_Paved_2m", "Env_Path_Paved_1m",
                         "Env_Path_Paved_Corner") else 'center')
        E.apply_transforms(obj)
        E.uv_all(obj, ms, angle=58.0, margin=0.010)
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
        E.log("%-26s %5d tris  %s" % (name, tris, probs or "ok"))
        part.append({
            "name": name, "family": FAM, "subfamily": name.split("_")[1],
            "path": os.path.relpath(path, E.REPO).replace("\\", "/"),
            "triangles": tris,
            "lods": [{"level": i + 1,
                      "path": os.path.relpath(p, E.REPO).replace("\\", "/"),
                      "triangles": t} for i, (p, t) in enumerate(lods)],
            "pivot": ("ground level, footprint centred, snaps on 0.5 m grid"
                      if name.startswith("Env_Path") else
                      "ground level, footprint centred"),
            "textures": [os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
                         os.path.relpath(ap["normal"], E.REPO).replace("\\", "/")],
            "windVertexColors": False,
            "notes": "",
        })
        E.delete_obj(obj)

    E.write_part(FAM, part)
    E.log("---- %d town assets, %d with problems" % (len(part), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))


if __name__ == "__main__":
    main()
