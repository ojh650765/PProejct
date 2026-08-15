"""Species 10 - Squirtle. Water, biped with a shell.

Modelled from data/pokemon_images/000701.png: light-blue biped with a big round
head, a brown domed carapace divided into panels, a cream ridged plastron on the
front, short limbs, and a curled tail.

The shell panels are cut into the carapace with inset-and-push rather than glued
on as separate plates. Eyes use the cast-wide simple dark oval idiom.
"""

import math
import os
import sys
from mathutils import Vector

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import pl_core as C
import pl_bodyplans as B

ID = 10
NAME = "Squirtle"
TYPES = ["Water"]
HEIGHT = 0.50
SIZE_AXIS = 'z'
DESIGN = "light-blue shelled biped with a panelled brown carapace and curled tail"
PROFILE = dict(tempo=1.0, weight=1.05, bounce=0.95, sway=0.95, stride=0.9)

SKIN = C.hexcol('69b1c4')
SKIN_LIGHT = C.hexcol('8fd0de')
SKIN_DARK = C.hexcol('4b8ea3')
SHELL = C.hexcol('a8785f')
SHELL_DARK = C.hexcol('7d5642')
PLASTRON = C.hexcol('e6d6b4')
PLASTRON_LINE = C.hexcol('bda37c')
MOUTH = C.hexcol('47606b')


def _shell(name, centre, rx, ry, rz):
    """Domed carapace with panel divisions cut in, plus a cream plastron ring."""
    st = [
        ((0.0, 0.0, -rz * 1.00), rx * 0.42, ry * 0.42),
        ((0.0, 0.0, -rz * 0.74), rx * 0.80, ry * 0.80),
        ((0.0, 0.0, -rz * 0.30), rx * 0.98, ry * 0.98),
        ((0.0, 0.0,  rz * 0.14), rx * 1.00, ry * 1.00),
        ((0.0, 0.0,  rz * 0.56), rx * 0.86, ry * 0.86),
        ((0.0, 0.0,  rz * 0.86), rx * 0.56, ry * 0.56),
    ]
    ob = C.loft_path(name, [(s[0], s[1], s[2], 2.4) for s in st], segments=26,
                     resample=1, round_start=0.7, round_end=0.8)
    C.paint(ob, lambda co, n, i: C.mix(SHELL_DARK, SHELL,
                                       C.smoothstep(co.z / (rz * 1.6) + 0.45)))

    # panel divisions: pick faces by their angular sector and ring, then recess
    def sector(f):
        c = f.calc_center_median()
        r = math.hypot(c.x, c.y)
        if r < rx * 0.20:
            return False
        a = (math.atan2(c.y, c.x) + math.tau) % math.tau
        band = (a % (math.tau / 6.0)) / (math.tau / 6.0)
        return 0.42 < band < 0.58

    C.recess(ob, sector, thickness=rx * 0.012, depth=-rx * 0.030,
             color=SHELL_DARK)

    def ring(f):
        c = f.calc_center_median()
        return abs(c.z - rz * 0.10) < rz * 0.055 and math.hypot(c.x, c.y) > rx * 0.55

    C.recess(ob, ring, thickness=rx * 0.012, depth=-rx * 0.026, color=SHELL_DARK)
    ob.rotation_euler = (math.radians(-14), 0, 0)
    C.apply_transforms(ob)
    ob.location = Vector(centre)
    C.apply_transforms(ob)
    return ob


def _plastron(name, centre, rx, rz):
    """The cream ridged front plate."""
    st = [
        ((0.0, 0.0, -rz * 0.98), rx * 0.46, rx * 0.46),
        ((0.0, 0.0, -rz * 0.60), rx * 0.84, rx * 0.84),
        ((0.0, 0.0, -rz * 0.10), rx * 1.00, rx * 1.00),
        ((0.0, 0.0,  rz * 0.42), rx * 0.92, rx * 0.92),
        ((0.0, 0.0,  rz * 0.82), rx * 0.60, rx * 0.60),
    ]
    ob = C.loft_path(name, [(s[0], s[1], s[2], 2.5) for s in st], segments=22,
                     resample=1, round_start=0.7, round_end=0.7)
    C.deform(ob, lambda co: Vector((co.x, co.y * 0.62, co.z)))
    C.paint(ob, lambda co, n, i: C.mix(PLASTRON_LINE, PLASTRON,
                                       0.35 + 0.65 * C.smoothstep(-n.y)))

    def ridge(f):
        c = f.calc_center_median()
        if c.y > -rx * 0.10:
            return False
        band = (c.z + rz) / (rz * 0.72)
        return abs(band - round(band)) < 0.10

    C.recess(ob, ridge, thickness=rx * 0.010, depth=-rx * 0.009,
             color=PLASTRON_LINE)
    ob.location = Vector(centre)
    C.apply_transforms(ob)
    return ob


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Squirtle_Body")

    plan.spine = [
        ((0.000,  0.010, 0.158), 0.072, 0.070),
        ((0.000,  0.000, 0.212), 0.082, 0.080),
        ((0.000, -0.006, 0.264), 0.076, 0.074),
        ((0.000, -0.006, 0.306), 0.050, 0.048),
    ]
    plan.arms = [
        ((0.048, -0.010, 0.268), 0.032, 0.032),
        ((0.086, -0.030, 0.246), 0.026, 0.026),
        ((0.116, -0.050, 0.226), 0.024, 0.021),
    ]
    plan.legs = [
        ((0.046,  0.004, 0.150), 0.044, 0.044),
        ((0.056, -0.006, 0.094), 0.038, 0.038),
        ((0.060, -0.012, 0.038), 0.034, 0.030),
    ]
    # the curled tail is the signature - a spiral swept in the XZ plane
    tail = []
    for i in range(7):
        t = i / 6.0
        ang = t * math.pi * 1.75
        rad = 0.062 * (1.0 - 0.52 * t)
        cx = 0.0
        cy = 0.150 + math.sin(ang) * rad * 1.15
        cz = 0.190 + (1.0 - math.cos(ang)) * rad * 1.05
        tail.append(((cx, cy, cz), 0.030 * (1.0 - 0.72 * t),
                     0.030 * (1.0 - 0.72 * t)))
    plan.tail = tail
    plan.head_co = Vector((0.0, -0.022, 0.396))
    plan.head_size = (0.184, 0.194, 0.176)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=16, limb_segments=12,
                           torso_square=2.1, resample=2, limb_resample=1,
                           round_torso_front=0.85, round_torso_back=0.85)
    C.noise_displace(body, 36.0, 0.0003, seed=11)

    head = B.sculpt_head(
        "Squirtle_Head", *plan.head_size, cuts=3,
        snout_len=0.16, snout_drop=0.03, snout_narrow=0.70, snout_z=-0.26,
        snout_blunt=0.72, crown=0.060, crown_back=0.03,
        brow=0.035, brow_z=0.16, brow_y=-0.30, brow_x=0.32,
        cheek=0.050, cheek_z=-0.08, jaw_width=1.02, chin=0.012,
        top_flat=0.16, subsurf=1)
    # explicit muzzle mass - the reference face is not a ball
    C.bulge(head, (0.0, -0.072, -0.024), 0.062, 0.019, direction=(0, -1, -0.16),
            ellipse=(1.25, 1.0, 0.82))
    B.head_place(head, plan.head_co)

    shell = _shell("Squirtle_Shell", (0.0, 0.030, 0.222), 0.132, 0.126, 0.122)
    plastron = _plastron("Squirtle_Plastron", (0.0, -0.050, 0.222), 0.094, 0.094)

    feet = B.paw_pair("Squirtle_Foot", (0.064, -0.032, 0.022),
                      length=0.076, width=0.054, height=0.032, toes=3,
                      toe_scale=0.40, spread=0.80, cuts=3)
    hands = B.paw_pair("Squirtle_Hand", (0.122, -0.066, 0.220),
                       length=0.046, width=0.034, height=0.026, toes=3,
                       toe_scale=0.42, spread=0.85, cuts=2, subsurf=0)

    grad = C.body_gradient(
        top=SKIN, bottom=SKIN_LIGHT, zmin=0.01, zmax=0.46,
        belly=SKIN_LIGHT, belly_axis_y=0.30,
        patches=[(SKIN_DARK, (0.0, 0.130, 0.200), 0.070, (1.0, 1.6, 1.0), 0.6)],
        noise_amt=0.012, seed=9)
    for ob in [body, head] + feet + hands:
        C.paint(ob, grad)

    C.carve_mouth(head, (0.0, -0.096, -0.040), width=0.078, height=0.024,
                  depth=0.010, look=Vector((0, -1, 0)), color=MOUTH,
                  corner_lift=0.012)

    face, eye_centres = B.simple_face(
        "Squirtle", plan.head_co,
        head_size=plan.head_size, eye_angles=(26.0, 8.0), eye_radius=0.0330,
        eye_squash=(1.0, 0.58, 1.18), eye_tilt=3.0, eye_sink=0.90,
        mouth_angles=-22.0, mouth_width=0.098, mouth_curve=0.44,
        mouth_thickness=0.0076, face_bow=0.018, highlight=0.32)

    parts = [body, head, shell, plastron] + feet + hands + face

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0330,
        muzzle_y=-0.134,
        anchors=dict(muzzle=(0.0, -0.130, 0.376)),
        bevel_scale=0.009,
        crease=True,
        albedo=dict(detail_scale=28.0, cavity=0.38, ao_strength=0.38, speckle=0.03,
                    voronoi_scale=12.0, voronoi_amount=0.08),
        normal=dict(detail_scale=44.0, bump=0.10, pattern_scale=12.0,
                    pattern_bump=0.06),
    )
