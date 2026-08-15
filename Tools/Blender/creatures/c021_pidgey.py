"""Species 21 - Pidgey. Normal / Flying, small round bird.

Modelled from data/pokemon_images/001601.png: plump cream-fronted bird with brown
wings and back, a swept brown crest over the eyes, a short pink hooked beak, a
dark bandit stripe through the eye, short brown tail feathers, and stubby pink
feet with pale claws.

Eyes use the cast-wide simple dark oval idiom.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 21
NAME = "Pidgey"
TYPES = ["Normal", "Flying"]
HEIGHT = 0.30
SIZE_AXIS = 'z'
DESIGN = "plump cream-and-brown bird with a swept crest and a pink beak"
PROFILE = dict(tempo=1.55, weight=0.55, bounce=1.35, sway=1.15, stride=1.2)

CREAM = C.hexcol('efe4b0')
CREAM_DARK = C.hexcol('d8c78f')
BROWN = C.hexcol('a5763f')
BROWN_DARK = C.hexcol('7d5426')
BROWN_LIGHT = C.hexcol('c49b67')
BEAK = C.hexcol('d5a0a8')
BEAK_DARK = C.hexcol('b07c86')
FOOT = C.hexcol('cf9aa2')
STRIPE = C.hexcol('3d352e')
CLAW = C.hexcol('e9e2d4')


def build():
    plan = B.CreaturePlan('avian', HEIGHT, name="Pidgey_Body")

    # one plump mass: a bird this small has no visible neck
    plan.spine = [
        ((0.000,  0.108, 0.128), 0.038, 0.038),   # tail root
        ((0.000,  0.062, 0.146), 0.070, 0.072),
        ((0.000,  0.004, 0.156), 0.086, 0.092),   # widest
        ((0.000, -0.052, 0.162), 0.078, 0.086),
        ((0.000, -0.088, 0.176), 0.056, 0.060),   # shoulders into the head
    ]
    plan.wings = [
        ((0.062, -0.020, 0.180), 0.030, 0.022),
        ((0.104,  0.016, 0.166), 0.024, 0.014),
        ((0.132,  0.058, 0.146), 0.014, 0.008),
    ]
    plan.legs = [
        ((0.034,  0.010, 0.096), 0.021, 0.021),
        ((0.037,  0.004, 0.058), 0.017, 0.017),
        ((0.039,  0.000, 0.022), 0.014, 0.014),
    ]
    plan.tail = [
        ((0.000,  0.124, 0.132), 0.034, 0.030),
        ((0.000,  0.176, 0.140), 0.026, 0.014),
        ((0.000,  0.222, 0.150), 0.016, 0.008),
    ]
    plan.head_co = Vector((0.0, -0.104, 0.216))
    plan.head_size = (0.116, 0.116, 0.108)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=16, limb_segments=10,
                           torso_square=2.05, resample=2, limb_resample=1,
                           round_torso_front=0.9, round_torso_back=0.55)
    C.noise_displace(body, 60.0, 0.0004, seed=21)

    head = B.sculpt_head(
        "Pidgey_Head", *plan.head_size, cuts=3,
        snout_len=0.10, snout_drop=0.02, snout_narrow=0.80, snout_z=-0.20,
        snout_blunt=0.80, crown=0.060, crown_back=0.02,
        brow=0.030, brow_z=0.18, brow_y=-0.30, brow_x=0.32,
        cheek=0.030, cheek_z=-0.06, jaw_width=1.0, chin=0.0,
        top_flat=0.10, subsurf=1)
    B.head_place(head, plan.head_co)

    # beak: two short lathed cones, the upper one hooked down
    beak_up = B.spike("Pidgey_BeakUpper",
                      base=tuple(plan.head_co + Vector((0.0, -0.036, -0.006))),
                      direction=(0, -1, -0.20), length=0.046, radius=0.020,
                      curve=(0, 0, -0.012), samples=6, ring=9, sharp=1.3,
                      color=BEAK)
    beak_lo = B.spike("Pidgey_BeakLower",
                      base=tuple(plan.head_co + Vector((0.0, -0.034, -0.020))),
                      direction=(0, -1, 0.02), length=0.030, radius=0.014,
                      curve=(0, 0, 0.004), samples=5, ring=8, sharp=1.4,
                      color=BEAK_DARK)

    # the swept crest is the strongest silhouette cue on this bird
    crest = []
    for i, (sx, ang, ln) in enumerate(((0.0, 0.0, 0.084), (1.0, 26.0, 0.072),
                                       (-1.0, -26.0, 0.072),
                                       (1.0, 48.0, 0.056), (-1.0, -48.0, 0.056))):
        c = B.spike("Pidgey_Crest_%d" % i,
                    base=tuple(plan.head_co + Vector((0.026 * sx, 0.020, 0.044))),
                    direction=(math.sin(math.radians(ang)) * 0.5, 0.62, 0.60),
                    length=ln, radius=0.016, curve=(0, 0.012, -0.014),
                    samples=6, ring=6, sharp=1.6, color=BROWN)
        C.paint(c, lambda co, n, i2: C.mix(BROWN_LIGHT, BROWN_DARK,
                                           C.smoothstep((co.y - 0.06) / 0.09)))
        crest.append(c)

    feet = B.paw_pair("Pidgey_Foot", (0.038, -0.014, 0.010),
                      length=0.040, width=0.026, height=0.014, toes=3,
                      toe_scale=0.55, spread=0.95, cuts=2, subsurf=0)
    for f in feet:
        C.paint_flat(f, FOOT)

    grad = C.body_gradient(
        top=BROWN, bottom=CREAM, zmin=0.02, zmax=0.28,
        belly=CREAM, belly_axis_y=0.20,
        patches=[
            # brown mantle over the back and wings, cream front
            (BROWN, (0.0, 0.056, 0.226), 0.130, (1.5, 1.6, 0.80), 0.55),
            (BROWN, (0.098, 0.006, 0.184), 0.090, (1.0, 1.7, 1.2), 0.55),
            (BROWN, (-0.098, 0.006, 0.184), 0.090, (1.0, 1.7, 1.2), 0.55),
            (BROWN_DARK, (0.0, 0.180, 0.142), 0.056, (1.4, 1.4, 0.9), 0.5),
            (CREAM, (0.0, -0.070, 0.140), 0.070, (1.3, 1.0, 1.3), 0.5),
        ],
        noise_amt=0.020, seed=21)
    C.paint(body, grad)

    head_grad = C.body_gradient(
        top=CREAM, bottom=CREAM, zmin=0.16, zmax=0.28,
        patches=[
            # the dark bandit stripe through the eye, and a brown cap
            (STRIPE, (0.050, -0.144, 0.224), 0.052, (1.6, 1.0, 0.50), 0.45),
            (STRIPE, (-0.050, -0.144, 0.224), 0.052, (1.6, 1.0, 0.50), 0.45),
            (BROWN, (0.0, -0.070, 0.262), 0.050, (1.6, 1.2, 0.7), 0.45),
        ],
        noise_amt=0.014, seed=22)
    C.paint(head, head_grad)

    face, eye_centres = B.simple_face(
        "Pidgey", plan.head_co,
        head_size=plan.head_size, eye_angles=(31.0, 7.0), eye_radius=0.0182,
        eye_squash=(1.0, 0.58, 1.10), eye_tilt=3.0, eye_sink=0.90,
        mouth_width=0.0, highlight=0.34)

    claws = []
    for sx in (1, -1):
        claws.extend(B.claw_set("Pidgey_Claw_%d" % sx,
                                (0.038 * sx, -0.030, 0.008), forward=(0, -1, 0),
                                count=3, length=0.011, radius=0.0034,
                                spread=0.010, drop=0.001, color=CLAW))

    parts = [body, head, beak_up, beak_lo] + crest + feet + face + claws

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0182,
        muzzle_y=-0.190,
        anchors=dict(muzzle=(0.0, -0.196, 0.206)),
        bevel_scale=0.011,
        albedo=dict(detail_scale=44.0, cavity=0.36, ao_strength=0.36, speckle=0.05,
                    voronoi_scale=18.0, voronoi_amount=0.10),
        normal=dict(detail_scale=70.0, bump=0.14, pattern_scale=22.0,
                    pattern_bump=0.09),
    )
