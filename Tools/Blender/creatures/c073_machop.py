"""Species 73 - Machop. Fighting, muscular biped.

Modelled from data/pokemon_images/006601.png: a grey-blue muscular biped with
three brown ridges running back over the crown, heavy shoulders and thighs,
defined pectoral and abdominal masses, big blocky fists, and a short tail.

Revision notes (second art pass) - musculature
  The first pass tried `recess()` on the chest; because the picked faces formed
  one contiguous block the inset produced a single rectangular panel that read as
  a plate glued on. Removing it was right; concluding that muscle had to be
  faked with a few millimetres of bulge was not. Muscle here is *shape*:

    * The torso profile itself carries it. The spine stations go 0.090 at the
      hips, pinch to 0.074 at the waist and open to 0.120 across the chest, so
      the shoulders are a third wider than the hips and the V is in the loft, not
      in a decal.
    * Pectoral, abdominal, deltoid, bicep, quadricep and hamstring masses are
      pushed out of that skin with enough amplitude to survive the bevel and to
      read in a 64 px silhouette (8-20 mm on a 0.8 m creature, where the previous
      pass used 7-14 mm spread over a much wider falloff).
    * Creases are *only* where two masses meet - the sternum, under each pec, the
      linea alba, the two ab divisions - and they are narrow negative bulges plus
      a thin painted line, never a cut groove.

  Also fixed: `carve_mouth` was being handed head-local coordinates after the
  head had already been moved onto the body, so it silently did nothing, and the
  mouth line was buried inside the muzzle. Both now use the real surface.

Eyes use the cast-wide simple dark oval idiom.
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

# sampled off 006601.png (#a7c6d0 dominant, #8ea7b0 in shadow)
SKIN = C.hexcol('8fb2c0')
SKIN_LIGHT = C.hexcol('a9c9d4')
SKIN_DARK = C.hexcol('6e8b96')
# the crease colour is pushed well below the sampled shadow tone for the same
# reason Bulbasaur's blotches are: a painted line has to beat the key light
SKIN_DEEP = C.hexcol('42606c')
RIDGE = C.hexcol('b9a184')
RIDGE_DARK = C.hexcol('8d7862')
MOUTH = C.hexcol('6e3446')
MOUTH_SHADOW = C.hexcol('7a8f98')

# front-surface y of the torso at each height the muscle lines are drawn at, so
# the thin dark patches land on the chest and never wrap round to the back.
FRONT_CHEST = -0.110
FRONT_RIBS = -0.096
FRONT_WAIST = -0.062


def _muscle_lines():
    """Thin dark patches where two muscle masses meet.

    Reference art draws these as lines; at the size this creature is actually
    seen, a painted line does more for the read than any amount of geometry, and
    it costs nothing. The y ellipse is kept tight enough that the far side of the
    torso is well outside the falloff.
    """
    out = []
    # sternum
    out.append((SKIN_DEEP, (0.000, FRONT_CHEST, 0.474), 0.034,
                (0.18, 1.6, 1.7), 0.30))
    for sx in (1, -1):
        # under each pectoral
        out.append((SKIN_DEEP, (0.054 * sx, FRONT_CHEST + 0.008, 0.452), 0.032,
                    (1.90, 1.6, 0.22), 0.30))
    # linea alba down the middle of the abdomen
    out.append((SKIN_DEEP, (0.000, FRONT_WAIST - 0.004, 0.398), 0.038,
                (0.15, 1.5, 1.5), 0.30))
    # two ab divisions
    out.append((SKIN_DEEP, (0.000, FRONT_RIBS + 0.012, 0.426), 0.032,
                (1.45, 1.5, 0.19), 0.30))
    out.append((SKIN_DEEP, (0.000, FRONT_WAIST - 0.002, 0.386), 0.030,
                (1.45, 1.5, 0.19), 0.30))
    return out


def build():
    plan = B.CreaturePlan('biped', HEIGHT, name="Machop_Body")

    # The fighter's V is authored into the loft: wide chest, pinched waist,
    # narrower hips. Shoulders end up a third broader than the hips.
    plan.spine = [
        ((0.000,  0.020, 0.312), 0.092, 0.084),   # hips
        ((0.000,  0.004, 0.390), 0.072, 0.066),   # waist - the pinch
        ((0.000, -0.008, 0.448), 0.104, 0.088),   # lower ribs
        ((0.000, -0.014, 0.492), 0.126, 0.096),   # chest / shoulder girdle
        ((0.000, -0.006, 0.554), 0.064, 0.056),   # neck
    ]
    # the upper arm goes out before it goes down, so the deltoid caps the
    # shoulder instead of the arm hanging off the side of the ribs
    plan.arms = [
        ((0.100, -0.006, 0.514), 0.066, 0.066),
        ((0.176, -0.006, 0.470), 0.054, 0.054),
        ((0.222, -0.028, 0.402), 0.046, 0.046),
        ((0.244, -0.046, 0.372), 0.033, 0.033),   # wrist, pinched below the fist
    ]
    plan.legs = [
        ((0.076,  0.010, 0.300), 0.084, 0.084),
        ((0.084, -0.004, 0.190), 0.062, 0.062),
        ((0.088, -0.014, 0.066), 0.050, 0.046),
    ]
    plan.tail = [
        ((0.000,  0.086, 0.318), 0.034, 0.032),
        ((0.000,  0.138, 0.286), 0.022, 0.021),
        ((0.000,  0.170, 0.250), 0.010, 0.010),
    ]
    plan.head_co = Vector((0.0, -0.024, 0.634))
    plan.head_size = (0.176, 0.196, 0.166)
    plan.subsurf = 0

    body = plan.build_body(method='loft', torso_segments=20, limb_segments=12,
                           torso_square=2.2, resample=2, limb_resample=2,
                           round_torso_front=0.8, round_torso_back=0.8)

    # --- muscle as mass -----------------------------------------------------
    for sx in (1, -1):
        # pectorals: a real dome, not a hint
        C.bulge(body, (0.048 * sx, FRONT_CHEST + 0.010, 0.478), 0.050, 0.020,
                direction=(0.22 * sx, -1.0, 0.12), ellipse=(1.0, 1.0, 0.9))
        # deltoid capping the shoulder
        C.bulge(body, (0.108 * sx, -0.006, 0.516), 0.056, 0.024,
                direction=(0.75 * sx, -0.15, 0.66))
        C.bulge(body, (0.128 * sx, -0.006, 0.494), 0.044, 0.014,
                direction=(sx, -0.10, 0.10))
        # latissimus flaring off the ribs
        C.bulge(body, (0.096 * sx, 0.010, 0.446), 0.048, 0.013,
                direction=(sx, 0.35, -0.10))
        # obliques / ab columns either side of the linea alba
        C.bulge(body, (0.026 * sx, FRONT_RIBS + 0.006, 0.428), 0.026, 0.011,
                direction=(0.18 * sx, -1.0, 0.0))
        C.bulge(body, (0.026 * sx, FRONT_WAIST - 0.004, 0.400), 0.026, 0.010,
                direction=(0.18 * sx, -1.0, 0.0))
        C.bulge(body, (0.024 * sx, FRONT_WAIST + 0.002, 0.370), 0.024, 0.008,
                direction=(0.18 * sx, -1.0, 0.0))
        # biceps and quads
        C.bulge(body, (0.132 * sx, -0.018, 0.474), 0.044, 0.014,
                direction=(0.55 * sx, -0.75, 0.30))
        C.bulge(body, (0.072 * sx, -0.030, 0.258), 0.056, 0.014,
                direction=(0.45 * sx, -0.86, 0.0))
        C.bulge(body, (0.078 * sx, 0.026, 0.250), 0.054, 0.012,
                direction=(0.45 * sx, 0.86, 0.0))

    # --- creases, only where two masses meet --------------------------------
    C.bulge(body, (0.0, FRONT_CHEST - 0.002, 0.470), 0.024, -0.008,
            direction=(0, -1, 0), ellipse=(0.45, 1.0, 1.6))      # sternum
    C.bulge(body, (0.0, FRONT_WAIST - 0.008, 0.398), 0.028, -0.005,
            direction=(0, -1, 0), ellipse=(0.40, 1.0, 1.8))      # linea alba
    for sx in (1, -1):
        C.bulge(body, (0.050 * sx, FRONT_CHEST + 0.014, 0.450), 0.026, -0.006,
                direction=(0.15 * sx, -1.0, 0.0), ellipse=(1.7, 1.0, 0.45))
    C.noise_displace(body, 30.0, 0.0005, seed=73)

    head = B.sculpt_head(
        "Machop_Head", *plan.head_size, cuts=3,
        snout_len=0.24, snout_drop=0.06, snout_narrow=0.58, snout_z=-0.26,
        snout_blunt=0.52, crown=0.030, crown_back=0.04,
        brow=0.060, brow_z=0.16, brow_y=-0.28, brow_x=0.34,
        cheek=0.045, cheek_z=-0.10, jaw_width=1.06, chin=0.030,
        top_flat=0.30, subsurf=1)
    B.head_place(head, plan.head_co)

    # three brown ridges sweeping back over the crown, raised clear of the skull
    ridges = []
    for i, (sx, off) in enumerate(((0.0, 0.0), (1.0, 0.026), (-1.0, 0.026))):
        st = []
        for (px, py, pz), hw, hh in (
                ((0.044 * sx, -0.048 + off * 0.22, 0.088), 0.018, 0.017),
                ((0.048 * sx, 0.014 + off * 0.22, 0.112), 0.021, 0.024),
                ((0.044 * sx, 0.070 + off * 0.22, 0.100), 0.018, 0.021),
                ((0.036 * sx, 0.118 + off * 0.22, 0.054), 0.010, 0.011)):
            st.append(((plan.head_co.x + px, plan.head_co.y + py,
                        plan.head_co.z + pz), hw, hh, 2.6))
        r = C.loft_path("Machop_Ridge_%d" % i, st, segments=10, resample=2,
                        round_start=0.7, round_end=0.6)
        C.paint(r, lambda co, n, i2: C.mix(RIDGE, RIDGE_DARK,
                                           C.smoothstep((co.y - plan.head_co.y)
                                                        / 0.13)))
        ridges.append(r)

    # Big blocky fists with knuckles - the reference's are enormous, and after
    # the weld a fist only slightly wider than the forearm melts into a rounded
    # stump. The wrist is deliberately pinched below the fist so there is a real
    # step for the remesh to keep.
    fists = []
    for sx in (1.0, -1.0):
        f = C.spherified_cube("Machop_Fist_%s" % ('L' if sx > 0 else 'R'),
                              cuts=3, radius=0.5)
        C.deform(f, lambda co: Vector((co.x * 0.126, co.y * 0.140, co.z * 0.130)))
        for k in range(4):
            u = (k / 3.0) * 2.0 - 1.0
            C.bulge(f, (u * 0.036, -0.056, 0.024), 0.042, 0.019,
                    direction=(0, -1, 0.30))
        C.bulge(f, (0.0, 0.008, -0.042), 0.054, 0.012, direction=(0, 0.2, -1))
        C.place(f, location=(0.276 * sx, -0.062, 0.336),
                rotation=(0, 0, math.radians(-14 * sx)))
        fists.append(f)

    feet = B.paw_pair("Machop_Foot", (0.088, -0.048, 0.036),
                      length=0.108, width=0.074, height=0.054, toes=3,
                      toe_scale=0.38, spread=0.85, cuts=3)

    grad = C.body_gradient(
        top=SKIN, bottom=SKIN_DARK, zmin=0.02, zmax=0.74,
        belly=SKIN_LIGHT, belly_axis_y=0.40,
        patches=([(SKIN_DEEP, (0.0, 0.086, 0.440), 0.100, (1.5, 1.2, 1.8), 0.7)]
                 + _muscle_lines()),
        noise_amt=0.012, seed=73)
    for ob in [body, head] + fists + feet:
        C.paint(ob, grad)

    # World space: the head has already been moved onto the body, so head-local
    # coordinates here would miss the mesh entirely (the first pass's bug).
    C.carve_mouth(head, tuple(plan.head_co + Vector((0.0, -0.104, -0.036))),
                  width=0.062, height=0.018, depth=0.009,
                  look=Vector((0, -1, 0)), color=MOUTH_SHADOW, corner_lift=0.010)

    face, eye_centres = B.simple_face(
        "Machop", plan.head_co,
        head_size=plan.head_size, eye_angles=(28.0, 9.0), eye_radius=0.0280,
        eye_squash=(1.0, 0.58, 1.10), eye_tilt=8.0, eye_sink=0.92,
        mouth_width=0.0, highlight=0.30)

    # the wide mouth, laid on the real surface of the muzzle
    mouth = C.drape_line("Machop_Mouth", head, plan.head_co,
                         look=(0, -1, -0.30), yaw_span=25.0, pitch=-15.0,
                         curve=13.0, thickness=0.0062, color=MOUTH, samples=13)

    parts = [body, head] + ridges + fists + feet + face + [mouth]

    return dict(
        parts=parts,
        plan=plan,
        eye_centres=eye_centres,
        eye_radius=0.0280,
        muzzle_y=-0.154,
        anchors=dict(muzzle=(0.0, -0.150, 0.608)),
        bevel_scale=0.008,
        albedo=dict(detail_scale=26.0, cavity=0.46, ao_strength=0.46, speckle=0.035,
                    voronoi_scale=9.0, voronoi_amount=0.08),
        normal=dict(detail_scale=40.0, bump=0.12, pattern_scale=9.0,
                    pattern_bump=0.07),
    )
