"""Species 5 - Charmander. Fire, biped with the flame burning at the tail tip.

Modelled from data/pokemon_images/000401.png: upright orange biped with a cream
belly, a tapering snout with a squared-off jaw and a visible mouth line, a brow
over each eye, a short neck that separates the head from the shoulders, short
arms with an elbow break and three pale claws, chunky three-toed feet, and a
thick tapering tail that curves up to an asymmetric licking flame.

Revision notes (second art pass)
  * The head is a *lofted* form, not a sphere with a bump. Stations run from the
    back of the cranium forward to a squared muzzle tip, with the superellipse
    squareness climbing along the way so the cranium stays round while the jaw
    ends up flat-sided. That is the only way to get a real snout: a spherified
    cube plus a proportional-edit grab flattens straight back out under subsurf,
    which is exactly how the first pass ended up with an orange ball.
  * The eyes are placed off the *measured* head section (`_head_section`) rather
    than off an ellipsoid proxy, because the proxy no longer describes the head.
  * The mouth carve is in world space. The first pass passed head-local
    coordinates to `carve_mouth` after the head had already been moved onto the
    body, so the carve fell through empty space and silently did nothing.
  * The flame is asymmetric, leans, and curls at the tip. The two-tone read comes
    from painting the scallop valleys hot and the ridges cool, so the yellow core
    sits *inside* the orange the way the reference draws it, instead of being a
    vertical gradient on a symmetrical teardrop.

Eyes use the cast-wide simple dark oval idiom. Colours sampled from the reference
(Tools/Blender/ref_palettes.json plus direct pixel probes off 000401.png).
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

# Sampled off 000401.png. The flat artwork sits around #eead83; the studio key
# light lifts a mid-tone by roughly a fifth and clips the red channel, so the
# authored albedo is pushed a step deeper and more saturated to land on the
# reference once it is lit and once the bake multiplies AO and cavity in.
SKIN = C.hexcol('ec9354')
SKIN_LIGHT = C.hexcol('f4b183')
SKIN_DEEP = C.hexcol('d97f42')
SKIN_DARK = C.hexcol('c06a30')
BELLY = C.hexcol('f7e9d0')
CLAW = C.hexcol('e8e4dc')
MOUTH = C.hexcol('6b3231')
MOUTH_SHADOW = C.hexcol('a35b34')
FLAME_HOT = C.hexcol('ffd23f')
FLAME_COOL = C.hexcol('f2622a')
FLAME_TIP = C.hexcol('e0431a')

# The reference is markedly big-headed - the head is better than a third of the
# standing height. Everything below is authored at a natural scale and then blown
# up by this factor, so the station table stays readable.
HEAD_SCALE = 1.18

# head-local: origin at the cranium centre, front is -Y, Z up.
# (co, half_width, half_height, superellipse squareness)
HEAD_STATIONS = [
    ((0.0,  0.092,  0.000), 0.026, 0.026, 2.0),   # back of the skull
    ((0.0,  0.072,  0.010), 0.058, 0.058, 2.0),
    ((0.0,  0.040,  0.014), 0.079, 0.077, 2.1),
    ((0.0,  0.004,  0.010), 0.087, 0.083, 2.2),   # widest - cranium
    ((0.0, -0.028,  0.002), 0.082, 0.078, 2.3),   # brow / eye line
    ((0.0, -0.052, -0.010), 0.064, 0.058, 2.4),   # muzzle root: the step
    ((0.0, -0.076, -0.022), 0.048, 0.043, 2.7),   # mid muzzle
    ((0.0, -0.098, -0.031), 0.040, 0.035, 3.0),   # squared-off jaw front
    ((0.0, -0.110, -0.035), 0.031, 0.027, 3.0),   # tip
]


def _head_section(y):
    """(z centre, half width, half height) of the head at head-local y.

    Features are placed off this rather than off `plan.head_size`: once the head
    is a loft the ellipsoid proxy `face_point` assumes is simply wrong, and eyes
    positioned against it end up buried in the muzzle.
    """
    st = HEAD_STATIONS
    if y >= st[0][0][1]:
        return st[0][0][2], st[0][1], st[0][2]
    for i in range(len(st) - 1):
        y0, y1 = st[i][0][1], st[i + 1][0][1]
        if y1 <= y <= y0:
            t = (y0 - y) / max(1e-9, (y0 - y1))
            a, b = st[i], st[i + 1]
            return (a[0][2] + (b[0][2] - a[0][2]) * t,
                    a[1] + (b[1] - a[1]) * t,
                    a[2] + (b[2] - a[2]) * t)
    return st[-1][0][2], st[-1][1], st[-1][2]


def _head(name):
    """Cranium -> snout as one continuous lofted skin, then brow / jaw / chin
    mass sculpted into it."""
    ob = C.loft_path(name, HEAD_STATIONS, segments=14, resample=1,
                     round_start=0.95, round_end=0.55)

    # brow ridge over each eye - the difference between a face and a ball
    for sx in (1.0, -1.0):
        C.bulge(ob, (0.050 * sx, -0.036, 0.038), 0.044, 0.0085,
                direction=(0.30 * sx, -0.52, 0.80), ellipse=(1.0, 0.9, 0.75))
        # a shallow forward-facing plane under the brow for the eye to sit on.
        # Without it the loft tapers straight into the muzzle and an eye placed
        # on the section lands on the silhouette edge, wrapping onto the temple.
        C.bulge(ob, (0.046 * sx, -0.050, 0.014), 0.038, 0.0065,
                direction=(0.28 * sx, -1.0, 0.06), ellipse=(1.0, 1.0, 0.85))
        # squared jaw: push the lower muzzle sides out and flatten them
        C.bulge(ob, (0.042 * sx, -0.072, -0.030), 0.036, 0.0055,
                direction=(sx, -0.30, -0.30))
        # cheek mass behind the muzzle so the snout reads as a separate volume
        C.bulge(ob, (0.070 * sx, -0.030, -0.018), 0.040, 0.005,
                direction=(sx, -0.15, -0.30))
    # chin
    C.bulge(ob, (0.0, -0.098, -0.042), 0.030, 0.0055, direction=(0, -1, -0.45))
    # a shallow crown so the skull is not flat on top
    C.bulge(ob, (0.0, 0.020, 0.070), 0.058, 0.006, direction=(0, 0, 1))
    C.deform(ob, lambda co: co * HEAD_SCALE)
    return ob


def _head_world(head_co, local):
    """head-local (pre-scale) coordinates -> world."""
    return Vector(head_co) + Vector(local) * HEAD_SCALE


def _jaw_line(name, head_co, thickness=0.0072, color=None):
    """The mouth line, threaded through *measured* points on the jaw.

    `C.mouth_arc` builds its curve in one flat tangent frame, which is fine on a
    round head and useless on a snout: the middle of the arc ends up inside the
    muzzle and only the tip pokes out, which is what the first attempt looked
    like. Sampling `_head_section` along the jaw instead puts every point of the
    line on the surface it is supposed to be drawn on.
    """
    pts = []
    radii = []
    n = 15
    for i in range(n):
        u = (i / float(n - 1)) * 2.0 - 1.0
        t = abs(u)
        y = -0.100 + 0.050 * t
        zc, hw, hh = _head_section(y)
        theta = math.radians(-90.0 + 72.0 * t)
        x = hw * math.cos(theta) * (1.0 if u >= 0 else -1.0)
        z = zc + hh * math.sin(theta)
        nrm = Vector((x, 0.0, z - zc))
        nrm = nrm.normalized() if nrm.length > 1e-6 else Vector((0, 0, -1))
        p = _head_world(head_co, (x, y, z)) + nrm * (thickness * 0.55)
        pts.append(p)
        radii.append(thickness * (0.5 + 0.5 * math.cos(u * math.pi * 0.5) ** 0.7))
    ob = C.tube_along(name, pts, radii, segments=6, up=Vector((0, -1, 0)))
    C.paint_flat(ob, color or C.MOUTH_DARK)
    return ob


def _flame(name, base, height_, width):
    """Asymmetric licking flame.

    Each tongue is a lobed loft swept along a path that leans back, then hooks
    forward at the tip. Colour comes off the scallop phase, so the grooves
    between the lobes run hot yellow and the ridges run orange - the yellow core
    sits inside the orange instead of being a vertical gradient.
    """
    objs = []
    spec = (
        # tag,   h,    w,   lean,  curl, lobes, seg, phase,  yaw
        ("_A", 1.00, 1.00, -0.10, 0.17, 4, 20, 0.00, 0.0),
        ("_B", 0.60, 0.56, 0.30, -0.12, 4, 16, 0.90, 2.25),
        ("_C", 0.38, 0.40, -0.34, 0.22, 4, 14, 1.70, -1.85),
    )
    for tag, hf, wf, lean, curl, lobes, seg, phase, yaw in spec:
        h = height_ * hf
        w = width * wf
        pts = [
            (0.000, 0.0, 0.000),
            (lean * w * 0.28, 0.0, h * 0.20),
            (lean * w * 0.72, 0.0, h * 0.42),
            (lean * w * 0.92, 0.0, h * 0.62),
            ((lean * 0.62 + curl) * w, 0.0, h * 0.80),
            ((lean * 0.10 + curl * 1.9) * w, 0.0, h * 0.93),
            ((lean * -0.30 + curl * 2.7) * w, 0.0, h * 1.00),
        ]
        rad = (0.36, 0.54, 0.48, 0.36, 0.23, 0.11, 0.02)
        st = [(p, w * r, w * r * 0.88, 2.0) for p, r in zip(pts, rad)]
        ob = C.lobed_loft(name + tag, st, lobes=lobes, lobe_depth=0.26,
                          phase=phase, segments=seg, resample=0,
                          round_start=0.7, round_end=0.0)

        def col(co, n, i, _h=h, _l=lobes, _p=phase):
            ang = math.atan2(co.y, co.x)
            # 1 in a scallop valley (the hot core), 0 on a ridge
            groove = 0.5 - 0.5 * math.cos(_l * (ang + _p))
            t = C.smoothstep(co.z / max(1e-6, _h))
            cool = C.mix(FLAME_COOL, FLAME_TIP, C.smoothstep((t - 0.55) / 0.45))
            return C.mix(cool, FLAME_HOT, groove * (1.0 - 0.75 * t))

        C.paint(ob, col)
        C.place(ob, location=tuple(Vector(base)), rotation=(0, 0, yaw))
        objs.append(ob)
    return objs


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Charmander_Body")

    # upright, pot-bellied, short-legged, with a genuine neck taper between the
    # shoulders and the head. faces -Y.
    plan.spine = [
        ((0.000,  0.014, 0.188), 0.088, 0.086),   # hips
        ((0.000, -0.008, 0.256), 0.102, 0.099),   # belly, the widest point
        ((0.000, -0.018, 0.322), 0.091, 0.087),   # chest
        ((0.000, -0.014, 0.368), 0.064, 0.060),   # shoulders
        ((0.000, -0.012, 0.402), 0.038, 0.038),   # neck - the head sits on this
    ]
    # a real elbow: the upper arm swings back and out, the forearm comes forward
    plan.arms = [
        ((0.050, -0.010, 0.338), 0.036, 0.036),
        ((0.098,  0.004, 0.296), 0.032, 0.032),   # elbow, swung back
        ((0.132, -0.046, 0.276), 0.024, 0.024),   # forearm, swung forward
        ((0.152, -0.074, 0.262), 0.020, 0.020),   # wrist
    ]
    plan.legs = [
        ((0.052,  0.006, 0.182), 0.052, 0.052),
        ((0.062, -0.006, 0.114), 0.044, 0.044),
        ((0.066, -0.012, 0.044), 0.040, 0.036),
    ]
    # thick tail sweeping back then up - the flame has to sit on something
    plan.tail = [
        ((0.000,  0.072, 0.190), 0.078, 0.076),
        ((0.000,  0.160, 0.152), 0.064, 0.062),
        ((0.000,  0.248, 0.150), 0.050, 0.049),
        ((0.000,  0.316, 0.202), 0.038, 0.037),
        ((0.000,  0.348, 0.286), 0.026, 0.026),
        ((0.000,  0.352, 0.360), 0.015, 0.015),
    ]
    plan.head_co = Vector((0.0, -0.026, 0.480))
    plan.head_size = (0.174 * HEAD_SCALE, 0.200 * HEAD_SCALE, 0.166 * HEAD_SCALE)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=16, limb_segments=11,
                           torso_square=2.1, resample=2, limb_resample=1,
                           round_torso_front=0.85, round_torso_back=0.8)
    # push the belly forward and flatten the back
    C.bulge(body, (0, -0.090, 0.256), 0.11, 0.016, direction=(0, -1, 0),
            ellipse=(1.2, 1.0, 1.3))
    # crease the elbow so the arm is not a smooth tube at silhouette size
    for sx in (1, -1):
        C.bulge(body, (0.100 * sx, 0.006, 0.294), 0.028, 0.006,
                direction=(0.30 * sx, 0.90, 0.20))     # point of the elbow
        C.bulge(body, (0.106 * sx, -0.022, 0.290), 0.020, -0.0045,
                direction=(0.25 * sx, -0.92, -0.15))   # crease inside the joint
    C.noise_displace(body, 34.0, 0.0004, seed=7)

    head = _head("Charmander_Head")
    B.head_place(head, plan.head_co)

    feet = B.paw_pair("Charmander_Foot", (0.070, -0.030, 0.026),
                      length=0.088, width=0.062, height=0.038, toes=3,
                      toe_scale=0.40, spread=0.80, cuts=3)
    hands = B.paw_pair("Charmander_Hand", (0.158, -0.082, 0.252),
                       length=0.046, width=0.036, height=0.030, toes=3,
                       toe_scale=0.50, spread=0.92, cuts=2, subsurf=0)

    grad = C.body_gradient(
        top=SKIN, bottom=SKIN_DEEP, zmin=0.02, zmax=0.56,
        belly=None,
        patches=[
            # the cream front is an oval on the belly, not the whole torso
            (BELLY, (0.0, -0.090, 0.246), 0.064, (0.90, 1.0, 1.25), 0.22),
            (BELLY, (0.0, -0.072, 0.198), 0.048, (1.00, 1.0, 1.10), 0.26),
            (SKIN_DARK, (0.0, 0.150, 0.190), 0.075, (1.0, 1.9, 0.9), 0.6),
        ],
        noise_amt=0.012, seed=5)
    for ob in [body, head] + feet + hands:
        C.paint(ob, grad)

    # mouth line along the jaw. World space: the head has already been moved onto
    # the body, so head-local coordinates here would miss the mesh entirely.
    # A shallow crease under the jaw, kept small so it reads as shadow along the
    # mouth line rather than as a stain across the whole muzzle. It also creates
    # the "Mouth" vertex group the rig uses to weight the jaw bone.
    C.carve_mouth(head, tuple(_head_world(plan.head_co, (0.0, -0.084, -0.048))),
                  width=0.050 * HEAD_SCALE, height=0.014 * HEAD_SCALE,
                  depth=0.007 * HEAD_SCALE, look=Vector((0, -1, 0)),
                  color=MOUTH_SHADOW, corner_lift=0.008)

    # Eyes placed off the measured head section, not off an ellipsoid proxy, and
    # sat *on* the surface rather than sunk into it: `make_eye` squashes the oval
    # to little more than a disc along the look axis, so a centre 12 mm inside a
    # bulged head leaves only a sliver of eye showing.
    eye_r = 0.0305 * HEAD_SCALE
    eye_objs = []
    eye_centres = []
    eye_y = -0.048
    zc, hw, hh = _head_section(eye_y)
    for sx in (1.0, -1.0):
        theta = math.radians(24.0)
        look = Vector((0.38 * sx, -1.0, 0.14)).normalized()
        c = (_head_world(plan.head_co,
                         (hw * math.cos(theta) * 0.85 * sx, eye_y,
                          zc + hh * math.sin(theta)))
             + look * 0.010)
        eye, hl = C.make_eye("Charmander_Eye_%s" % ('L' if sx > 0 else 'R'),
                             c, eye_r, look=look, squash=(1.0, 0.56, 1.18),
                             tilt=math.radians(6.0) * sx, highlight=0.32,
                             highlight_dir=(-0.42 * sx, 0.0, 0.44))
        eye_objs.append(eye)
        if hl:
            eye_objs.append(hl)
        eye_centres.append(c)

    mouth = _jaw_line("Charmander_Mouth", plan.head_co,
                      thickness=0.0072 * HEAD_SCALE, color=MOUTH)

    flame = _flame("Charmander_Flame", (0.0, 0.352, 0.366), height_=0.156,
                   width=0.090)

    claws = []
    for sx in (1, -1):
        claws.extend(B.claw_set("Charmander_ToeClaw_%d" % sx,
                                (0.070 * sx, -0.072, 0.020), forward=(0, -1, 0),
                                count=3, length=0.018, radius=0.0064,
                                spread=0.020, drop=0.003, color=CLAW))
        claws.extend(B.claw_set("Charmander_HandClaw_%d" % sx,
                                (0.163 * sx, -0.102, 0.248),
                                forward=(0.35 * sx, -0.90, -0.18), count=3,
                                length=0.020, radius=0.0055, spread=0.018,
                                drop=0.003, color=CLAW))

    parts = ([body, head] + feet + hands + eye_objs + [mouth] + flame + claws)

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=eye_r * 0.95,
        muzzle_y=-0.162,
        anchors=dict(muzzle=(0.0, -0.158, 0.440)),
        bevel_scale=0.009,
        albedo=dict(detail_scale=30.0, cavity=0.34, ao_strength=0.34, speckle=0.035,
                    voronoi_scale=11.0, voronoi_amount=0.07),
        normal=dict(detail_scale=48.0, bump=0.09, pattern_scale=11.0,
                    pattern_bump=0.05),
    )
