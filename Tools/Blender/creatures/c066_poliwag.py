"""Species 66 - Poliwag. Water, tadpole with the spiral belly.

Modelled from data/pokemon_images/006001.png: a near-spherical blue tadpole with
a big black-and-white spiral covering the belly, a small pink mouth, two thin
legs, and a long thin tail ending in a broad translucent fin.

The spiral is swept geometry rather than a painted decal - at this vertex density
a painted spiral would smear, and a raised ridge also catches light.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 66
NAME = "Poliwag"
TYPES = ["Water"]
HEIGHT = 0.60
SIZE_AXIS = 'z'
DESIGN = "spherical blue tadpole with a spiral belly and a long finned tail"
PROFILE = dict(tempo=1.15, weight=0.7, bounce=1.2, sway=1.25, stride=0.8)

BLUE = C.hexcol('5b86b8')
BLUE_LIGHT = C.hexcol('82a3c9')
BLUE_DARK = C.hexcol('42648e')
SPIRAL_W = C.hexcol('eef2f8')
SPIRAL_K = C.hexcol('27282c')
MOUTH = C.hexcol('daa3c0')
FIN = C.hexcol('c2d5e5')


def _spiral(name, centre, radius, turns=2.6, thickness=0.008, bulge=0.055):
    """A raised black spiral ridge wrapped onto the belly."""
    pts = []
    radii = []
    n = 76
    for i in range(n):
        t = i / float(n - 1)
        ang = t * turns * math.tau
        r = radius * (0.06 + 0.94 * t)
        x = math.cos(ang) * r
        z = math.sin(ang) * r
        # bow the ridge forward so it follows the sphere
        y = -bulge * math.sqrt(max(0.0, 1.0 - (r / radius) ** 2))
        pts.append(Vector(centre) + Vector((x, y, z)))
        radii.append(thickness * (0.55 + 0.45 * t))
    ob = C.tube_along(name, pts, radii, segments=6, up=Vector((0, -1, 0)))
    C.paint_flat(ob, SPIRAL_K)
    return ob


def _belly_disc(name, centre, radius, bulge=0.020):
    """The pale belly plate.

    A flattened dome hugging the sphere. An earlier version lofted it forward with
    a rounded cap, which pushed a big white dome out of the face like a snout.
    """
    ob = C.spherified_cube(name, cuts=3, radius=radius)
    C.deform(ob, lambda co: Vector((co.x, co.y * (bulge / max(1e-6, radius)), co.z)))
    C.paint(ob, lambda co, n, i: C.mix(SPIRAL_W, C.hexcol('cfd8e4'),
                                       C.smoothstep(math.hypot(co.x, co.z)
                                                    / max(1e-6, radius)) * 0.7))
    ob.location = Vector(centre)
    C.apply_transforms(ob)
    return ob


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Poliwag_Body")

    plan.spine = [
        ((0.000,  0.010, 0.190), 0.036, 0.036),
        ((0.000,  0.000, 0.250), 0.038, 0.038),
        ((0.000, -0.004, 0.300), 0.034, 0.034),
    ]
    plan.arms = []
    plan.legs = [
        ((0.038,  0.010, 0.180), 0.019, 0.019),
        ((0.044,  0.006, 0.108), 0.015, 0.015),
        ((0.050,  0.000, 0.034), 0.017, 0.013),
    ]
    # long thin tail sweeping back and up into a broad fin
    plan.tail = [
        ((0.000,  0.108, 0.252), 0.026, 0.026),
        ((0.000,  0.186, 0.268), 0.019, 0.019),
        ((0.000,  0.264, 0.286), 0.013, 0.013),
        ((0.000,  0.330, 0.306), 0.008, 0.008),
    ]
    plan.head_co = Vector((0.0, 0.000, 0.262))
    plan.head_size = (0.290, 0.286, 0.278)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=10, limb_segments=10,
                           torso_square=2.0, resample=1, limb_resample=2,
                           round_torso_front=0.6, round_torso_back=0.6)

    head = B.sculpt_head(
        "Poliwag_Bulb", *plan.head_size, cuts=3,
        snout_len=0.04, snout_drop=0.0, snout_narrow=0.94, snout_z=-0.20,
        snout_blunt=0.90, crown=0.020, crown_back=0.0,
        brow=0.015, brow_z=0.20, brow_y=-0.28, brow_x=0.30,
        cheek=0.020, cheek_z=-0.08, jaw_width=1.0, chin=0.0,
        top_flat=0.0, subsurf=1)
    B.head_place(head, plan.head_co)

    belly = _belly_disc("Poliwag_Belly", (0.0, -0.126, 0.238), 0.090, bulge=0.030)
    spiral = _spiral("Poliwag_Spiral", (0.0, -0.146, 0.238), 0.078, turns=2.5,
                     thickness=0.0075, bulge=0.012)

    # broad tail fin
    fin = C.leaf_blade("Poliwag_Fin", length=0.196, width=0.086, thickness=0.0055,
                       curl=0.10, segments=7, taper=1.6)
    C.paint(fin, lambda co, n, i: C.mix(FIN, BLUE_LIGHT,
                                        C.smoothstep(1.0 - co.y / 0.196)))
    C.place(fin, location=(0.0, 0.262, 0.294),
            rotation=(math.radians(-86), 0.0, math.radians(180)))

    feet = B.paw_pair("Poliwag_Foot", (0.052, -0.010, 0.020),
                      length=0.044, width=0.028, height=0.020, toes=1,
                      toe_scale=0.30, spread=0.5, cuts=2, subsurf=0)

    grad = C.body_gradient(
        top=BLUE_LIGHT, bottom=BLUE_DARK, zmin=0.05, zmax=0.42,
        belly=BLUE, belly_axis_y=0.30,
        patches=[(BLUE_DARK, (0.0, 0.110, 0.300), 0.080, (1.4, 1.2, 1.2), 0.7)],
        noise_amt=0.014, seed=66)
    for ob in [body, head] + feet:
        C.paint(ob, grad)

    face, eye_centres = B.simple_face(
        "Poliwag", plan.head_co,
        head_size=plan.head_size, eye_angles=(26.0, 22.0), eye_radius=0.0330,
        eye_squash=(1.0, 0.56, 1.16), eye_tilt=4.0, eye_sink=0.92,
        mouth_angles=4.0, mouth_width=0.048, mouth_curve=0.55,
        mouth_thickness=0.0075, face_bow=0.014, highlight=0.34)

    parts = [body, head, belly, spiral, fin] + feet + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0350,
        muzzle_y=-0.160,
        anchors=dict(muzzle=(0.0, -0.156, 0.286)),
        bevel_scale=0.007,
        albedo=dict(detail_scale=26.0, cavity=0.30, ao_strength=0.32, speckle=0.025,
                    voronoi_scale=10.0, voronoi_amount=0.06),
        normal=dict(detail_scale=40.0, bump=0.08, pattern_scale=10.0,
                    pattern_bump=0.05),
    )
