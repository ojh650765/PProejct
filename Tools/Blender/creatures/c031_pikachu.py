"""Species 31 - Pikachu. Electric, small biped.

Modelled from data/pokemon_images/002501.png. Proportions are *measured* off that
PNG rather than estimated - see the table below - because the first pass eyeballed
them and got a small ball head on a pear-shaped body, which is most of why it did
not read as Pikachu.

Measured off the reference (390 px art, 347 px from ear tip to sole):

    total standing height (ear tip -> sole)   347 px   ->  0.400 m
    head height, ears excluded                116 px   ->  0.134   (33% of total)
    head width                                162 px   ->  0.187   (wider than tall)
    ear length                                100 px   ->  0.115
    ear width at the base                      48 px   ->  0.055   (2.1 : 1)
    black ear tip                              45 px   ->  0.052   (45% of the ear)
    body height                               124 px   ->  0.143
    body width                                177 px   ->  0.204   (round, not pear)
    legs                                       30 px   ->  0.035   (stubby)
    cheek patch                                35 px   ->  0.040 across
    tail stroke width                          89 px   ->  0.103

The head is a third of the height and is *wider than it is tall*; the body is
wider than it is tall too. That single fact is most of the character.

Ears and tail are lofted solids with real thickness, not flat cards. The tail is
the identifying silhouette feature of the whole design and gets the largest share
of the modelling budget; it emerges low, off the base of the spine, and sweeps up
and back, rather than standing out of the middle of the back like a flag.

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

# sampled off 002501.png: body #f9dd82, cheek #df6f5d, tail root #916f40.
# The albedo is authored a step deeper than the flat art because the studio key
# clips a mid yellow to white.
YELLOW = C.hexcol('f2d067')
YELLOW_LIGHT = C.hexcol('f9e5a2')
YELLOW_DARK = C.hexcol('d3ae52')
BROWN = C.hexcol('8b6631')
BROWN_DARK = C.hexcol('6b4d24')
BLACK = C.hexcol('2b2724')
CHEEK = C.hexcol('dd5847')
MOUTH = C.hexcol('7a3340')

EAR_LEN = 0.115


def _ear(name, base, direction, sx):
    """A solid ear: broad at the base, tapering to a rounded black tip.

    The first pass used `spike()` squashed on one axis, which is a cone, and a
    cone photographs as a paper triangle. This is a loft with an authored width
    profile across the ear and a real thickness through it.
    """
    L = EAR_LEN
    st = [
        ((0.0, 0.0, -0.026), 0.030, 0.019, 2.2),   # buried in the skull
        ((0.0, 0.0,  0.002), 0.033, 0.019, 2.2),   # widest, just clear of it
        ((0.0, 0.0,  0.030), 0.029, 0.017, 2.2),
        ((0.0, 0.0,  0.056), 0.024, 0.014, 2.2),
        ((0.0, 0.0,  0.080), 0.017, 0.010, 2.2),
        ((0.0, 0.0,  0.097), 0.009, 0.006, 2.2),
    ]
    ob = C.loft_path(name, st, segments=12, resample=1, round_start=0.0,
                     round_end=0.9)
    # black over the top 45% of the ear, the way the reference splits it
    C.paint(ob, lambda co, n, i: C.mix(
        YELLOW, BLACK, C.smoothstep((co.z - L * 0.50) / (L * 0.10))))
    B.place_along(ob, base, direction, local_axis=(0, 0, 1))
    return ob


def _bolt_tail(name, base):
    """The lightning bolt as a solid.

    Half-thickness runs at roughly 40% of the stroke half-width, so the section
    is a rounded slab rather than a card: it still reads flat from the side, as
    the design wants, but it has a silhouette from every other angle.
    """
    pts = [
        (0.000, 0.046, 0.052),   # rooted inside the rump
        (0.000, 0.084, 0.098),
        (0.000, 0.058, 0.148),
        (0.000, 0.122, 0.174),
        (0.000, 0.082, 0.226),
        (0.000, 0.152, 0.250),
        (0.000, 0.114, 0.306),
    ]
    st = []
    n = len(pts)
    for i, p in enumerate(pts):
        t = i / float(n - 1)
        half_w = 0.020 + 0.030 * t          # stroke, in the tail's own plane
        half_t = 0.021 - 0.004 * t          # through-thickness
        st.append(((base[0] + p[0], base[1] + p[1], base[2] + p[2]),
                   half_t, half_w, 2.6))
    ob = C.loft_path(name, st, segments=14, resample=1, round_start=0.0,
                     round_end=0.55)
    z0 = base[2] + pts[0][2]
    C.paint(ob, lambda co, n, i: C.mix(
        BROWN, YELLOW, C.smoothstep((co.z - z0 - 0.030) / 0.036)))
    return ob


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Pikachu_Body")

    # Round body, wider than tall, on very short legs. Measured, not guessed.
    plan.spine = [
        ((0.000,  0.014, 0.048), 0.086, 0.078),   # hips
        ((0.000, -0.004, 0.100), 0.102, 0.092),   # widest
        ((0.000, -0.010, 0.150), 0.092, 0.084),   # chest
        ((0.000, -0.008, 0.186), 0.072, 0.066),   # runs straight into the head
    ]
    plan.arms = [
        ((0.070, -0.006, 0.152), 0.030, 0.030),
        ((0.104, -0.022, 0.126), 0.025, 0.025),
        ((0.128, -0.036, 0.108), 0.022, 0.022),
    ]
    plan.legs = [
        ((0.042,  0.006, 0.052), 0.036, 0.036),
        ((0.046, -0.004, 0.028), 0.030, 0.030),
        ((0.048, -0.012, 0.012), 0.026, 0.022),
    ]
    plan.tail = []
    plan.head_co = Vector((0.0, -0.012, 0.246))
    plan.head_size = (0.187, 0.170, 0.140)
    plan.subsurf = 0

    # Dense on purpose - the weld discards this mesh, and its only jobs are to
    # voxelise faithfully and to carry the back stripes across to the welded
    # skin. Coarse quads here turn a stripe into a smear.
    body = plan.build_body(method='loft', torso_segments=24, limb_segments=13,
                           torso_square=2.1, resample=4, limb_resample=2,
                           round_torso_front=0.85, round_torso_back=0.85)
    C.noise_displace(body, 46.0, 0.0004, seed=31)

    head = B.sculpt_head(
        "Pikachu_Head", *plan.head_size, cuts=3,
        snout_len=0.13, snout_drop=0.02, snout_narrow=0.80, snout_z=-0.28,
        snout_blunt=0.80, crown=0.040, crown_back=0.02,
        brow=0.028, brow_z=0.18, brow_y=-0.28, brow_x=0.34,
        cheek=0.075, cheek_z=-0.10, jaw_width=1.10, chin=0.0,
        top_flat=0.10, subsurf=1)
    B.head_place(head, plan.head_co)

    ears = []
    for sx in (1.0, -1.0):
        ears.append(_ear("Pikachu_Ear_%s" % ('L' if sx > 0 else 'R'),
                         base=tuple(plan.head_co + Vector((0.046 * sx, 0.016,
                                                           0.046))),
                         direction=(0.30 * sx, 0.15, 0.94), sx=sx))

    feet = B.paw_pair("Pikachu_Foot", (0.052, -0.030, 0.016),
                      length=0.062, width=0.046, height=0.028, toes=3,
                      toe_scale=0.62, spread=0.95, cuts=3)
    hands = B.paw_pair("Pikachu_Hand", (0.136, -0.048, 0.102),
                       length=0.040, width=0.032, height=0.028, toes=3,
                       toe_scale=0.62, spread=0.95, cuts=2, subsurf=0)

    tail = _bolt_tail("Pikachu_Tail", (0.0, 0.030, 0.040))

    grad = C.body_gradient(
        top=YELLOW, bottom=YELLOW_DARK, zmin=0.0, zmax=0.34,
        belly=YELLOW_LIGHT, belly_axis_y=0.16,
        patches=[
            # The two brown stripes across the back - a signature marking. The y
            # ellipse is generous so the band wraps round the curve of the back
            # instead of fading out at the flanks, and the edge is soft enough
            # that a 6 mm quad grid does not turn the boundary into a staircase.
            (BROWN, (0.0, 0.074, 0.140), 0.050, (2.40, 1.50, 0.30), 0.42),
            (BROWN, (0.0, 0.070, 0.104), 0.046, (2.40, 1.50, 0.28), 0.42),
            (BROWN_DARK, (0.0, 0.062, 0.072), 0.038, (2.00, 1.50, 0.26), 0.48),
        ],
        noise_amt=0.012, seed=31)
    for ob in [body] + feet + hands:
        C.paint(ob, grad)
    C.paint(head, C.body_gradient(top=YELLOW, bottom=YELLOW_LIGHT,
                                 zmin=0.18, zmax=0.32, belly=YELLOW_LIGHT,
                                 belly_axis_y=0.28, noise_amt=0.010, seed=32))

    face, eye_centres = B.simple_face(
        "Pikachu", plan.head_co,
        head_size=plan.head_size, eye_angles=(34.0, 9.0), eye_radius=0.0215,
        eye_squash=(1.0, 0.58, 1.12), eye_tilt=3.0, eye_sink=1.00,
        mouth_width=0.0, highlight=0.34,
        blush=CHEEK, blush_angles=(60.0, -24.0), blush_radius=0.0270)

    mouth = C.drape_line("Pikachu_Mouth", head, plan.head_co,
                         look=(0, -1, -0.38), yaw_span=17.0, pitch=-8.0,
                         curve=15.0, thickness=0.0052, color=MOUTH, samples=13)

    parts = [body, head] + ears + feet + hands + [tail] + face + [mouth]

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0215,
        ear_pts=[(0.046, 0.006, 0.292), (0.076, 0.032, 0.396)],
        muzzle_y=-0.104,
        anchors=dict(muzzle=(0.0, -0.102, 0.228)),
        bevel_scale=0.009,
        albedo=dict(detail_scale=40.0, cavity=0.32, ao_strength=0.32, speckle=0.035,
                    voronoi_scale=16.0, voronoi_amount=0.07),
        normal=dict(detail_scale=60.0, bump=0.10, pattern_scale=18.0,
                    pattern_bump=0.06),
    )
