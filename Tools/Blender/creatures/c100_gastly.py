"""Species 100 - Gastly. Ghost / Poison, gas cloud with a core.

Modelled from data/pokemon_images/009201.png: a near-black purple-sheened sphere
surrounded by a much larger ragged purple gas cloud, with two large angular pale
eyes and a wide fanged grin.

This is the one creature that never touches the ground: its clips drift and bob
and its faint deflates and sinks rather than collapsing. The eyes invert the
cast idiom - pale ovals with a dark highlight - because dark eyes would vanish
on a black core, and the reference's eyes are pale anyway.
"""

import math
import os
import random
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 100
NAME = "Gastly"
TYPES = ["Ghost", "Poison"]
HEIGHT = 1.30
SIZE_AXIS = 'z'
DESIGN = "black gas core wreathed in a ragged purple cloud, pale angular eyes"
PROFILE = dict(tempo=0.85, weight=0.4, bounce=1.0, sway=1.4, stride=0.6,
               floats=True)

CORE = C.hexcol('26232b')
CORE_SHEEN = C.hexcol('4a4054')
GAS = C.hexcol('9580a3')
GAS_LIGHT = C.hexcol('b7a4c4')
GAS_DARK = C.hexcol('6d5c7c')
EYE_PALE = C.hexcol('efeaf2')
MOUTH = C.hexcol('1a1119')
FANG = C.hexcol('f6f1f6')


def _cloud(name, centre, ring_radius, count=11, seed=100):
    """The ragged gas wreath.

    Deliberately a RING of billows around the core rather than one big enclosing
    sphere: an enclosing sphere simply swallows the core and the face, which is
    what the first attempt did. The lobes are pushed toward the back so the front
    of the core stays clear for the eyes and grin, exactly as the reference reads.
    """
    rnd = random.Random(seed)
    lobes = []
    for i in range(count):
        # jitter angle, radius and size hard - evenly spaced equal lobes read as a
        # string of beads, which is the opposite of a gas cloud
        a = (i / float(count)) * math.tau + rnd.uniform(-0.34, 0.34)
        r = ring_radius * rnd.uniform(0.74, 1.24)
        front = max(0.0, -math.cos(a - math.pi * 0.5))
        depth = ring_radius * (0.14 + 0.46 * abs(math.sin(a)) + 0.34 * front
                               + rnd.uniform(-0.10, 0.16))
        pos = Vector((math.cos(a) * r, depth, math.sin(a) * r))
        lr = ring_radius * rnd.uniform(0.24, 0.50)
        ob = C.spherified_cube("%s_%d" % (name, i), cuts=3, radius=lr)
        for _ in range(4):
            d = Vector((rnd.uniform(-1, 1), rnd.uniform(-1, 1), rnd.uniform(-1, 1)))
            if d.length < 1e-4:
                continue
            d.normalize()
            C.bulge(ob, tuple(d * lr * 0.85), lr * rnd.uniform(0.55, 0.95),
                    rnd.uniform(-0.30, 0.36) * lr, direction=tuple(d))
        C.noise_displace(ob, 8.0 / lr, lr * 0.075, seed=seed + i)
        C.paint(ob, lambda co, n, i2, _r=lr: C.mix(
            GAS_DARK, GAS_LIGHT, C.smoothstep(0.45 + 0.55 * n.z)))
        ob.location = Vector(centre) + pos
        C.apply_transforms(ob)
        lobes.append(ob)
    return lobes


def build():
    plan = B.CreaturePlan('floater', HEIGHT, name="Gastly_Body")

    # an internal stub spine only - the visible mass is the core and the cloud
    plan.spine = [
        ((0.000,  0.010, 0.520), 0.030, 0.030),
        ((0.000,  0.000, 0.620), 0.032, 0.032),
        ((0.000, -0.004, 0.700), 0.028, 0.028),
    ]
    plan.arms = []
    plan.legs = []
    plan.tail = []
    plan.head_co = Vector((0.0, 0.000, 0.640))
    plan.head_size = (0.600, 0.590, 0.580)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=8, limb_segments=8,
                           torso_square=2.0, resample=1,
                           round_torso_front=0.6, round_torso_back=0.6)
    C.paint_flat(body, CORE)

    core = B.sculpt_head(
        "Gastly_Core", *plan.head_size, cuts=3,
        snout_len=0.03, snout_drop=0.0, snout_narrow=0.96, snout_z=-0.20,
        snout_blunt=0.92, crown=0.015, crown_back=0.0,
        brow=0.0, cheek=0.015, cheek_z=-0.08, jaw_width=1.0, chin=0.0,
        top_flat=0.0, subsurf=1)
    C.paint(core, lambda co, n, i: C.mix(
        CORE, CORE_SHEEN, C.smoothstep(0.35 + 0.65 * max(0.0, n.z)) * 0.55))
    B.head_place(core, plan.head_co)

    cloud = _cloud("Gastly_Cloud", (0.0, 0.020, 0.648), 0.400, count=11)

    C.carve_mouth(core, (0.0, -0.196, -0.110), width=0.150, height=0.052,
                  depth=0.026, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.028)

    # pale angular eyes - the cast idiom, inverted so it reads on a black core
    face, eye_centres = B.simple_face(
        "Gastly", plan.head_co,
        head_size=plan.head_size, eye_angles=(27.0, 12.0), eye_radius=0.0880,
        eye_squash=(1.0, 0.44, 0.86), eye_tilt=16.0, eye_sink=0.94,
        mouth_angles=-30.0, mouth_width=0.230, mouth_curve=0.34,
        mouth_thickness=0.0130, face_bow=0.038, highlight=0.0)
    for ob in face:
        C.paint(ob, lambda co, n, i: C.mix(EYE_PALE, C.hexcol('cbbfd2'),
                                           C.smoothstep(-n.z * 0.8)))
    # a dark inner mark so the pale eye still has structure
    pupils = []
    for c in eye_centres:
        p = C.uv_sphere("Gastly_Pupil_%d" % len(pupils), radius=0.030,
                        segments=10, rings=7)
        p.scale = (1.0, 0.42, 0.72)
        C.apply_transforms(p)
        C.paint_flat(p, C.hexcol('2a2430'))
        p.location = Vector(c) + Vector((0.0, -0.028, -0.006))
        C.apply_transforms(p)
        pupils.append(p)

    fangs = []
    for sx in (1.0, -1.0):
        fangs.append(B.spike(
            "Gastly_Fang_%s" % ('L' if sx > 0 else 'R'),
            base=tuple(plan.head_co + Vector((0.062 * sx, -0.214, -0.086))),
            direction=(0.10 * sx, -0.30, -1.0), length=0.062, radius=0.020,
            samples=5, ring=7, sharp=1.6, color=FANG))

    parts = [body, core] + cloud + face + pupils + fangs

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0880,
        jaw=True,
        muzzle_y=-0.290,
        anchors=dict(muzzle=(0.0, -0.286, 0.590),
                     body=(0.0, 0.0, 0.640)),
        bevel_scale=0.006,
        smooth_angle=44.0,
        albedo=dict(detail_scale=16.0, cavity=0.30, ao_strength=0.34, speckle=0.05,
                    voronoi_scale=7.0, voronoi_amount=0.12),
        normal=dict(detail_scale=22.0, bump=0.14, pattern_scale=6.0,
                    pattern_bump=0.10),
    )
