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
import townlib as TL

FAM = "Town"
OUT = E.FAMILY_DIR[FAM]

PLASTER_C, PLASTER_B, PLASTER_R, BEAM = 0, 1, 2, 3
ROOF_RED, ROOF_BLUE, ROOF_GREEN, METAL_ROOF = 4, 5, 6, 7
GLASS, DOOR, PAVING, STONE_WALL = 8, 9, 10, 11
FENCE, LAMP, AWNING, TRIM = 12, 13, 14, 15

GRID = 0.5

# Filled by every building as it is built: the result of firing a ray through
# each authored opening on the bare shell. A non-empty list means a window or
# door frame is sitting on unbroken wall, which is the defect this rebuild
# exists to remove, so main() treats it as a hard failure.
HOLE_CHECKS = []


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
    """Cottage: rectangular plan, steep gable, half-timbered street gable.

    Rebuilt on townlib.Shell. The previous L-shaped plan bought a silhouette at
    the cost of a wall the extrusion never closed; a clean rectangle with three
    real openings reads better and is watertight. The plan is deliberately
    simple because the interest is meant to come from the openings and the
    roof, not from more slabs.
    """
    w, d, eave, th = 4.5, 4.0, 2.55, 0.26
    poly = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]

    sh = TL.Shell(bm, poly, 0.0, eave, th, PLASTER_C, PLASTER_C)
    # segment 0 is the -Y street face for a CCW rectangle starting at (-w/2,-d/2)
    front, right, back, left = 0, 1, 2, 3
    o_door = sh.add_opening(front, 1.00, 2.05, 0.0, 0.34, "door")
    o_w1 = sh.add_opening(front, 0.85, 0.95, 1.10, 0.68, "window")
    o_w2 = sh.add_opening(left, 0.80, 0.90, 1.15, 0.50, "window")
    o_w3 = sh.add_opening(back, 0.85, 0.90, 1.15, 0.55, "window")
    sh.build()
    HOLE_CHECKS.extend(sh.assert_openings())

    # stone plinth: a closed prism that swallows the wall foot rather than
    # sitting flush against it
    TL.solid_prism(bm, [(x * 1.03, y * 1.035) for (x, y) in poly],
                   -0.02, 0.44, STONE_WALL)

    TL.gable_roof(bm, poly, eave, 1.70, 0.38, 0.16, ROOF_RED, TRIM,
                  ridge_along_x=True, gable_mat=PLASTER_C)

    TL.corner_posts(bm, poly, 0.0, eave, 0.15, BEAM)

    # half timbering on the street face only, and it stops at the openings
    for z in (0.62, 1.86, 2.34):
        TL.beams_around(bm, sh, front, z, 0.13, 0.07, BEAM)
    TL.beams_around(bm, sh, front, eave - 0.09, 0.16, 0.09, BEAM)

    TL.window_furniture(bm, o_w1, TRIM, GLASS, th)
    TL.window_furniture(bm, o_w2, TRIM, GLASS, th)
    TL.window_furniture(bm, o_w3, TRIM, GLASS, th, mullion=False)
    TL.door_furniture(bm, o_door, DOOR, TRIM, PAVING, th)

    # dormer on the street pitch: a small shell of its own, with a real
    # window cut through it, so it reads as a room rather than a bump
    dw, dd = 1.30, 1.10
    dpoly = [(-1.15 - dw / 2, -d / 2 - 0.05), (-1.15 + dw / 2, -d / 2 - 0.05),
             (-1.15 + dw / 2, -d / 2 + dd), (-1.15 - dw / 2, -d / 2 + dd)]
    dsh = TL.Shell(bm, dpoly, eave + 0.10, 0.95, 0.16, PLASTER_C, PLASTER_C)
    o_dorm = dsh.add_opening(0, 0.62, 0.66, eave + 0.28, 0.5, "window")
    dsh.build()
    HOLE_CHECKS.extend(dsh.assert_openings())
    TL.gable_roof(bm, dpoly, eave + 1.05, 0.46, 0.16, 0.10, ROOF_RED, TRIM,
                  ridge_along_x=False, gable_mat=PLASTER_C)
    TL.window_furniture(bm, o_dorm, TRIM, GLASS, 0.16, mullion=False,
                        sill=False)

    # window box under the street window -- cheap, and it says "lived in"
    TL.solid_box(bm, (o_w1.centre.x, -d / 2 - 0.16,
                      o_w1.centre.z - o_w1.height * 0.5 - 0.18),
                 (o_w1.width + 0.24, 0.26, 0.20), BEAM)

    chimney(bm, w / 2 - 0.95, 0.9, 0.6, eave + 2.30)


def house_b(bm, rng):
    """Two-storey townhouse: jettied upper floor, shopfront, hipped-look roof.

    Two stacked shells, the upper one oversailing on the street side. Both are
    closed volumes, so the jetty has a real soffit and the building has a back.
    """
    w, d, e1, e2, th = 4.0, 4.4, 2.55, 4.90, 0.24
    j = 0.24
    lower = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]
    upper = [(-w / 2 - j, -d / 2 - j), (w / 2 + j, -d / 2 - j),
             (w / 2 + j, d / 2), (-w / 2 - j, d / 2)]

    s1 = TL.Shell(bm, lower, 0.0, e1, th, PLASTER_B, PLASTER_B)
    o_shop = s1.add_opening(0, 1.30, 1.35, 0.90, 0.30, "window")
    o_door = s1.add_opening(0, 1.05, 2.10, 0.0, 0.70, "door")
    o_side = s1.add_opening(1, 0.75, 0.95, 1.10, 0.55, "window")
    s1.build()
    HOLE_CHECKS.extend(s1.assert_openings())

    s2 = TL.Shell(bm, upper, e1, e2 - e1, th, PLASTER_C, PLASTER_C)
    # spaced so the shutters do not run into each other: 0.70 window plus two
    # 0.26 shutters is 1.30 m of frontage, and the wall is 4.48 m
    ups = [s2.add_opening(0, 0.70, 1.00, e1 + 0.65, cs, "window")
           for cs in (0.175, 0.5, 0.825)]
    o_up_side = s2.add_opening(1, 0.70, 1.00, e1 + 0.65, 0.55, "window")
    s2.build()
    HOLE_CHECKS.extend(s2.assert_openings())

    # Jetty brackets, each biting into both shells.  Sat at e1 - 0.02 in the
    # first pass, which left their soffit at z 2.430 -- only 92 mm above the
    # top of the shop window's lintel course at z 2.338.  A shopfront awning
    # cannot be built with a real slab thickness and real clearance top and
    # bottom in 92 mm, so the brackets are lifted 50 mm.  At e1 + 0.03 they
    # still bite 70 mm into the lower shell and 130 mm into the upper, which
    # is the whole reason they are here.
    for x in (-1.35, 0.0, 1.35):
        TL.solid_box(bm, (x, -d / 2 - j * 0.45, e1 + 0.03),
                     (0.15, j + 0.20, 0.20), BEAM)
    TL.solid_box(bm, (0, -d / 2 - j + 0.03, e1 + 0.13),
                 (w + 2 * j + 0.04, 0.14, 0.18), BEAM)

    TL.gable_roof(bm, upper, e2, 1.25, 0.40, 0.16, ROOF_BLUE, TRIM,
                  ridge_along_x=True, gable_mat=PLASTER_C)
    TL.corner_posts(bm, lower, 0.0, e1, 0.15, BEAM)

    # ----------------------------------------------------------------------
    # Shopfront awning.
    #
    # The first pass authored this as a slab floating 33-60 mm off the wall,
    # a valance 50 mm wider than the slab it hung on, and two horizontal
    # "struts" that crossed out through the canopy and then lay 158 mm above
    # its top surface in open air -- which is precisely what the game's
    # three-quarter overhead camera looks straight down at.  Rebuilt as a
    # piece of construction:
    #
    #   * the canopy's back edge is buried 10-28 mm INSIDE the wall face, so
    #     there is no gap to close anywhere along the head;
    #   * it is carried on two diagonal knee brackets running from a cleat on
    #     the wall out and up into the soffit -- a load path a real awning
    #     could use -- and every part of them stops 15 mm inside the wall
    #     face, 225 mm short of the inner skin, so nothing shows in the room;
    #   * measured clearances: 17 mm under the jetty brackets above, 20 mm
    #     over the shop window's lintel course below, and the knee brackets
    #     stop 40 mm short of punching through the canopy's TOP surface;
    #   * the valance, its piping and the scalloped hem are all inset inside
    #     the canopy's own x range, so no trim overhangs what it is nailed to.
    #
    # It is centred on the shop window at x = -0.80 rather than on the facade.
    # That is deliberate, not a slip: the window is the shop and the door at
    # x = +0.80 has to stay walkable, so an awning that framed the whole
    # frontage would read as a porch.  The old span (-1.95..0.55) was centred
    # on neither -- it stabbed 47 mm into the front corner post at one end and
    # half-covered the door at the other, which is what made it read as a
    # mistake rather than as a shopfront.
    aw_x0, aw_x1 = -1.75, 0.15                 # 1.90 m, centred on the window
    aw_y0, aw_y1 = -d / 2 + 0.01, -d / 2 - 0.91    # -2.19 into the wall, -3.11
    aw_z0, aw_z1 = 2.463, 2.150                # head and eave of the top face
    aw_cx = (aw_x0 + aw_x1) * 0.5
    TL.solid_from_quad(bm, [
        (aw_x0, aw_y0, aw_z0), (aw_x1, aw_y0, aw_z0),
        (aw_x1, aw_y1, aw_z1), (aw_x0, aw_y1, aw_z1)],
        0.057, AWNING, up=(0, 0, 1))
    # valance on the eave, inset 20 mm inside the canopy at both ends
    val_w = (aw_x1 - aw_x0) - 0.04
    TL.solid_box(bm, (aw_cx, aw_y1 + 0.027, 1.995), (val_w, 0.075, 0.24),
                 AWNING)
    TL.solid_box(bm, (aw_cx, aw_y1 + 0.012, 2.105), (val_w + 0.02, 0.055, 0.05),
                 TRIM)
    # scalloped hem: what makes it read as a shop awning at ten metres
    for k in range(7):
        sx = aw_x0 + 0.13 + k * ((aw_x1 - aw_x0) - 0.26) / 6.0
        TL.solid_box(bm, (sx, aw_y1 + 0.027, 1.845), (0.215, 0.065, 0.12),
                     AWNING)
    # knee brackets: a cleat bedded in the wall and a diagonal into the soffit
    for sx in (-1.66, 0.06):
        TL.solid_box(bm, (sx, -d / 2 - 0.035, 1.965), (0.14, 0.11, 0.19), BEAM)
        TL.solid_from_quad(bm, [
            (sx - 0.045, -d / 2 + 0.015, 1.990),
            (sx + 0.045, -d / 2 + 0.015, 1.990),
            (sx + 0.045, -2.860, 2.195),
            (sx - 0.045, -2.860, 2.195)], 0.07, BEAM, up=(0, 0, 1))

    TL.window_furniture(bm, o_shop, TRIM, GLASS, th, sill=True)
    TL.window_furniture(bm, o_side, TRIM, GLASS, th)
    TL.door_furniture(bm, o_door, DOOR, TRIM, PAVING, th)
    for o in ups:
        TL.window_furniture(bm, o, TRIM, GLASS, th, shutters=True,
                            mat_shutter=BEAM)
    TL.window_furniture(bm, o_up_side, TRIM, GLASS, th)

    chimney(bm, -w / 2 + 0.60, 1.2, e1, e2 + 1.60, 0.40)


def house_c(bm, rng):
    """Long low farmhouse: shallow pitch, porch on real posts, shutters."""
    w, d, eave, th = 6.0, 3.6, 2.45, 0.26
    poly = [(-w / 2, -d / 2), (w / 2, -d / 2), (w / 2, d / 2), (-w / 2, d / 2)]

    sh = TL.Shell(bm, poly, 0.0, eave, th, PLASTER_R, PLASTER_R)
    o_door = sh.add_opening(0, 1.05, 2.10, 0.0, 0.52, "door")
    front_w = [sh.add_opening(0, 0.75, 0.95, 1.15, cs, "window")
               for cs in (0.14, 0.30, 0.74, 0.90)]
    o_end = sh.add_opening(1, 0.70, 0.90, 1.20, 0.5, "window")
    o_back = sh.add_opening(2, 0.80, 0.90, 1.20, 0.35, "window")
    sh.build()
    HOLE_CHECKS.extend(sh.assert_openings())

    TL.solid_prism(bm, [(x * 1.02, y * 1.035) for (x, y) in poly],
                   -0.02, 0.36, STONE_WALL)
    TL.gable_roof(bm, poly, eave, 1.15, 0.50, 0.16, ROOF_GREEN, TRIM,
                  ridge_along_x=True, gable_mat=PLASTER_R)
    TL.corner_posts(bm, poly, 0.0, eave, 0.15, BEAM)

    # porch: posts, a head beam and a lean-to roof with real thickness
    py = -d / 2 - 1.20
    for x in (-1.75, 0.0, 1.75):
        TL.solid_box(bm, (x, py, 1.15), (0.15, 0.15, 2.30), BEAM)
        TL.solid_box(bm, (x, py, 2.24), (0.32, 0.32, 0.14), BEAM)
    TL.solid_box(bm, (0, py, 2.38), (3.85, 0.16, 0.18), BEAM)
    TL.solid_from_quad(bm, [
        (-2.25, py - 0.32, 2.44), (2.25, py - 0.32, 2.44),
        (2.25, -d / 2 + 0.06, 2.98), (-2.25, -d / 2 + 0.06, 2.98)],
        0.11, ROOF_GREEN, up=(0, 0, 1))

    for o in front_w:
        TL.window_furniture(bm, o, TRIM, GLASS, th, shutters=True,
                            mat_shutter=BEAM)
    TL.window_furniture(bm, o_end, TRIM, GLASS, th)
    TL.window_furniture(bm, o_back, TRIM, GLASS, th, mullion=False)
    TL.door_furniture(bm, o_door, DOOR, TRIM, PAVING, th)

    chimney(bm, -1.9, 0.5, eave - 0.5, eave + 1.65, 0.46)


def poke_lab(bm, rng):
    """The civic building: stone podium, a faceted drum with real glazed bays,
    a glazed clerestory, a shallow dome and a canopied entrance.

    The drum is a Shell like every other wall here, so its bays are cut through
    the stone rather than painted on it, and the podium and cornices are closed
    prisms rather than open extrusions.
    """
    # 12 facets, not 16: at R = 3.6 that gives a 1.86 m facet, wide enough to
    # take a 1.55 m civic entrance as a real opening in one wall. A 16-gon's
    # 1.33 m facet cannot, and forcing it was what deleted the wall.
    R = 3.6
    sides = 12
    th = 0.30

    def ring(r, phase=0.0):
        return [(math.cos(2 * math.pi * (i + phase) / sides) * r,
                 math.sin(2 * math.pi * (i + phase) / sides) * r)
                for i in range(sides)]

    for (r, z0, z1, m) in ((R + 1.10, -0.02, 0.20, PAVING),
                           (R + 0.76, 0.18, 0.42, PAVING),
                           (R + 0.44, 0.40, 0.72, STONE_WALL)):
        TL.solid_prism(bm, ring(r), z0, z1, m)

    base_z = 0.70
    drum = ring(R)
    sh = TL.Shell(bm, drum, base_z, 2.85, th, PLASTER_C, PLASTER_C)
    # facet 9 of a 12-gon starting at angle 0 faces -Y, which is the street
    door_seg = 9
    o_door = sh.add_opening(door_seg, 1.55, 2.35, base_z, 0.5, "door")
    bays = [sh.add_opening(seg, 1.15, 1.45, base_z + 0.85, 0.5, "window")
            for seg in (7, 8, 10, 11, 1, 4)]
    sh.build()
    HOLE_CHECKS.extend(sh.assert_openings())

    # floor band
    TL.solid_prism(bm, ring(R + 0.14), base_z + 2.72, base_z + 2.98, TRIM)

    # glazed clerestory drum: a closed prism of glass with mullions biting in
    cz0 = base_z + 2.94
    cz1 = cz0 + 2.05
    TL.solid_prism(bm, ring(R - 0.20), cz0, cz1, GLASS)
    for i in range(sides):
        a = 2 * math.pi * (i + 0.5) / sides
        TL.solid_box(bm, (math.cos(a) * (R - 0.17), math.sin(a) * (R - 0.17),
                          (cz0 + cz1) * 0.5), (0.14, 0.14, cz1 - cz0), TRIM,
                     rot_z=a)
    TL.solid_prism(bm, ring(R + 0.22), cz1 - 0.06, cz1 + 0.24, TRIM)

    # dome: closed, with a floor ring so it is a solid and not a lid
    dome_z = cz1 + 0.22
    steps = 5
    rings = []
    for k in range(steps + 1):
        t = k / float(steps)
        r = (R + 0.22) * math.cos(t * math.pi * 0.44)
        z = dome_z + math.sin(t * math.pi * 0.44) * 1.30
        rings.append([bm.verts.new((math.cos(2 * math.pi * i / sides) * r,
                                    math.sin(2 * math.pi * i / sides) * r, z))
                      for i in range(sides)])
    dome_faces = []
    for k in range(steps):
        for i in range(sides):
            j = (i + 1) % sides
            dome_faces.append(bm.faces.new((rings[k][i], rings[k][j],
                                            rings[k + 1][j], rings[k + 1][i])))
    dome_faces.append(bm.faces.new(rings[-1]))
    dome_faces.append(bm.faces.new(list(reversed(rings[0]))))
    for f in dome_faces:
        f.material_index = METAL_ROOF
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=dome_faces)

    top = dome_z + 1.30
    TL.solid_box(bm, (0, 0, top + 0.24), (0.74, 0.74, 0.56), TRIM)
    TL.solid_box(bm, (0, 0, top + 0.58), (0.94, 0.94, 0.13), METAL_ROOF)
    TL.solid_box(bm, (0, 0, top + 1.35), (0.09, 0.09, 1.50), LAMP)
    for k in range(3):
        TL.solid_box(bm, (0, 0, top + 1.05 + k * 0.30),
                     (0.48 - k * 0.12, 0.06, 0.04), LAMP)

    # Entrance canopy: posts, a thick slab, a sign, and steps that are solids.
    #
    # Three measured defects fixed here.  (1) The slab used to stop at
    # y = -3.175, and the drum's wall at the slab's own ends (x = +-1.70) is
    # at y = -3.144 -- so the outer 114 mm of each end hung free with up to
    # 30 mm of daylight behind it while the middle buried itself 425 mm in.
    # The back edge now runs to y = -3.10, which is inside the wall by 45 mm
    # at the ends and 500 mm at the centre: contact along the whole head.
    # (2) The fascia was 3.44 m against a 3.40 m slab, 20 mm proud at each
    # end; it is now inset 20 mm instead.  (3) The sign board sat at
    # z 2.310..2.850 with the fascia soffit at 2.930 -- an 80 mm gap to the
    # only thing near it, i.e. a board hanging on nothing.  It is lifted to
    # butt 60 mm up into the fascia.
    cy = -R - 1.00
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * 1.35, cy, 1.55), (0.26, 0.26, 3.10), TRIM)
        TL.solid_box(bm, (sx * 1.35, cy, 0.26), (0.48, 0.48, 0.52), STONE_WALL)
    TL.solid_box(bm, (0, cy + 0.4375, 3.16), (3.40, 2.125, 0.22), METAL_ROOF)
    TL.solid_box(bm, (0, cy - 0.58, 3.10), (3.36, 0.16, 0.34), TRIM)
    TL.solid_box(bm, (0, cy - 0.64, 2.72), (2.15, 0.12, 0.54), AWNING)
    TL.solid_box(bm, (0, cy - 0.71, 2.72), (1.90, 0.06, 0.36), TRIM)
    for k in range(3):
        TL.solid_box(bm, (0, cy + 0.62 + k * 0.32, 0.06 + k * 0.13),
                     (3.1 - k * 0.24, 0.40, 0.14 + k * 0.10), PAVING)

    for o in bays:
        TL.window_furniture(bm, o, TRIM, GLASS, th, mullion=True)
    TL.door_furniture(bm, o_door, DOOR, TRIM, PAVING, th)



# --------------------------------------------------------------------------
# More street kit.  Cheapest density per triangle a town has: a market square
# with one stall reads as a set dressing, with two it reads as a market, and a
# fence run that ends in mid air reads as neither.
#
# Everything here is built from closed solids and overlaps whatever it sits on,
# for the same reasons the buildings are -- see townlib's header.
# --------------------------------------------------------------------------

def market_stall_b(bm, rng):
    """A second stall: a lean-to against a wall rather than a free-standing
    canopy, so two of them in a square do not read as a repeat."""
    w, d, h = 2.0, 1.15, 2.15
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * (w * 0.5 - 0.06), -d * 0.5 + 0.06, h * 0.5),
                     (0.10, 0.10, h), BEAM)
        # The back posts were 2.60 tall against a canopy whose TOP surface is
        # at 2.543..2.574 over their footprint, so 26-57 mm of post stood up
        # through the awning -- visible from the overhead camera as two stubs
        # in the middle of the cloth.  At 2.52 the post is bedded 12-43 mm
        # into the canopy and still 23-54 mm below its top face.
        TL.solid_box(bm, (sx * (w * 0.5 - 0.06), d * 0.5 - 0.06, 1.26),
                     (0.09, 0.09, 2.52), BEAM)
    # sloped canopy with real thickness
    TL.solid_from_quad(bm, [
        (-w * 0.5 - 0.18, -d * 0.5 - 0.10, h),
        (w * 0.5 + 0.18, -d * 0.5 - 0.10, h),
        (w * 0.5 + 0.18, d * 0.5 + 0.12, 2.62),
        (-w * 0.5 - 0.18, d * 0.5 + 0.12, 2.62)], 0.07, AWNING, up=(0, 0, 1))
    # counter: a solid, with a lip, at a believable 0.95 m
    TL.solid_box(bm, (0, -0.06, 0.86), (w - 0.10, d - 0.22, 0.10), BEAM)
    TL.solid_box(bm, (0, -d * 0.5 + 0.10, 0.44), (w - 0.30, 0.07, 0.86), BEAM)
    TL.solid_box(bm, (0, -0.06, 0.94), (w + 0.06, d - 0.10, 0.07), TRIM)
    # crates of produce, so the counter is not bare
    for i in range(3):
        x = -0.62 + i * 0.62
        TL.solid_box(bm, (x, 0.02, 1.07), (0.42, 0.36, 0.20), BEAM)
        TL.solid_box(bm, (x, 0.02, 1.20), (0.34, 0.28, 0.10), AWNING)
    # A hanging sign -- hanging on something.  It used to float 54 mm below
    # the canopy's front edge with no solid within 150 mm of it in any
    # direction; two straps now run from the sign up into the canopy's front
    # edge, biting 36 mm into the soffit and stopping 30 mm short of its top
    # face so nothing pokes through the cloth.
    TL.solid_box(bm, (0, -d * 0.5 - 0.12, 1.86), (1.10, 0.05, 0.34), AWNING)
    TL.solid_box(bm, (0, -d * 0.5 - 0.16, 1.86), (0.94, 0.03, 0.22), TRIM)
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * 0.42, -d * 0.5 - 0.095, 2.04),
                     (0.05, 0.05, 0.16), BEAM)


def wall_lamp(bm, rng):
    """A lamp that mounts on a wall instead of standing in the street.

    Pivot at the wall face, arm along -Y, so it hangs off a facade with no
    ground footprint -- which is the point: a town square with six lamp posts
    in it looks like a car park.
    """
    TL.solid_box(bm, (0, 0.05, 0.0), (0.22, 0.10, 0.42), LAMP)
    TL.solid_box(bm, (0, -0.20, 0.10), (0.07, 0.52, 0.07), LAMP)
    # diagonal brace, so the arm is carried rather than cantilevered by magic
    TL.solid_from_quad(bm, [
        (-0.025, 0.02, -0.16), (0.025, 0.02, -0.16),
        (0.025, -0.34, 0.07), (-0.025, -0.34, 0.07)], 0.04, LAMP, up=(1, 0, 0))
    TL.solid_box(bm, (0, -0.42, 0.02), (0.11, 0.11, 0.10), LAMP)
    # lantern: glazed box with a cap and a finial
    TL.solid_box(bm, (0, -0.42, -0.20), (0.24, 0.24, 0.34), GLASS)
    for sx in (-1, 1):
        for sy in (-1, 1):
            TL.solid_box(bm, (sx * 0.115, -0.42 + sy * 0.115, -0.20),
                         (0.035, 0.035, 0.36), LAMP)
    TL.solid_box(bm, (0, -0.42, -0.01), (0.32, 0.32, 0.06), LAMP)
    TL.solid_box(bm, (0, -0.42, -0.40), (0.20, 0.20, 0.06), LAMP)
    TL.solid_box(bm, (0, -0.42, -0.47), (0.07, 0.07, 0.09), LAMP)


def notice_board(bm, rng):
    """Town notice board: two posts, a glazed case with a pitched cap."""
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * 0.62, 0.0, 0.72), (0.11, 0.11, 1.44), BEAM)
        TL.solid_box(bm, (sx * 0.62, 0.0, 0.06), (0.20, 0.20, 0.12), STONE_WALL)
    TL.solid_box(bm, (0, 0.0, 1.26), (1.46, 0.13, 0.86), BEAM)
    TL.solid_box(bm, (0, -0.075, 1.26), (1.26, 0.03, 0.68), GLASS)
    TL.solid_box(bm, (0, -0.10, 1.26), (1.18, 0.02, 0.60), TRIM)
    # pitched cap with thickness, so the top is not a card
    for sy in (-1, 1):
        TL.solid_from_quad(bm, [
            (-0.84, 0.0, 1.86), (0.84, 0.0, 1.86),
            (0.84, sy * 0.30, 1.74), (-0.84, sy * 0.30, 1.74)],
            0.05, ROOF_RED, up=(0, 0, 1))
    TL.solid_box(bm, (0, 0.0, 1.90), (1.74, 0.10, 0.07), TRIM)
    # a few pinned notices, at angles, because a tidy board looks unused
    for (dx, dz, a) in ((-0.34, 0.10, 0.05), (0.10, -0.04, -0.07),
                        (0.42, 0.14, 0.03)):
        TL.solid_box(bm, (dx, -0.115, 1.26 + dz), (0.26, 0.01, 0.32), TRIM,
                     rot_z=a)


def cart(bm, rng):
    """A two-wheeled handcart, parked. Bed, sides, shafts, spoked wheels."""
    bed_z = 0.62
    TL.solid_box(bm, (0, 0, bed_z), (1.70, 0.96, 0.10), BEAM)
    for sy in (-1, 1):
        TL.solid_box(bm, (0, sy * 0.47, bed_z + 0.22), (1.70, 0.07, 0.36), BEAM)
    TL.solid_box(bm, (-0.86, 0, bed_z + 0.22), (0.07, 0.96, 0.36), BEAM)
    # shafts, angled down to the ground so the cart is parked, not floating
    for sy in (-1, 1):
        TL.solid_from_quad(bm, [
            (0.82, sy * 0.34 - 0.035, bed_z + 0.02),
            (0.82, sy * 0.34 + 0.035, bed_z + 0.02),
            (1.86, sy * 0.34 + 0.035, 0.20),
            (1.86, sy * 0.34 - 0.035, 0.20)], 0.06, BEAM, up=(0, 0, 1))
    TL.solid_box(bm, (1.86, 0, 0.19), (0.10, 0.76, 0.08), BEAM)
    # wheels in their own plane -- see townlib.cart_wheel for why this is not
    # a ring of solid_box calls
    for sy in (-1, 1):
        TL.cart_wheel(bm, (0.0, sy * 0.55, 0.42), 0.42, 0.33, 0.10,
                      BEAM, LAMP, seg=12, spokes=5)
    # cargo
    TL.solid_box(bm, (-0.30, 0, bed_z + 0.28), (0.52, 0.52, 0.46), AWNING)
    TL.solid_box(bm, (0.36, 0, bed_z + 0.20), (0.46, 0.62, 0.30), BEAM)


def trough(bm, rng):
    """A stone water trough: a hollow, not a box with a blue lid.

    Built as four closed walls and a floor with the water surface set down
    inside them, so the water reads as being IN something.
    """
    w, d, h, t = 1.60, 0.72, 0.56, 0.11
    TL.solid_box(bm, (0, 0, t * 0.5), (w, d, t), STONE_WALL)
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * (w * 0.5 - t * 0.5), 0, h * 0.5),
                     (t, d, h), STONE_WALL)
    for sy in (-1, 1):
        TL.solid_box(bm, (0, sy * (d * 0.5 - t * 0.5), h * 0.5),
                     (w - t * 2, t, h), STONE_WALL)
    # water, 6 cm below the rim, inset so it never touches the stone faces
    TL.solid_box(bm, (0, 0, (h - 0.06) * 0.5 + t * 0.4),
                 (w - t * 2 - 0.01, d - t * 2 - 0.01, h - 0.06 - t * 0.4),
                 GLASS)
    # a coping course and a mossy foot, so it is not a plain rectangle
    TL.solid_box(bm, (0, 0, h - 0.02), (w + 0.09, d + 0.09, 0.07), STONE_WALL)
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * (w * 0.5 - 0.18), 0, 0.05),
                     (0.30, d + 0.10, 0.10), STONE_WALL)


def gate(bm, rng):
    """A gate for the fence run. Two posts, a hung leaf, a latch.

    The layout runs 29 fence sections and has nowhere to walk through them.
    """
    ph, pw = 1.42, 0.15
    for sx in (-1, 1):
        TL.solid_box(bm, (sx * 0.95, 0, ph * 0.5), (pw, pw, ph), FENCE)
        TL.solid_box(bm, (sx * 0.95, 0, ph + 0.05), (pw + 0.09, pw + 0.09, 0.10),
                     FENCE)
        TL.solid_box(bm, (sx * 0.95, 0, ph + 0.14), (0.09, 0.09, 0.10), FENCE)
    # leaf, swung a few degrees open so it reads as a gate and not a panel
    ang = math.radians(9.0)
    ca, sa = math.cos(ang), math.sin(ang)

    def leaf_box(lx, lz, sx_, sz_, mat):
        x = -0.86 + lx * ca
        y = lx * sa
        TL.solid_box(bm, (x, y, lz), (sx_, 0.055, sz_), mat, rot_z=ang)

    for i in range(6):
        leaf_box(0.13 + i * 0.29, 0.62, 0.075, 1.10, FENCE)
    for z in (0.22, 0.98):
        leaf_box(0.86, z, 1.72, 0.09, FENCE)
    # diagonal brace: a gate without one sags, and everyone knows it
    TL.solid_from_quad(bm, [
        (-0.82, 0.0, 0.16), (-0.74, 0.02, 0.16),
        (0.78, 0.26, 1.04), (0.70, 0.24, 1.04)], 0.05, FENCE, up=(0, 1, 0))
    TL.solid_box(bm, (0.86, 0.14, 1.02), (0.20, 0.05, 0.05), LAMP, rot_z=ang)


def stone_wall_module(bm, rng, length=2.0):
    """A 2 m dry-stone wall module with a coping course.

    Built as a solid with the courses stepped in and out rather than a slab
    with a texture: at a 6-12 m camera the read comes from the shadow line
    between courses.
    """
    h, base_t, top_t = 0.92, 0.44, 0.30
    courses = 4
    rng2 = random.Random(11)
    for c in range(courses):
        z0 = c * (h / courses)
        z1 = z0 + (h / courses)
        t = base_t + (top_t - base_t) * (c / float(courses - 1))
        # break each course into stones so the wall is not four extrusions
        stones = max(2, int(round(length / (0.52 + rng2.random() * 0.22))))
        x = -length * 0.5
        for i in range(stones):
            sw = length / stones * rng2.uniform(0.86, 1.14)
            sw = min(sw, length * 0.5 + length * 0.5 - x)
            if sw < 0.08:
                break
            jitter = rng2.uniform(-0.018, 0.018)
            TL.solid_box(bm, (x + sw * 0.5, jitter, (z0 + z1) * 0.5),
                         (sw * 0.99, t + rng2.uniform(-0.03, 0.03),
                          (z1 - z0) * 1.02), STONE_WALL)
            x += sw
    # coping: stones on edge along the top, the signature of a dry-stone wall
    n = max(3, int(round(length / 0.28)))
    for i in range(n):
        x = -length * 0.5 + (i + 0.5) * (length / n)
        TL.solid_box(bm, (x, rng2.uniform(-0.02, 0.02), h + 0.09),
                     (length / n * 0.92, top_t + 0.06, 0.20), STONE_WALL,
                     rot_z=rng2.uniform(-0.06, 0.06))


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
    # The canopy's eave is at z = h but the eave line is at |y| = 1.03, while
    # the posts stand at |y| = 0.75 -- so the pitched cloth is already 150 mm
    # above z = h where the posts actually are.  Posts stopped at h in the
    # first pass and the whole canopy floated 150 mm clear of all four of
    # them.  post_top puts them into the cloth: 30 mm of bite, and still
    # 15 mm short of pushing through the top surface at 2.3445.
    post_top = h + 0.18
    for (sx, sy) in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        E.bm_polytube(bm, [Vector((sx * w * .5, sy * d * .5, 0)),
                           Vector((sx * w * .5, sy * d * .5, post_top))],
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
        # Scalloped valance.  It used to hang at |y| = 1.02 with its top at
        # z = h = 2.15 while the cloth soffit over it runs 2.155..2.169 --
        # 5 to 19 mm of daylight between the hem and the awning it hangs
        # from.  Pulled 20 mm inboard and lifted 40 mm, each tab now bites
        # 10-37 mm into the soffit and stays 8-34 mm below the top face.
        for k in range(7):
            x = -w * .5 - 0.10 + k * (w + 0.20) / 6.0
            box(bm, (x, sy * (d * .5 + 0.25), h - 0.06), (0.22, 0.05, 0.20),
                AWNING)
    # goods on the counter
    for k in range(5):
        crate_r = 0.09 + rng.random() * 0.05
        E.bm_puff(bm, (rng.uniform(-0.9, 0.9), rng.uniform(-0.35, 0.35),
                       0.96 + crate_r * 0.6), crate_r, rng, AWNING, sides=6,
                  squash=(1.0, 1.0, 0.8), lumpy=0.12, rings_n=3)


def revolve(bm, profile, sides):
    """Lathe an open profile of (r, z, mat) about +Z.

    Both ends of the profile must lie on the axis (r == 0); those rings
    collapse to a pole and the band becomes a triangle fan.  The result is
    therefore a single CLOSED solid, which is the whole point: on a closed
    solid `recalc_face_normals` points every face away from the material, so
    the wall of a blind hole ends up facing INTO the hole -- toward a player
    looking down it -- with no special casing.  Building the same shape as a
    stack of open cylinders, which is what this replaced, gives the opposite
    result and the hole reads as back-face culled.

    profile[k][2] is the material of the band from point k to point k+1.
    """
    rings = []
    for (r, z, _m) in profile:
        if r <= 1e-6:
            rings.append([bm.verts.new((0.0, 0.0, z))])
        else:
            rings.append([bm.verts.new((math.cos(2 * math.pi * i / sides) * r,
                                        math.sin(2 * math.pi * i / sides) * r,
                                        z)) for i in range(sides)])
    faces = []
    for k in range(len(profile) - 1):
        a, b = rings[k], rings[k + 1]
        mat = profile[k][2]
        for i in range(sides):
            j = (i + 1) % sides
            if len(a) == 1:
                vs = (a[0], b[j], b[i])
            elif len(b) == 1:
                vs = (a[i], a[j], b[0])
            else:
                vs = (a[i], a[j], b[j], b[i])
            f = bm.faces.new(vs)
            f.material_index = mat
            f.smooth = False
            faces.append(f)
    return faces


def sweep_x(bm, section, x0, x1, mat, cap=True):
    """Sweep a closed (y,z) cross section along X.  Returns the faces."""
    n = len(section)
    a = [bm.verts.new((x0, y, z)) for (y, z) in section]
    b = [bm.verts.new((x1, y, z)) for (y, z) in section]
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces.append(bm.faces.new((a[i], a[j], b[j], b[i])))
    if cap:
        faces.append(bm.faces.new(list(reversed(a))))
        faces.append(bm.faces.new(b))
    for f in faces:
        f.material_index = mat
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def taper_box(bm, x, y, z0, z1, s0, s1, mat):
    """Square post, s0 across at z0 tapering to s1 at z1.  Returns the half
    width at any height so the callers can butt things onto its faces."""
    prof = ((-1, -1), (1, -1), (1, 1), (-1, 1))
    bot = [bm.verts.new((x + a * s0 * .5, y + b * s0 * .5, z0))
           for (a, b) in prof]
    top = [bm.verts.new((x + a * s1 * .5, y + b * s1 * .5, z1))
           for (a, b) in prof]
    faces = []
    for i in range(4):
        j = (i + 1) % 4
        faces.append(bm.faces.new((bot[i], bot[j], top[j], top[i])))
    faces.append(bm.faces.new(list(reversed(bot))))
    faces.append(bm.faces.new(top))
    for f in faces:
        f.material_index = mat
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return lambda zz: 0.5 * (s0 + (s1 - s0) *
                             max(0.0, min(1.0, (zz - z0) / (z1 - z0))))


# ---- the well ------------------------------------------------------------
# Every dimension that decides the asset's bounding box is named here, because
# the level's placement pass is keyed to it: the plinth's 14-gon sets X to
# +-0.860 and Y to +-0.8384, and the roof ridge sets the 2.3449 m height.
WELL_SIDES = 14
WELL_RIM_Z = 1.090          # top of the coping -- the surface you look over
WELL_WATER_Z = 0.200        # dark water, 0.890 m below the rim
WELL_SHAFT_R = 0.588        # inner face of the shaft
WELL_POST_X = 0.700         # posts stand on the coping annulus (0.610..0.792)
WELL_SOFFIT_Z = 2.208       # flat underside of the roof's ridge cap
WELL_RIDGE_Z = 2.3449       # top of the ridge == the shipped asset's height


def well(bm, rng):
    # --- drum, coping and shaft: ONE closed solid of revolution -----------
    # Read the profile as the section of the masonry: up the outside, across
    # the coping's top annulus, down the shaft wall, and back to the axis
    # across the water.  Because it closes, the shaft wall is a real inward-
    # facing surface rather than the inside of an outward-facing cylinder.
    profile = [
        (0.000, 0.000, PAVING),      # underside disc
        (0.860, 0.000, PAVING),      # plinth outer face
        (0.860, 0.150, PAVING),      # plinth weathering
        (0.755, 0.190, STONE_WALL),  # drum, battered
        (0.740, 0.880, STONE_WALL),  # corbel out to the coping
        (0.825, 0.918, TRIM),        # coping outer face
        (0.825, 1.048, TRIM),        # coping top chamfer
        (0.792, WELL_RIM_Z, TRIM),   # >>> the coping's TOP ANNULUS <<<
        (0.610, WELL_RIM_Z, STONE_WALL),   # inner chamfer into the shaft
        (WELL_SHAFT_R, 1.032, STONE_WALL),  # >>> the SHAFT WALL <<<
        (WELL_SHAFT_R, WELL_WATER_Z, GLASS),  # water disc
        (0.000, WELL_WATER_Z, GLASS),
    ]
    revolve(bm, profile, WELL_SIDES)

    # --- roof: one closed prism swept along X ----------------------------
    # The old roof was two flat cards with no gable end and no underside, and
    # it floated 260 mm clear of the posts.  This is a solid with a real
    # thickness, closed gable ends, and a flat ridge soffit at WELL_SOFFIT_Z
    # for the posts to butt into.
    section = [
        (-0.800, 1.958), (-0.110, 2.293), (-0.110, WELL_RIDGE_Z),
        (0.110, WELL_RIDGE_Z), (0.110, 2.293), (0.800, 1.958),
        (0.800, 1.873), (0.110, WELL_SOFFIT_Z), (-0.110, WELL_SOFFIT_Z),
        (-0.800, 1.873),
    ]
    sweep_x(bm, section, -0.95, 0.95, ROOF_GREEN)

    # --- posts: base ON the coping annulus, head ON the roof soffit ------
    half_at = {}
    for sx in (-1, 1):
        # 0.140 across is the widest square that still lands wholly inside the
        # coping's 14-gon annulus (inner 0.610, outer edge 0.7789 at the post's
        # bearing) -- a post whose corner overhangs the rim is the floating
        # look this rebuild exists to remove.
        half_at[sx] = taper_box(bm, sx * WELL_POST_X, 0.0,
                                WELL_RIM_Z, WELL_SOFFIT_Z, 0.140, 0.110, BEAM)

    # --- windlass: axle butting the posts' inner faces, crank outside ----
    # The axle's iron collars are steps in the axle's own radius profile, not
    # separate rings dropped over it: a ring around a shaft is a lap joint, and
    # this asset is meant to contain none.
    axle_z = 1.780
    inner = WELL_POST_X - half_at[1](axle_z)
    axle_r = []
    axle_p = []
    for (t, r) in ((-inner, 0.072), (-0.352, 0.072), (-0.328, 0.090),
                   (-0.316, 0.090), (-0.292, 0.072), (0.292, 0.072),
                   (0.316, 0.090), (0.328, 0.090), (0.352, 0.072),
                   (inner, 0.072)):
        axle_p.append(Vector((t, 0, axle_z)))
        axle_r.append(r)
    afaces, _ = E.bm_polytube(bm, axle_p, axle_r, 8, BEAM, cap_start=True,
                              cap_end=True, smooth=False)
    for k in (2, 6):                       # the two collar bands go to iron
        for f in afaces[k * 8:(k + 1) * 8]:
            f.material_index = LAMP
    outer = WELL_POST_X + half_at[1](axle_z)
    E.bm_polytube(bm, [Vector((outer, 0, axle_z)),
                       Vector((outer + 0.09, 0, axle_z)),
                       Vector((outer + 0.09, 0.24, axle_z)),
                       Vector((outer + 0.09, 0.24, axle_z - 0.17))],
                  [0.032] * 4, 6, LAMP, cap_start=True, cap_end=True,
                  smooth=False)

    # --- bucket on a rope, hanging inside the mouth ----------------------
    bx = 0.09
    E.bm_polytube(bm, [Vector((bx, 0, axle_z - 0.07)), Vector((bx, 0, 1.335))],
                  [0.013, 0.013], 4, LAMP, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [Vector((bx, 0, 1.335)), Vector((bx, 0.115, 1.290)),
                       Vector((bx, 0, 1.250))],
                  [0.011] * 3, 4, LAMP, cap_start=False, cap_end=False,
                  smooth=True)
    # Same treatment for the pail: the two iron hoops are a bulge in its own
    # radius profile, so they sit ON the staves instead of floating a
    # centimetre off them with a see-through slot at each rim.
    pail_z = (1.250, 1.234, 1.198, 1.182, 1.038, 1.002, 0.985)
    pail_r = []
    for z in pail_z:
        t = (1.250 - z) / (1.250 - 0.985)
        pail_r.append(0.165 + 0.040 * t)
    for k in (1, 2, 4, 5):
        pail_r[k] += 0.013
    bfaces, _ = E.bm_polytube(bm, [Vector((bx, 0, z)) for z in pail_z],
                              pail_r, 10, BEAM, cap_start=True, cap_end=True,
                              smooth=False)
    for k in (1, 4):
        for f in bfaces[k * 10:(k + 1) * 10]:
            f.material_index = LAMP



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
    ("Env_House_Cottage_A", 4101, (900, 6000), house_a),
    ("Env_House_Townhouse_B", 4102, (900, 6000), house_b),
    ("Env_House_Farmhouse_C", 4103, (900, 6000), house_c),
    ("Env_Building_PokeLab", 4104, (900, 6000), poke_lab),
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
    ("Env_Path_Paved_Corner", 4273, (150, 2000),
     lambda bm, rng: path_tile(bm, rng, 2.0, 1.0)),
    # second pass: the pieces a town square actually needs
    ("Env_Market_Stall_B", 4242, (300, 3000), market_stall_b),
    ("Env_Lamp_Wall", 4213, (150, 2000), wall_lamp),
    ("Env_Notice_Board", 4214, (200, 2000), notice_board),
    ("Env_Cart", 4281, (300, 2500), cart),
    ("Env_Trough", 4282, (150, 2000), trough),
    ("Env_Fence_Gate", 4203, (200, 2000), gate),
    ("Env_Wall_Stone_2m", 4291, (300, 2500),
     lambda bm, rng: stone_wall_module(bm, rng, 2.0)),
    ("Env_Wall_Stone_1m", 4292, (150, 2000),
     lambda bm, rng: stone_wall_module(bm, rng, 1.0)),
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
        del HOLE_CHECKS[:]
        fn(bm, rng)
        if HOLE_CHECKS:
            problems.append((name, ["OPENING NOT PIERCED: " + m
                                    for m in HOLE_CHECKS]))
        # Bevel every hard edge on the street kit -- but not on the buildings.
        # A building is an assembly of interpenetrating closed solids, and
        # bevelling across those intersections collapses faces to zero area;
        # cleanup then deletes them and the hull is no longer closed. The
        # buildings get their edge definition from real wall thickness and real
        # reveals instead, which is a better source of it anyway.
        if name not in BUILDINGS:
            E.bevel_sharp(bm, width=0.012, segments=1, angle_deg=38.0,
                          mat_break=False)
        obj = E.bm_to_obj(bm, name, ms.materials())
        E.finalize(obj, smooth_angle=22.0,
                   merge=0.0 if name in BUILDINGS else 1e-5)
        E.pivot_to_base(obj, xy='center' if name not in
                        ("Env_Path_Paved_2m", "Env_Path_Paved_1m",
                         "Env_Path_Paved_Corner") else 'center')
        E.apply_transforms(obj)
        E.uv_all(obj, ms, angle=58.0, margin=0.010)
        # Buildings are held to a harder standard than the street kit: a
        # closed hull (the missing rear wall would have shown up here) and no
        # coplanar duplicate faces (the wall z-fighting would have).
        tris, probs = E.validate(obj, budget=budget, need_vcol=False,
                                 strict=False,
                                 closed=name in BUILDINGS,
                                 max_coplanar_dupes=0 if name in BUILDINGS
                                 else None)
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
