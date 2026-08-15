"""Species 47 - Zubat. Poison / Flying, eyeless winged creature, 0.8 m wingspan.

Modelled from data/pokemon_images/004101.png: small blue body with no eyes at
all, a wide open mouth showing four fangs, two large pointed ears, two big wings
with purple membranes on blue struts, and two long thin trailing legs.

This is the one creature in the cast with no eyes - the reference has none, so
the shared simple-eye idiom does not apply here. The face is the mouth.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 47
NAME = "Zubat"
TYPES = ["Poison", "Flying"]
HEIGHT = 0.80          # authored to wingspan, see SIZE_AXIS
SIZE_AXIS = 'x'
DESIGN = "eyeless blue bat with purple wing membranes and a fanged open mouth"
PROFILE = dict(tempo=1.45, weight=0.5, bounce=1.2, sway=1.3, stride=1.0, floats=True)

BLUE = C.hexcol('4d92b5')
BLUE_DARK = C.hexcol('35708f')
BLUE_LIGHT = C.hexcol('76accb')
MEMBRANE = C.hexcol('a26da4')
MEMBRANE_DARK = C.hexcol('7d5280')
EAR_IN = C.hexcol('c69ac6')
MOUTH = C.hexcol('2b1b28')
FANG = C.hexcol('f2ece2')


def _membrane(name, root, span, chord, droop=0.16, scallops=2, thickness=0.006,
              sx=1.0):
    """A wing membrane: a swept sheet with a scalloped trailing edge, thickened.

    Built as a grid so the scallops are real geometry and the wing keeps clean
    quads for the rig to bend.
    """
    cols = 9
    rows = 5
    verts = []
    faces = []
    for i in range(rows + 1):
        v = i / float(rows)                     # 0 leading edge -> 1 trailing
        for j in range(cols + 1):
            u = j / float(cols)                 # 0 shoulder -> 1 tip
            # trailing edge scalloped between the finger struts
            sc = math.sin(u * math.pi * scallops) ** 2
            c = chord * (1.0 - 0.55 * u) * (1.0 - 0.30 * sc * v)
            # chord runs DOWN and slightly back, so the membrane faces the
            # camera like the reference rather than lying flat like a bird wing
            x = root[0] + sx * span * u
            y = root[1] + c * v * 0.34
            z = (root[2] - c * v * 0.94
                 - droop * span * (u * u) * 0.45)
            verts.append((x, y, z))
    for i in range(rows):
        for j in range(cols):
            a = i * (cols + 1) + j
            faces.append((a, a + 1, a + cols + 2, a + cols + 1))
    ob = C.new_mesh_object(name, verts, [], faces)
    C.solidify(ob, thickness)
    C.paint(ob, lambda co, n, i: C.mix(MEMBRANE, MEMBRANE_DARK,
                                       C.smoothstep(abs(co.x - root[0])
                                                    / max(1e-6, span))))
    return ob


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Zubat_Body")

    # compact hanging body; the wings carry the silhouette
    plan.spine = [
        ((0.000,  0.020, 0.180), 0.052, 0.050),
        ((0.000,  0.006, 0.244), 0.070, 0.068),
        ((0.000, -0.004, 0.306), 0.072, 0.072),
        ((0.000, -0.004, 0.352), 0.056, 0.056),
    ]
    # long thin trailing legs
    plan.legs = [
        ((0.030,  0.020, 0.176), 0.017, 0.017),
        ((0.038,  0.030, 0.100), 0.010, 0.010),
        ((0.044,  0.038, 0.022), 0.005, 0.005),
    ]
    # wing arm struts
    plan.wings = [
        ((0.062, -0.006, 0.352), 0.021, 0.019),
        ((0.150,  0.008, 0.372), 0.015, 0.013),
        ((0.246,  0.020, 0.352), 0.010, 0.009),
        ((0.336,  0.030, 0.312), 0.006, 0.006),
    ]
    plan.head_co = Vector((0.0, -0.020, 0.398))
    plan.head_size = (0.148, 0.140, 0.128)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=15, limb_segments=9,
                           torso_square=2.1, resample=2, limb_resample=1,
                           round_torso_front=0.85, round_torso_back=0.9)
    C.noise_displace(body, 40.0, 0.0004, seed=47)

    head = B.sculpt_head(
        "Zubat_Head", *plan.head_size, cuts=3,
        snout_len=0.14, snout_drop=0.06, snout_narrow=0.72, snout_z=-0.26,
        snout_blunt=0.70, crown=0.040, crown_back=0.02,
        brow=0.030, brow_z=0.20, brow_y=-0.28, brow_x=0.34,
        cheek=0.040, cheek_z=-0.10, jaw_width=1.06, chin=0.0,
        top_flat=0.14, subsurf=1)
    B.head_place(head, plan.head_co)

    # the wide open mouth IS the face here
    C.carve_mouth(head, (0.0, -0.058, -0.012), width=0.066, height=0.046,
                  depth=0.036, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.006)

    mouth = C.uv_sphere("Zubat_Mouth", radius=1.0, segments=16, rings=10)
    mouth.scale = (0.052, 0.030, 0.040)
    C.apply_transforms(mouth)
    C.paint(mouth, lambda co, n, i: C.mix(MOUTH, C.hexcol('7a3a52'),
                                          C.smoothstep(-n.y * 0.8)))
    mouth.location = plan.head_co + Vector((0.0, -0.052, -0.012))
    C.apply_transforms(mouth)

    ears = []
    for sx in (1.0, -1.0):
        outer = B.spike("Zubat_Ear_%s" % ('L' if sx > 0 else 'R'),
                        base=tuple(plan.head_co + Vector((0.048 * sx, 0.026, 0.048))),
                        direction=(0.44 * sx, 0.20, 0.87), length=0.132,
                        radius=0.046, curve=(0.010 * sx, 0.010, 0.0),
                        samples=7, ring=9, sharp=1.4, color=BLUE)
        C.deform(outer, lambda co: Vector((co.x, co.y * 0.66, co.z)))
        inner = B.spike("Zubat_EarIn_%s" % ('L' if sx > 0 else 'R'),
                        base=tuple(plan.head_co + Vector((0.048 * sx, 0.012, 0.052))),
                        direction=(0.44 * sx, 0.10, 0.89), length=0.100,
                        radius=0.028, curve=(0.008 * sx, 0.006, 0.0),
                        samples=6, ring=8, sharp=1.5, color=EAR_IN)
        C.deform(inner, lambda co: Vector((co.x, co.y * 0.50, co.z)))
        ears.extend([outer, inner])

    wings = []
    for sx in (1.0, -1.0):
        wings.append(_membrane("Zubat_Membrane_%s" % ('L' if sx > 0 else 'R'),
                               root=(0.058 * sx, -0.010, 0.372), span=0.300,
                               chord=0.210, droop=0.20, scallops=2,
                               thickness=0.005, sx=sx))

    fangs = []
    for sx in (1.0, -1.0):
        for dz, ln in ((0.012, 0.020), (-0.024, 0.016)):
            fangs.append(B.spike(
                "Zubat_Fang_%s_%d" % ('L' if sx > 0 else 'R', int(dz * 1000)),
                base=tuple(plan.head_co + Vector((0.024 * sx, -0.072, dz - 0.012))),
                direction=(0.10 * sx, -0.28, -1.0 if dz > 0 else 1.0),
                length=ln, radius=0.0072, samples=5, ring=6, sharp=1.5,
                color=FANG))

    grad = C.body_gradient(
        top=BLUE, bottom=BLUE_DARK, zmin=0.0, zmax=0.50,
        belly=BLUE_LIGHT, belly_axis_y=0.34,
        patches=[(BLUE_DARK, (0.0, 0.060, 0.300), 0.070, (1.4, 1.2, 1.2), 0.6)],
        noise_amt=0.014, seed=47)
    for ob in [body, head]:
        C.paint(ob, grad)

    parts = [body, head, mouth] + ears + wings + fangs

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=[],        # eyeless by design, matching the reference
        eye_radius=0.0,
        muzzle_y=-0.100,
        anchors=dict(muzzle=(0.0, -0.096, 0.386)),
        bevel_scale=0.006,
        albedo=dict(detail_scale=30.0, cavity=0.36, ao_strength=0.36, speckle=0.035,
                    voronoi_scale=10.0, voronoi_amount=0.08),
        normal=dict(detail_scale=44.0, bump=0.10, pattern_scale=10.0,
                    pattern_bump=0.06),
    )
