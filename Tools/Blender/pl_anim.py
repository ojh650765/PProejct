"""
Poke Lab creature framework - animation layer.

One procedural clip library, driven by the shared bone roles from pl_rig, so all
twelve creatures move like they belong to one game while still reading with their
own weight and tempo.

Every clip is evaluated analytically per frame and keyed on every frame at 30 fps.
Looping clips are exactly periodic, so the value *and* the sampled derivative match
at the loop point - no hitch at the seam.

World-space authoring: `rot()` takes a rotation expressed in world axes and
conjugates it into bone-local space, so "pitch the head down 12 degrees" means the
same thing on every rig regardless of bone roll.
"""

import bpy
import math
from mathutils import Vector, Matrix, Quaternion, Euler

TAU = math.pi * 2.0
FPS = 30

# name -> (base frames, loops)
CLIPS = [
    ("Idle",           96,  True),
    ("IdleBattle",     72,  True),
    ("Walk",           40,  True),
    ("Run",            24,  True),
    ("AttackPhysical", 36,  False),
    ("AttackSpecial",  48,  False),
    ("AttackStatus",   42,  False),
    ("Hit",            26,  False),
    ("Dodge",          24,  False),
    ("Faint",          54,  False),
    ("Celebrate",      66,  False),
    ("SentOut",        42,  False),
    ("Recalled",       26,  False),
    ("Sleep",         120,  True),
]

DEFAULT_PROFILE = dict(
    tempo=1.0,      # >1 = faster / lighter creature
    weight=1.0,     # >1 = heavier, more anticipation and settle
    bounce=1.0,     # vertical liveliness
    sway=1.0,       # lateral looseness
    floats=False,   # never touches the ground
    stride=1.0,
    height=1.0,     # metres, filled in by the builder
)


# ---------------------------------------------------------------------------
# maths helpers
# ---------------------------------------------------------------------------

def clamp01(x):
    return max(0.0, min(1.0, x))


def smoothstep(t):
    t = clamp01(t)
    return t * t * (3.0 - 2.0 * t)


def window(t, a, b):
    """0 before a, ramps smoothly to 1 across [a, b], 1 after."""
    if b <= a:
        return 1.0 if t >= b else 0.0
    return smoothstep((t - a) / (b - a))


def pulse(t, a, peak, b, sharp=1.0):
    """0 -> 1 -> 0 with the crest at `peak`."""
    if t <= a or t >= b:
        return 0.0
    if t < peak:
        return smoothstep((t - a) / max(1e-6, peak - a)) ** sharp
    return smoothstep((b - t) / max(1e-6, b - peak)) ** sharp


def overshoot(t, a, b, amount=1.0, wobble=2.0, damp=5.0):
    """Springy settle used for recoils and landings."""
    if t <= a:
        return 0.0
    u = clamp01((t - a) / max(1e-6, b - a))
    return amount * math.exp(-damp * u) * math.cos(TAU * wobble * u)


def d(x):
    return math.radians(x)


# ---------------------------------------------------------------------------
# pose context
# ---------------------------------------------------------------------------

class PoseCtx(object):
    """Accumulates world-space rotations / offsets, then writes bone-local values."""

    def __init__(self, rig, profile):
        self.rig = rig
        self.arm = rig.obj
        self.p = profile
        self.rot_acc = {}
        self.loc_acc = {}
        self.scl_acc = {}
        self._rest = {}
        for pb in self.arm.pose.bones:
            self._rest[pb.name] = pb.bone.matrix_local.to_3x3()

    # -- role access -------------------------------------------------------
    def role(self, key):
        return self.rig.roles.get(key)

    def bones(self, key):
        return self.rig.bones(key)

    def exists(self, name):
        return bool(name) and name in self.arm.pose.bones

    # -- authoring ---------------------------------------------------------
    def rot(self, bone, rx=0.0, ry=0.0, rz=0.0):
        """Rotate about world X (pitch), Y (roll along depth) and Z (yaw), degrees."""
        if not self.exists(bone):
            return
        if abs(rx) < 1e-9 and abs(ry) < 1e-9 and abs(rz) < 1e-9:
            return
        q = (Quaternion((1, 0, 0), d(rx)) @ Quaternion((0, 1, 0), d(ry))
             @ Quaternion((0, 0, 1), d(rz)))
        m = self._rest[bone]
        local = (m.inverted() @ q.to_matrix() @ m).to_quaternion()
        cur = self.rot_acc.get(bone)
        self.rot_acc[bone] = local if cur is None else cur @ local

    def move(self, bone, x=0.0, y=0.0, z=0.0):
        """Translate in world axes (metres)."""
        if not self.exists(bone):
            return
        m = self._rest[bone]
        local = m.inverted() @ Vector((x, y, z))
        cur = self.loc_acc.get(bone, Vector((0, 0, 0)))
        self.loc_acc[bone] = cur + local

    def scale(self, bone, sx=1.0, sy=1.0, sz=1.0):
        if not self.exists(bone):
            return
        cur = self.scl_acc.get(bone, Vector((1, 1, 1)))
        self.scl_acc[bone] = Vector((cur.x * sx, cur.y * sy, cur.z * sz))

    def apply(self):
        for pb in self.arm.pose.bones:
            pb.rotation_mode = 'QUATERNION'
            pb.rotation_quaternion = self.rot_acc.get(pb.name, Quaternion((1, 0, 0, 0)))
            pb.location = self.loc_acc.get(pb.name, Vector((0, 0, 0)))
            pb.scale = self.scl_acc.get(pb.name, Vector((1, 1, 1)))

    def key(self, frame):
        for pb in self.arm.pose.bones:
            pb.keyframe_insert('rotation_quaternion', frame=frame)
            pb.keyframe_insert('location', frame=frame)
            pb.keyframe_insert('scale', frame=frame)


# ---------------------------------------------------------------------------
# reusable motion primitives
# ---------------------------------------------------------------------------

def spine_chain(ctx):
    out = []
    if ctx.role('hips'):
        out.append(ctx.role('hips'))
    out.extend(ctx.bones('spine'))
    if ctx.role('neck'):
        out.append(ctx.role('neck'))
    return [b for b in out if ctx.exists(b)]


def breathe(ctx, phase, amp=1.0):
    """The single most important idle detail - the body has to fill and empty."""
    p = ctx.p
    s = math.sin(phase)
    c = math.cos(phase)
    chest = ctx.bones('spine')
    for i, b in enumerate(chest):
        w = 0.6 + 0.4 * i
        ctx.rot(b, rx=-1.6 * amp * s * w)
        ctx.scale(b, sx=1.0 + 0.020 * amp * s * w, sz=1.0 + 0.016 * amp * s * w,
                  sy=1.0 + 0.010 * amp * s * w)
    if ctx.role('hips'):
        ctx.rot(ctx.role('hips'), rx=0.8 * amp * s)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=1.4 * amp * c * 0.6 + 0.9 * amp * s)
    if ctx.role('root'):
        ctx.move(ctx.role('root'), z=0.006 * p['height'] * amp * s * p['bounce'])


def sway(ctx, phase, amp=1.0):
    p = ctx.p
    s = math.sin(phase)
    for i, b in enumerate(spine_chain(ctx)):
        ctx.rot(b, ry=1.3 * amp * p['sway'] * s * (0.5 + 0.25 * i))
    head = ctx.role('head')
    if head:
        ctx.rot(head, ry=-2.2 * amp * p['sway'] * s, rz=1.4 * amp * p['sway'] * math.cos(phase))


def tail_wave(ctx, phase, amp=1.0, vertical=0.35, lag=0.55):
    tail = ctx.bones('tail')
    for i, b in enumerate(tail):
        ph = phase - i * lag
        ctx.rot(b, rz=6.5 * amp * math.sin(ph) * (0.55 + 0.2 * i),
                rx=6.5 * amp * vertical * math.cos(ph * 1.0) * (0.4 + 0.2 * i))


def ear_flick(ctx, phase, amp=1.0, lag=0.6):
    for ears in (ctx.role('ears') or []):
        for i, b in enumerate(ears):
            ctx.rot(b, rx=4.0 * amp * math.sin(phase - i * lag),
                    ry=3.0 * amp * math.cos(phase * 0.7 - i * lag))


def tendril_drift(ctx, phase, amp=1.0):
    for grp in (ctx.role('tendrils') or []):
        for i, b in enumerate(grp):
            ph = phase - i * 0.7
            ctx.rot(b, rx=7.0 * amp * math.sin(ph), rz=6.0 * amp * math.cos(ph * 0.83))


def wing_flap(ctx, phase, amp=1.0, spread=1.0):
    wings = ctx.role('wings') or []
    for side, chain in enumerate(wings):
        sgn = 1.0 if side == 0 else -1.0
        for i, b in enumerate(chain):
            ph = phase - i * 0.45
            up = math.sin(ph)
            ctx.rot(b, ry=-sgn * (34.0 * amp * up + 10.0 * spread) * (1.0 - 0.25 * i),
                    rx=6.0 * amp * math.cos(ph) * (1.0 + 0.4 * i))


def wing_glide(ctx, phase, amp=1.0, spread=1.0):
    wings = ctx.role('wings') or []
    for side, chain in enumerate(wings):
        sgn = 1.0 if side == 0 else -1.0
        for i, b in enumerate(chain):
            ph = phase - i * 0.5
            ctx.rot(b, ry=-sgn * (22.0 * spread + 5.0 * amp * math.sin(ph)) * (1.0 - 0.2 * i),
                    rx=3.0 * amp * math.sin(ph * 0.7))


def _leg_pose(ctx, chain, phase, stride, lift, plan_back=True):
    """A single limb through one gait cycle. chain = [upper, lower, foot(, toe)]."""
    if not chain:
        return
    swing = math.sin(phase)
    flex = (1.0 - math.cos(phase)) * 0.5
    upper = chain[0]
    ctx.rot(upper, rx=stride * swing)
    if len(chain) > 1:
        ctx.rot(chain[1], rx=-lift * (0.35 + 0.65 * flex))
    if len(chain) > 2:
        ctx.rot(chain[2], rx=lift * 0.45 * flex - stride * 0.25 * swing)
    if len(chain) > 3:
        ctx.rot(chain[3], rx=-lift * 0.3 * flex)


def gait(ctx, t, speed=1.0, stride=26.0, lift=42.0, bob=1.0, quad=None):
    """Full-body locomotion. Quadrupeds get a diagonal gait, bipeds a paired one."""
    p = ctx.p
    if quad is None:
        quad = bool(ctx.role('legs')) and bool(ctx.role('arms')) and \
            ctx.role('plan') in ('quadruped', 'avian')
    phase = t * TAU
    legs = ctx.role('legs') or []
    arms = ctx.role('arms') or []
    st = stride * p['stride']

    for i, chain in enumerate(legs):
        _leg_pose(ctx, chain, phase + (math.pi if i else 0.0), st, lift)
    for i, chain in enumerate(arms):
        if quad:
            # diagonal pairs: front-left with back-right
            off = math.pi if i == 0 else 0.0
            _leg_pose(ctx, chain, phase + off, st * 0.85, lift * 0.8)
        else:
            # counter-swinging arms
            ctx.rot(chain[0] if len(chain) < 3 else chain[1],
                    rx=-st * 0.55 * math.sin(phase + (0.0 if i else math.pi)))
            if len(chain) > 2:
                ctx.rot(chain[2], rx=-lift * 0.22 * (1.0 - math.cos(phase * 2.0)) * 0.5)

    root = ctx.role('root')
    if root:
        ctx.move(root, z=0.018 * p['height'] * bob * p['bounce'] * (-math.cos(2 * phase)))
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rz=3.2 * p['sway'] * math.sin(phase),
                ry=2.6 * p['sway'] * math.sin(phase + math.pi * 0.5),
                rx=2.0 * speed)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rz=-1.6 * p['sway'] * math.sin(phase), rx=1.6 * speed)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-2.6 * speed + 1.6 * math.cos(2 * phase), rz=1.4 * math.sin(phase))
    tail_wave(ctx, phase, amp=0.9 + 0.5 * speed, vertical=0.25)
    ear_flick(ctx, phase * 2.0, amp=0.5 + 0.4 * speed)
    if ctx.role('wings'):
        wing_flap(ctx, phase * 2.0, amp=0.35 + 0.3 * speed, spread=0.5)


def float_bob(ctx, t, amp=1.0, cycles=1.0):
    p = ctx.p
    phase = t * TAU * cycles
    root = ctx.role('root')
    if root:
        ctx.move(root, z=0.035 * p['height'] * amp * math.sin(phase),
                 x=0.012 * p['height'] * amp * math.sin(phase * 0.5))
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=3.0 * amp * math.sin(phase + 0.6),
                ry=2.4 * amp * math.cos(phase * 0.7))
    tendril_drift(ctx, phase, amp=amp)


def jaw_open(ctx, amount):
    jaw = ctx.role('jaw')
    if jaw and amount > 0.0:
        ctx.rot(jaw, rx=26.0 * amount)


def brace(ctx, amount, crouch=1.0):
    """Battle-ready stance: lower, wider, forward-leaning."""
    p = ctx.p
    root = ctx.role('root')
    if root:
        ctx.move(root, z=-0.035 * p['height'] * amount * crouch)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=6.0 * amount)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-5.0 * amount * (1.0 + 0.4 * i))
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=7.0 * amount)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-11.0 * amount)
            ctx.rot(chain[1], rx=18.0 * amount)
        if len(chain) > 2:
            ctx.rot(chain[2], rx=-8.0 * amount)
    for i, chain in enumerate(ctx.role('arms') or []):
        if ctx.role('plan') == 'biped' and len(chain) >= 2:
            sgn = 1.0 if i == 0 else -1.0
            ctx.rot(chain[-2], rx=-24.0 * amount, rz=-sgn * 12.0 * amount)
            if len(chain) > 2:
                ctx.rot(chain[-1], rx=-30.0 * amount)


# ---------------------------------------------------------------------------
# the clips
# ---------------------------------------------------------------------------

def clip_idle(ctx, t):
    p = ctx.p
    phase = t * TAU
    breathe(ctx, phase, amp=1.0)
    sway(ctx, phase * 0.5, amp=0.8)
    tail_wave(ctx, phase * 1.0 + 0.9, amp=0.75, vertical=0.5)
    ear_flick(ctx, phase * 0.5, amp=0.6)
    # a small weight shift so the pose is never symmetric-static
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rz=1.8 * math.sin(phase * 0.5 + 0.7) * p['sway'])
    head = ctx.role('head')
    if head:
        ctx.rot(head, rz=3.0 * math.sin(phase * 0.5 + 1.4),
                rx=-1.5 * math.sin(phase * 0.5))
    if p['floats']:
        float_bob(ctx, t, amp=1.0, cycles=1.0)
    if ctx.role('wings'):
        wing_glide(ctx, phase, amp=0.8, spread=0.35)


def clip_idle_battle(ctx, t):
    p = ctx.p
    phase = t * TAU
    brace(ctx, 1.0)
    breathe(ctx, phase * 2.0, amp=0.75)
    # bobbing readiness, twice the idle rate
    root = ctx.role('root')
    if root:
        ctx.move(root, z=0.012 * p['height'] * p['bounce'] * (-abs(math.sin(phase))),
                 y=0.006 * p['height'] * math.sin(phase * 2.0))
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rz=3.5 * math.sin(phase), rx=2.0 * math.cos(phase * 2.0))
    head = ctx.role('head')
    if head:
        ctx.rot(head, rz=-4.0 * math.sin(phase), rx=1.5 * math.cos(phase * 2.0))
    tail_wave(ctx, phase * 2.0, amp=1.3, vertical=0.6)
    ear_flick(ctx, phase * 2.0, amp=1.0)
    if p['floats']:
        float_bob(ctx, t, amp=1.2, cycles=2.0)
    if ctx.role('wings'):
        wing_flap(ctx, phase * 2.0, amp=0.55, spread=0.8)


def clip_walk(ctx, t):
    if ctx.p['floats']:
        float_bob(ctx, t, amp=0.8, cycles=2.0)
        breathe(ctx, t * TAU, amp=0.6)
        tendril_drift(ctx, t * TAU * 2.0, amp=1.2)
        return
    gait(ctx, t, speed=0.35, stride=22.0, lift=38.0, bob=0.9)
    breathe(ctx, t * TAU, amp=0.35)


def clip_run(ctx, t):
    if ctx.p['floats']:
        float_bob(ctx, t, amp=1.1, cycles=3.0)
        ctx.rot(ctx.role('hips') or '', rx=8.0)
        tendril_drift(ctx, t * TAU * 3.0, amp=1.6)
        return
    gait(ctx, t, speed=1.0, stride=34.0, lift=58.0, bob=1.6)
    for b in ctx.bones('spine'):
        ctx.rot(b, rx=-6.0)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=8.0)


def clip_attack_physical(ctx, t):
    p = ctx.p
    w = p['weight']
    anticip = pulse(t, 0.0, 0.22 * w, 0.42, sharp=1.0)
    strike = window(t, 0.34, 0.46) * (1.0 - window(t, 0.5, 0.78))
    recover = window(t, 0.62, 1.0)
    root = ctx.role('root')
    if root:
        ctx.move(root, y=-0.05 * p['height'] * anticip + 0.16 * p['height'] * strike,
                 z=(0.03 * p['height'] * strike * p['bounce']
                    - 0.02 * p['height'] * anticip)
                 + overshoot(t, 0.5, 1.0, 0.012 * p['height'], 1.6, 6.0))
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=-16.0 * anticip + 22.0 * strike, rz=10.0 * anticip - 8.0 * strike)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-13.0 * anticip + 20.0 * strike, rz=8.0 * anticip - 12.0 * strike)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-10.0 * anticip + 24.0 * strike + overshoot(t, 0.52, 1.0, 6.0))
    jaw_open(ctx, 0.35 * anticip + 0.9 * strike)
    for i, chain in enumerate(ctx.role('arms') or []):
        lead = (i == 0)
        a = anticip * (1.0 if lead else 0.5)
        s = strike * (1.0 if lead else 0.4)
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-58.0 * a + 74.0 * s, rz=(-1 if lead else 1) * 18.0 * a)
        if len(chain) >= 3:
            ctx.rot(chain[-1], rx=-46.0 * a + 40.0 * s)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-12.0 * anticip + 10.0 * strike)
            ctx.rot(chain[1], rx=20.0 * anticip - 6.0 * strike)
    tail_wave(ctx, t * TAU * 1.2, amp=1.0 + 1.4 * strike, vertical=0.9)
    if ctx.role('wings'):
        wing_flap(ctx, t * TAU * 1.5, amp=0.9 * (anticip + strike), spread=1.0)
    _ = recover


def clip_attack_special(ctx, t):
    p = ctx.p
    charge = window(t, 0.05, 0.42) * (1.0 - window(t, 0.46, 0.56))
    release = pulse(t, 0.46, 0.58, 0.9, sharp=0.8)
    root = ctx.role('root')
    if root:
        ctx.move(root, z=-0.028 * p['height'] * charge + 0.05 * p['height'] * release,
                 y=-0.03 * p['height'] * charge + 0.05 * p['height'] * release)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=14.0 * charge - 12.0 * release)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=12.0 * charge * (1 + 0.3 * i) - 18.0 * release)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=16.0 * charge - 34.0 * release + overshoot(t, 0.62, 1.0, 8.0))
    jaw_open(ctx, 0.25 * charge + 1.0 * release)
    # a tremor while charging sells the effort
    tremor = charge * 1.6 * math.sin(t * TAU * 9.0)
    for b in ctx.bones('spine'):
        ctx.rot(b, rz=tremor)
    for i, chain in enumerate(ctx.role('arms') or []):
        sgn = 1.0 if i == 0 else -1.0
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-40.0 * charge - 20.0 * release, rz=-sgn * (26.0 * charge - 34.0 * release))
        if len(chain) >= 3:
            ctx.rot(chain[-1], rx=-50.0 * charge + 22.0 * release)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-14.0 * charge)
            ctx.rot(chain[1], rx=24.0 * charge - 8.0 * release)
    tail_wave(ctx, t * TAU * 2.0, amp=0.8 + 1.6 * charge, vertical=1.0)
    if ctx.role('wings'):
        wing_flap(ctx, t * TAU * 2.0, amp=0.6 + 0.9 * release, spread=1.0)
    if p['floats']:
        float_bob(ctx, t, amp=0.6, cycles=1.5)


def clip_attack_status(ctx, t):
    p = ctx.p
    rise = window(t, 0.08, 0.38)
    hold = window(t, 0.32, 0.44) * (1.0 - window(t, 0.62, 0.9))
    fall = window(t, 0.72, 1.0)
    shimmer = hold * math.sin(t * TAU * 5.0)
    root = ctx.role('root')
    if root:
        ctx.move(root, z=0.03 * p['height'] * (rise - fall) * p['bounce'])
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-9.0 * (rise - fall), ry=4.0 * shimmer)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-16.0 * (rise - fall), rz=6.0 * shimmer)
    jaw_open(ctx, 0.55 * hold)
    for i, chain in enumerate(ctx.role('arms') or []):
        sgn = 1.0 if i == 0 else -1.0
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-64.0 * (rise - fall), rz=-sgn * 30.0 * (rise - fall))
        if len(chain) >= 3:
            ctx.rot(chain[-1], rx=-24.0 * (rise - fall) + 8.0 * shimmer)
    ear_flick(ctx, t * TAU * 3.0, amp=1.4 * (rise - fall) + 0.3)
    tail_wave(ctx, t * TAU * 1.5, amp=0.7 + 1.2 * hold, vertical=0.8)
    if ctx.role('wings'):
        wing_glide(ctx, t * TAU, amp=1.0, spread=0.8 + 0.6 * (rise - fall))
    if p['floats']:
        float_bob(ctx, t, amp=0.9, cycles=1.0)


def clip_hit(ctx, t):
    """Real recoil then a real recovery - the impact must land in one frame."""
    p = ctx.p
    impact = pulse(t, 0.0, 0.10, 0.34, sharp=0.7)
    settle = overshoot(t, 0.14, 1.0, 1.0, wobble=1.8, damp=4.2)
    root = ctx.role('root')
    if root:
        ctx.move(root, y=-0.09 * p['height'] * impact - 0.02 * p['height'] * settle,
                 z=-0.03 * p['height'] * impact * p['weight'])
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=-18.0 * impact - 5.0 * settle, rz=9.0 * impact)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-22.0 * impact * (1.0 + 0.25 * i) - 6.0 * settle,
                rz=7.0 * impact)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-30.0 * impact - 10.0 * settle, rz=13.0 * impact)
    jaw_open(ctx, 0.8 * impact)
    for i, chain in enumerate(ctx.role('arms') or []):
        sgn = 1.0 if i == 0 else -1.0
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-40.0 * impact, rz=-sgn * 24.0 * impact)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=10.0 * impact)
            ctx.rot(chain[1], rx=26.0 * impact + 6.0 * settle)
    tail_wave(ctx, t * TAU * 2.5, amp=1.6 * impact + 0.4, vertical=1.2)
    ear_flick(ctx, t * TAU * 3.0, amp=2.0 * impact)
    if ctx.role('wings'):
        wing_flap(ctx, t * TAU * 2.5, amp=1.4 * impact, spread=1.2)


def clip_dodge(ctx, t):
    p = ctx.p
    out = pulse(t, 0.0, 0.30, 0.72, sharp=0.65)
    back = window(t, 0.55, 1.0)
    root = ctx.role('root')
    if root:
        ctx.move(root, x=0.26 * p['height'] * out * (1.0 - 0.6 * back),
                 z=0.09 * p['height'] * out * p['bounce'],
                 y=-0.05 * p['height'] * out)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, ry=-22.0 * out, rz=-14.0 * out)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, ry=-16.0 * out, rz=-10.0 * out, rx=-6.0 * out)
    head = ctx.role('head')
    if head:
        ctx.rot(head, ry=-10.0 * out, rz=18.0 * out)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-22.0 * out)
            ctx.rot(chain[1], rx=44.0 * out)
    for i, chain in enumerate(ctx.role('arms') or []):
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0], rx=-34.0 * out)
    tail_wave(ctx, t * TAU * 2.0, amp=1.5, vertical=1.0)
    if ctx.role('wings'):
        wing_flap(ctx, t * TAU * 2.0, amp=1.5 * out, spread=1.3)


def clip_faint(ctx, t):
    """A collapse with weight: knees buckle, the body folds, then it settles."""
    p = ctx.p
    stagger = pulse(t, 0.0, 0.10, 0.30)
    buckle = window(t, 0.16, 0.46)
    fall = window(t, 0.30, 0.66)
    settle = window(t, 0.60, 0.86)
    bounce_s = overshoot(t, 0.60, 0.95, 1.0, wobble=1.2, damp=6.0)
    h = p['height']
    root = ctx.role('root')
    if root:
        ctx.move(root,
                 z=-0.62 * h * fall + 0.015 * h * bounce_s + 0.03 * h * stagger,
                 y=-0.10 * h * fall - 0.05 * h * stagger,
                 x=0.05 * h * fall)
        ctx.rot(root, rz=-12.0 * fall, ry=-58.0 * fall - 6.0 * bounce_s)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=-24.0 * buckle + 16.0 * fall, rz=8.0 * stagger)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-18.0 * buckle - 14.0 * fall * (1.0 + 0.3 * i),
                rz=6.0 * stagger - 5.0 * fall)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-12.0 * stagger - 34.0 * fall - 6.0 * bounce_s,
                rz=10.0 * fall)
    jaw_open(ctx, 0.6 * stagger + 0.35 * fall - 0.3 * settle)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-30.0 * buckle - 26.0 * fall)
            ctx.rot(chain[1], rx=62.0 * buckle + 30.0 * fall)
        if len(chain) > 2:
            ctx.rot(chain[2], rx=-24.0 * buckle)
    for i, chain in enumerate(ctx.role('arms') or []):
        sgn = 1.0 if i == 0 else -1.0
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-20.0 * buckle - 40.0 * fall, rz=-sgn * 26.0 * fall)
        if len(chain) >= 3:
            ctx.rot(chain[-1], rx=-18.0 * fall)
    for i, b in enumerate(ctx.bones('tail')):
        ctx.rot(b, rx=-16.0 * fall * (0.5 + 0.3 * i), rz=12.0 * fall)
    for ears in (ctx.role('ears') or []):
        for i, b in enumerate(ears):
            ctx.rot(b, rx=-26.0 * fall * (0.6 + 0.3 * i))
    if ctx.role('wings'):
        for side, chain in enumerate(ctx.role('wings')):
            sgn = 1.0 if side == 0 else -1.0
            for i, b in enumerate(chain):
                ctx.rot(b, ry=sgn * 26.0 * fall, rx=-14.0 * fall)
    if p['floats']:
        # gas creatures sink and deflate rather than topple
        ctx.rot(root, ry=0.0)
        ctx.move(root, z=-0.45 * h * fall)
        for b in [ctx.role('hips'), ctx.role('head')]:
            if b:
                ctx.scale(b, sx=1.0 - 0.18 * fall, sy=1.0 - 0.18 * fall,
                          sz=1.0 - 0.30 * fall)


def clip_celebrate(ctx, t):
    """Personality clip: two hops, a spin flourish, a proud settle."""
    p = ctx.p
    h = p['height']
    hop1 = pulse(t, 0.02, 0.14, 0.30, sharp=0.6)
    hop2 = pulse(t, 0.30, 0.42, 0.58, sharp=0.6)
    spin = window(t, 0.52, 0.80)
    proud = window(t, 0.78, 1.0)
    squash1 = pulse(t, 0.0, 0.05, 0.12)
    squash2 = pulse(t, 0.26, 0.31, 0.38)
    land = pulse(t, 0.55, 0.60, 0.70)
    hop = hop1 + hop2
    squash = squash1 + squash2 + land

    root = ctx.role('root')
    if root:
        ctx.move(root, z=0.30 * h * hop * p['bounce'] - 0.07 * h * squash)
        ctx.rot(root, ry=-360.0 * smoothstep(clamp01((t - 0.52) / 0.28)) * (1.0 if spin > 0 else 0.0))
        ctx.scale(root, sz=1.0 - 0.10 * squash + 0.06 * hop,
                  sx=1.0 + 0.09 * squash - 0.04 * hop,
                  sy=1.0 + 0.09 * squash - 0.04 * hop)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=-14.0 * hop + 18.0 * squash - 8.0 * proud)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-12.0 * hop + 12.0 * squash - 10.0 * proud)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-20.0 * hop + 10.0 * squash - 14.0 * proud,
                rz=8.0 * math.sin(t * TAU * 2.0))
    jaw_open(ctx, 0.55 * hop + 0.35 * proud)
    for i, chain in enumerate(ctx.role('arms') or []):
        sgn = 1.0 if i == 0 else -1.0
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-92.0 * hop - 60.0 * proud, rz=-sgn * (34.0 * hop + 26.0 * proud))
        if len(chain) >= 3:
            ctx.rot(chain[-1], rx=-40.0 * hop - 30.0 * proud)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-26.0 * hop + 20.0 * squash)
            ctx.rot(chain[1], rx=48.0 * hop + 36.0 * squash)
    tail_wave(ctx, t * TAU * 3.0, amp=1.8, vertical=1.2)
    ear_flick(ctx, t * TAU * 3.0, amp=1.8)
    if ctx.role('wings'):
        wing_flap(ctx, t * TAU * 4.0, amp=1.3, spread=1.2)
    if p['floats']:
        float_bob(ctx, t, amp=1.4, cycles=3.0)


def clip_sent_out(ctx, t):
    """Materialise, land, shake it off, rise into a battle stance."""
    p = ctx.p
    h = p['height']
    appear = 1.0 - window(t, 0.0, 0.16)
    drop = 1.0 - window(t, 0.05, 0.30)
    land = pulse(t, 0.26, 0.33, 0.48)
    shake = window(t, 0.40, 0.58) * (1.0 - window(t, 0.62, 0.82))
    ready = window(t, 0.68, 1.0)
    root = ctx.role('root')
    if root:
        ctx.move(root, z=0.75 * h * drop - 0.10 * h * land)
        ctx.scale(root, sx=1.0 - 0.55 * appear + 0.12 * land,
                  sy=1.0 - 0.55 * appear + 0.12 * land,
                  sz=1.0 - 0.55 * appear - 0.16 * land)
        ctx.rot(root, ry=-40.0 * appear)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=22.0 * land - 6.0 * ready,
                rz=10.0 * shake * math.sin(t * TAU * 8.0))
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=16.0 * land - 8.0 * ready,
                rz=8.0 * shake * math.sin(t * TAU * 8.0 - 0.5 * i))
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=12.0 * land - 12.0 * ready,
                ry=26.0 * shake * math.sin(t * TAU * 9.0))
    jaw_open(ctx, 0.7 * ready * pulse(t, 0.7, 0.8, 1.0))
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-34.0 * drop + 24.0 * land)
            ctx.rot(chain[1], rx=54.0 * drop + 40.0 * land)
    for i, chain in enumerate(ctx.role('arms') or []):
        sgn = 1.0 if i == 0 else -1.0
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0],
                    rx=-46.0 * drop - 20.0 * ready, rz=-sgn * 18.0 * ready)
    brace(ctx, 0.7 * ready)
    tail_wave(ctx, t * TAU * 2.0, amp=1.2 + 1.0 * shake, vertical=0.9)
    ear_flick(ctx, t * TAU * 3.0, amp=1.6 * shake + 0.4)
    if ctx.role('wings'):
        wing_flap(ctx, t * TAU * 3.0, amp=1.2 * (drop + land), spread=1.0)


def clip_recalled(ctx, t):
    p = ctx.p
    h = p['height']
    look = window(t, 0.0, 0.22)
    pull = window(t, 0.28, 0.72)
    gone = window(t, 0.60, 1.0)
    root = ctx.role('root')
    if root:
        ctx.move(root, y=0.05 * h * pull, z=0.06 * h * pull)
        ctx.scale(root, sx=1.0 - 0.92 * gone, sy=1.0 - 0.92 * gone,
                  sz=1.0 - 0.92 * gone + 0.10 * pull)
        ctx.rot(root, ry=30.0 * pull)
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=-10.0 * look - 16.0 * pull, rz=-12.0 * look)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=-8.0 * pull)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-18.0 * pull)
            ctx.rot(chain[1], rx=30.0 * pull)
    for i, chain in enumerate(ctx.role('arms') or []):
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0], rx=-30.0 * pull)
    tail_wave(ctx, t * TAU, amp=0.8, vertical=0.6)


def clip_sleep(ctx, t):
    p = ctx.p
    phase = t * TAU
    h = p['height']
    root = ctx.role('root')
    if root:
        if p['floats']:
            ctx.move(root, z=-0.15 * h + 0.02 * h * math.sin(phase))
        else:
            ctx.move(root, z=-0.30 * h)
            ctx.rot(root, ry=-14.0)
    hips = ctx.role('hips')
    if hips:
        ctx.rot(hips, rx=10.0)
    for i, b in enumerate(ctx.bones('spine')):
        ctx.rot(b, rx=8.0 * (1.0 + 0.2 * i))
    head = ctx.role('head')
    if head:
        ctx.rot(head, rx=26.0, rz=10.0)
    # slow, deep breathing - half the idle rate, twice the amplitude
    breathe(ctx, phase, amp=1.9)
    for chain in (ctx.role('legs') or []):
        if len(chain) > 1:
            ctx.rot(chain[0], rx=-40.0)
            ctx.rot(chain[1], rx=76.0)
        if len(chain) > 2:
            ctx.rot(chain[2], rx=-28.0)
    for i, chain in enumerate(ctx.role('arms') or []):
        if len(chain) >= 2:
            ctx.rot(chain[-2] if len(chain) > 2 else chain[0], rx=-36.0)
        if len(chain) >= 3:
            ctx.rot(chain[-1], rx=-40.0)
    for i, b in enumerate(ctx.bones('tail')):
        ctx.rot(b, rz=14.0 + 3.0 * math.sin(phase - i * 0.6), rx=-8.0)
    for ears in (ctx.role('ears') or []):
        for i, b in enumerate(ears):
            ctx.rot(b, rx=-22.0 + 2.0 * math.sin(phase - i * 0.5))
    if ctx.role('wings'):
        for side, chain in enumerate(ctx.role('wings')):
            sgn = 1.0 if side == 0 else -1.0
            for i, b in enumerate(chain):
                ctx.rot(b, ry=sgn * 30.0 + 2.0 * math.sin(phase), rx=-10.0)
    if p['floats']:
        tendril_drift(ctx, phase * 0.5, amp=0.5)


CLIP_FUNCS = {
    "Idle": clip_idle,
    "IdleBattle": clip_idle_battle,
    "Walk": clip_walk,
    "Run": clip_run,
    "AttackPhysical": clip_attack_physical,
    "AttackSpecial": clip_attack_special,
    "AttackStatus": clip_attack_status,
    "Hit": clip_hit,
    "Dodge": clip_dodge,
    "Faint": clip_faint,
    "Celebrate": clip_celebrate,
    "SentOut": clip_sent_out,
    "Recalled": clip_recalled,
    "Sleep": clip_sleep,
}


# ---------------------------------------------------------------------------
# baking
# ---------------------------------------------------------------------------

def _rest_pose(rig):
    for pb in rig.obj.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)


def build_all_clips(rig, profile=None, length_scale=None):
    """Create one Blender action per CreatureAnimation value.

    Returns [(clip_name, frame_count, loops), ...] for the manifest.
    """
    prof = dict(DEFAULT_PROFILE)
    prof.update(profile or {})
    arm = rig.obj
    arm.animation_data_create()
    bpy.context.scene.render.fps = FPS
    out = []
    for name, base_frames, loops in CLIPS:
        frames = base_frames
        if length_scale:
            frames = int(round(base_frames * length_scale.get(name, 1.0)))
        # tempo shortens or lengthens everything except the sleep loop
        tempo = prof['tempo'] if name != 'Sleep' else (1.0 + prof['tempo']) * 0.5
        frames = max(8, int(round(frames / max(0.35, tempo))))
        action = bpy.data.actions.new(name)
        action.use_fake_user = True
        arm.animation_data.action = action
        fn = CLIP_FUNCS[name]
        last = frames  # inclusive; for loops pose(frames) == pose(0)
        for f in range(last + 1):
            t = (f % frames) / float(frames) if loops else f / float(frames)
            _rest_pose(rig)
            ctx = PoseCtx(rig, prof)
            fn(ctx, t)
            ctx.apply()
            ctx.key(f)
        for fc in action.fcurves:
            for kp in fc.keyframe_points:
                kp.interpolation = 'LINEAR'
        action.frame_range  # touch so the range caches
        out.append(dict(name=name, frames=frames, loop=loops,
                        seconds=round(frames / float(FPS), 3)))
    arm.animation_data.action = None
    _rest_pose(rig)
    return out
