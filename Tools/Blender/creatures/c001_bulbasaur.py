"""Species 1 - Bulbasaur. Grass / Poison, quadruped with the bulb on its back.

Modelled from data/pokemon_images/000101.png: squat seafoam-green quadruped, very
wide and low, dark blue-green blotches over the back and legs, small pointed ears
on top of a broad head, wide grinning mouth, four stubby legs with three pale
claws each, and the closed green bulb of overlapping pointed lobes on the back.

The only deliberate deviation from the reference is the eyes: the whole cast uses
simple dark ovals with a soft highlight instead of the original's red iris.
Colours are the k-means palette sampled off that same PNG (Tools/Blender/
ref_palettes.json), not guessed.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 1
NAME = "Bulbasaur"
TYPES = ["Grass", "Poison"]
HEIGHT = 0.70
SIZE_AXIS = 'z'
DESIGN = "squat seafoam quadruped with dark blotches and a closed green bulb"
PROFILE = dict(tempo=0.85, weight=1.25, bounce=0.75, sway=0.9, stride=0.85)

# Sampled off 000101.png: body #85ccb2, blotch #5c9a70, bulb #85c291. The spot is
# pushed a further step down in value from the body so the marking survives the
# studio key and the bake's ambient occlusion - in the reference these read as
# graphic shapes, and the first pass's #47836a on #7ab89e washed out to mud.
SKIN = C.hexcol('7cc3a6')
SKIN_LIGHT = C.hexcol('9ed8bd')
SKIN_DARK = C.hexcol('55917a')
# The reference's blotches are only ~30% darker than the body, but they read
# because the illustration draws a hard dark outline round each one. In 3D with
# no outline, and after the bake multiplies in AO and a voronoi tint, that much
# separation disappears. These are deliberately pushed well past the sampled
# value so the marking survives to the screen.
SPOT = C.hexcol('2a6141')
SPOT_DEEP = C.hexcol('1e4d33')
BELLY = C.hexcol('a8d6bf')
BULB = C.hexcol('82c184')
BULB_LIGHT = C.hexcol('9cd39a')
BULB_DARK = C.hexcol('4e8a5a')
CLAW = C.hexcol('efe7d2')
MOUTH_PINK = C.hexcol('b56a80')
MOUTH_LINE = C.hexcol('6d3846')


def _bulb(name, centre, radius, lobes=5):
    """The closed bulb: one continuous grooved skin whose lobes are cut in by
    scalloped rings, not five leaf flaps glued to a ball.

    Second pass: the grooves were too shallow to read once the form was smoothed,
    so the bud came out as a plain dome with a nipple on top. Now the scallop is
    deeper (0.30 rather than 0.21), the lobe tips are pulled up and outward so
    each one ends in a point the way a closed bud does, and the groove is shaded
    with a real value difference rather than a 0.55 tint. The depth is chosen to
    survive the weld pass: 30 mm of relief against a 3.8 mm voxel.
    """
    r = radius
    stations = [
        ((0.0, 0.0, -r * 1.10), r * 0.52, r * 0.52),
        ((0.0, 0.0, -r * 0.72), r * 0.92, r * 0.92),
        ((0.0, 0.0, -r * 0.26), r * 1.16, r * 1.16),
        ((0.0, 0.0,  r * 0.16), r * 1.18, r * 1.18),
        ((0.0, 0.0,  r * 0.52), r * 1.00, r * 1.00),
        ((0.0, 0.0,  r * 0.80), r * 0.66, r * 0.66),
        ((0.0, 0.0,  r * 1.00), r * 0.26, r * 0.26),
    ]
    ob = C.lobed_loft(name, stations, lobes=lobes, lobe_depth=0.30,
                      phase=math.pi * 0.5, segments=lobes * 6, resample=1,
                      round_start=0.0, round_end=0.55, cap_start=True)

    # Lobe tips: on the crest of each scallop, lift and flare the upper part so
    # every lobe finishes in a point instead of melting into the dome.
    def tips(co):
        rad = math.hypot(co.x, co.y)
        if rad < 1e-6:
            return co
        ang = math.atan2(co.y, co.x)
        crest = max(0.0, math.cos(lobes * (ang + math.pi * 0.5)))
        up = C.smoothstep((co.z + r * 0.10) / (r * 0.95))
        k = 1.0 + 0.22 * crest * up
        return Vector((co.x * k, co.y * k, co.z + r * 0.30 * crest * up * up))

    C.deform(ob, tips)

    def col(co, n, i):
        t = C.smoothstep((co.z + r * 1.2) / (r * 2.3))
        ang = math.atan2(co.y, co.x)
        # 1 in the groove between two lobes, 0 on a lobe crest
        groove = 0.5 - 0.5 * math.cos(lobes * (ang + math.pi * 0.5))
        base = C.mix(BULB_DARK, C.mix(BULB, BULB_LIGHT, t), C.smoothstep(t * 1.15))
        return C.mix(base, BULB_DARK, groove ** 1.6)

    C.paint(ob, col)
    ob.location = Vector(centre)
    C.apply_transforms(ob)
    return [ob]


def build():
    plan = B.CreaturePlan('quadruped', HEIGHT, name="Bulbasaur_Body")

    # Squat and wide - the reference is far broader than it is tall. Lofted
    # cross-sections rather than a stack of spheres, so the barrel of the ribcage,
    # the pinch at the neck and the flat of the back are all authored values.
    # (half-width, half-height) per station.
    plan.spine = [
        ((0.000,  0.205, 0.245), 0.096, 0.096),   # rump
        ((0.000,  0.120, 0.252), 0.164, 0.150),   # haunches
        ((0.000,  0.005, 0.256), 0.180, 0.158),   # widest point
        ((0.000, -0.105, 0.252), 0.172, 0.152),   # chest
        ((0.000, -0.180, 0.248), 0.148, 0.140),   # thick neck, buried in the head
    ]
    # limb tops start well inside the torso so the leg grows out of the body
    plan.arms = [
        ((0.072, -0.118, 0.230), 0.086, 0.086),
        ((0.114, -0.126, 0.140), 0.074, 0.074),
        ((0.122, -0.130, 0.048), 0.074, 0.066),
    ]
    plan.legs = [
        ((0.076,  0.110, 0.234), 0.092, 0.092),
        ((0.120,  0.116, 0.140), 0.078, 0.078),
        ((0.130,  0.112, 0.048), 0.078, 0.070),
    ]
    plan.tail = [
        ((0.000,  0.236, 0.256), 0.042, 0.040),
        ((0.000,  0.284, 0.268), 0.012, 0.012),
    ]
    plan.head_co = Vector((0.0, -0.290, 0.248))
    plan.head_size = (0.300, 0.284, 0.236)
    plan.subsurf = 0   # resampled loft stations smooth the length; no subsurf needed

    # Dense on purpose. This mesh is *discarded* by the weld pass - it only has to
    # (a) voxelise faithfully and (b) carry the vertex colours that get sampled
    # onto the welded skin. At 16 segments the torso quads were 30 mm across, and
    # a 30 mm quad cannot represent the hard-edged blotch this creature needs no
    # matter what `edge` is set to; the spots came out as smears.
    body = plan.build_body(method='loft', torso_segments=26, limb_segments=14,
                           torso_square=2.15, round_torso_front=0.7,
                           round_torso_back=0.9, resample=4, limb_resample=2)
    # flatten the back where the bulb sits, and drop a soft belly
    C.deform(body, lambda co: Vector((co.x, co.y, co.z - 0.020 * C.falloff(
        Vector((co.x / 1.5, (co.y - 0.03) / 1.9, (co.z - 0.40) / 0.8)).length, 0.17))))
    C.bulge(body, (0, 0.02, 0.150), 0.19, 0.014, direction=(0, 0, -1),
            ellipse=(1.0, 1.6, 0.9))
    for sx in (1, -1):
        C.bulge(body, (0.150 * sx, -0.115, 0.240), 0.100, 0.012, direction=(sx, 0, 0.2))
        C.bulge(body, (0.160 * sx, 0.120, 0.250), 0.110, 0.014, direction=(sx, 0, 0.2))
    C.noise_displace(body, 26.0, 0.0004, seed=3)

    head = B.sculpt_head(
        "Bulbasaur_Head", *plan.head_size, cuts=3,
        snout_len=0.16, snout_drop=0.02, snout_narrow=0.74, snout_z=-0.22,
        snout_blunt=0.70, crown=0.030, crown_back=0.02,
        brow=0.045, brow_z=0.18, brow_y=-0.30, brow_x=0.34,
        cheek=0.055, cheek_z=-0.06, jaw_width=1.08, chin=0.015,
        top_flat=0.42, face_flat=0.20, subsurf=1)
    # explicit muzzle mass - the reference face is not a ball
    C.bulge(head, (0.0, -0.098, -0.026), 0.082, 0.020, direction=(0, -1, -0.20),
            ellipse=(1.25, 1.0, 0.85))
    B.head_place(head, plan.head_co)

    paws = []
    for cy, cz, ln in ((-0.140, 0.028, 0.084), (0.118, 0.028, 0.088)):
        paws.extend(B.paw_pair("Bulbasaur_Paw_%s" % ('F' if cy < 0 else 'B'),
                               (0.126, cy, cz), length=ln, width=0.084, height=0.048,
                               toes=3, toe_scale=0.36, spread=0.85, cuts=3))

    grad = C.body_gradient(
        top=SKIN, bottom=SKIN_LIGHT, zmin=0.02, zmax=0.38,
        belly=BELLY, belly_axis_y=0.22,
        patches=[
            # Big graphic blotches placed off the reference: flanks, haunches,
            # shoulders, cheeks and the outside of each leg. edge=0.16 keeps them
            # hard-edged - the reference draws shapes, not airbrush - and the
            # deeper SPOT_DEEP is used on the flanks where the largest blotches
            # need to carry at silhouette size.
            (SPOT_DEEP, (0.186, 0.048, 0.268), 0.086, (1.0, 1.5, 1.2), 0.16),
            (SPOT_DEEP, (-0.186, 0.048, 0.268), 0.086, (1.0, 1.5, 1.2), 0.16),
            (SPOT, (0.184, -0.092, 0.232), 0.072, (1.0, 1.2, 1.1), 0.16),
            (SPOT, (-0.184, -0.092, 0.232), 0.072, (1.0, 1.2, 1.1), 0.16),
            (SPOT, (0.120, 0.168, 0.208), 0.066, (1.0, 1.2, 1.0), 0.16),
            (SPOT, (-0.120, 0.168, 0.208), 0.066, (1.0, 1.2, 1.0), 0.16),
            (SPOT, (0.172, -0.130, 0.118), 0.060, (1.0, 1.0, 1.4), 0.16),
            (SPOT, (-0.172, -0.130, 0.118), 0.060, (1.0, 1.0, 1.4), 0.16),
            (SPOT, (0.180, 0.114, 0.118), 0.062, (1.0, 1.0, 1.4), 0.16),
            (SPOT, (-0.180, 0.114, 0.118), 0.062, (1.0, 1.0, 1.4), 0.16),
            (SPOT, (0.150, -0.300, 0.300), 0.060, (1.0, 1.2, 1.0), 0.16),
            (SPOT, (-0.150, -0.300, 0.300), 0.060, (1.0, 1.2, 1.0), 0.16),
            (SPOT_DEEP, (0.076, 0.010, 0.336), 0.066, (1.4, 1.1, 0.8), 0.16),
            (SPOT_DEEP, (-0.076, 0.010, 0.336), 0.066, (1.4, 1.1, 0.8), 0.16),
        ],
        noise_amt=0.010, seed=11)
    for ob in [body, head] + paws:
        C.paint(ob, grad)

    # Wide grin, pressed in and tinted, after the base colour so it survives.
    # World space: the head has already been moved onto the body, so the
    # head-local coordinates the first pass used fell through empty space.
    C.carve_mouth(head, tuple(plan.head_co + Vector((0.0, -0.128, -0.058))),
                  width=0.120, height=0.030, depth=0.014,
                  look=Vector((0, -1, 0)), color=MOUTH_PINK, corner_lift=0.012)

    ears = []
    for sx in (1.0, -1.0):
        ears.append(B.spike("Bulbasaur_Ear_%s" % ('L' if sx > 0 else 'R'),
                            base=tuple(plan.head_co + Vector((0.092 * sx, 0.058, 0.084))),
                            direction=(0.32 * sx, 0.30, 0.90), length=0.076,
                            radius=0.050, curve=(0.004 * sx, 0.006, 0.0),
                            samples=6, ring=7, sharp=1.5, color=SKIN))
    ear_dark = C.hexcol('64a385')
    for e in ears:
        C.paint(e, lambda co, n, i: C.mix(SKIN, ear_dark,
                                          C.smoothstep((co.z - 0.30) / 0.09)))

    face, eye_centres = B.simple_face(
        "Bulbasaur", plan.head_co,
        head_size=plan.head_size, eye_angles=(30.0, 6.0), eye_radius=0.0430,
        eye_squash=(1.0, 0.58, 1.12), eye_tilt=4.0, eye_sink=1.00,
        mouth_width=0.0, highlight=0.30)

    # the wide grin, threaded along the real surface of the muzzle
    mouth = C.drape_line("Bulbasaur_Mouth", head, plan.head_co,
                         look=(0, -1, -0.34), yaw_span=40.0, pitch=-10.0,
                         curve=16.0, thickness=0.0090, color=MOUTH_LINE,
                         samples=17)

    bulb = _bulb("Bulbasaur_Bulb", (0.0, 0.045, 0.360), 0.134)

    claws = []
    for cy, sx in ((-0.140, 1), (-0.140, -1), (0.118, 1), (0.118, -1)):
        claws.extend(B.claw_set("Bulbasaur_Claw_%d_%d" % (int(cy * 1000), sx),
                                (0.122 * sx, cy - 0.036, 0.024),
                                forward=(0, -1, 0), count=3, length=0.019,
                                radius=0.0072, spread=0.022, drop=0.004,
                                color=CLAW))

    parts = [body, head] + paws + ears + face + bulb + claws + [mouth]

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0425,
        ear_pts=[(0.086, -0.266, 0.322), (0.115, -0.245, 0.392)],
        muzzle_y=-0.452,
        anchors=dict(muzzle=(0.0, -0.458, 0.216)),
        bevel_scale=0.009,
        albedo=dict(detail_scale=30.0, cavity=0.30, ao_strength=0.30,
                    speckle=0.018, voronoi_amount=0.045),
        normal=dict(detail_scale=46.0, bump=0.09, pattern_scale=10.0, pattern_bump=0.05),
    )
