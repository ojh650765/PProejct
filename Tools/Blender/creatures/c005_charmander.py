"""Species 5 - Charmander. Fire, biped with the flame burning at the tail tip.

Modelled from data/pokemon_images/000401.png: upright orange biped with a cream
belly and underside, a rounded head with a short blunt snout and a small open
mouth, short arms with three pale claws, chunky three-toed feet, and a thick
tapering tail that curves up to a flame.

Eyes use the cast-wide simple dark oval idiom. Colours sampled from the reference
(Tools/Blender/ref_palettes.json).
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 5
NAME = "Charmander"
TYPES = ["Fire"]
HEIGHT = 0.60
SIZE_AXIS = 'z'
DESIGN = "upright orange fire biped with cream belly and a flame-tipped tail"
PROFILE = dict(tempo=1.05, weight=0.9, bounce=1.1, sway=1.0, stride=1.0)

SKIN = C.hexcol('ef9f65')
SKIN_LIGHT = C.hexcol('f4b98a')
SKIN_DARK = C.hexcol('c87c48')
BELLY = C.hexcol('f2e2c6')
CLAW = C.hexcol('e8e4dc')
MOUTH = C.hexcol('7d3a3f')
FLAME_HOT = C.hexcol('ffde74')
FLAME_COOL = C.hexcol('f2622a')


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Charmander_Body")

    # upright, pot-bellied, short-legged. faces -Y.
    plan.spine = [
        ((0.000,  0.012, 0.196), 0.088, 0.086),   # hips
        ((0.000, -0.006, 0.264), 0.100, 0.098),   # belly, the widest point
        ((0.000, -0.012, 0.330), 0.092, 0.088),   # chest
        ((0.000, -0.008, 0.386), 0.058, 0.056),   # neck
    ]
    plan.arms = [
        ((0.052, -0.018, 0.336), 0.036, 0.036),
        ((0.096, -0.036, 0.302), 0.028, 0.028),
        ((0.130, -0.058, 0.268), 0.024, 0.024),
        ((0.150, -0.072, 0.252), 0.021, 0.021),
    ]
    plan.legs = [
        ((0.052,  0.006, 0.186), 0.052, 0.052),
        ((0.062, -0.006, 0.116), 0.044, 0.044),
        ((0.066, -0.012, 0.046), 0.040, 0.036),
    ]
    # thick tail sweeping back then up - the flame has to sit on something
    plan.tail = [
        ((0.000,  0.070, 0.196), 0.078, 0.076),
        ((0.000,  0.158, 0.160), 0.064, 0.062),
        ((0.000,  0.244, 0.156), 0.050, 0.049),
        ((0.000,  0.312, 0.204), 0.038, 0.037),
        ((0.000,  0.344, 0.286), 0.026, 0.026),
        ((0.000,  0.348, 0.360), 0.016, 0.016),
    ]
    plan.head_co = Vector((0.0, -0.020, 0.470))
    plan.head_size = (0.176, 0.196, 0.166)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=16, limb_segments=12,
                           torso_square=2.1, resample=2, limb_resample=1,
                           round_torso_front=0.85, round_torso_back=0.8)
    # push the belly forward and flatten the back
    C.bulge(body, (0, -0.090, 0.262), 0.11, 0.016, direction=(0, -1, 0),
            ellipse=(1.2, 1.0, 1.3))
    C.noise_displace(body, 34.0, 0.0004, seed=7)

    head = B.sculpt_head(
        "Charmander_Head", *plan.head_size, cuts=3,
        snout_len=0.34, snout_drop=0.07, snout_narrow=0.50, snout_z=-0.26,
        snout_blunt=0.45, crown=0.060, crown_back=0.03,
        brow=0.040, brow_z=0.16, brow_y=-0.30, brow_x=0.34,
        cheek=0.045, cheek_z=-0.08, jaw_width=1.04, chin=0.02,
        top_flat=0.20, subsurf=1)
    # explicit muzzle mass - the reference face is not a ball
    C.bulge(head, (0.0, -0.074, -0.024), 0.060, 0.024, direction=(0, -1, -0.18),
            ellipse=(1.20, 1.0, 0.80))
    B.head_place(head, plan.head_co)

    feet = B.paw_pair("Charmander_Foot", (0.070, -0.030, 0.026),
                      length=0.088, width=0.062, height=0.038, toes=3,
                      toe_scale=0.40, spread=0.80, cuts=3)
    hands = B.paw_pair("Charmander_Hand", (0.156, -0.080, 0.246),
                       length=0.042, width=0.032, height=0.026, toes=3,
                       toe_scale=0.44, spread=0.85, cuts=2, subsurf=0)

    grad = C.body_gradient(
        top=SKIN, bottom=SKIN_LIGHT, zmin=0.02, zmax=0.52,
        belly=None,
        patches=[
            # the cream front runs from throat to groin
            (BELLY, (0.0, -0.082, 0.246), 0.098, (1.15, 1.0, 1.35), 0.40),
            (BELLY, (0.0, -0.062, 0.190), 0.070, (1.3, 1.0, 1.2), 0.45),
            (SKIN_DARK, (0.0, 0.140, 0.196), 0.075, (1.0, 1.9, 0.9), 0.6),
        ],
        noise_amt=0.014, seed=5)
    for ob in [body, head] + feet + hands:
        C.paint(ob, grad)

    C.carve_mouth(head, (0.0, -0.098, -0.036), width=0.086, height=0.030,
                  depth=0.014, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.010)

    face, eye_centres = B.simple_face(
        "Charmander", plan.head_co,
        head_size=plan.head_size, eye_angles=(26.0, 6.0), eye_radius=0.0290,
        eye_squash=(1.0, 0.58, 1.20), eye_tilt=3.0, eye_sink=0.90,
        mouth_angles=-22.0, mouth_width=0.100, mouth_curve=0.46,
        mouth_thickness=0.0078, face_bow=0.018, highlight=0.32)

    flame = B.flame("Charmander_Flame", (0.0, 0.348, 0.368), height_=0.168,
                    width=0.094, lobes=4, tongues=3,
                    colour_hot=FLAME_HOT, colour_cool=FLAME_COOL)

    claws = []
    for sx in (1, -1):
        claws.extend(B.claw_set("Charmander_ToeClaw_%d" % sx,
                                (0.070 * sx, -0.072, 0.020), forward=(0, -1, 0),
                                count=3, length=0.017, radius=0.0062,
                                spread=0.019, drop=0.003, color=CLAW))
        claws.extend(B.claw_set("Charmander_HandClaw_%d" % sx,
                                (0.160 * sx, -0.098, 0.244),
                                forward=(0.35 * sx, -0.90, -0.2), count=3,
                                length=0.014, radius=0.0044, spread=0.013,
                                drop=0.002, color=CLAW))

    parts = [body, head] + feet + hands + face + flame + claws

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0272,
        muzzle_y=-0.140,
        anchors=dict(muzzle=(0.0, -0.136, 0.446)),
        bevel_scale=0.009,
        albedo=dict(detail_scale=30.0, cavity=0.34, ao_strength=0.34, speckle=0.035,
                    voronoi_scale=11.0, voronoi_amount=0.07),
        normal=dict(detail_scale=48.0, bump=0.09, pattern_scale=11.0,
                    pattern_bump=0.05),
    )
