"""
Foliage family: 4 tree species x 3 sizes, grass clumps + single blade, bushes,
ferns, reeds, lily pads, flowers, cave moss and hanging vines.

Wind vertex colours (hard requirement, read by the VFX worker's shader):
    R = sway mask, 0 at the trunk base rising to 1 at the leaf tips
    G = phase offset, randomised per leaf cluster so clusters do not move in lockstep
    B = high-frequency flutter mask (leaves only)
    A = 1
"""

import sys
import os
import math
import random

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Matrix, Euler

import envlib as E
import textures as T

FAM = "Foliage"
OUT = E.FAMILY_DIR[FAM]

# atlas cell / material slot constants (slot index == atlas cell index)
BARK_BROAD, BARK_CONIF, BARK_BIRCH, BARK_WILLOW = 0, 1, 2, 3
LEAF_A, LEAF_B, LEAF_AUTUMN, NEEDLE = 4, 5, 6, 7
GRASS, BUSH, FERN, REED = 8, 9, 10, 11
FLOWER, LILYPAD, MOSS, VINE = 12, 13, 14, 15

FLOWER_COLOURS = [("Red", 0), ("Yellow", 1), ("Purple", 2), ("White", 3)]


def matset():
    extra = [("flower_%s" % n.lower(), FLOWER, (i, 4))
             for (n, i) in FLOWER_COLOURS]
    return T.full_matset(FAM, extra)


# --------------------------------------------------------------------------
# shared plant helpers
# --------------------------------------------------------------------------

def curve_points(start, direction, length, segments, rng, curl=0.35,
                 gravity=0.0, wobble=0.06):
    """Polyline that bends smoothly -- no straight sticks anywhere."""
    p = Vector(start)
    d = Vector(direction).normalized()
    pts = [p.copy()]
    step = length / segments
    # a consistent bend axis per branch keeps the curve graceful, not noisy
    axis = d.cross(Vector((rng.uniform(-1, 1), rng.uniform(-1, 1),
                           rng.uniform(-0.2, 0.2)))).normalized()
    for i in range(segments):
        t = (i + 1) / float(segments)
        d = (Matrix.Rotation(curl * step * 0.9, 3, axis) @ d)
        d.z -= gravity * step
        d += Vector((rng.uniform(-1, 1), rng.uniform(-1, 1),
                     rng.uniform(-0.4, 0.4))) * wobble * step
        d.normalize()
        p = p + d * step
        pts.append(p.copy())
    return pts


def taper(r0, r1, n, power=1.25):
    return [r0 + (r1 - r0) * ((i / float(n - 1)) ** power) for i in range(n)]


def leaf_cluster(bm, wp, centre, radius, rng, mat, count=3, sway=0.85,
                 phase=None, squash=(1.0, 1.0, 0.8), lumpy=0.3, rings_n=3,
                 sides=6):
    """A canopy mass made from a few overlapping puffs so the silhouette is
    lumpy rather than spherical."""
    ph = rng.random() if phase is None else phase
    made = []
    for i in range(count):
        off = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1),
                      rng.uniform(-0.7, 0.7))) * radius * 0.45
        r = radius * rng.uniform(0.62, 1.0)
        faces, verts = E.bm_puff(bm, Vector(centre) + off, r, rng, mat,
                                 sides=sides, squash=squash, lumpy=lumpy,
                                 rot=rng.uniform(0, 3.14), rings_n=rings_n)
        c = Vector(centre) + off
        for v in verts:
            d = (v.co - c).length / max(r, 1e-5)
            s = min(1.0, sway * (0.78 + 0.30 * d))
            wp.mark(v.co, s, ph, min(1.0, 0.45 + 0.55 * d))
        made.extend(faces)
    return made


def canopy_mass(bm, wp, rng, centre, rx, ry, rz, count, mats, rad_lo, rad_hi,
                anchors=None, sway_base=0.80, sides=6, rings_n=3,
                squash=(1.18, 1.18, 0.80), lumpy=0.34, droop=0.0):
    """Fill an ellipsoidal crown volume with overlapping puffs, biased toward
    supplied branch-tip anchors.  This is what turns a handful of balls on
    sticks into a single readable tree crown."""
    c = Vector(centre)
    made = []
    for i in range(count):
        if anchors and rng.random() < 0.62:
            a = anchors[rng.randrange(len(anchors))]
            p = Vector(a) + Vector((rng.uniform(-1, 1), rng.uniform(-1, 1),
                                    rng.uniform(-0.5, 1.0))) * rx * 0.22
        else:
            # rejection-free: sample the surface shell, pull inward a little
            u = rng.uniform(0, 6.2832)
            v = math.acos(rng.uniform(-1, 1))
            k = rng.uniform(0.45, 1.0) ** 0.5
            p = c + Vector((math.sin(v) * math.cos(u) * rx,
                            math.sin(v) * math.sin(u) * ry,
                            math.cos(v) * rz)) * k
        # crown reads better when it thins toward the top and sags at the edge
        dz = (p.z - c.z) / max(rz, 1e-5)
        dr = math.hypot((p.x - c.x) / max(rx, 1e-5), (p.y - c.y) / max(ry, 1e-5))
        p.z -= droop * dr * rz
        r = rng.uniform(rad_lo, rad_hi) * (1.0 - 0.22 * max(0.0, dz))
        mat = mats[rng.randrange(len(mats))]
        ph = rng.random()
        faces, verts = E.bm_puff(bm, p, r, rng, mat, sides=sides,
                                 squash=squash, lumpy=lumpy,
                                 rot=rng.uniform(0, 3.14), rings_n=rings_n)
        for vv in verts:
            d = (vv.co - p).length / max(r, 1e-5)
            s = min(1.0, sway_base * (0.80 + 0.28 * d) + 0.12 * dr)
            wp.mark(vv.co, s, ph, min(1.0, 0.45 + 0.55 * d))
        made.extend(faces)
    return made


def paint_tube(wp, rings, sway_from, sway_to, phase):
    n = len(rings)
    for i, ring in enumerate(rings):
        t = i / float(max(n - 1, 1))
        s = sway_from + (sway_to - sway_from) * (t ** 1.4)
        for v in ring:
            wp.mark(v.co, s, phase, 0.0)


# --------------------------------------------------------------------------
# trees
# --------------------------------------------------------------------------

def tree_broadleaf(bm, wp, rng, scale, sides=6, nprim=4, nsec=2, crown=18,
                   trunk_ratio=2.2, crown_wide=1.30, crown_flat=0.60,
                   puff=(0.34, 0.54)):
    """Spreading deciduous tree: flared trunk, 3-5 primaries, lumpy canopy."""
    H = trunk_ratio * scale
    r0 = 0.20 * scale
    trunk_pts = curve_points((0, 0, 0), (rng.uniform(-.06, .06),
                                         rng.uniform(-.06, .06), 1),
                             H, 5, rng, curl=rng.uniform(0.05, 0.22),
                             wobble=0.03)
    radii = taper(r0, r0 * 0.34, len(trunk_pts), 0.95)
    radii[0] *= 1.55          # root flare
    radii[1] *= 1.14
    _, rings = E.bm_polytube(bm, trunk_pts, radii, sides, BARK_BROAD,
                             cap_start=True, cap_end=False)
    paint_tube(wp, rings, 0.0, 0.30, 0.0)

    # root buttresses -- three small flared lobes so the base meets ground well
    for i in range(3):
        a = rng.uniform(0, 6.28) + i * 2.094
        d = Vector((math.cos(a), math.sin(a), 0.34))
        pts = curve_points((0, 0, 0.02 * scale), d, 0.30 * scale, 2, rng,
                           curl=-0.6, wobble=0.0)
        rr = taper(r0 * 0.52, r0 * 0.12, len(pts), 1.0)
        _, rr_rings = E.bm_polytube(bm, pts, rr, 5, BARK_BROAD,
                                    cap_start=False, cap_end=True)
        paint_tube(wp, rr_rings, 0.0, 0.05, 0.0)

    top = trunk_pts[-1]
    canopy_c = top + Vector((0, 0, 0.62 * scale))
    anchors = []
    a0 = rng.uniform(0, 6.28)
    for i in range(nprim):
        a = a0 + i * (6.2832 / nprim) + rng.uniform(-0.45, 0.45)
        tilt = rng.uniform(0.62, 1.05)
        base_t = rng.uniform(0.60, 0.95)
        bi = int(base_t * (len(trunk_pts) - 1))
        start = trunk_pts[bi]
        d = Vector((math.cos(a) * tilt, math.sin(a) * tilt, 1.0))
        ln = rng.uniform(0.85, 1.25) * scale
        pts = curve_points(start, d, ln, 4, rng, curl=rng.uniform(0.25, 0.6),
                           gravity=-0.06, wobble=0.05)
        br = radii[bi] * rng.uniform(0.50, 0.68)
        rr = taper(br, br * 0.28, len(pts), 1.1)
        _, prings = E.bm_polytube(bm, pts, rr, 5, BARK_BROAD,
                                  cap_start=False, cap_end=False)
        paint_tube(wp, prings, 0.28, 0.55, 0.0)

        # secondaries
        for k in range(nsec):
            st = rng.uniform(0.45, 0.9)
            si = max(1, int(st * (len(pts) - 1)))
            sd = (pts[si] - pts[si - 1]).normalized()
            sa = rng.uniform(0, 6.28)
            perp = sd.cross(Vector((math.cos(sa), math.sin(sa), 0.2))).normalized()
            nd = (sd + perp * rng.uniform(0.6, 1.15)).normalized()
            sl = ln * rng.uniform(0.42, 0.66)
            spts = curve_points(pts[si], nd, sl, 3, rng,
                                curl=rng.uniform(0.3, 0.7), wobble=0.05)
            sr = rr[si] * rng.uniform(0.5, 0.7)
            srr = taper(sr, sr * 0.2, len(spts), 1.0)
            _, srings = E.bm_polytube(bm, spts, srr, 4, BARK_BROAD,
                                      cap_start=False, cap_end=True)
            paint_tube(wp, srings, 0.5, 0.72, 0.0)
            anchors.append(spts[-1] + Vector((0, 0, 0.10 * scale)))
        anchors.append(pts[-1] + Vector((0, 0, 0.12 * scale)))

    # one coherent crown volume rather than balls stuck on stick ends
    rx = crown_wide * scale
    canopy_mass(bm, wp, rng, canopy_c, rx, rx * 0.94, rx * crown_flat,
                count=crown, mats=(LEAF_A, LEAF_A, LEAF_B),
                rad_lo=puff[0] * scale, rad_hi=puff[1] * scale, anchors=anchors,
                sway_base=0.88, squash=(1.22, 1.22, 0.78), lumpy=0.36,
                droop=0.16)
    return H * 1.9


def tree_conifer(bm, wp, rng, scale, sides=7, layers=15, wide=1.30,
                 droop_lo=0.26, trunk_ratio=4.0):
    """Layered spruce: strong conical silhouette from drooping needle fronds."""
    H = trunk_ratio * scale
    r0 = 0.16 * scale
    trunk_pts = curve_points((0, 0, 0), (0, 0, 1), H, 8, rng,
                             curl=rng.uniform(0.02, 0.10), wobble=0.015)
    radii = taper(r0, r0 * 0.10, len(trunk_pts), 0.85)
    radii[0] *= 1.5
    _, rings = E.bm_polytube(bm, trunk_pts, radii, sides, BARK_CONIF,
                             cap_start=True, cap_end=True)
    paint_tube(wp, rings, 0.0, 0.34, 0.0)

    a0 = rng.uniform(0, 6.28)
    for L in range(layers):
        t = 0.11 + 0.86 * (L / float(layers - 1))
        z = t * H
        ti = max(0, min(len(trunk_pts) - 1, int(t * (len(trunk_pts) - 1))))
        origin = Vector((trunk_pts[ti].x, trunk_pts[ti].y, z))
        # widest low down, tapering to the spire
        spread = (1.0 - t) ** 0.80
        rad = (0.22 + wide * spread) * scale * 0.80
        if t < 0.20:
            rad *= 0.66
        n = max(4, int(4 + 4 * spread))
        for i in range(n):
            a = a0 + L * 1.399 + i * (6.2832 / n) + rng.uniform(-0.24, 0.24)
            droop = -droop_lo - 0.34 * rng.random()
            d = Vector((math.cos(a), math.sin(a), droop))
            ln = rad * rng.uniform(0.82, 1.18)
            pts = curve_points(origin, d, ln, 3, rng, curl=0.30,
                               gravity=0.26, wobble=0.05)
            w0 = ln * rng.uniform(0.22, 0.32)
            widths = [w0 * 0.55, w0, w0 * 0.62, 0.0]
            ph = rng.random()
            faces, vs = E.bm_ribbon(bm, pts, widths, NEEDLE, double=True,
                                    up=(0, 0, 1), thickness=0.006)
            for v in vs:
                dt = (v.co - origin).length / max(ln, 1e-5)
                wp.mark(v.co, min(1.0, 0.40 + 0.62 * dt), ph,
                        min(1.0, 0.3 + 0.7 * dt))
    # spire tuft
    leaf_cluster(bm, wp, trunk_pts[-1] + Vector((0, 0, 0.10 * scale)),
                 0.17 * scale, rng, NEEDLE, count=2, sway=1.0,
                 squash=(0.75, 0.75, 1.7), lumpy=0.22, rings_n=3, sides=5)
    return H


def tree_birch(bm, wp, rng, scale, multi=False, sides=6, nbr=6,
               crown=24, crown_r=0.98, puff=(0.30, 0.46), trunk_ratio=3.0):
    """Slender pale-barked tree, airy high canopy; variant A is a multi-stem."""
    stems = 3 if multi else 1
    top_h = 0.0
    anchors = []
    crowns = []
    for s in range(stems):
        lean = Vector((rng.uniform(-0.16, 0.16), rng.uniform(-0.16, 0.16), 1))
        base = Vector((rng.uniform(-0.12, 0.12) * scale,
                       rng.uniform(-0.12, 0.12) * scale, 0)) if multi else Vector((0, 0, 0))
        H = (trunk_ratio if not multi else
             rng.uniform(0.72, 1.0) * trunk_ratio) * scale
        pts = curve_points(base, lean, H, 7, rng, curl=rng.uniform(0.06, 0.18),
                           wobble=0.02)
        r0 = (0.115 if not multi else 0.075) * scale
        radii = taper(r0, r0 * 0.28, len(pts), 0.9)
        radii[0] *= 1.35
        _, rings = E.bm_polytube(bm, pts, radii, sides, BARK_BIRCH,
                                 cap_start=True, cap_end=False)
        paint_tube(wp, rings, 0.0, 0.34, 0.0)
        top_h = max(top_h, pts[-1].z)

        a0 = rng.uniform(0, 6.28)
        for i in range(nbr):
            a = a0 + i * (6.2832 / nbr) + rng.uniform(-0.5, 0.5)
            bt = rng.uniform(0.48, 0.95)
            bi = int(bt * (len(pts) - 1))
            d = Vector((math.cos(a) * rng.uniform(0.5, 0.95),
                        math.sin(a) * rng.uniform(0.5, 0.95), 1.0))
            ln = rng.uniform(0.55, 0.95) * scale
            bp = curve_points(pts[bi], d, ln, 3, rng, curl=0.35,
                              gravity=0.10, wobble=0.05)
            br = radii[bi] * 0.55
            _, brings = E.bm_polytube(bm, bp, taper(br, br * 0.2, len(bp)), 4,
                                      BARK_BIRCH, cap_start=False, cap_end=True)
            paint_tube(wp, brings, 0.34, 0.62, 0.0)
            anchors.append(bp[-1] + Vector((0, 0, 0.10 * scale)))
        crowns.append((pts[-1] + Vector((0, 0, 0.26 * scale)),
                       crown_r * scale * (0.76 if multi else 1.0)))

    per = max(5, int(crown / stems))
    for (c, rx) in crowns:
        canopy_mass(bm, wp, rng, c, rx, rx * 0.92, rx * 0.72,
                    count=per, mats=(LEAF_B, LEAF_B, LEAF_A),
                    rad_lo=puff[0] * scale, rad_hi=puff[1] * scale,
                    anchors=[a for a in anchors if (a - c).length < rx * 2.2],
                    sway_base=0.92, squash=(1.16, 1.16, 0.86), lumpy=0.38,
                    droop=0.10)
    return top_h


def tree_willow(bm, wp, rng, scale, sides=8, nlimb=5, nstr=(9, 12),
                strand_w=(0.075, 0.125), crown=8, trunk_ratio=1.9):
    """Lakeside willow: leaning trunk, arching limbs, hanging leaf strands."""
    H = trunk_ratio * scale
    r0 = 0.24 * scale
    lean = Vector((rng.uniform(0.10, 0.24), rng.uniform(-0.18, 0.18), 1))
    trunk_pts = curve_points((0, 0, 0), lean, H, 5, rng, curl=0.28, wobble=0.04)
    radii = taper(r0, r0 * 0.44, len(trunk_pts), 0.9)
    radii[0] *= 1.6
    _, rings = E.bm_polytube(bm, trunk_pts, radii, sides, BARK_WILLOW,
                             cap_start=True, cap_end=False)
    paint_tube(wp, rings, 0.0, 0.24, 0.0)

    a0 = rng.uniform(0, 6.28)
    for i in range(nlimb):
        a = a0 + i * (6.2832 / nlimb) + rng.uniform(-0.35, 0.35)
        d = Vector((math.cos(a) * 0.85, math.sin(a) * 0.85, 1.0))
        ln = rng.uniform(1.0, 1.4) * scale
        pts = curve_points(trunk_pts[-1], d, ln, 4, rng, curl=0.30,
                           gravity=0.55, wobble=0.05)
        br = radii[-1] * rng.uniform(0.55, 0.72)
        _, lrings = E.bm_polytube(bm, pts, taper(br, br * 0.22, len(pts)), 5,
                                  BARK_WILLOW, cap_start=False, cap_end=True)
        paint_tube(wp, lrings, 0.24, 0.48, 0.0)

        # hanging strands from the outer half of each limb
        for k in range(rng.randint(nstr[0], nstr[1])):
            si = rng.randint(max(1, len(pts) - 3), len(pts) - 1)
            anchor = pts[si] + Vector((rng.uniform(-.20, .20),
                                       rng.uniform(-.20, .20), 0)) * scale
            drop = rng.uniform(0.95, 1.75) * scale
            spts = curve_points(anchor, (rng.uniform(-.10, .10),
                                         rng.uniform(-.10, .10), -1),
                                drop, 3, rng, curl=0.05, gravity=1.4,
                                wobble=0.02)
            w = rng.uniform(strand_w[0], strand_w[1]) * scale
            widths = [w * 0.55, w, w * 0.78, 0.0]
            ph = rng.random()
            faces, vs = E.bm_ribbon(bm, spts, widths, LEAF_B, double=True,
                                    up=(0, 0, 1), thickness=0.006)
            for v in vs:
                dt = (anchor.z - v.co.z) / max(drop, 1e-5)
                wp.mark(v.co, min(1.0, 0.52 + 0.5 * dt), ph,
                        min(1.0, 0.4 + 0.6 * dt))
        leaf_cluster(bm, wp, pts[-1] + Vector((0, 0, 0.06 * scale)),
                     rng.uniform(0.26, 0.38) * scale, rng, LEAF_A, count=1,
                     sway=0.8, squash=(1.3, 1.3, 0.62), rings_n=3)
    # a light crown over the arch so the top is not bald
    canopy_mass(bm, wp, rng, trunk_pts[-1] + Vector((0, 0, 0.62 * scale)),
                1.05 * scale, 1.0 * scale, 0.36 * scale, count=crown,
                mats=(LEAF_A, LEAF_B), rad_lo=0.26 * scale, rad_hi=0.40 * scale,
                sway_base=0.72, squash=(1.30, 1.30, 0.62), lumpy=0.36,
                droop=0.20)
    return H + 1.2 * scale


# Size variants differ in structure, not only in scale: branch counts, crown
# proportions, layer counts and droop all change, so an A next to a C never
# reads as the same asset scaled up.
TREE_SPECS = [
    ("Env_Tree_Broadleaf_A", "broadleaf", 1.00, 1101,
     dict(nprim=3, nsec=2, crown=13, trunk_ratio=1.75, crown_wide=1.16,
          crown_flat=0.66, puff=(0.36, 0.56))),
    ("Env_Tree_Broadleaf_B", "broadleaf", 1.40, 1102,
     dict(nprim=4, nsec=2, crown=18, trunk_ratio=2.20, crown_wide=1.30,
          crown_flat=0.58, puff=(0.34, 0.54))),
    ("Env_Tree_Broadleaf_C", "broadleaf", 1.85, 1103,
     dict(nprim=5, nsec=2, crown=21, trunk_ratio=2.55, crown_wide=1.44,
          crown_flat=0.52, puff=(0.32, 0.50))),
    ("Env_Tree_Conifer_A", "conifer", 0.80, 1201,
     dict(layers=11, wide=1.52, droop_lo=0.18, trunk_ratio=3.4)),
    ("Env_Tree_Conifer_B", "conifer", 1.15, 1202,
     dict(layers=15, wide=1.30, droop_lo=0.26, trunk_ratio=4.0)),
    ("Env_Tree_Conifer_C", "conifer", 1.55, 1203,
     dict(layers=18, wide=1.12, droop_lo=0.34, trunk_ratio=4.6)),
    ("Env_Tree_Birch_A", "birch_multi", 0.95, 1301,
     dict(nbr=4, crown=21, crown_r=0.90, puff=(0.30, 0.46), trunk_ratio=2.6)),
    ("Env_Tree_Birch_B", "birch", 1.30, 1302,
     dict(nbr=6, crown=22, crown_r=0.95, puff=(0.32, 0.50), trunk_ratio=3.0)),
    ("Env_Tree_Birch_C", "birch", 1.75, 1303,
     dict(nbr=7, crown=24, crown_r=1.02, puff=(0.30, 0.48), trunk_ratio=3.3)),
    ("Env_Tree_Willow_A", "willow", 0.95, 1401,
     dict(nlimb=4, nstr=(7, 9), strand_w=(0.085, 0.130), crown=6,
          trunk_ratio=1.6)),
    ("Env_Tree_Willow_B", "willow", 1.30, 1402,
     dict(nlimb=5, nstr=(9, 12), strand_w=(0.075, 0.120), crown=8,
          trunk_ratio=1.9)),
    ("Env_Tree_Willow_C", "willow", 1.70, 1403,
     dict(nlimb=6, nstr=(8, 10), strand_w=(0.070, 0.110), crown=9,
          trunk_ratio=2.1)),
]


def build_tree(name, species, scale, seed, params):
    rng = random.Random(seed)
    bm = E.bm_new()
    wp = E.WindPainter()
    if species == "broadleaf":
        tree_broadleaf(bm, wp, rng, scale, **params)
    elif species == "conifer":
        tree_conifer(bm, wp, rng, scale, **params)
    elif species == "birch":
        tree_birch(bm, wp, rng, scale, multi=False, **params)
    elif species == "birch_multi":
        tree_birch(bm, wp, rng, scale, multi=True, **params)
    elif species == "willow":
        tree_willow(bm, wp, rng, scale, **params)
    return bm, wp


# --------------------------------------------------------------------------
# ground plants
# --------------------------------------------------------------------------

def grass_clump(bm, wp, rng, blades=16, height=0.42, spread=0.16, mat=GRASS,
                arch=0.55, width=0.045):
    for i in range(blades):
        a = rng.uniform(0, 6.2832)
        r = spread * math.sqrt(rng.random())
        base = Vector((math.cos(a) * r, math.sin(a) * r, 0.0))
        h = height * rng.uniform(0.55, 1.15)
        d = Vector((math.cos(a) * rng.uniform(0.1, 0.45),
                    math.sin(a) * rng.uniform(0.1, 0.45), 1.0))
        pts = curve_points(base, d, h, 3, rng, curl=arch, gravity=0.30,
                           wobble=0.04)
        w = width * rng.uniform(0.7, 1.3)
        widths = [w, w * 0.85, w * 0.5, 0.0]
        ph = rng.random()
        faces, vs = E.bm_ribbon(bm, pts, widths, mat, double=True,
                                up=(0, 0, 1), thickness=0.004)
        for v in vs:
            t = max(0.0, min(1.0, (v.co.z - base.z) / max(h, 1e-5)))
            wp.mark(v.co, t ** 0.85, ph, t)


def bush(bm, wp, rng, radius=0.5, height=0.62, puffs=7, mat=BUSH,
         stems=True):
    if stems:
        for i in range(rng.randint(3, 4)):
            a = rng.uniform(0, 6.28)
            d = Vector((math.cos(a) * 0.5, math.sin(a) * 0.5, 1.0))
            pts = curve_points((0, 0, 0), d, height * 0.75, 2, rng, curl=0.3)
            _, rings = E.bm_polytube(bm, pts, taper(0.028, 0.012, len(pts)), 4,
                                     BARK_BROAD, cap_start=False, cap_end=True)
            paint_tube(wp, rings, 0.0, 0.35, 0.0)
    for i in range(puffs):
        a = rng.uniform(0, 6.2832)
        rr = radius * math.sqrt(rng.random()) * 0.8
        z = height * rng.uniform(0.35, 0.95)
        c = Vector((math.cos(a) * rr, math.sin(a) * rr, z))
        pr = radius * rng.uniform(0.32, 0.50)
        ph = rng.random()
        faces, vs = E.bm_puff(bm, c, pr, rng, mat, sides=6,
                              squash=(1.15, 1.15, 0.85), lumpy=0.34,
                              rot=rng.uniform(0, 3.14))
        for v in vs:
            t = max(0.0, min(1.0, v.co.z / max(height * 1.3, 1e-5)))
            d = (v.co - c).length / max(pr, 1e-5)
            wp.mark(v.co, min(1.0, 0.35 + 0.55 * t + 0.25 * d), ph,
                    min(1.0, 0.4 + 0.6 * d))


def fern(bm, wp, rng, fronds=7, length=0.55, mat=FERN):
    for i in range(fronds):
        a = rng.uniform(0, 6.2832)
        d = Vector((math.cos(a) * rng.uniform(0.55, 1.0),
                    math.sin(a) * rng.uniform(0.55, 1.0), 1.0))
        ln = length * rng.uniform(0.7, 1.2)
        pts = curve_points((0, 0, 0.02), d, ln, 4, rng, curl=0.42,
                           gravity=0.45, wobble=0.03)
        w = ln * rng.uniform(0.20, 0.30)
        widths = [w * 0.3, w, w * 0.85, w * 0.45, 0.0]
        ph = rng.random()
        faces, vs = E.bm_ribbon(bm, pts, widths, mat, double=True,
                                up=(0, 0, 1), thickness=0.005)
        for v in vs:
            t = (v.co - Vector((0, 0, 0.02))).length / max(ln, 1e-5)
            wp.mark(v.co, min(1.0, 0.18 + 0.85 * t), ph, min(1.0, 0.3 + 0.7 * t))


def reeds(bm, wp, rng, count=13, height=1.05, spread=0.22, mat=REED):
    for i in range(count):
        a = rng.uniform(0, 6.2832)
        r = spread * math.sqrt(rng.random())
        base = Vector((math.cos(a) * r, math.sin(a) * r, 0))
        h = height * rng.uniform(0.6, 1.15)
        d = Vector((math.cos(a) * rng.uniform(0.02, 0.22),
                    math.sin(a) * rng.uniform(0.02, 0.22), 1.0))
        pts = curve_points(base, d, h, 4, rng, curl=0.20, gravity=0.16,
                           wobble=0.02)
        ph = rng.random()
        if rng.random() < 0.55:
            # bladed reed
            w = 0.05 * rng.uniform(0.7, 1.3)
            faces, vs = E.bm_ribbon(bm, pts, [w, w * 0.9, w * 0.7, w * 0.4, 0.0],
                                    mat, double=True, up=(0, 0, 1),
                                    thickness=0.004)
        else:
            # stalk with a seed head
            rr = taper(0.014, 0.008, len(pts))
            faces, rings = E.bm_polytube(bm, pts, rr, 4, mat, cap_start=False,
                                         cap_end=False)
            vs = [v for rg in rings for v in rg]
            hd = pts[-1]
            hf, hv = E.bm_puff(bm, hd + Vector((0, 0, 0.06)), 0.055, rng, mat,
                               sides=5, squash=(0.55, 0.55, 2.1), lumpy=0.12)
            vs.extend(hv)
        for v in vs:
            t = max(0.0, min(1.0, (v.co.z - base.z) / max(h, 1e-5)))
            wp.mark(v.co, t ** 0.8, ph, t * 0.6)


def lilypad(bm, wp, rng, radius=0.34, pads=3, mat=LILYPAD):
    for i in range(pads):
        a = rng.uniform(0, 6.2832)
        r = radius * 1.1 * rng.random() if i else 0.0
        c = Vector((math.cos(a) * r, math.sin(a) * r, 0.0))
        pr = radius * rng.uniform(0.55, 1.0)
        sides = 9
        rot = rng.uniform(0, 6.28)
        ring = []
        notch = rng.uniform(0, 6.28)
        for j in range(sides):
            ang = rot + 2 * math.pi * j / sides
            # the classic lily-pad notch
            dn = abs(((ang - notch + math.pi) % 6.2832) - math.pi)
            k = 0.30 if dn < 0.45 else 1.0
            rr = pr * k * (1.0 + rng.uniform(-0.06, 0.06))
            ring.append(bm.verts.new(c + Vector((math.cos(ang) * rr,
                                                 math.sin(ang) * rr,
                                                 0.012 + 0.02 * (1 - k)))))
        centre = bm.verts.new(c + Vector((0, 0, 0.026)))
        under = bm.verts.new(c + Vector((0, 0, 0.0)))
        ph = rng.random()
        for j in range(sides):
            k = (j + 1) % sides
            f = bm.faces.new((ring[j], ring[k], centre))
            f.smooth = True
            f.material_index = mat
            f2 = bm.faces.new((ring[k], ring[j], under))
            f2.smooth = True
            f2.material_index = mat
        for v in ring + [centre, under]:
            d = (v.co - c).length / max(pr, 1e-5)
            wp.mark(v.co, 0.25 + 0.35 * d, ph, 0.2)


def flower_plant(bm, wp, rng, colour_mat, stem_h=0.30, blooms=4):
    for i in range(blooms):
        a = rng.uniform(0, 6.2832)
        r = 0.045 * math.sqrt(rng.random())
        base = Vector((math.cos(a) * r, math.sin(a) * r, 0))
        h = stem_h * rng.uniform(0.7, 1.2)
        pts = curve_points(base, (math.cos(a) * 0.18, math.sin(a) * 0.18, 1),
                           h, 3, rng, curl=0.25, gravity=0.12, wobble=0.02)
        _, rings = E.bm_polytube(bm, pts, taper(0.011, 0.007, len(pts)), 4,
                                 GRASS, cap_start=False, cap_end=False)
        ph = rng.random()
        for rg in rings:
            for v in rg:
                t = (v.co.z - base.z) / max(h, 1e-5)
                wp.mark(v.co, 0.15 + 0.5 * t, ph, 0.0)
        tip = pts[-1]
        tdir = (pts[-1] - pts[-2]).normalized()
        npet = 5
        pu = tdir.orthogonal().normalized()
        pv = tdir.cross(pu).normalized()
        pr = 0.055 * rng.uniform(0.85, 1.2)
        for k in range(npet):
            ang = 2 * math.pi * k / npet + rng.uniform(-0.15, 0.15)
            outw = (pu * math.cos(ang) + pv * math.sin(ang))
            ppts = [tip + outw * pr * 0.18 + tdir * 0.004,
                    tip + outw * pr * 0.72 + tdir * 0.016,
                    tip + outw * pr * 1.15 + tdir * 0.006]
            w = pr * 0.95
            faces, vs = E.bm_ribbon(bm, ppts, [w * 0.45, w, 0.0], colour_mat,
                                    double=True, up=tuple(tdir),
                                    thickness=0.003)
            for v in vs:
                wp.mark(v.co, 0.72, ph, 0.9)
        cf, cv = E.bm_puff(bm, tip + tdir * 0.012, pr * 0.30, rng, FLOWER,
                           sides=5, squash=(1, 1, 0.6), lumpy=0.1)
        for v in cv:
            wp.mark(v.co, 0.68, ph, 0.5)
        # a leaf or two low on the stem
        if rng.random() < 0.6:
            li = 1
            ld = Vector((math.cos(a + 1.6), math.sin(a + 1.6), 0.5)).normalized()
            lp = curve_points(pts[li], ld, 0.09, 2, rng, curl=0.5, gravity=0.4)
            faces, vs = E.bm_ribbon(bm, lp, [0.014, 0.030, 0.0], GRASS,
                                    double=True, up=(0, 0, 1), thickness=0.003)
            for v in vs:
                wp.mark(v.co, 0.35, ph, 0.4)


def cave_moss(bm, wp, rng, radius=0.42, blobs=9, mat=MOSS):
    """Flat mossy crust for cave floors and rock bases."""
    for i in range(blobs):
        a = rng.uniform(0, 6.2832)
        r = radius * math.sqrt(rng.random())
        c = Vector((math.cos(a) * r, math.sin(a) * r,
                    rng.uniform(0.01, 0.05)))
        pr = radius * rng.uniform(0.22, 0.40)
        f, vs = E.bm_puff(bm, c, pr, rng, mat, sides=6,
                          squash=(1.35, 1.35, 0.34), lumpy=0.38,
                          rot=rng.uniform(0, 3.14))
        ph = rng.random()
        for v in vs:
            wp.mark(v.co, 0.10 + 0.25 * (v.co.z / max(0.12, 1e-5)), ph, 0.3)


def hanging_vine(bm, wp, rng, length=1.6, strands=5, mat=VINE):
    """Hangs from a ceiling/branch: pivot at the TOP anchor, grows downward."""
    for i in range(strands):
        a = rng.uniform(0, 6.2832)
        r = 0.045 * math.sqrt(rng.random())
        anchor = Vector((math.cos(a) * r, math.sin(a) * r, 0.0))
        ln = length * rng.uniform(0.55, 1.0)
        pts = curve_points(anchor, (rng.uniform(-.12, .12),
                                    rng.uniform(-.12, .12), -1),
                           ln, 4, rng, curl=0.10, gravity=0.35, wobble=0.03)
        _, rings = E.bm_polytube(bm, pts, taper(0.016, 0.007, len(pts)), 4,
                                 BARK_BROAD, cap_start=False, cap_end=True)
        ph = rng.random()
        for j, rg in enumerate(rings):
            t = j / float(len(rings) - 1)
            for v in rg:
                wp.mark(v.co, 0.15 + 0.55 * t, ph, t * 0.4)
        for k in range(rng.randint(3, 5)):
            t = rng.uniform(0.2, 1.0)
            idx = min(len(pts) - 1, int(t * (len(pts) - 1)))
            c = pts[idx] + Vector((rng.uniform(-.05, .05),
                                   rng.uniform(-.05, .05), -0.02))
            pr = rng.uniform(0.055, 0.10)
            f, vs = E.bm_puff(bm, c, pr, rng, mat, sides=5,
                              squash=(1.3, 1.3, 0.62), lumpy=0.3,
                              rot=rng.uniform(0, 3.14))
            for v in vs:
                wp.mark(v.co, min(1.0, 0.35 + 0.7 * t), ph, 0.8)


# --------------------------------------------------------------------------
# asset table
# --------------------------------------------------------------------------

# --------------------------------------------------------------------------
# Tall grass -- encounter territory, and the kit's largest gameplay gap
#
# The tallest thing in the kit was Env_Grass_Clump_C at 0.70 m, which reads as
# lawn. The layout compensated by burning 834 objects on a route that is meant
# to have grass a creature can hide in, and it still reads as lawn, because 834
# instances of a 0.7 m clump is 834 instances of a 0.7 m clump.
#
# These are 1.0-1.3 m and cover roughly a metre square each, so one instance is
# a patch and ~80 of them replace the 834.
#
# The instancing contract they are built to (integrator's, and it shapes the
# geometry, not just the export):
#   * ONE mesh, ONE material. Every blade is on the `grass` atlas cell, so the
#     cluster has a single material slot and an instanced draw stays one batch.
#   * Variation lives in the mesh. Four genuinely different clusters, not twelve
#     near-identical ones; rotation, scale and phase are per-instance at draw
#     time and cost nothing.
#   * Under 200 triangles. At thousands of instances the triangle count
#     multiplies directly, and at HD-2D framing the read comes from the
#     silhouette and the wind, not from blade density -- so the blades are
#     single-sided (the foliage shader is Cull Off, so a double-sided ribbon
#     buys nothing but doubles the cost) and wide enough to carry the atlas.
#   * No LODs.
#   * Pivot at the base, centred, so a per-instance Y rotation spins the clump
#     in place instead of swinging it off its spot.
#
# Wind: R is the sway mask, 0 at the base and 1 at the tip; G is the WITHIN-
# cluster phase, so neighbouring blades in one clump do not move as a slab. The
# per-instance phase is added by the renderer on top, which is why G here is
# deliberately only a local decorrelation.
# --------------------------------------------------------------------------

def tall_grass(bm, wp, rng, blades=22, height=1.15, spread=0.22, arch=0.26,
               width=0.085, tuft=3, lean=0.07, mat=GRASS):
    """A cluster of tufts covering ~1 x 1 m.

    Built as a handful of tufts rather than one even spray: real grass grows in
    crowns, and a clump with two or three dense centres reads as a patch from
    the three-quarter camera while an even scatter reads as a hemisphere.
    """
    crowns = []
    for i in range(tuft):
        a = 2.0 * math.pi * (i + rng.uniform(-0.25, 0.25)) / tuft
        r = spread * (0.35 + 0.65 * rng.random())
        crowns.append((math.cos(a) * r, math.sin(a) * r))
    # a shared lean direction, so the whole clump agrees which way the wind has
    # been blowing rather than splaying evenly in every direction
    la = rng.uniform(0, 6.2832)
    lx, ly = math.cos(la) * lean, math.sin(la) * lean
    for i in range(blades):
        cx, cy = crowns[i % len(crowns)]
        a = rng.uniform(0, 6.2832)
        r = 0.045 * math.sqrt(rng.random())
        base = Vector((cx + math.cos(a) * r, cy + math.sin(a) * r, 0.0))
        h = height * rng.uniform(0.78, 1.05)
        # Blades stand UP. The first pass let them lean out by up to 0.34 of
        # their length and arch under gravity, and at the shipped 42 deg camera
        # every clump read as a flat starburst on the ground -- the render made
        # that obvious in a way the numbers did not. A tuft is near-vertical
        # with only the top third bending over.
        out = 0.02 + 0.13 * rng.random()
        d = Vector((math.cos(a) * out + lx, math.sin(a) * out + ly, 1.0))
        # three segments, not four: at 200 triangles a cluster the budget buys
        # either more blades or more bend per blade, and a patch reads on the
        # number of blades in the silhouette, not on how smoothly each curves
        pts = curve_points(base, d, h, 3, rng, curl=arch, gravity=0.10,
                           wobble=0.022)
        w = width * rng.uniform(0.72, 1.25)
        widths = [w, w * 0.94, w * 0.62, 0.0]
        ph = rng.random()
        # up=None, deliberately. bm_ribbon derives the blade's width direction
        # from up.cross(tangent), and for a near-vertical blade that cross
        # product is degenerate -- the strip collapses and the clump renders as
        # a flat starburst lying on the ground, which is exactly what the first
        # assembled shot showed. With up=None the ribbon uses its own
        # parallel-transported frame, which is stable at any tangent.
        # Single sided: 6 tris a blade instead of 12, and the shader is Cull Off.
        faces, vs = E.bm_ribbon(bm, pts, widths, mat, double=False,
                                up=None, thickness=0.0)
        for v in vs:
            t = max(0.0, min(1.0, v.co.z / max(h, 1e-5)))
            wp.mark(v.co, t ** 0.9, ph, t)


def _tall(seed, blades, h, spread, arch, w, tuft, lean):
    def f(bm, wp, rng):
        tall_grass(bm, wp, rng, blades, h, spread, arch, w, tuft, lean)
    return f


def _grass(seed, blades, h, spread, arch, w):
    def f(bm, wp, rng):
        grass_clump(bm, wp, rng, blades, h, spread, GRASS, arch, w)
    return f


PLANT_SPECS = [
    ("Env_Grass_Clump_A", 2101, (200, 1500), _grass(0, 27, 0.44, 0.16, 0.55, 0.045)),
    ("Env_Grass_Clump_B", 2102, (200, 1500), _grass(0, 32, 0.30, 0.21, 0.85, 0.055)),
    ("Env_Grass_Clump_C", 2103, (200, 1500), _grass(0, 24, 0.64, 0.14, 0.35, 0.038)),
    ("Env_Grass_Clump_D", 2104, (200, 1500), _grass(0, 38, 0.24, 0.27, 1.10, 0.062)),
    # tall grass: encounter cover, GPU instanced, no LODs, one material
    ("Env_TallGrass_Cluster_A", 2111, (40, 260),
     _tall(0, 32, 1.24, 0.15, 0.24, 0.100, 3, 0.06)),
    ("Env_TallGrass_Cluster_B", 2112, (40, 260),
     _tall(0, 34, 1.08, 0.19, 0.36, 0.092, 4, 0.11)),
    ("Env_TallGrass_Cluster_C", 2113, (40, 260),
     _tall(0, 30, 1.34, 0.12, 0.15, 0.106, 2, 0.04)),
    ("Env_TallGrass_Cluster_D", 2114, (40, 260),
     _tall(0, 36, 1.16, 0.17, 0.30, 0.086, 5, 0.09)),
    ("Env_Grass_Blade", 2105, (8, 1500),
     lambda bm, wp, rng: grass_clump(bm, wp, rng, 1, 0.46, 0.0, GRASS, 0.5, 0.055)),
    ("Env_Bush_A", 2201, (200, 1500),
     lambda bm, wp, rng: bush(bm, wp, rng, 0.52, 0.66, 8)),
    ("Env_Bush_B", 2202, (200, 1500),
     lambda bm, wp, rng: bush(bm, wp, rng, 0.36, 0.42, 6)),
    ("Env_Bush_C", 2203, (200, 1500),
     lambda bm, wp, rng: bush(bm, wp, rng, 0.72, 0.95, 10)),
    ("Env_Fern_A", 2301, (200, 1500),
     lambda bm, wp, rng: fern(bm, wp, rng, 17, 0.58)),
    ("Env_Fern_B", 2302, (200, 1500),
     lambda bm, wp, rng: fern(bm, wp, rng, 15, 0.36)),
    ("Env_Reed_A", 2401, (200, 1500),
     lambda bm, wp, rng: reeds(bm, wp, rng, 14, 1.10, 0.22)),
    ("Env_Reed_B", 2402, (200, 1500),
     lambda bm, wp, rng: reeds(bm, wp, rng, 10, 0.70, 0.30)),
    ("Env_Lilypad_A", 2501, (30, 1500),
     lambda bm, wp, rng: lilypad(bm, wp, rng, 0.36, 3)),
    ("Env_Lilypad_B", 2502, (30, 1500),
     lambda bm, wp, rng: lilypad(bm, wp, rng, 0.26, 2)),
    ("Env_Moss_Cave_A", 2701, (100, 1500),
     lambda bm, wp, rng: cave_moss(bm, wp, rng, 0.46, 10)),
    ("Env_Moss_Cave_B", 2702, (100, 1500),
     lambda bm, wp, rng: cave_moss(bm, wp, rng, 0.28, 6)),
    ("Env_Vine_Hanging_A", 2801, (200, 1500),
     lambda bm, wp, rng: hanging_vine(bm, wp, rng, 1.8, 5)),
    ("Env_Vine_Hanging_B", 2802, (200, 1500),
     lambda bm, wp, rng: hanging_vine(bm, wp, rng, 1.0, 3)),
]

for _cn, _ci in FLOWER_COLOURS:
    PLANT_SPECS.append(
        ("Env_Flower_%s" % _cn, 2600 + _ci, (150, 1500),
         (lambda cn: (lambda bm, wp, rng: flower_plant(
             bm, wp, rng, MS_LOOKUP["flower_%s" % cn.lower()],
             0.30 + 0.05 * rng.random(), 4 + rng.randint(0, 2))))(_cn)))

MS_LOOKUP = {}


# --------------------------------------------------------------------------
# build
# --------------------------------------------------------------------------

def SINGLE_MATERIAL(name):
    """Assets the integrator draws with GPU instancing: one mesh, one material,
    no exceptions, because a second material is a second batch."""
    return name.startswith("Env_TallGrass_")


def finish(bm, wp, name, ms, budget, pivot='center', smooth=70.0):
    obj = E.bm_to_obj(bm, name, ms.materials())
    E.finalize(obj, smooth_angle=smooth)
    if pivot == 'center':
        E.pivot_to_base(obj)
    elif pivot == 'top':
        me = obj.data
        zs = [v.co.z for v in me.vertices]
        E.set_pivot(obj, (0, 0, max(zs)))
    E.apply_transforms(obj)
    E.uv_all(obj, ms, angle=68.0, margin=0.012)
    # after the UV pack, which is the last step that needs material_index to
    # equal the atlas cell
    E.strip_unused_materials(obj)
    wp.apply(obj)
    tris, probs = E.validate(obj, budget=budget, need_vcol=True, strict=False,
                             max_materials=1 if SINGLE_MATERIAL(obj.name)
                             else None)
    return obj, tris, probs


def main():
    E.ensure_dirs()
    T.ensure_atlas(FAM)
    E.reset_scene()
    ms = matset()
    for k, v in ms.index.items():
        MS_LOOKUP[k] = v

    entries = []
    problems = []
    made = []

    for (name, species, scale, seed, params) in TREE_SPECS:
        bm, wp = build_tree(name, species, scale, seed, params)
        obj, tris, probs = finish(bm, wp, name, ms, (200, 1500))
        path = os.path.join(OUT, name + ".fbx")
        E.export_fbx([obj], path)
        entries.append(dict(name=name, family="Tree", path=path, tris=tris,
                            pivot="trunk base, XY centred on trunk",
                            wind=True, notes=species))
        if probs:
            problems.append((name, probs))
        E.log("%-26s %5d tris  %s" % (name, tris, probs or "ok"))
        made.append(obj)

    for (name, seed, budget, fn) in PLANT_SPECS:
        rng = random.Random(seed)
        bm = E.bm_new()
        wp = E.WindPainter()
        fn(bm, wp, rng)
        pivot = 'top' if 'Vine_Hanging' in name else 'center'
        obj, tris, probs = finish(bm, wp, name, ms, budget, pivot=pivot)
        path = os.path.join(OUT, name + ".fbx")
        E.export_fbx([obj], path)
        entries.append(dict(name=name, family=name.split("_")[1], path=path,
                            tris=tris, wind=True,
                            pivot=("top anchor (hangs downward)" if pivot == 'top'
                                   else "base, XY centred"),
                            notes=""))
        if probs:
            problems.append((name, probs))
        E.log("%-26s %5d tris  %s" % (name, tris, probs or "ok"))
        made.append(obj)

    ap = T.atlas_paths(FAM)
    part = []
    for e in entries:
        part.append({
            "name": e["name"], "family": FAM, "subfamily": e["family"],
            "path": os.path.relpath(e["path"], E.REPO).replace("\\", "/"),
            "triangles": e["tris"], "lods": [],
            "pivot": e["pivot"],
            "textures": [os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
                         os.path.relpath(ap["normal"], E.REPO).replace("\\", "/")],
            "windVertexColors": True,
            "notes": e["notes"],
        })
        if e["name"].startswith("Env_TallGrass_"):
            part[-1]["budgetClass"] = "scatter"
            part[-1]["gpuInstanced"] = True
            part[-1]["materialSlots"] = 1
            part[-1]["notes"] = (
                "Encounter-height grass, 1.0-1.3 m, covering roughly 1 x 1 m -- "
                "one instance is a patch, so ~80 of these replace the layout's "
                "834 lawn clumps. Built to the instancing contract: one mesh, "
                "one material, no LODs, pivot at the base and centred so a "
                "per-instance Y rotation spins it in place. The green channel "
                "is the WITHIN-cluster wind phase only; add the per-instance "
                "phase on top at draw time.")
    E.write_part(FAM, part)

    E.log("---- %d foliage assets, %d with problems" % (len(entries), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))
    if "--keep" in sys.argv:
        bpy.ops.wm.save_as_mainfile(
            filepath=os.path.join(E.THIS_DIR, "previews", "_foliage.blend"))


if __name__ == "__main__":
    main()
