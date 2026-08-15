"""Species 81 - Geodude. Rock / Ground, a boulder with arms.

Modelled from data/pokemon_images/007401.png: a rough grey boulder with a heavy
angled brow, two thick muscular arms ending in blocky four-knuckle fists, and no
legs at all - it rests directly on the ground.

The rock surface is faceted geometry with a low smoothing angle, so the planes
catch light as facets rather than reading as a smooth ball. Eyes use the
cast-wide simple dark oval idiom under the reference's heavy brow.
"""

import math
import os
import random
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 81
NAME = "Geodude"
TYPES = ["Rock", "Ground"]
HEIGHT = 0.40
SIZE_AXIS = 'z'
DESIGN = "rough grey boulder with a heavy brow and two muscular stone arms"
PROFILE = dict(tempo=0.75, weight=1.6, bounce=0.5, sway=0.6, stride=0.7)
TRI_MIN = 2000

ROCK = C.hexcol('a5a29c')
ROCK_LIGHT = C.hexcol('c2bfb8')
ROCK_DARK = C.hexcol('6f6c6b')
ROCK_SHADOW = C.hexcol('55524f')
BROW = C.hexcol('8a8681')


def _boulder(name, radius, seed=81):
    """A rock, not a sphere: large-scale lumps plus a facet pass."""
    ob = C.spherified_cube(name, cuts=4, radius=radius)
    rnd = random.Random(seed)
    # a handful of big asymmetric lumps and bites
    for _ in range(9):
        d = Vector((rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1)))
        if d.length < 1e-4:
            continue
        d.normalize()
        amt = rnd.uniform(-0.24, 0.20) * radius
        C.bulge(ob, tuple(d * radius * 0.92), radius * rnd.uniform(0.55, 0.95),
                amt, direction=tuple(d))
    # medium chunk and fine grain
    C.noise_displace(ob, 9.0 / max(1e-6, radius), radius * 0.045, seed=seed)
    C.noise_displace(ob, 26.0 / max(1e-6, radius), radius * 0.012, seed=seed + 7)
    # flatten the base so it sits on the ground instead of balancing on a point
    zmin = min(v.co.z for v in ob.data.vertices)
    C.deform(ob, lambda co: Vector(
        (co.x, co.y, co.z + (zmin * 0.80 - co.z) * 0.45
         * C.falloff(abs(co.z - zmin) / (radius * 0.42), 1.0))))
    return ob


def _fist(name, centre, size, sx=1.0):
    """A blocky four-knuckle fist - the reference hands are square, not spheres."""
    ob = C.spherified_cube(name, cuts=3, radius=0.5)
    C.deform(ob, lambda co: Vector((co.x * size[0], co.y * size[1], co.z * size[2])))
    # knuckle row
    for i in range(4):
        u = (i / 3.0) * 2.0 - 1.0
        C.bulge(ob, (u * size[0] * 0.26, -size[1] * 0.40, size[2] * 0.16),
                max(size) * 0.34, size[1] * 0.16, direction=(0, -1, 0.25),
                ellipse=(1.0, 1.0, 1.1))
    # thumb ridge
    C.bulge(ob, (sx * size[0] * 0.42, -size[1] * 0.12, -size[2] * 0.10),
            max(size) * 0.40, size[0] * 0.16, direction=(sx, -0.4, -0.2))
    C.noise_displace(ob, 22.0 / max(size), min(size) * 0.05, seed=int(81 + sx))
    ob.location = Vector(centre)
    C.apply_transforms(ob)
    return ob


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Geodude_Body")

    # no legs at all; the boulder IS the torso and it rests on the ground
    plan.spine = [
        ((0.000,  0.010, 0.108), 0.086, 0.078),
        ((0.000,  0.000, 0.170), 0.104, 0.098),
        ((0.000, -0.004, 0.230), 0.096, 0.092),
    ]
    plan.arms = [
        ((0.070,  0.006, 0.206), 0.038, 0.038),
        ((0.146,  0.010, 0.176), 0.032, 0.032),
        ((0.196,  0.014, 0.238), 0.028, 0.028),
        ((0.214,  0.016, 0.286), 0.024, 0.024),
    ]
    plan.legs = []
    plan.tail = []
    plan.head_co = Vector((0.0, 0.000, 0.196))
    plan.head_size = (0.230, 0.222, 0.212)
    plan.subsurf = 0

    # the "body" here is really just the arms - the boulder replaces the torso
    body = plan.build_body(method='loft', torso_segments=14, limb_segments=11,
                           torso_square=2.1, resample=1, limb_resample=2,
                           round_torso_front=0.6, round_torso_back=0.6)

    boulder = _boulder("Geodude_Rock", 0.118)
    boulder.location = plan.head_co
    C.apply_transforms(boulder)

    fists = []
    for sx in (1.0, -1.0):
        fists.append(_fist("Geodude_Fist_%s" % ('L' if sx > 0 else 'R'),
                           (0.224 * sx, 0.016, 0.300), (0.062, 0.058, 0.062), sx))

    # heavy angled brow ridges - the reference's whole expression lives here
    brows = []
    for sx in (1.0, -1.0):
        b = B.spike("Geodude_Brow_%s" % ('L' if sx > 0 else 'R'),
                    base=tuple(plan.head_co + Vector((0.010 * sx, -0.116, 0.070))),
                    direction=(0.94 * sx, -0.10, -0.32), length=0.082,
                    radius=0.028, samples=5, ring=7, sharp=2.4, color=BROW)
        C.deform(b, lambda co: Vector((co.x, co.y * 0.72, co.z * 0.66)))
        brows.append(b)

    grad = C.body_gradient(
        top=ROCK_LIGHT, bottom=ROCK_DARK, zmin=0.0, zmax=0.36,
        belly=None,
        patches=[
            (ROCK, (0.0, -0.086, 0.208), 0.100, (1.5, 1.0, 1.2), 0.7),
            (ROCK_SHADOW, (0.0, 0.086, 0.120), 0.090, (1.6, 1.1, 1.0), 0.75),
            (ROCK_LIGHT, (0.052, -0.030, 0.288), 0.070, (1.4, 1.3, 0.8), 0.8),
        ],
        noise_amt=0.035, seed=81)
    for ob in [body, boulder] + fists + brows:
        C.paint(ob, grad)

    C.carve_mouth(boulder, (0.0, -0.100, -0.056), width=0.064, height=0.014,
                  depth=0.008, look=Vector((0, -1, 0)), color=ROCK_SHADOW,
                  corner_lift=-0.006)

    face, eye_centres = B.simple_face(
        "Geodude", plan.head_co,
        head_size=plan.head_size, eye_angles=(24.0, 10.0), eye_radius=0.0270,
        eye_squash=(1.0, 0.62, 0.58), eye_tilt=12.0, eye_sink=1.02,
        mouth_angles=-34.0, mouth_width=0.078, mouth_curve=-0.30,
        mouth_thickness=0.0075, face_bow=0.020, highlight=0.26)

    parts = [body, boulder] + fists + brows + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0250,
        muzzle_y=-0.130,
        anchors=dict(muzzle=(0.0, -0.134, 0.176)),
        bevel_scale=0.008,
        # a low smoothing angle is what makes the rock read as facets, not plastic
        smooth_angle=26.0,
        sharp_angle=24.0,
        albedo=dict(detail_scale=26.0, cavity=0.52, ao_strength=0.50, speckle=0.09,
                    voronoi_scale=14.0, voronoi_amount=0.14),
        normal=dict(detail_scale=38.0, bump=0.30, pattern_scale=9.0,
                    pattern_bump=0.22),
    )
