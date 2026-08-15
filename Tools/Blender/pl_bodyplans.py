"""
Poke Lab creature framework - body plans.

A creature script declares a `CreaturePlan`: a small set of joint positions and
radii in metres. From that single declaration the framework builds

  * the body mesh (skin-modifier limb network -> subdivided -> sculpted), and
  * the matching armature,

so the geometry and the rig can never drift apart. Heads are sculpted separately
from a spherified cube because a head is the thing a player actually looks at.

Convention: creatures face -Y, up is +Z, feet at z = 0, +X is the creature's left
in Blender (see the axis probe in previews/axis_probe.txt for why).
"""

import bpy
import math
from mathutils import Vector, Quaternion

import pl_core as C
import pl_rig as R

FRONT = Vector((0.0, -1.0, 0.0))


# ---------------------------------------------------------------------------
# plan
# ---------------------------------------------------------------------------

class CreaturePlan(object):
    """kind: 'quadruped' | 'biped' | 'avian' | 'floater' | 'serpentine'"""

    def __init__(self, kind, height, name="Body"):
        self.kind = kind
        self.height = height
        self.name = name
        self.spine = []     # [(co, r, ry)] hips -> chest -> neck top
        self.neck = []      # [(co, r, ry)] optional extra neck samples
        self.arms = []      # left-side chain [(co, r)], shoulder -> hand
        self.legs = []      # left-side chain [(co, r)], hip -> foot
        self.tail = []      # [(co, r)] base -> tip
        self.wings = []     # left-side chain [(co, r)]
        self.head_co = Vector((0, 0, 0))
        self.head_size = (0.1, 0.1, 0.1)
        self.subsurf = 1
        self.symmetric_limbs = True

    # -- mesh --------------------------------------------------------------
    def build_body(self, method='loft', torso_segments=14, limb_segments=9,
                   torso_square=2.0, limb_square=2.0, subsurf=None,
                   round_torso_front=0.0, round_torso_back=0.85,
                   round_limb_end=0.95, tail_point=True, resample=2,
                   limb_resample=2):
        """Build the body mass.

        'loft' sweeps a superelliptical cross-section along the spine and along
        each limb - real volume control, edge loops where the rig bends, and a
        torso that can be genuinely squat and wide. 'skin' is the older
        skin-modifier route, kept for blobby creatures where a swept section is
        the wrong tool.
        """
        if method == 'skin':
            return self._build_body_skin()
        sub = self.subsurf if subsurf is None else subsurf
        objs = []

        torso = C.loft_path(self.name, [(s[0], s[1], s[2] if len(s) > 2 else s[1],
                                         torso_square) for s in self.spine],
                            segments=torso_segments, resample=resample,
                            round_start=round_torso_front, round_end=round_torso_back)
        objs.append(torso)

        def limb(chain, tag, mirror=True):
            sides = [1.0, -1.0] if mirror else [1.0]
            for sx in sides:
                st = []
                for s in chain:
                    co = (s[0][0] * sx, s[0][1], s[0][2])
                    w = s[1]
                    hh = s[2] if len(s) > 2 else s[1]
                    st.append((co, w, hh, limb_square))
                objs.append(C.loft_path("%s_%s_%s" % (self.name, tag,
                                                      'L' if sx > 0 else 'R'),
                                        st, segments=limb_segments,
                                        resample=limb_resample,
                                        round_start=0.55, round_end=round_limb_end))

        if self.arms:
            limb(self.arms, 'Arm')
        if self.legs:
            limb(self.legs, 'Leg')
        if self.wings:
            limb(self.wings, 'Wing')
        if self.tail:
            st = [(s[0], s[1], s[2] if len(s) > 2 else s[1], limb_square)
                  for s in self.tail]
            objs.append(C.loft_path(self.name + "_Tail", st, segments=limb_segments,
                                    resample=limb_resample, round_start=0.5,
                                    round_end=0.0 if tail_point else 0.9))

        body = C.join_objects(objs, self.name)
        C.cleanup_mesh(body)
        if sub:
            C.subsurf(body, sub)
        return body

    def _build_body_skin(self):
        sk = C.SkinSkeleton()
        spine_idx = []
        prev = None
        for s in self.spine:
            co, r = s[0], s[1]
            ry = s[2] if len(s) > 2 else None
            prev = sk.add(co, r, ry, parent=prev)
            spine_idx.append(prev)
        sk.root = spine_idx[0]

        def attach(chain, anchor_idx, mirror=True):
            sides = [1.0, -1.0] if mirror else [1.0]
            for sx in sides:
                prev_i = anchor_idx
                for s in chain:
                    co = (s[0][0] * sx, s[0][1], s[0][2])
                    r = s[1]
                    ry = s[2] if len(s) > 2 else None
                    prev_i = sk.add(co, r, ry, parent=prev_i)

        chest_idx = spine_idx[min(len(spine_idx) - 1, max(0, len(spine_idx) - 2))]
        hips_idx = spine_idx[0]
        if self.arms:
            attach(self.arms, chest_idx)
        if self.legs:
            attach(self.legs, hips_idx)
        if self.wings:
            attach(self.wings, chest_idx)
        if self.tail:
            prev_i = hips_idx
            for s in self.tail:
                co, r = s[0], s[1]
                ry = s[2] if len(s) > 2 else None
                prev_i = sk.add(co, r, ry, parent=prev_i)
        obj = sk.build(self.name, subsurf=self.subsurf)
        return obj

    # -- rig ---------------------------------------------------------------
    def _pts(self, chain):
        return [tuple(s[0]) for s in chain]

    def build_rig(self, arm_name, jaw=True, muzzle_y=None, ear_pts=None, extra=None):
        spine_pts = self._pts(self.spine)
        head_top = tuple(Vector(self.head_co) + Vector((0, 0, self.head_size[2] * 0.5)))
        if self.kind == 'biped':
            # spine + head centre + crown, so the last bone is a real Head bone
            # rather than the neck being mislabelled as the head
            specs, roles = R.biped_skeleton(
                hips_z=spine_pts[0][2],
                spine_pts=spine_pts + [tuple(self.head_co), head_top],
                head_top=head_top,
                arm_pts=self._pts(self.arms) if self.arms else None,
                leg_pts=self._pts(self.legs) if self.legs else None,
                tail_pts=self._pts(self.tail) if self.tail else None,
                ear_pts=ear_pts, extra=extra, jaw=jaw, muzzle_y=muzzle_y)
        elif self.kind in ('quadruped', 'serpentine'):
            neck_base = spine_pts[-1]
            head_pts = [neck_base, tuple(self.head_co),
                        tuple(Vector(self.head_co) + FRONT * (self.head_size[1] * 0.55))]
            specs, roles = R.quadruped_skeleton(
                spine_pts=spine_pts,
                front_leg_pts=self._pts(self.arms) if self.arms else None,
                back_leg_pts=self._pts(self.legs) if self.legs else None,
                head_pts=head_pts,
                tail_pts=self._pts(self.tail) if self.tail else None,
                ear_pts=ear_pts, extra=extra, jaw=jaw, muzzle_y=muzzle_y)
        elif self.kind == 'avian':
            neck_base = spine_pts[-1]
            head_pts = [neck_base, tuple(self.head_co),
                        tuple(Vector(self.head_co) + FRONT * (self.head_size[1] * 0.55))]
            specs, roles = R.avian_skeleton(
                spine_pts=spine_pts, head_pts=head_pts,
                wing_pts=self._pts(self.wings), leg_pts=self._pts(self.legs),
                tail_pts=self._pts(self.tail) if self.tail else None,
                extra=extra, jaw=jaw, muzzle_y=muzzle_y)
        else:  # floater
            head_pts = [spine_pts[0], tuple(self.head_co),
                        tuple(Vector(self.head_co) + FRONT * (self.head_size[1] * 0.55))]
            specs, roles = R.floater_skeleton(
                core_z=self.head_co[2], head_pts=head_pts,
                tendrils=[self._pts(t) for t in (self.extras_tendrils or [])]
                if getattr(self, 'extras_tendrils', None) else None,
                extra=extra, jaw=jaw, muzzle_y=muzzle_y)
        rig = R.build_armature(arm_name, specs, roles)
        return rig


# ---------------------------------------------------------------------------
# head sculpting
# ---------------------------------------------------------------------------

def sculpt_head(name, width, depth, height, cuts=3,
                snout_len=0.0, snout_drop=0.0, snout_narrow=0.55, snout_z=-0.10,
                snout_radius=0.30, snout_blunt=0.0,
                crown=0.0, crown_back=0.0, brow=0.0, brow_z=0.14, brow_y=-0.34,
                brow_x=0.30, cheek=0.0, cheek_z=-0.12, jaw_width=1.0,
                chin=0.0, back_flat=0.0, top_flat=0.0, face_flat=0.0,
                subsurf=1):
    """A head with actual structure: cranium mass, a tapering snout, a brow ridge
    over the eyes, cheeks, and a jaw line. Everything else on the creature reads
    off this, so it gets the most shaping work."""
    obj = C.spherified_cube(name, cuts=cuts, radius=0.5)

    # base proportions
    C.deform(obj, lambda co: Vector((co.x * width, co.y * depth, co.z * height)))

    hw, hd, hh = width * 0.5, depth * 0.5, height * 0.5

    # cranium: fuller and taller at the back-top, the classic stylised skull mass
    if crown:
        C.bulge(obj, (0, hd * 0.10, hh * 0.42), hh * 1.25, height * crown,
                direction=(0, 0, 1), ellipse=(1.0, 1.15, 0.85))
    if crown_back:
        C.bulge(obj, (0, hd * 0.62, hh * 0.20), hh * 1.15, depth * crown_back,
                direction=(0, 1, 0))

    # snout: pull forward, drop, and taper so it ends in a small muzzle
    if snout_len:
        cy = -hd * 0.55
        cz = hh * snout_z * 2.0
        rad = max(hw, hh) * (0.85 + snout_radius)

        def snout_fn(co):
            d = Vector(((co.x) / (hw * 1.05), (co.y - cy) / (hd * 1.25),
                        (co.z - cz) / (hh * 1.05))).length
            w = C.falloff(d, 1.0)
            if w <= 0.0:
                return co
            fwd = -depth * snout_len * w
            drop = -height * snout_drop * w
            narrow = 1.0 - (1.0 - snout_narrow) * w
            return Vector((co.x * narrow, co.y + fwd, cz + (co.z - cz) * narrow + drop))

        C.deform(obj, snout_fn)
        _ = rad
        if snout_blunt:
            # flatten the very tip so the muzzle has a plane, not a point
            ymin = min(v.co.y for v in obj.data.vertices)

            def blunt(co):
                w = C.falloff(abs(co.y - ymin) / (depth * 0.22), 1.0)
                return Vector((co.x, co.y + (ymin + depth * 0.03 - co.y) * w * snout_blunt,
                               co.z))

            C.deform(obj, blunt)

    # brow ridge over each eye - the difference between a face and a ball
    if brow:
        for sx in (1.0, -1.0):
            C.bulge(obj, (hw * brow_x * sx, hd * brow_y, hh * brow_z),
                    max(hw, hh) * 0.62, height * brow,
                    ellipse=(1.0, 0.85, 0.7))

    if cheek:
        for sx in (1.0, -1.0):
            C.bulge(obj, (hw * 0.72 * sx, hd * -0.18, hh * cheek_z),
                    max(hw, hh) * 0.72, width * cheek,
                    direction=(sx, -0.25, -0.15), ellipse=(1.0, 1.0, 0.9))

    if jaw_width != 1.0:
        C.scale_region(obj, (0, -hd * 0.25, -hh * 0.55), hh * 1.1,
                       (jaw_width, 1.0, 1.0), pivot=(0, 0, 0))

    if chin:
        C.bulge(obj, (0, -hd * 0.78, -hh * 0.45), max(hw, hh) * 0.6,
                depth * chin, direction=(0, -1, -0.35))

    if back_flat:
        C.deform(obj, lambda co: Vector(
            (co.x, co.y - (co.y - hd * 0.55) * back_flat * C.falloff(
                abs(co.y - hd) / (depth * 0.5), 1.0), co.z)))
    if top_flat:
        zmax = max(v.co.z for v in obj.data.vertices)
        C.deform(obj, lambda co: Vector(
            (co.x, co.y, co.z - (co.z - zmax * 0.82) * top_flat * C.falloff(
                abs(co.z - zmax) / (height * 0.35), 1.0))))
    if face_flat:
        ymin = min(v.co.y for v in obj.data.vertices)
        C.deform(obj, lambda co: Vector(
            (co.x, co.y - (co.y - ymin * 0.86) * face_flat * C.falloff(
                abs(co.y - ymin) / (depth * 0.42), 1.0), co.z)))

    if subsurf:
        C.subsurf(obj, subsurf)
    obj.location = Vector((0, 0, 0))
    return obj


def head_place(head, co):
    head.location = Vector(co)
    C.apply_transforms(head)
    return head


def eye_pair(name, head_co, offset, radius, **kwargs):
    """Mirrored eyes. offset is (x, y, z) relative to the head centre for the left."""
    out = []
    for sx in (1.0, -1.0):
        c = Vector(head_co) + Vector((offset[0] * sx, offset[1], offset[2]))
        eye, hl = C.make_eye("%s_Eye_%s" % (name, 'L' if sx > 0 else 'R'), c, radius,
                             **kwargs)
        out.extend([eye, hl])
    return out


def face_point(head_co, head_size, yaw_deg, pitch_deg, sink=0.86):
    """A point on the head ellipsoid at a given yaw/pitch off the forward axis,
    plus the outward normal there.

    Placing features by angle rather than by raw XYZ offsets is what keeps eyes on
    the *front* of a face. Offsets that look right on paper slide onto the side of
    the skull as soon as the head gets rounder, which is exactly how the first
    pass ended up with eyes on the temples.
    """
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    d = Vector((math.sin(yaw) * math.cos(pitch),
                -math.cos(yaw) * math.cos(pitch),
                math.sin(pitch)))
    half = Vector((head_size[0] * 0.5, head_size[1] * 0.5, head_size[2] * 0.5))
    pos = Vector(head_co) + Vector((d.x * half.x, d.y * half.y, d.z * half.z)) * sink
    nrm = Vector((d.x / max(1e-6, half.x), d.y / max(1e-6, half.y),
                  d.z / max(1e-6, half.z))).normalized()
    return pos, nrm


def simple_face(name, head_co, eye_offset=None, eye_radius=0.02, mouth_offset=None,
                mouth_width=0.0, mouth_curve=0.30, mouth_thickness=None,
                face_bow=0.0, eye_squash=(1.0, 0.55, 1.15), eye_tilt=0.0,
                blush=None, blush_offset=None, blush_radius=0.0,
                highlight=0.30, look=(0, -1, 0), head_size=None,
                eye_angles=None, mouth_angles=None, eye_sink=0.84,
                mouth_sink=1.06, blush_angles=None):
    """The cast-wide facial language in one call: two simple dark oval eyes with a
    soft highlight, and a simple curved mouth. Returns (objects, eye_centres).

    Every creature except Zubat (eyeless) uses this, which is what makes the twelve
    read as one game rather than twelve separate assets.
    """
    objs = []
    centres = []
    for sx in (1.0, -1.0):
        if eye_angles is not None and head_size is not None:
            c, nrm = face_point(head_co, head_size, eye_angles[0] * sx,
                                eye_angles[1], sink=eye_sink)
            eye_look = nrm
        else:
            c = Vector(head_co) + Vector((eye_offset[0] * sx, eye_offset[1],
                                          eye_offset[2]))
            eye_look = Vector(look)
        centres.append(c)
        eye, hl = C.make_eye("%s_Eye_%s" % (name, 'L' if sx > 0 else 'R'), c,
                             eye_radius, look=eye_look, squash=eye_squash,
                             tilt=math.radians(eye_tilt) * sx, highlight=highlight,
                             highlight_dir=(-0.42 * sx, 0.0, 0.44))
        objs.append(eye)
        if hl:
            objs.append(hl)
    if mouth_width > 0.0:
        if mouth_angles is not None and head_size is not None:
            mc, mnrm = face_point(head_co, head_size, 0.0, mouth_angles,
                                  sink=mouth_sink)
            mlook = mnrm
        elif mouth_offset is not None:
            mc = Vector(head_co) + Vector(mouth_offset)
            mlook = Vector(look)
        else:
            mc, mlook = None, None
        if mc is not None:
            th = mouth_thickness if mouth_thickness else eye_radius * 0.22
            objs.append(C.mouth_arc("%s_Mouth" % name, mc, mouth_width,
                                    curve=mouth_curve, thickness=th,
                                    face_bow=face_bow, look=mlook))
    if blush and blush_radius > 0.0:
        for sx in (1.0, -1.0):
            if blush_angles is not None and head_size is not None:
                c, _ = face_point(head_co, head_size, blush_angles[0] * sx,
                                  blush_angles[1], sink=0.94)
            else:
                c = Vector(head_co) + Vector((blush_offset[0] * sx, blush_offset[1],
                                              blush_offset[2]))
            objs.append(C.cheek_blush("%s_Blush_%s" % (name, 'L' if sx > 0 else 'R'),
                                      c, blush_radius, color=blush))
    return objs, centres


def brow_ridge(name, head_co, offset, length, thickness, angle=0.0, color=None,
               taper=0.6):
    """A separate angled brow / eyelid bar. Gives an expression, and reads at 64 px."""
    objs = []
    for sx in (1.0, -1.0):
        prof = [(thickness * taper, -length * 0.5), (thickness, 0.0),
                (thickness * taper, length * 0.5)]
        b = C.lathe("%s_Brow_%s" % (name, 'L' if sx > 0 else 'R'), prof, segments=10)
        C.rotate_obj(b, (0, math.radians(90), 0))
        b.rotation_euler = (0, 0, 0)
        c = Vector(head_co) + Vector((offset[0] * sx, offset[1], offset[2]))
        C.place(b, location=c, rotation=(math.radians(-8), 0, math.radians(angle * sx)))
        if color:
            C.paint_flat(b, color)
        objs.append(b)
    return objs


def paw(name, centre, length, width, height, toes=3, toe_scale=0.42, spread=0.72,
        look=(0, -1, 0), sole_flat=0.55, cuts=2, subsurf=None):
    """A foot with actual toes, not a sphere on a stick. Built by sculpting toe
    lobes into a flattened quad-sphere so the topology stays clean."""
    ob = C.spherified_cube(name, cuts=cuts, radius=0.5)
    C.deform(ob, lambda co: Vector((co.x * width, co.y * length, co.z * height)))
    d = Vector(look).normalized()
    for i in range(toes):
        u = (i / float(max(1, toes - 1))) * 2.0 - 1.0 if toes > 1 else 0.0
        c = Vector((u * width * 0.30 * spread, 0.0, -height * 0.10)) + d * (length * 0.34)
        C.bulge(ob, c, max(width, length) * 0.42 * (1.0 + 0.1 * abs(u)),
                length * toe_scale * 0.5, direction=d + Vector((u * 0.35, 0, -0.12)),
                ellipse=(1.0, 1.0, 0.75))
    # flatten the sole
    zmin = min(v.co.z for v in ob.data.vertices)
    C.deform(ob, lambda co: Vector((co.x, co.y, co.z + (zmin * 0.55 - co.z)
                                    * sole_flat * C.falloff(abs(co.z - zmin)
                                                            / (height * 0.55), 1.0))))
    sub = (1 if cuts < 3 else 0) if subsurf is None else subsurf
    if sub:
        C.subsurf(ob, sub)
    ob.location = Vector(centre)
    C.apply_transforms(ob)
    return ob


def flame(name, base, height_, width, lobes=4, tongues=3, colour_hot=None,
          colour_cool=None, seed=5):
    """A stylised flame: overlapping tapered tongues with scalloped rings, hot in
    the core and cooling toward the tips. Built as geometry so it holds up in a
    turntable, and the VFX worker can add motion on top."""
    import random as _r
    rnd = _r.Random(seed)
    objs = []
    for t in range(tongues):
        f = 1.0 - t * 0.24
        lean = (rnd.random() - 0.5) * 0.30
        st = [
            ((0.0, 0.0, 0.0), width * 0.34 * f, width * 0.34 * f),
            ((lean * width * 0.3, 0.0, height_ * 0.22 * f), width * 0.52 * f,
             width * 0.52 * f),
            ((lean * width * 0.6, 0.0, height_ * 0.48 * f), width * 0.42 * f,
             width * 0.42 * f),
            ((lean * width * 1.1, 0.0, height_ * 0.74 * f), width * 0.24 * f,
             width * 0.24 * f),
            ((lean * width * 1.7, 0.0, height_ * 1.00 * f), width * 0.04 * f,
             width * 0.04 * f),
        ]
        ob = C.lobed_loft("%s_%d" % (name, t), st, lobes=lobes, lobe_depth=0.20,
                          phase=t * 0.7, segments=lobes * 5, resample=1,
                          round_start=0.6, round_end=0.0)
        hot = colour_hot or C.hexcol('ffe07a')
        cool = colour_cool or C.hexcol('f2622a')
        C.paint(ob, lambda co, n, i, _h=height_ * max(0.2, f): C.mix(
            hot, cool, C.smoothstep(co.z / _h)))
        C.place(ob, location=tuple(Vector(base) + Vector((0, 0, 0))),
                rotation=(0, 0, rnd.random() * 2.0))
        objs.append(ob)
    return objs


def spike(name, base, direction, length, radius, curve=(0, 0, 0), samples=7,
          ring=7, sharp=2.0, color=None):
    """A tapered horn / claw / ear spike swept along a gently curving line.

    A cone primitive would read as procedural; this keeps a real taper curve and a
    soft tip, and its ring topology bevels and shades cleanly.
    """
    d = Vector(direction).normalized()
    cv = Vector(curve)
    pts = []
    radii = []
    for i in range(samples):
        t = i / float(samples - 1)
        pts.append(Vector(base) + d * (length * t) + cv * (t * t))
        radii.append(max(radius * 0.06, radius * (1.0 - t ** (1.0 / sharp) * 0.99)))
    ob = C.tube_along(name, pts, radii, segments=ring, up=Vector((0, 0, 1)))
    if color:
        C.paint_flat(ob, color)
    return ob


def claw_set(name, centre, forward, count=3, length=0.02, radius=0.008,
             spread=0.024, drop=0.006, color=None, up=(0, 0, 1)):
    """The little toe claws that stop a stubby foot reading as a blob."""
    f = Vector(forward).normalized()
    side = f.cross(Vector(up)).normalized()
    out = []
    for i in range(count):
        u = (i / float(max(1, count - 1))) * 2.0 - 1.0 if count > 1 else 0.0
        b = Vector(centre) + side * (u * spread)
        out.append(spike("%s_%d" % (name, i), b, f + Vector((0, 0, -0.25)),
                         length, radius, curve=(0, 0, -drop), samples=5, ring=6,
                         color=color))
    return out


def place_along(obj, base, direction, roll=0.0, local_axis=(0, 1, 0)):
    """Aim an object's growth axis at a world direction, then move it to `base`.

    Composing Euler angles by hand to aim a leaf or a fin is how parts end up
    buried inside the body: a rotation about the wrong axis silently does nothing
    visible. Rotating by the measured difference between the local axis and the
    target direction cannot get this wrong.
    """
    src = Vector(local_axis).normalized()
    dst = Vector(direction).normalized()
    quat = src.rotation_difference(dst)
    if roll:
        quat = Quaternion(dst, math.radians(roll)) @ quat
    mat = quat.to_matrix()
    for v in obj.data.vertices:
        v.co = mat @ Vector(v.co)
    obj.data.update()
    obj.location = Vector(base)
    C.apply_transforms(obj)
    return obj


def paw_pair(name, centre, *args, **kwargs):
    out = []
    for sx in (1.0, -1.0):
        c = (centre[0] * sx, centre[1], centre[2])
        p = paw("%s_%s" % (name, 'L' if sx > 0 else 'R'), c, *args, **kwargs)
        out.append(p)
    return out


# ---------------------------------------------------------------------------
# shared finishing
# ---------------------------------------------------------------------------

def finish_mesh(obj, height, bevel_scale=0.012, bevel_segments=1,
                smooth_angle=38.0, sharp_angle=44.0, crease=False, tri_max=None,
                bevel_angle=62.0):
    """The common last mile: tidy, crease, smooth-with-sharp, bevel every hard edge,
    custom split normals. Skipping the bevel is the clearest tell of procedural art."""
    C.cleanup_mesh(obj)
    if crease:
        C.crease_sharp_edges(obj)
    C.mark_sharp_by_angle(obj, sharp_angle)
    C.shade_smooth_with_sharp(obj, smooth_angle)
    before = C.tri_count(obj)
    # Only genuinely hard edges get chamfered. A single segment is enough to catch
    # a highlight, which is the whole point; a 34-degree threshold caught most of a
    # smooth organic mesh and doubled the triangle count for nothing.
    C.bevel_hard_edges(obj, width=height * bevel_scale, segments=bevel_segments,
                       angle_deg=bevel_angle, harden=True)
    print("  bevel: %d -> %d tris" % (before, C.tri_count(obj)))
    C.cleanup_mesh(obj)
    holes = C.fill_holes(obj)
    if holes:
        print("  filled %d boundary edges" % holes)
        C.cleanup_mesh(obj)
    C.triangulate_ngons(obj)
    if tri_max:
        fit_budget(obj, tri_max)
        C.triangulate_ngons(obj)
    C.shade_smooth_with_sharp(obj, smooth_angle)
    C.add_custom_split_normals(obj)
    return obj


def fit_budget(obj, tri_max, min_ratio=0.35):
    """Last-resort collapse so nothing ships over the triangle budget. Building
    within budget is the intent; this only catches the overshoot."""
    tris = C.tri_count(obj)
    if tris <= tri_max:
        return 1.0
    ratio = max(min_ratio, (tri_max * 0.94) / float(tris))
    m = obj.modifiers.new("BudgetDecimate", 'DECIMATE')
    m.decimate_type = 'COLLAPSE'
    m.ratio = ratio
    m.use_collapse_triangulate = False
    C.apply_modifier(obj, m.name)
    print("  budget: decimated %d -> %d tris (ratio %.3f)"
          % (tris, C.tri_count(obj), ratio))
    return ratio


def ground_and_size(obj, target_height, axis='z'):
    return C.fit_size(obj, target_height, axis=axis, ground=True, center_xy=True)
