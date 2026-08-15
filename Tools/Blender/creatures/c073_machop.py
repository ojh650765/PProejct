"""Species 73 - Machop. Fighting, muscular biped.

Modelled from data/pokemon_images/006601.png: a grey-blue muscular biped with
three brown ridges running back over the crown, heavy shoulders and thighs,
defined pectoral and abdominal grooves, big blocky fists, and a short tail.

Muscle definition is built as mass (pectorals, abs, biceps, hamstrings pushed out
with falloff bulges) rather than cut in. Eyes use the cast-wide simple dark oval
idiom.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 73
NAME = "Machop"
TYPES = ["Fighting"]
HEIGHT = 0.80
SIZE_AXIS = 'z'
DESIGN = "grey-blue muscular fighter with three brown crown ridges"
PROFILE = dict(tempo=0.75, weight=1.55, bounce=0.7, sway=0.85, stride=0.9)

SKIN = C.hexcol('98b4bc')
SKIN_LIGHT = C.hexcol('bad4de')
SKIN_DARK = C.hexcol('6f8992')
RIDGE = C.hexcol('b9a184')
RIDGE_DARK = C.hexcol('8d7862')
MOUTH = C.hexcol('a8506a')
GROOVE = C.hexcol('7b959e')


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Machop_Body")

    # heavy shoulders tapering to a narrow waist, then wide hips
    plan.spine = [
        ((0.000,  0.020, 0.320), 0.098, 0.086),   # hips
        ((0.000,  0.004, 0.400), 0.084, 0.076),   # waist
        ((0.000, -0.008, 0.478), 0.108, 0.092),   # chest
        ((0.000, -0.006, 0.548), 0.070, 0.062),   # neck
    ]
    plan.arms = [
        ((0.084, -0.006, 0.528), 0.058, 0.058),
        ((0.158, -0.010, 0.474), 0.048, 0.048),
        ((0.212, -0.030, 0.408), 0.040, 0.040),
        ((0.238, -0.044, 0.376), 0.036, 0.036),
    ]
    plan.legs = [
        ((0.070,  0.010, 0.306), 0.070, 0.070),
        ((0.078, -0.004, 0.196), 0.054, 0.054),
        ((0.082, -0.014, 0.072), 0.046, 0.042),
    ]
    plan.tail = [
        ((0.000,  0.088, 0.320), 0.034, 0.032),
        ((0.000,  0.140, 0.288), 0.022, 0.021),
        ((0.000,  0.172, 0.252), 0.010, 0.010),
    ]
    plan.head_co = Vector((0.0, -0.024, 0.630))
    plan.head_size = (0.176, 0.196, 0.166)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=18, limb_segments=12,
                           torso_square=2.2, resample=2, limb_resample=2,
                           round_torso_front=0.8, round_torso_back=0.8)
    # pectorals, deltoids and thighs as real mass
    for sx in (1, -1):
        C.bulge(body, (0.052 * sx, -0.086, 0.482), 0.072, 0.014,
                direction=(0.25 * sx, -1, 0.1))
        C.bulge(body, (0.096 * sx, -0.010, 0.520), 0.070, 0.012,
                direction=(sx, 0, 0.35))
        C.bulge(body, (0.080 * sx, -0.040, 0.280), 0.076, 0.012,
                direction=(0.5 * sx, -0.8, 0))
    C.bulge(body, (0.0, -0.076, 0.398), 0.062, 0.008, direction=(0, -1, 0),
            ellipse=(1.5, 1.0, 1.5))

    # Muscle as mass, not as cut grooves. An earlier pass used inset-and-push
    # recesses here; because the picked faces formed one contiguous block the
    # inset produced a single rectangular panel on the chest, which read as a
    # plate glued on. Bulges give the same read with no artefact.
    for sx in (1, -1):
        C.bulge(body, (0.040 * sx, -0.092, 0.492), 0.058, 0.012,
                direction=(0.2 * sx, -1, 0.15))          # pectorals
        C.bulge(body, (0.030 * sx, -0.086, 0.404), 0.040, 0.008,
                direction=(0.15 * sx, -1, 0))            # upper abs
        C.bulge(body, (0.028 * sx, -0.084, 0.366), 0.036, 0.007,
                direction=(0.15 * sx, -1, 0))            # lower abs
        C.bulge(body, (0.130 * sx, -0.020, 0.500), 0.052, 0.010,
                direction=(sx, -0.2, 0.3))               # biceps
        C.bulge(body, (0.076 * sx, 0.010, 0.246), 0.062, 0.010,
                direction=(0.6 * sx, 0.3, 0))            # hamstrings
    C.noise_displace(body, 30.0, 0.0005, seed=73)

    head = B.sculpt_head(
        "Machop_Head", *plan.head_size, cuts=3,
        snout_len=0.24, snout_drop=0.06, snout_narrow=0.58, snout_z=-0.26,
        snout_blunt=0.52, crown=0.030, crown_back=0.04,
        brow=0.055, brow_z=0.16, brow_y=-0.28, brow_x=0.34,
        cheek=0.045, cheek_z=-0.10, jaw_width=1.06, chin=0.030,
        top_flat=0.30, subsurf=1)
    B.head_place(head, plan.head_co)

    # three brown ridges sweeping back over the crown
    ridges = []
    for i, (sx, off) in enumerate(((0.0, 0.0), (1.0, 0.036), (-1.0, 0.036))):
        st = []
        for (px, py, pz), hw, hh in (
                ((0.030 * sx, -0.052 + off * 0.20, 0.080), 0.019, 0.013),
                ((0.032 * sx, 0.010 + off * 0.20, 0.098), 0.021, 0.017),
                ((0.030 * sx, 0.066 + off * 0.20, 0.086), 0.018, 0.015),
                ((0.026 * sx, 0.112 + off * 0.20, 0.048), 0.011, 0.009)):
            st.append(((plan.head_co.x + px, plan.head_co.y + py,
                        plan.head_co.z + pz), hw, hh, 2.6))
        r = C.loft_path("Machop_Ridge_%d" % i, st, segments=10, resample=2,
                        round_start=0.7, round_end=0.6)
        C.paint(r, lambda co, n, i2: C.mix(RIDGE, RIDGE_DARK,
                                           C.smoothstep((co.y - plan.head_co.y)
                                                        / 0.13)))
        ridges.append(r)

    fists = []
    for sx in (1.0, -1.0):
        f = C.spherified_cube("Machop_Fist_%s" % ('L' if sx > 0 else 'R'),
                              cuts=3, radius=0.5)
        C.deform(f, lambda co: Vector((co.x * 0.078, co.y * 0.086, co.z * 0.080)))
        for k in range(4):
            u = (k / 3.0) * 2.0 - 1.0
            C.bulge(f, (u * 0.020, -0.034, 0.014), 0.030, 0.010,
                    direction=(0, -1, 0.25))
        C.place(f, location=(0.252 * sx, -0.058, 0.354),
                rotation=(0, 0, math.radians(-14 * sx)))
        fists.append(f)

    feet = B.paw_pair("Machop_Foot", (0.086, -0.048, 0.036),
                      length=0.104, width=0.070, height=0.052, toes=3,
                      toe_scale=0.38, spread=0.85, cuts=3)

    grad = C.body_gradient(
        top=SKIN, bottom=SKIN_LIGHT, zmin=0.02, zmax=0.72,
        belly=SKIN_LIGHT, belly_axis_y=0.46,
        patches=[(SKIN_DARK, (0.0, 0.080, 0.440), 0.100, (1.5, 1.2, 1.8), 0.7)],
        noise_amt=0.014, seed=73)
    for ob in [body, head] + fists + feet:
        C.paint(ob, grad)

    C.carve_mouth(head, (0.0, -0.100, -0.034), width=0.076, height=0.026,
                  depth=0.014, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.010)

    face, eye_centres = B.simple_face(
        "Machop", plan.head_co,
        head_size=plan.head_size, eye_angles=(28.0, 9.0), eye_radius=0.0270,
        eye_squash=(1.0, 0.58, 1.10), eye_tilt=8.0, eye_sink=0.90,
        mouth_angles=-24.0, mouth_width=0.096, mouth_curve=0.34,
        mouth_thickness=0.0070, face_bow=0.016, highlight=0.30)

    parts = [body, head] + ridges + fists + feet + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0270,
        muzzle_y=-0.150,
        anchors=dict(muzzle=(0.0, -0.146, 0.604)),
        bevel_scale=0.008,
        albedo=dict(detail_scale=26.0, cavity=0.42, ao_strength=0.42, speckle=0.035,
                    voronoi_scale=9.0, voronoi_amount=0.08),
        normal=dict(detail_scale=40.0, bump=0.12, pattern_scale=9.0,
                    pattern_bump=0.07),
    )
