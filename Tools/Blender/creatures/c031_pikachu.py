"""Species 31 - Pikachu. Electric, small biped.

Modelled from data/pokemon_images/002501.png: chubby yellow biped with long
black-tipped ears, round red cheek pouches, two brown stripes across the back,
short arms and feet, and a flat zigzag lightning-bolt tail with a brown base.

Eyes use the cast-wide simple dark oval idiom, which is close to the reference's
own eyes anyway.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 31
NAME = "Pikachu"
TYPES = ["Electric"]
HEIGHT = 0.40
SIZE_AXIS = 'z'
DESIGN = "chubby yellow biped with black-tipped ears, red cheeks and a bolt tail"
PROFILE = dict(tempo=1.35, weight=0.7, bounce=1.3, sway=1.1, stride=1.15)

YELLOW = C.hexcol('f5d872')
YELLOW_LIGHT = C.hexcol('fbeaa8')
YELLOW_DARK = C.hexcol('d8b95c')
BROWN = C.hexcol('8a6a3a')
BROWN_DARK = C.hexcol('6b5029')
BLACK = C.hexcol('2b2724')
CHEEK = C.hexcol('e2554a')
MOUTH = C.hexcol('7d3a48')


def _bolt_tail(name, base, length, width, thickness):
    """The lightning-bolt tail: a flat zigzag lofted along a stepped path, with a
    brown wedge at the root the way the reference draws it."""
    st = []
    pts = [
        (0.000, 0.010, 0.000),
        (0.000, 0.052, 0.034),
        (0.000, 0.020, 0.086),
        (0.000, 0.086, 0.104),
        (0.000, 0.048, 0.168),
        (0.000, 0.126, 0.186),
        (0.000, 0.096, 0.256),
    ]
    for i, p in enumerate(pts):
        t = i / float(len(pts) - 1)
        w = width * (0.42 + 0.58 * t)
        st.append(((base[0] + p[0], base[1] + p[1] * length / 0.256,
                    base[2] + p[2] * length / 0.256),
                   thickness * (1.0 - 0.25 * t), w, 3.2))
    ob = C.loft_path(name, st, segments=12, resample=1, round_start=0.5,
                     round_end=0.15)
    C.paint(ob, lambda co, n, i: C.mix(BROWN, YELLOW,
                                       C.smoothstep((co.z - base[2]) / (length * 0.34))))
    return ob


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Pikachu_Body")

    plan.spine = [
        ((0.000,  0.014, 0.114), 0.068, 0.062),
        ((0.000, -0.002, 0.166), 0.076, 0.072),
        ((0.000, -0.008, 0.212), 0.066, 0.062),
        ((0.000, -0.006, 0.246), 0.048, 0.046),
    ]
    plan.arms = [
        ((0.044, -0.008, 0.216), 0.024, 0.024),
        ((0.078, -0.026, 0.196), 0.019, 0.019),
        ((0.100, -0.042, 0.180), 0.016, 0.016),
    ]
    plan.legs = [
        ((0.038,  0.008, 0.106), 0.034, 0.034),
        ((0.044, -0.004, 0.064), 0.027, 0.027),
        ((0.048, -0.012, 0.026), 0.024, 0.020),
    ]
    plan.tail = []
    plan.head_co = Vector((0.0, -0.016, 0.310))
    plan.head_size = (0.150, 0.150, 0.134)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=16, limb_segments=11,
                           torso_square=2.1, resample=2, limb_resample=1,
                           round_torso_front=0.85, round_torso_back=0.85)
    C.noise_displace(body, 46.0, 0.0004, seed=31)

    head = B.sculpt_head(
        "Pikachu_Head", *plan.head_size, cuts=3,
        snout_len=0.14, snout_drop=0.02, snout_narrow=0.74, snout_z=-0.24,
        snout_blunt=0.74, crown=0.045, crown_back=0.02,
        brow=0.030, brow_z=0.18, brow_y=-0.28, brow_x=0.32,
        cheek=0.060, cheek_z=-0.08, jaw_width=1.06, chin=0.0,
        top_flat=0.12, subsurf=1)
    C.bulge(head, (0.0, -0.062, -0.020), 0.052, 0.014, direction=(0, -1, -0.15),
            ellipse=(1.30, 1.0, 0.85))
    B.head_place(head, plan.head_co)

    # long ears with black tips
    ears = []
    for sx in (1.0, -1.0):
        e = B.spike("Pikachu_Ear_%s" % ('L' if sx > 0 else 'R'),
                    base=tuple(plan.head_co + Vector((0.044 * sx, 0.024, 0.052))),
                    direction=(0.30 * sx, 0.16, 0.94), length=0.150,
                    radius=0.040, curve=(0.016 * sx, 0.010, 0.0),
                    samples=8, ring=9, sharp=1.25)
        C.deform(e, lambda co: Vector((co.x, co.y * 0.62, co.z)))
        top = plan.head_co.z + 0.052 + 0.148
        C.paint(e, lambda co, n, i, _t=top: C.mix(
            YELLOW, BLACK, C.smoothstep((co.z - (_t - 0.062)) / 0.026)))
        ears.append(e)

    feet = B.paw_pair("Pikachu_Foot", (0.050, -0.030, 0.014),
                      length=0.056, width=0.038, height=0.022, toes=3,
                      toe_scale=0.46, spread=0.85, cuts=2, subsurf=0)
    hands = B.paw_pair("Pikachu_Hand", (0.106, -0.054, 0.174),
                       length=0.030, width=0.022, height=0.018, toes=3,
                       toe_scale=0.46, spread=0.85, cuts=2, subsurf=0)

    tail = _bolt_tail("Pikachu_Tail", (0.0, 0.056, 0.148), length=0.230,
                      width=0.052, thickness=0.013)

    grad = C.body_gradient(
        top=YELLOW, bottom=YELLOW_LIGHT, zmin=0.0, zmax=0.40,
        belly=YELLOW_LIGHT, belly_axis_y=0.28,
        patches=[
            # the two brown stripes across the back
            (BROWN, (0.0, 0.062, 0.196), 0.052, (2.6, 0.55, 0.75), 0.45),
            (BROWN, (0.0, 0.056, 0.146), 0.048, (2.6, 0.55, 0.70), 0.45),
            (BROWN_DARK, (0.0, 0.060, 0.126), 0.040, (2.0, 0.6, 0.6), 0.55),
        ],
        noise_amt=0.014, seed=31)
    for ob in [body] + feet + hands:
        C.paint(ob, grad)
    C.paint(head, C.body_gradient(top=YELLOW, bottom=YELLOW_LIGHT,
                                 zmin=0.24, zmax=0.40, belly=YELLOW_LIGHT,
                                 belly_axis_y=0.34, noise_amt=0.012, seed=32))

    C.carve_mouth(head, (0.0, -0.070, -0.030), width=0.052, height=0.026,
                  depth=0.012, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.010)

    face, eye_centres = B.simple_face(
        "Pikachu", plan.head_co,
        head_size=plan.head_size, eye_angles=(30.0, 10.0), eye_radius=0.0255,
        eye_squash=(1.0, 0.58, 1.12), eye_tilt=3.0, eye_sink=0.90,
        mouth_angles=-22.0, mouth_width=0.076, mouth_curve=0.44,
        mouth_thickness=0.0060, face_bow=0.014, highlight=0.34,
        blush=CHEEK, blush_angles=(58.0, -20.0), blush_radius=0.0245)

    parts = [body, head] + ears + feet + hands + [tail] + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0255,
        ear_pts=[(0.044, 0.008, 0.362), (0.070, 0.030, 0.470)],
        muzzle_y=-0.106,
        anchors=dict(muzzle=(0.0, -0.104, 0.292)),
        bevel_scale=0.009,
        albedo=dict(detail_scale=40.0, cavity=0.32, ao_strength=0.32, speckle=0.035,
                    voronoi_scale=16.0, voronoi_amount=0.07),
        normal=dict(detail_scale=60.0, bump=0.10, pattern_scale=18.0,
                    pattern_bump=0.06),
    )
