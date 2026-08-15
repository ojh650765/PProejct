"""Species 25 - Rattata. Normal, small quadruped rodent.

Modelled from data/pokemon_images/001901.png: low purple rodent with a cream
belly and muzzle, a pointed snout with two prominent white incisors, big rounded
ears with pale inners, long stiff whiskers, and a long thin tail that curls at
the tip.

Eyes use the cast-wide simple dark oval idiom.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 25
NAME = "Rattata"
TYPES = ["Normal"]
HEIGHT = 0.30
SIZE_AXIS = 'z'
DESIGN = "low purple rodent with big ears, buck teeth and a curling tail"
PROFILE = dict(tempo=1.45, weight=0.6, bounce=1.15, sway=1.1, stride=1.25)

FUR = C.hexcol('9e85b9')
FUR_LIGHT = C.hexcol('b8a2c9')
FUR_DARK = C.hexcol('735f87')
BELLY = C.hexcol('e0cfbd')
EAR_IN = C.hexcol('cbb7a6')
TOOTH = C.hexcol('fbf7ec')
MOUTH = C.hexcol('6d2f3c')
WHISKER = C.hexcol('efe7d8')
FOOT = C.hexcol('d6c3ae')


def build():
    plan = B.CreaturePlan('quadruped', HEIGHT, name="Rattata_Body")

    plan.spine = [
        ((0.000,  0.128, 0.126), 0.044, 0.044),
        ((0.000,  0.070, 0.142), 0.074, 0.070),
        ((0.000, -0.006, 0.146), 0.082, 0.076),
        ((0.000, -0.078, 0.140), 0.070, 0.066),
        ((0.000, -0.126, 0.140), 0.048, 0.046),
    ]
    plan.arms = [
        ((0.044, -0.076, 0.116), 0.026, 0.026),
        ((0.052, -0.084, 0.070), 0.020, 0.020),
        ((0.056, -0.090, 0.024), 0.019, 0.016),
    ]
    plan.legs = [
        ((0.050,  0.078, 0.118), 0.032, 0.032),
        ((0.058,  0.086, 0.068), 0.024, 0.024),
        ((0.062,  0.080, 0.024), 0.022, 0.018),
    ]
    # long thin tail that curls at the tip
    tail = []
    for i in range(9):
        t = i / 8.0
        ang = max(0.0, (t - 0.55) / 0.45) * math.pi * 1.7
        base_y = 0.150 + t * 0.150
        base_z = 0.138 + t * 0.090
        cy = base_y - math.sin(ang) * 0.032 * (t > 0.55)
        cz = base_z + (1.0 - math.cos(ang)) * 0.030 * (t > 0.55)
        tail.append(((0.0, cy, cz), 0.016 * (1.0 - 0.68 * t),
                     0.016 * (1.0 - 0.68 * t)))
    plan.tail = tail
    plan.head_co = Vector((0.0, -0.170, 0.146))
    plan.head_size = (0.098, 0.126, 0.096)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=15, limb_segments=10,
                           torso_square=2.05, resample=2, limb_resample=1,
                           round_torso_front=0.8, round_torso_back=0.85)
    C.noise_displace(body, 60.0, 0.0003, seed=25)

    head = B.sculpt_head(
        "Rattata_Head", *plan.head_size, cuts=3,
        snout_len=0.40, snout_drop=0.10, snout_narrow=0.40, snout_z=-0.24,
        snout_blunt=0.30, crown=0.045, crown_back=0.02,
        brow=0.035, brow_z=0.16, brow_y=-0.26, brow_x=0.34,
        cheek=0.040, cheek_z=-0.08, jaw_width=1.02, chin=0.0,
        top_flat=0.12, subsurf=1)
    B.head_place(head, plan.head_co)

    # big rounded ears - flattened discs, not spikes
    ears = []
    for sx in (1.0, -1.0):
        outer = C.spherified_cube("Rattata_Ear_%s" % ('L' if sx > 0 else 'R'),
                                  cuts=2, radius=0.5)
        C.deform(outer, lambda co: Vector((co.x * 0.052, co.y * 0.020, co.z * 0.058)))
        C.paint_flat(outer, FUR)
        C.place(outer, location=tuple(plan.head_co
                                      + Vector((0.048 * sx, 0.046, 0.062))),
                rotation=(math.radians(-16), math.radians(-24 * sx), 0))
        inner = C.spherified_cube("Rattata_EarIn_%s" % ('L' if sx > 0 else 'R'),
                                  cuts=2, radius=0.5)
        C.deform(inner, lambda co: Vector((co.x * 0.034, co.y * 0.012, co.z * 0.038)))
        C.paint_flat(inner, EAR_IN)
        C.place(inner, location=tuple(plan.head_co
                                      + Vector((0.048 * sx - 0.008 * sx, 0.034, 0.062))),
                rotation=(math.radians(-16), math.radians(-24 * sx), 0))
        ears.extend([outer, inner])

    # the buck teeth are the single most recognisable feature
    teeth = []
    for sx in (1.0, -1.0):
        t = C.spherified_cube("Rattata_Tooth_%s" % ('L' if sx > 0 else 'R'),
                              cuts=1, radius=0.5)
        C.deform(t, lambda co: Vector((co.x * 0.014, co.y * 0.010, co.z * 0.030)))
        C.paint_flat(t, TOOTH)
        C.place(t, location=tuple(plan.head_co + Vector((0.011 * sx, -0.070, -0.040))),
                rotation=(math.radians(6), 0, 0))
        teeth.append(t)

    whiskers = []
    for sx in (1.0, -1.0):
        for i, (ang, ln) in enumerate(((14.0, 0.088), (-4.0, 0.080))):
            whiskers.append(B.spike(
                "Rattata_Whisker_%s_%d" % ('L' if sx > 0 else 'R', i),
                base=tuple(plan.head_co + Vector((0.020 * sx, -0.052, -0.006))),
                direction=(0.86 * sx, -0.40, math.sin(math.radians(ang))),
                length=ln, radius=0.0062, curve=(0, 0, -0.010), samples=5,
                ring=6, sharp=1.6, color=WHISKER))

    feet = B.paw_pair("Rattata_Foot_F", (0.058, -0.104, 0.012),
                      length=0.038, width=0.026, height=0.016, toes=3,
                      toe_scale=0.50, spread=0.9, cuts=2, subsurf=0)
    feet += B.paw_pair("Rattata_Foot_B", (0.064, 0.070, 0.012),
                       length=0.042, width=0.028, height=0.016, toes=3,
                       toe_scale=0.50, spread=0.9, cuts=2, subsurf=0)
    for f in feet:
        C.paint_flat(f, FOOT)

    grad = C.body_gradient(
        top=FUR, bottom=FUR_LIGHT, zmin=0.0, zmax=0.24,
        belly=BELLY, belly_axis_y=0.16,
        patches=[
            (BELLY, (0.0, 0.010, 0.086), 0.085, (1.2, 2.0, 0.9), 0.5),
            (BELLY, (0.0, -0.196, 0.116), 0.048, (1.1, 1.3, 0.9), 0.5),
            (FUR_DARK, (0.0, 0.060, 0.190), 0.058, (1.5, 1.8, 0.7), 0.6),
        ],
        noise_amt=0.018, seed=25)
    for ob in [body, head]:
        C.paint(ob, grad)

    C.carve_mouth(head, (0.0, -0.056, -0.032), width=0.040, height=0.020,
                  depth=0.012, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.004)

    face, eye_centres = B.simple_face(
        "Rattata", plan.head_co,
        head_size=plan.head_size, eye_angles=(34.0, 12.0), eye_radius=0.0165,
        eye_squash=(1.0, 0.58, 1.10), eye_tilt=6.0, eye_sink=0.90,
        mouth_angles=-26.0, mouth_width=0.050, mouth_curve=0.30,
        mouth_thickness=0.0042, face_bow=0.008, highlight=0.32)

    parts = [body, head] + ears + teeth + whiskers + feet + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0165,
        ear_pts=[(0.048, -0.124, 0.208), (0.070, -0.100, 0.252)],
        muzzle_y=-0.246,
        anchors=dict(muzzle=(0.0, -0.250, 0.128)),
        bevel_scale=0.010,
        albedo=dict(detail_scale=48.0, cavity=0.34, ao_strength=0.34, speckle=0.045,
                    voronoi_scale=20.0, voronoi_amount=0.09),
        normal=dict(detail_scale=76.0, bump=0.12, pattern_scale=26.0,
                    pattern_bump=0.07),
    )
