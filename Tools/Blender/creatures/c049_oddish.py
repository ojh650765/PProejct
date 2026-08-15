"""Species 49 - Oddish. Grass / Poison, bulbous body with leaves.

Modelled from data/pokemon_images/004301.png: a round navy-blue body that is
essentially all head, two small stubby feet, a wide open mouth, and five broad
green leaves sprouting from the crown and fanning backwards.

Eyes use the cast-wide simple dark oval idiom (the reference's are small red
dots, so the simplified treatment sits close to it).
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 49
NAME = "Oddish"
TYPES = ["Grass", "Poison"]
HEIGHT = 0.50
SIZE_AXIS = 'z'
DESIGN = "round navy body with stubby feet and five broad green head leaves"
PROFILE = dict(tempo=0.95, weight=0.95, bounce=1.25, sway=1.0, stride=0.7)

BODY = C.hexcol('577f9b')
BODY_LIGHT = C.hexcol('7ba0b8')
BODY_DARK = C.hexcol('3f6280')
LEAF = C.hexcol('52aa47')
LEAF_LIGHT = C.hexcol('8fc07c')
LEAF_DARK = C.hexcol('3c7c35')
MOUTH = C.hexcol('8f3d4a')
FOOT = C.hexcol('44667f')


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Oddish_Body")

    # the body IS the head; the spine is a short internal stub for the rig only
    plan.spine = [
        ((0.000,  0.006, 0.098), 0.030, 0.030),
        ((0.000,  0.000, 0.140), 0.032, 0.032),
        ((0.000, -0.002, 0.176), 0.028, 0.028),
    ]
    plan.arms = []
    plan.legs = [
        ((0.040,  0.006, 0.106), 0.026, 0.026),
        ((0.044,  0.000, 0.062), 0.023, 0.023),
        ((0.046, -0.004, 0.020), 0.022, 0.019),
    ]
    plan.tail = []
    plan.head_co = Vector((0.0, 0.000, 0.180))
    plan.head_size = (0.256, 0.244, 0.226)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=10, limb_segments=11,
                           torso_square=2.0, resample=1, limb_resample=2,
                           round_torso_front=0.6, round_torso_back=0.6)

    head = B.sculpt_head(
        "Oddish_Bulb", *plan.head_size, cuts=3,
        snout_len=0.06, snout_drop=0.02, snout_narrow=0.90, snout_z=-0.24,
        snout_blunt=0.85, crown=0.035, crown_back=0.02,
        brow=0.020, brow_z=0.16, brow_y=-0.28, brow_x=0.30,
        cheek=0.045, cheek_z=-0.10, jaw_width=1.06, chin=0.0,
        top_flat=0.10, subsurf=1)
    # heavier below the middle - the reference body sags like a bulb
    C.scale_region(head, (0.0, 0.0, -0.070), 0.140, (1.10, 1.10, 1.0),
                   pivot=(0, 0, 0))
    B.head_place(head, plan.head_co)

    feet = B.paw_pair("Oddish_Foot", (0.048, -0.008, 0.014),
                      length=0.052, width=0.036, height=0.026, toes=1,
                      toe_scale=0.30, spread=0.5, cuts=2, subsurf=0)
    for f in feet:
        C.paint_flat(f, FOOT)

    # Five broad leaves fanning up and back off the crown - the defining feature.
    # Aimed with place_along rather than composed Euler angles; an earlier pass
    # built them with hand-composed rotations and every leaf ended up inside the
    # body, invisible.
    leaves = []
    fan = (
        (( 0.00, 0.62, 0.78), 0.268, 0.104),
        (( 0.46, 0.58, 0.68), 0.248, 0.098),
        ((-0.46, 0.58, 0.68), 0.248, 0.098),
        (( 0.80, 0.44, 0.42), 0.216, 0.090),
        ((-0.80, 0.44, 0.42), 0.216, 0.090),
    )
    for i, (direction, ln, wd) in enumerate(fan):
        # Thicker than the reference silhouette suggests on purpose: a 6 mm blade
        # photographs as a paper card, and it is too thin to survive the weld
        # pass's 2.7 mm voxel with any volume left.
        lf = C.leaf_blade("Oddish_Leaf_%d" % i, length=ln, width=wd,
                          thickness=0.0135, curl=0.26, segments=8, taper=2.2)
        C.paint(lf, lambda co, n, i2, _l=ln: C.mix(
            LEAF_DARK, C.mix(LEAF, LEAF_LIGHT, 0.55), C.smoothstep(co.y / _l)))
        B.place_along(lf, (0.0, 0.026, 0.272), direction)
        leaves.append(lf)

    grad = C.body_gradient(
        top=BODY_LIGHT, bottom=BODY_DARK, zmin=0.02, zmax=0.30,
        belly=BODY_LIGHT, belly_axis_y=0.24,
        patches=[(BODY, (0.0, -0.090, 0.190), 0.110, (1.3, 1.0, 1.3), 0.7)],
        noise_amt=0.016, seed=49)
    for ob in [body, head]:
        C.paint(ob, grad)

    C.carve_mouth(head, (0.0, -0.104, -0.030), width=0.062, height=0.030,
                  depth=0.016, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.012)

    face, eye_centres = B.simple_face(
        "Oddish", plan.head_co,
        head_size=plan.head_size, eye_angles=(26.0, 8.0), eye_radius=0.0225,
        eye_squash=(1.0, 0.60, 1.05), eye_tilt=2.0, eye_sink=0.92,
        mouth_angles=-18.0, mouth_width=0.104, mouth_curve=0.50,
        mouth_thickness=0.0080, face_bow=0.020, highlight=0.32)

    parts = [body, head] + feet + leaves + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0225,
        muzzle_y=-0.140,
        anchors=dict(muzzle=(0.0, -0.136, 0.160)),
        bevel_scale=0.008,
        albedo=dict(detail_scale=28.0, cavity=0.36, ao_strength=0.36, speckle=0.035,
                    voronoi_scale=11.0, voronoi_amount=0.08),
        normal=dict(detail_scale=44.0, bump=0.10, pattern_scale=12.0,
                    pattern_bump=0.06),
    )
