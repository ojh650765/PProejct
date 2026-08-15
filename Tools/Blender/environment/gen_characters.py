"""
Characters: one player and three NPCs, on a Unity-Humanoid-compatible
armature, plus nine shared animation clips.

All four share one skeleton with identical proportions and identical bone
names, so the nine clips authored once on the player rig retarget onto every
character (and Unity's Humanoid retargeting would handle it even if they did
not).  Clips ship as separate `<Rig>@<Clip>.fbx` files, which is the import
convention Unity handles most reliably.

Bone naming follows the Unity Humanoid requirement list: Hips, Spine, Chest,
UpperChest, Neck, Head, and Shoulder/UpperArm/LowerArm/Hand and
UpperLeg/LowerLeg/Foot/Toes in .L/.R pairs.  Bones roll along +Y (the FBX
exporter's primary_bone_axis), and the T-pose is authored as the rest pose so
Unity's automatic avatar mapping succeeds without manual correction.
"""

import sys
import os
import math
import random

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Matrix, Euler, Quaternion

import envlib as E
import textures as T

FAM = "Characters"
OUT = E.FAMILY_DIR[FAM]

SKIN_L, SKIN_M, SKIN_D, HAIR_BR = 0, 1, 2, 3
HAIR_BK, HAIR_BL, SHIRT_R, SHIRT_B = 4, 5, 6, 7
JACKET, DENIM, SHOES, CAP = 8, 9, 10, 11
PACK, BELT, FACE, ACCENT = 12, 13, 14, 15

# ---- proportions (metres).  Stylised: ~6.5 heads tall, slightly large head
# and hands, which is what reads as appealing at gameplay camera distance.
H_TOTAL = 1.62
HIP_Z = 0.86
CHEST_Z = 1.18
NECK_Z = 1.36
HEAD_C = 1.455
HEAD_R = 0.140
SHOULDER_X = 0.155
ARM_UP = 0.255
ARM_LO = 0.235
HAND_L = 0.105
LEG_UP = 0.42
LEG_LO = 0.40
FOOT_L = 0.185
HIP_X = 0.082


# --------------------------------------------------------------------------
# armature
# --------------------------------------------------------------------------

BONES = [
    # (name, head, tail, parent)
    ("Hips",        (0, 0, HIP_Z),            (0, 0, HIP_Z + 0.10),   None),
    ("Spine",       (0, 0, HIP_Z + 0.10),     (0, 0, HIP_Z + 0.20),   "Hips"),
    ("Chest",       (0, 0, HIP_Z + 0.20),     (0, 0, CHEST_Z),        "Spine"),
    ("UpperChest",  (0, 0, CHEST_Z),          (0, 0, NECK_Z - 0.04),  "Chest"),
    ("Neck",        (0, 0, NECK_Z - 0.04),    (0, 0, NECK_Z + 0.05),  "UpperChest"),
    ("Head",        (0, 0, NECK_Z + 0.05),    (0, 0, HEAD_C + HEAD_R), "Neck"),
]
for s, sx in (("L", 1), ("R", -1)):
    BONES += [
        ("Shoulder." + s, (sx * 0.035, 0, CHEST_Z + 0.10),
         (sx * SHOULDER_X, 0, CHEST_Z + 0.09), "UpperChest"),
        ("UpperArm." + s, (sx * SHOULDER_X, 0, CHEST_Z + 0.09),
         (sx * (SHOULDER_X + ARM_UP), 0, CHEST_Z + 0.09), "Shoulder." + s),
        ("LowerArm." + s, (sx * (SHOULDER_X + ARM_UP), 0, CHEST_Z + 0.09),
         (sx * (SHOULDER_X + ARM_UP + ARM_LO), 0, CHEST_Z + 0.09),
         "UpperArm." + s),
        ("Hand." + s, (sx * (SHOULDER_X + ARM_UP + ARM_LO), 0, CHEST_Z + 0.09),
         (sx * (SHOULDER_X + ARM_UP + ARM_LO + HAND_L), 0, CHEST_Z + 0.09),
         "LowerArm." + s),
        ("UpperLeg." + s, (sx * HIP_X, 0, HIP_Z),
         (sx * HIP_X, 0, HIP_Z - LEG_UP), "Hips"),
        ("LowerLeg." + s, (sx * HIP_X, 0, HIP_Z - LEG_UP),
         (sx * HIP_X, 0, HIP_Z - LEG_UP - LEG_LO), "UpperLeg." + s),
        ("Foot." + s, (sx * HIP_X, 0, HIP_Z - LEG_UP - LEG_LO),
         (sx * HIP_X, -FOOT_L * 0.62, 0.012), "LowerLeg." + s),
        ("Toes." + s, (sx * HIP_X, -FOOT_L * 0.62, 0.012),
         (sx * HIP_X, -FOOT_L * 1.05, 0.012), "Foot." + s),
    ]


def build_armature(name="Rig"):
    arm = bpy.data.armatures.new(name)
    obj = bpy.data.objects.new(name, arm)
    bpy.context.scene.collection.objects.link(obj)
    E.activate(obj)
    bpy.ops.object.mode_set(mode='EDIT')
    eb = arm.edit_bones
    made = {}
    for (bname, head, tail, parent) in BONES:
        b = eb.new(bname)
        b.head = Vector(head)
        b.tail = Vector(tail)
        b.use_connect = False
        made[bname] = b
    for (bname, head, tail, parent) in BONES:
        if parent:
            made[bname].parent = made[parent]
    bpy.ops.object.mode_set(mode='OBJECT')
    obj.show_in_front = True
    return obj


# --------------------------------------------------------------------------
# body mesh
# --------------------------------------------------------------------------

def limb(bm, a, b, r0, r1, mat, sides=10, cap_a=False, cap_b=False, bend=0.0,
         bend_axis=(1, 0, 0)):
    a = Vector(a)
    b = Vector(b)
    n = 5
    pts = []
    radii = []
    d = (b - a)
    ax = Vector(bend_axis).normalized()
    for i in range(n + 1):
        t = i / float(n)
        p = a + d * t + ax * (bend * math.sin(math.pi * t))
        pts.append(p)
        radii.append(r0 + (r1 - r0) * t)
    return E.bm_polytube(bm, pts, radii, sides, mat, cap_start=cap_a,
                         cap_end=cap_b, smooth=True)


def torso(bm, mat_top, mat_bottom, mat_belt):
    """Lofted torso: hips flare, waist nips, chest broadens, shoulders slope."""
    stations = [
        (HIP_Z - 0.055, 0.135, 0.098, mat_bottom),
        (HIP_Z + 0.030, 0.140, 0.102, mat_bottom),
        (HIP_Z + 0.085, 0.128, 0.092, mat_belt),
        (HIP_Z + 0.140, 0.124, 0.088, mat_top),
        (HIP_Z + 0.230, 0.145, 0.100, mat_top),
        (CHEST_Z + 0.030, 0.168, 0.108, mat_top),
        (CHEST_Z + 0.115, 0.162, 0.100, mat_top),
        (NECK_Z - 0.030, 0.105, 0.078, mat_top),
    ]
    sides = 20
    rings = []
    for (z, rx, ry, m) in stations:
        ring = []
        for i in range(sides):
            a = 2 * math.pi * i / sides
            # superellipse cross-section: a body, not a tube
            cx, cy = math.cos(a), math.sin(a)
            k = 0.72
            ring.append(bm.verts.new(
                (math.copysign(abs(cx) ** k, cx) * rx,
                 math.copysign(abs(cy) ** k, cy) * ry, z)))
        rings.append(ring)
    for k in range(len(rings) - 1):
        m = stations[k + 1][3]
        for i in range(sides):
            j = (i + 1) % sides
            f = bm.faces.new((rings[k][i], rings[k][j],
                              rings[k + 1][j], rings[k + 1][i]))
            f.material_index = m
            f.smooth = True
    f = bm.faces.new(list(reversed(rings[0])))
    f.material_index = stations[0][3]
    f.smooth = True
    f = bm.faces.new(rings[-1])
    f.material_index = stations[-1][3]
    f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))


def head_mesh(bm, mat_skin, mat_face, mat_hair, hair_style, mat_cap=None):
    """Rounded head with a slight jaw taper, ears, nose, brow, and hair built
    as a shell over the crown rather than a separate blob."""
    stations = [
        (HEAD_C - HEAD_R * 1.00, 0.034, 0.036),
        (HEAD_C - HEAD_R * 0.88, 0.070, 0.072),
        (HEAD_C - HEAD_R * 0.62, 0.096, 0.100),
        (HEAD_C - HEAD_R * 0.28, 0.114, 0.116),
        (HEAD_C + HEAD_R * 0.08, 0.122, 0.118),
        (HEAD_C + HEAD_R * 0.42, 0.117, 0.112),
        (HEAD_C + HEAD_R * 0.72, 0.096, 0.092),
        (HEAD_C + HEAD_R * 0.92, 0.056, 0.054),
        (HEAD_C + HEAD_R * 1.02, 0.0, 0.0),
    ]
    sides = 22
    rings = []
    for (z, rx, ry) in stations:
        if rx < 1e-6:
            v = bm.verts.new((0, 0, z))
            rings.append([v] * sides)
            continue
        ring = []
        for i in range(sides):
            a = 2 * math.pi * i / sides
            cx, cy = math.cos(a), math.sin(a)
            # flatten the back of the skull slightly, push the face forward
            fy = 1.0 + 0.10 * max(0.0, -cy)
            ring.append(bm.verts.new((cx * rx, cy * ry * fy, z)))
        rings.append(ring)
    for k in range(len(rings) - 1):
        for i in range(sides):
            j = (i + 1) % sides
            quad = [rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]]
            uniq = []
            for v in quad:
                if v not in uniq:
                    uniq.append(v)
            if len(uniq) < 3:
                continue
            f = bm.faces.new(uniq)
            f.material_index = mat_skin
            f.smooth = True
    f = bm.faces.new(list(reversed(rings[0])))
    f.material_index = mat_skin
    f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))

    # ears
    for sx in (-1, 1):
        E.bm_puff(bm, (sx * 0.118, 0.004, HEAD_C - HEAD_R * 0.02), 0.034,
                  random.Random(3), mat_skin, sides=6,
                  squash=(0.42, 0.85, 1.15), lumpy=0.10, rings_n=3)
    # nose
    E.bm_puff(bm, (0, -0.122, HEAD_C - HEAD_R * 0.16), 0.026,
              random.Random(5), mat_skin, sides=5,
              squash=(0.72, 1.10, 0.80), lumpy=0.08, rings_n=3)
    # brow ridge
    for sx in (-1, 1):
        # brows: two short strokes, not one bar across the face.  Wide brow
        # puffs merge with the eye lenses and read as a visor.
        E.bm_puff(bm, (sx * 0.052, -0.110, HEAD_C + HEAD_R * 0.30), 0.022,
                  random.Random(7), mat_hair, sides=5,
                  squash=(0.95, 0.26, 0.24), lumpy=0.04, rings_n=3)
    # eyes: shallow dark lenses set into the face
    for sx in (-1, 1):
        E.bm_puff(bm, (sx * 0.050, -0.104, HEAD_C + HEAD_R * 0.06), 0.026,
                  random.Random(11), FACE, sides=7,
                  squash=(0.90, 0.50, 1.00), lumpy=0.03, rings_n=3)
        E.bm_puff(bm, (sx * 0.050, -0.118, HEAD_C + HEAD_R * 0.05), 0.014,
                  random.Random(13), ACCENT, sides=6,
                  squash=(0.90, 0.60, 1.05), lumpy=0.02, rings_n=3)

    # hair as a shell of overlapping locks over the crown
    rng = random.Random(hash(hair_style) & 0xFFFF)
    if hair_style == 'cap':
        # baseball cap: crown panels plus a peak
        crown = []
        for k in range(4):
            t = k / 3.0
            z = HEAD_C + HEAD_R * (0.30 + t * 0.76)
            r = 0.128 * math.cos(t * 1.25) + 0.008
            crown.append((r, z))
        rings2 = []
        for (r, z) in crown:
            rings2.append([bm.verts.new((math.cos(2 * math.pi * i / 14) * r,
                                         math.sin(2 * math.pi * i / 14) * r, z))
                           for i in range(14)])
        for k in range(len(rings2) - 1):
            for i in range(14):
                j = (i + 1) % 14
                f = bm.faces.new((rings2[k][i], rings2[k][j],
                                  rings2[k + 1][j], rings2[k + 1][i]))
                f.material_index = mat_cap
                f.smooth = True
        f = bm.faces.new(rings2[-1])
        f.material_index = mat_cap
        f.smooth = True
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        # peak
        vs = []
        for i in range(7):
            t = i / 6.0
            a = math.pi * (0.28 + t * 0.44)
            vs.append((math.cos(a * 2 - math.pi * 0.5) * 0.10, 0, 0))
        peak = []
        for i in range(7):
            t = i / 6.0
            x = (t - 0.5) * 0.18
            y = -0.098 - math.cos((t - 0.5) * 2.4) * 0.075
            peak.append((x * 1.22, y * 1.16))
        base = [bm.verts.new((x, y * 0.42 - 0.045,
                              HEAD_C + HEAD_R * 0.30 + 0.004))
                for (x, y) in peak]
        tip = [bm.verts.new((x * 1.05, y, HEAD_C + HEAD_R * 0.26))
               for (x, y) in peak]
        for i in range(6):
            f = bm.faces.new((base[i], base[i + 1], tip[i + 1], tip[i]))
            f.material_index = mat_cap
            f.smooth = True
        bmesh.ops.recalc_face_normals(
            bm, faces=[f for f in bm.faces if any(v in f.verts for v in tip)])
        # a fringe of hair below the cap
        for k in range(7):
            a = math.pi * (-0.35 + k * 0.117)
            E.bm_puff(bm, (math.sin(a) * 0.106, -math.cos(a) * 0.110,
                           HEAD_C + HEAD_R * 0.24), 0.040, rng, mat_hair,
                      sides=5, squash=(1.0, 1.0, 0.60), lumpy=0.22, rings_n=3)
    else:
        n = {'short': 11, 'bob': 15, 'long': 17}[hair_style]
        for k in range(n):
            t = k / float(n - 1)
            a = 2 * math.pi * t
            if hair_style == 'short':
                z = HEAD_C + HEAD_R * (0.42 + 0.30 * math.cos(a * 2))
                r = 0.120
                sq = (1.0, 1.0, 0.62)
                pr = 0.052
            elif hair_style == 'bob':
                z = HEAD_C + HEAD_R * (0.10 + 0.60 * abs(math.cos(a)))
                r = 0.126
                sq = (1.0, 1.0, 0.80)
                pr = 0.058
            else:
                z = HEAD_C + HEAD_R * (-0.60 + 1.10 * abs(math.cos(a * 0.5)))
                r = 0.130
                sq = (1.0, 1.0, 1.10)
                pr = 0.060
            E.bm_puff(bm, (math.cos(a) * r * 0.92, math.sin(a) * r * 0.92, z),
                      pr * rng.uniform(0.85, 1.15), rng, mat_hair, sides=5,
                      squash=sq, lumpy=0.26, rot=rng.uniform(0, 3.14),
                      rings_n=3)
        # crown cap so there is no bald spot
        E.bm_puff(bm, (0, 0.008, HEAD_C + HEAD_R * 0.70), 0.100, rng, mat_hair,
                  sides=7, squash=(1.05, 1.05, 0.55), lumpy=0.18, rings_n=3)


def character_mesh(bm, spec):
    rng = random.Random(spec['seed'])
    skin = spec['skin']
    torso(bm, spec['top'], spec['bottom'], BELT)
    head_mesh(bm, skin, FACE, spec['hair'], spec['style'],
              mat_cap=spec.get('cap', CAP))

    # neck
    limb(bm, (0, 0, NECK_Z - 0.075), (0, 0.004, NECK_Z + 0.070),
         0.052, 0.048, skin, sides=10)

    for sx in (1, -1):
        # shoulder cap
        E.bm_puff(bm, (sx * (SHOULDER_X - 0.005), 0, CHEST_Z + 0.085), 0.062,
                  rng, spec['top'], sides=7, squash=(1.0, 1.0, 0.95),
                  lumpy=0.08, rings_n=3)
        # arms: sleeve then forearm skin
        limb(bm, (sx * SHOULDER_X, 0, CHEST_Z + 0.090),
             (sx * (SHOULDER_X + ARM_UP), 0, CHEST_Z + 0.090),
             0.052, 0.042, spec["top"], sides=10, cap_a=True,
             bend=0.014, bend_axis=(0, 0, -1))
        limb(bm, (sx * (SHOULDER_X + ARM_UP), 0, CHEST_Z + 0.090),
             (sx * (SHOULDER_X + ARM_UP + ARM_LO), 0, CHEST_Z + 0.090),
             0.042, 0.032, skin, sides=10,
             bend=0.010, bend_axis=(0, 0, -1))
        # hand: palm mass, three grouped fingers and an opposed thumb.  The
        # ThrowBall and RaiseScanner clips both put a hand near the camera, so
        # a mitten is not enough.
        hx = sx * (SHOULDER_X + ARM_UP + ARM_LO + HAND_L * 0.32)
        E.bm_puff(bm, (hx, 0, CHEST_Z + 0.090), 0.048, rng, skin, sides=8,
                  squash=(1.05, 0.66, 1.05), lumpy=0.08, rings_n=4)
        for fi, (fz, flen, frad) in enumerate(((0.030, 0.062, 0.0125),
                                               (0.002, 0.070, 0.0135),
                                               (-0.026, 0.060, 0.0120))):
            f0 = Vector((hx + sx * 0.030, -0.004, CHEST_Z + 0.090 + fz))
            f1 = f0 + Vector((sx * flen, -0.006, -0.008))
            E.bm_polytube(bm, [f0, f0.lerp(f1, 0.55), f1],
                          [frad, frad * 0.92, frad * 0.70], 5, skin,
                          cap_start=False, cap_end=True, smooth=True)
        th0 = Vector((hx + sx * 0.006, -0.030, CHEST_Z + 0.078))
        th1 = th0 + Vector((sx * 0.040, -0.026, -0.004))
        E.bm_polytube(bm, [th0, th0.lerp(th1, 0.55), th1],
                      [0.0155, 0.0140, 0.0105], 5, skin,
                      cap_start=False, cap_end=True, smooth=True)
        # legs
        limb(bm, (sx * HIP_X, 0, HIP_Z - 0.020),
             (sx * HIP_X * 0.92, 0, HIP_Z - LEG_UP),
             0.072, 0.058, spec["bottom"], sides=10, cap_a=True,
             bend=0.012, bend_axis=(0, -1, 0))
        limb(bm, (sx * HIP_X * 0.92, 0, HIP_Z - LEG_UP),
             (sx * HIP_X * 0.88, 0, HIP_Z - LEG_UP - LEG_LO + 0.03),
             0.058, 0.040, spec["bottom"], sides=10,
             bend=0.014, bend_axis=(0, 1, 0))
        # shoe: sole slab plus a rounded upper
        E.bm_puff(bm, (sx * HIP_X * 0.88, -0.030, 0.055), 0.070, rng, SHOES,
                  sides=7, squash=(0.82, 1.55, 0.62), lumpy=0.06, rings_n=4)
        E.bm_puff(bm, (sx * HIP_X * 0.88, -0.055, 0.022), 0.072, rng, ACCENT,
                  sides=7, squash=(0.86, 1.62, 0.24), lumpy=0.04, rings_n=3)

    # accessories
    if spec.get('backpack'):
        E.bm_puff(bm, (0, 0.115, CHEST_Z - 0.02), 0.115, rng, PACK, sides=8,
                  squash=(1.25, 0.72, 1.30), lumpy=0.10, rings_n=4)
        for sx in (-1, 1):
            limb(bm, (sx * 0.075, 0.075, CHEST_Z + 0.115),
                 (sx * 0.070, -0.070, HIP_Z + 0.16), 0.020, 0.018, PACK,
                 sides=5)
    if spec.get('scarf'):
        for k in range(8):
            a = 2 * math.pi * k / 8
            E.bm_puff(bm, (math.cos(a) * 0.070, math.sin(a) * 0.058,
                           NECK_Z - 0.052), 0.042, rng, spec['scarf'],
                      sides=5, squash=(1.0, 1.0, 0.62), lumpy=0.16, rings_n=3)
    if spec.get('belt_bag'):
        E.bm_puff(bm, (0.095, -0.062, HIP_Z + 0.085), 0.052, rng, PACK,
                  sides=6, squash=(1.0, 0.72, 0.95), lumpy=0.08, rings_n=3)


CHARACTERS = [
    dict(name="Env_Char_Player", seed=6101, skin=SKIN_L, top=SHIRT_R,
         bottom=DENIM, hair=HAIR_BR, style='cap', cap=CAP, backpack=True,
         belt_bag=True),
    dict(name="Env_Char_NPC_Townsfolk_A", seed=6102, skin=SKIN_M, top=JACKET,
         bottom=DENIM, hair=HAIR_BK, style='bob', belt_bag=False),
    dict(name="Env_Char_NPC_Townsfolk_B", seed=6103, skin=SKIN_D, top=SHIRT_B,
         bottom=BELT, hair=HAIR_BK, style='short', scarf=ACCENT),
    dict(name="Env_Char_NPC_Rival", seed=6104, skin=SKIN_L, top=ACCENT,
         bottom=DENIM, hair=HAIR_BL, style='long', backpack=True),
]


# --------------------------------------------------------------------------
# animation
# --------------------------------------------------------------------------

def bp(rig, name):
    return rig.pose.bones[name]


# Poses are authored about ARMATURE-SPACE axes and converted into each bone's
# own rest basis here.  Authoring in raw bone-local euler is the trap: a bone
# whose local Y runs along its own length turns a "lean forward" into a barrel
# roll, and every clip silently comes out as a T-pose with the character
# tipping over.  Conventions, all in armature space, character faces -Y:
#
#   Spine / Chest / Neck / Head / Hips  +rx lean forward, +ry side bend,
#                                       +rz yaw to the left
#   UpperArm     ry brings the arm down from the T-pose (+ for .L, - for .R);
#                +rx then swings the hanging arm backward
#   LowerArm     rz bends the elbow: negative on .L, positive on .R is forward
#   UpperLeg     -rx swings the leg forward
#   LowerLeg     +rx bends the knee (heel back)
#   Foot         +rx points the toe down
AX = {'x': Vector((1, 0, 0)), 'y': Vector((0, 1, 0)), 'z': Vector((0, 0, 1))}


def _local_q(pbone, rx, ry, rz):
    """Compose Rz * Rx * Ry about armature axes, expressed in bone rest space.
    Y first so an arm comes down before it swings; Z last so it reads as twist."""
    M = pbone.bone.matrix_local.to_3x3()
    Minv = M.inverted()
    q = Quaternion((1, 0, 0, 0))
    for (axis, ang) in (('y', ry), ('x', rx), ('z', rz)):
        if abs(ang) < 1e-9:
            continue
        a = (Minv @ AX[axis]).normalized()
        q = Quaternion(a, math.radians(ang)) @ q
    return q


def key(rig, frame, poses):
    """poses: {bone: (rx, ry, rz) in degrees about armature axes}, plus
    '<bone>@loc': (x, y, z) metres in armature space."""
    for bone, val in poses.items():
        if bone.endswith("@loc"):
            b = bp(rig, bone[:-4])
            M = b.bone.matrix_local.to_3x3()
            b.location = M.inverted() @ Vector(val)
            b.keyframe_insert("location", frame=frame)
            continue
        b = bp(rig, bone)
        b.rotation_mode = 'QUATERNION'
        b.rotation_quaternion = _local_q(b, val[0], val[1], val[2])
        b.keyframe_insert("rotation_quaternion", frame=frame)


ARM_DOWN = 74.0


def base_pose(down=ARM_DOWN, elbow=12.0):
    """Rest is a T-pose because Unity's avatar mapper wants one; every clip
    opens by dropping the arms into a natural A-pose with a little elbow."""
    return {
        "UpperArm.L": (0, down, 0),
        "UpperArm.R": (0, -down, 0),
        "LowerArm.L": (0, 0, -elbow),
        "LowerArm.R": (0, 0, elbow),
    }


def merge(*ds):
    out = {}
    for d in ds:
        out.update(d)
    return out


def arms(swingL, swingR, elbowL, elbowR, down=ARM_DOWN, outL=0.0, outR=0.0):
    return {
        "UpperArm.L": (swingL, down - outL, 0),
        "UpperArm.R": (swingR, -down + outR, 0),
        "LowerArm.L": (0, 0, -elbowL),
        "LowerArm.R": (0, 0, elbowR),
    }


def legs(swingL, swingR, kneeL, kneeR, footL=0.0, footR=0.0):
    return {
        "UpperLeg.L": (-swingL, 0, 0),
        "UpperLeg.R": (-swingR, 0, 0),
        "LowerLeg.L": (kneeL, 0, 0),
        "LowerLeg.R": (kneeR, 0, 0),
        "Foot.L": (footL, 0, 0),
        "Foot.R": (footR, 0, 0),
    }


def spine(lean, side=0.0, yaw=0.0, head_lean=0.0, head_yaw=0.0, head_side=0.0,
          hips_lean=0.0, hips_yaw=0.0, hips_side=0.0, lift=0.0):
    return {
        "Hips@loc": (0, 0, lift),
        "Hips": (hips_lean, hips_side, hips_yaw),
        "Spine": (lean * 0.45, side * 0.45, yaw * 0.35),
        "Chest": (lean * 0.35, side * 0.35, yaw * 0.35),
        "UpperChest": (lean * 0.20, side * 0.20, yaw * 0.30),
        "Neck": (head_lean * 0.35, head_side * 0.35, head_yaw * 0.30),
        "Head": (head_lean * 0.65, head_side * 0.65, head_yaw * 0.70),
    }


def make_clips(rig):
    """Returns {clip_name: (action, frame_end)}."""
    clips = {}

    def start(name):
        act = bpy.data.actions.new(name)
        if rig.animation_data is None:
            rig.animation_data_create()
        rig.animation_data.action = act
        for b in rig.pose.bones:
            b.rotation_mode = 'QUATERNION'
            b.rotation_quaternion = Quaternion((1, 0, 0, 0))
            b.location = Vector((0, 0, 0))
        return act

    # ---- Idle -----------------------------------------------------------
    act = start("Idle")
    for (f, k) in ((1, 0.0), (30, 1.0), (60, 0.0), (90, -1.0), (120, 0.0)):
        key(rig, f, merge(
            spine(lean=2.0 + abs(k) * 1.2, side=k * 1.8, yaw=k * 1.5,
                  head_lean=-1.5 - abs(k), head_yaw=k * 5.0, head_side=k * 2.0,
                  hips_lean=-1.0, hips_side=-k * 2.2, hips_yaw=-k * 1.2,
                  lift=-0.010 * abs(k)),
            arms(swingL=k * 2.5 - 2.0, swingR=-k * 2.5 - 2.0,
                 elbowL=16 + abs(k) * 5, elbowR=16 + abs(k) * 5,
                 outL=abs(k) * 2.0, outR=abs(k) * 2.0),
            legs(swingL=0.5, swingR=-0.5, kneeL=2.0, kneeR=2.0)))
    clips["Idle"] = (act, 120)

    # ---- Walk: 8 samples over 32 frames ---------------------------------
    act = start("Walk")
    for i in range(9):
        f = 1 + i * 4
        ph = 2 * math.pi * i / 8.0
        s = math.sin(ph)
        c = math.cos(ph)
        key(rig, f, merge(
            spine(lean=4.0, side=-s * 1.5, yaw=-c * 3.0,
                  head_lean=-2.0, head_yaw=s * 2.5,
                  hips_lean=-1.0, hips_side=s * 3.0, hips_yaw=c * 4.0,
                  lift=0.016 * abs(math.sin(ph * 2)) - 0.010),
            arms(swingL=-s * 30, swingR=s * 30,
                 elbowL=22 + max(0.0, -s) * 18, elbowR=22 + max(0.0, s) * 18),
            legs(swingL=s * 27, swingR=-s * 27,
                 kneeL=max(0.0, -s) * 46 + 4, kneeR=max(0.0, s) * 46 + 4,
                 footL=-s * 12, footR=s * 12)))
    clips["Walk"] = (act, 33)

    # ---- Run ------------------------------------------------------------
    act = start("Run")
    for i in range(9):
        f = 1 + i * 3
        ph = 2 * math.pi * i / 8.0
        s = math.sin(ph)
        c = math.cos(ph)
        key(rig, f, merge(
            spine(lean=16.0, side=-s * 2.0, yaw=-c * 5.0,
                  head_lean=-12.0, head_yaw=s * 3.0,
                  hips_lean=4.0, hips_side=s * 4.0, hips_yaw=c * 6.0,
                  lift=0.045 * abs(math.sin(ph * 2)) - 0.030),
            arms(swingL=-s * 52, swingR=s * 52,
                 elbowL=76 + max(0.0, -s) * 22, elbowR=76 + max(0.0, s) * 22,
                 down=ARM_DOWN - 8, outL=6, outR=6),
            legs(swingL=s * 48, swingR=-s * 48,
                 kneeL=max(0.0, -s) * 84 + 8, kneeR=max(0.0, s) * 84 + 8,
                 footL=-s * 18, footR=s * 18)))
    clips["Run"] = (act, 25)

    # ---- Turn: plant, swing the hips round, settle ----------------------
    act = start("Turn")
    for (f, t) in ((1, 0.0), (8, 0.22), (18, 0.78), (26, 0.98), (32, 1.0)):
        e = math.sin(t * math.pi)
        key(rig, f, merge(
            spine(lean=3.0 + e * 4.0, yaw=t * 20.0 + e * 14.0,
                  head_yaw=t * 26.0 + e * 20.0, head_lean=-2.0,
                  hips_yaw=t * 88.0, hips_lean=-1.0 - e * 2.0,
                  lift=-e * 0.020),
            arms(swingL=-e * 20, swingR=e * 20, elbowL=18 + e * 14,
                 elbowR=18 + e * 14, outL=e * 6, outR=e * 6),
            legs(swingL=e * 16, swingR=-e * 12, kneeL=e * 22 + 3,
                 kneeR=e * 14 + 3)))
    clips["Turn"] = (act, 32)

    # ---- Talk: gesturing, head nods -------------------------------------
    act = start("Talk")
    for i in range(9):
        f = 1 + i * 12
        t = i / 8.0
        s = math.sin(t * math.pi * 3.2)
        c = math.cos(t * math.pi * 2.4)
        key(rig, f, merge(
            spine(lean=3.0 + s * 2.0, yaw=c * 4.0, side=s * 1.5,
                  head_lean=-3.0 + s * 8.0, head_yaw=c * 10.0,
                  head_side=s * 4.0, hips_lean=-1.0),
            arms(swingL=-42 - s * 16, swingR=-34 - c * 18,
                 elbowL=68 + s * 22, elbowR=60 + c * 24,
                 outL=18 + c * 8, outR=14 + s * 8),
            legs(swingL=1.0, swingR=-1.0, kneeL=3.0, kneeR=3.0)))
        key(rig, f, {"Hand.L": (0, 0, -s * 20), "Hand.R": (0, 0, c * 20)})
    clips["Talk"] = (act, 97)

    # ---- ThrowBall: anticipate, wind up, snap, follow through -----------
    act = start("ThrowBall")
    #    frame, hips yaw, lean, R arm swing, R elbow, L leg, R leg, head yaw
    #    frame, hips yaw, lean, R swing, R elbow, L leg, R leg, head yaw,
    #    R arm lift out (0 = hanging, 74 = straight out sideways)
    throw = [
        (1,    0,    2,   -6,  20,   0,   0,   0,   4),
        (9,   16,   -6,   22,  56,  -8,   6,  10,  38),
        (19,  34,  -12,   62, 112, -18,  12,  22,  74),
        (25,  20,   -4,   50, 104, -10,   6,  14,  68),
        (30, -14,   10,  -30,  44,  16, -12, -10,  46),
        (34, -26,   18,  -76,  16,  26, -18, -18,  16),
        (42, -24,   16,  -96,  20,  22, -16, -20,   6),
        (56, -10,    7,  -44,  30,   9,  -7,  -9,   6),
        (74,   0,    2,   -8,  20,   0,   0,   0,   4),
    ]
    for (f, hy, ln, ar, ae, ll, rl, hyaw, lift) in throw:
        key(rig, f, merge(
            spine(lean=ln, yaw=hy * 0.55, head_yaw=hyaw, head_lean=ln * 0.4,
                  hips_yaw=hy, hips_lean=ln * 0.3,
                  lift=-0.012 * abs(ln) / 18.0),
            arms(swingL=-ar * 0.22 - 6, swingR=ar,
                 elbowL=24 + abs(hy) * 0.30, elbowR=ae,
                 outL=abs(hy) * 0.14, outR=lift),
            legs(swingL=ll, swingR=rl,
                 kneeL=max(0.0, -ll) * 1.3 + 4, kneeR=max(0.0, -rl) * 1.3 + 4)))
        key(rig, f, {"Hand.R": (0, 0, ar * 0.16)})
    clips["ThrowBall"] = (act, 74)

    # ---- Cheer: crouch, leap, arms up, land -----------------------------
    act = start("Cheer")
    cheer = [
        (1,  0.000,   4,  -6,   0),
        (10, -0.055, 16, -14,   6),
        (20, 0.105, -10, 152, -16),
        (28, 0.060,  -6, 140, -12),
        (38, 0.120, -12, 164, -20),
        (48, -0.010,  8, -10,   4),
        (60, 0.000,   4,  -6,   0),
    ]
    for (f, lift, crouch, armswing, headlean) in cheer:
        key(rig, f, merge(
            spine(lean=-crouch * 0.6, head_lean=headlean, hips_lean=crouch * 0.4,
                  lift=lift),
            arms(swingL=-armswing, swingR=-armswing,
                 elbowL=20 + max(0.0, armswing) * 0.10,
                 elbowR=20 + max(0.0, armswing) * 0.10,
                 outL=max(0.0, armswing) * 0.10,
                 outR=max(0.0, armswing) * 0.10),
            legs(swingL=-crouch * 1.5, swingR=-crouch * 1.5,
                 kneeL=max(0.0, crouch) * 3.2 + 3,
                 kneeR=max(0.0, crouch) * 3.2 + 3,
                 footL=max(0.0, -crouch) * 1.4, footR=max(0.0, -crouch) * 1.4)))
    clips["Cheer"] = (act, 60)

    # ---- Surprised: recoil, hands up, settle ----------------------------
    act = start("Surprised")
    sur = [
        (1,   0,   0,    0,  10, 22),
        (5, -14, -12,  -58,  46, 62),
        (12, -20, -18,  -74,  58, 78),
        (24, -12, -11,  -56,  44, 60),
        (44,  -4,  -4,  -22,  20, 32),
        (60,   0,   0,    0,  10, 22),
    ]
    for (f, lean, hips, armswing, out, elbow) in sur:
        key(rig, f, merge(
            spine(lean=lean, head_lean=lean * 1.4, hips_lean=hips,
                  lift=-0.012 * abs(lean) / 20.0),
            arms(swingL=armswing, swingR=armswing, elbowL=elbow, elbowR=elbow,
                 outL=out, outR=out),
            legs(swingL=-hips * 0.5, swingR=-hips * 0.5,
                 kneeL=max(0.0, -hips) * 1.1 + 4,
                 kneeR=max(0.0, -hips) * 1.1 + 4)))
        key(rig, f, {"Hand.L": (0, 0, -elbow * 0.25),
                     "Hand.R": (0, 0, elbow * 0.25)})
    clips["Surprised"] = (act, 60)

    # ---- RaiseScanner: bring the left hand up to eye level, hold, lower --
    act = start("RaiseScanner")
    rs = [
        (1,   0,    0,  16,   0,  0),
        (12, -30,  16,  56,  -4, -3),
        (24, -62,  30,  92,  -9, -6),
        (34, -74,  36, 104, -11, -7),
        (68, -73,  36, 103, -11, -7),
        (80, -44,  22,  72,  -7, -4),
        (94,   0,    0,  16,   0,  0),
    ]
    for (f, armswing, out, elbow, headlean, lean) in rs:
        key(rig, f, merge(
            spine(lean=lean, yaw=out * 0.12, head_lean=headlean,
                  head_yaw=out * 0.30, hips_lean=lean * 0.4),
            {"UpperArm.L": (armswing, ARM_DOWN - out, 0),
             "LowerArm.L": (0, 0, -elbow),
             "Hand.L": (0, 0, -elbow * 0.22),
             "UpperArm.R": (armswing * 0.22 - 4, -ARM_DOWN + out * 0.30, 0),
             "LowerArm.R": (0, 0, elbow * 0.45 + 12)},
            legs(swingL=1.0, swingR=-1.0, kneeL=3.0, kneeR=3.0)))
    clips["RaiseScanner"] = (act, 94)

    return clips


CLIP_ORDER = ["Idle", "Walk", "Run", "Turn", "Talk", "ThrowBall", "Cheer",
              "Surprised", "RaiseScanner"]


# --------------------------------------------------------------------------
# build
# --------------------------------------------------------------------------

def build_character(spec, ms):
    bm = E.bm_new()
    character_mesh(bm, spec)
    obj = E.bm_to_obj(bm, spec['name'], ms.materials())
    E.finalize(obj, smooth_angle=64.0, merge=1e-5)
    E.pivot_to_base(obj, xy='center')
    E.apply_transforms(obj)
    E.uv_all(obj, ms, angle=66.0, margin=0.010)
    return obj


def bind(obj, rig):
    E.deselect_all()
    obj.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type='ARMATURE_AUTO')
    return obj


def add_anchors(rig):
    """Attachment empties the cinematics/VFX workers can parent to."""
    out = []
    for (nm, bone, off) in (("Anchor_Head", "Head", (0, 0, 0.12)),
                            ("Anchor_Body", "Chest", (0, 0, 0.05)),
                            ("Anchor_HandR", "Hand.R", (0.05, 0, 0)),
                            ("Anchor_HandL", "Hand.L", (-0.05, 0, 0))):
        e = bpy.data.objects.new(nm, None)
        e.empty_display_type = 'PLAIN_AXES'
        e.empty_display_size = 0.05
        bpy.context.scene.collection.objects.link(e)
        e.parent = rig
        e.parent_type = 'BONE'
        e.parent_bone = bone
        e.matrix_parent_inverse = Matrix.Identity(4)
        e.location = Vector(off)
        out.append(e)
    return out


def render_pose_sheet(rig, mesh, clips, samples=6, only=None,
                      out='poses_player.png', cell_y=2.30):
    """Render the clips from the LIVE rig.  Re-importing the exported FBX is
    not a valid check: Blender's importer with automatic_bone_orientation
    rebuilds every bone's local axes, so quaternions authored against the
    original rest basis come back as garbage."""
    baked = []
    cell_x = 0.95
    order = only or CLIP_ORDER
    for ci, cname in enumerate(order):
        act, end = clips[cname]
        rig.animation_data.action = act
        cy = -ci * cell_y
        E.text_label(cname, (-cell_x * (samples / 2.0 + 0.70), cy, 0.95),
                     size=0.20)
        for si in range(samples):
            frame = int(1 + (end - 1) * si / float(samples - 1))
            bpy.context.scene.frame_set(frame)
            dg = bpy.context.evaluated_depsgraph_get()
            ev = mesh.evaluated_get(dg)
            me = bpy.data.meshes.new_from_object(ev)
            nobj = bpy.data.objects.new("P_%s_%d" % (cname, si), me)
            bpy.context.scene.collection.objects.link(nobj)
            nobj.location = ((si - (samples - 1) * 0.5) * cell_x, cy, 0)
            baked.append(nobj)
    for o in list(bpy.context.scene.objects):
        if o.type in ('MESH', 'ARMATURE') and o not in baked and \
                not o.name.startswith("P_"):
            o.hide_render = True
    E.setup_render(res=(2200, 1500), samples=48, world_rgb=(0.56, 0.68, 0.86),
                   strength=0.55)
    E.add_studio_lights(key_energy=3.1, scale=2.0)
    g = E.ground_plane(size=140, rgb=(0.46, 0.50, 0.47))
    cam = E.add_camera((0, -30, 8), (0, 0, 1.0), lens=50)
    E.fit_camera(cam, baked + [o for o in bpy.context.scene.objects
                               if o.type == 'FONT'],
                 direction=Vector((0.0, -1.0, 0.30)).normalized(),
                 margin=1.04, ortho=True)
    E.render_to(os.path.join(E.PREVIEWS, out))
    E.log("wrote %s (%d clips x %d samples, live rig)"
          % (out, len(order), samples))
    for o in baked:
        bpy.data.objects.remove(o, do_unlink=True)
    for o in list(bpy.context.scene.objects):
        if o.type == 'FONT' or o.name in ('Ground', 'Sun', 'Fill', 'Rim', 'Cam'):
            bpy.data.objects.remove(o, do_unlink=True)
        else:
            o.hide_render = False


def main():
    E.ensure_dirs()
    T.ensure_atlas(FAM)
    E.reset_scene()
    ms = T.full_matset(FAM)
    ap = T.atlas_paths(FAM)
    part = []
    problems = []

    # one shared skeleton; the player rig is the one that carries the clips
    rigs = {}
    meshes = {}
    for spec in CHARACTERS:
        rig = build_armature(spec['name'] + "_Rig")
        obj = build_character(spec, ms)
        tris, probs = E.validate(obj, budget=(3000, 8000), need_vcol=False,
                                 strict=False)
        bind(obj, rig)
        add_anchors(rig)
        rigs[spec['name']] = rig
        meshes[spec['name']] = (obj, tris, probs)
        if probs:
            problems.append((spec['name'], probs))
        E.log("%-30s %5d tris  %s" % (spec['name'], tris, probs or "ok"))

    # author the clips on the player rig
    player = rigs["Env_Char_Player"]
    clips = make_clips(player)

    clip_paths = {}
    for cname in CLIP_ORDER:
        act, end = clips[cname]
        player.animation_data.action = act
        sc = bpy.context.scene
        sc.frame_start = 1
        sc.frame_end = end
        p = os.path.join(OUT, "Env_Char_Player@%s.fbx" % cname)
        E.export_fbx([player], p, anim=True, anim_actions=False)
        clip_paths[cname] = p
        E.log("  clip %-14s %3d frames -> %s" % (cname, end,
                                                 os.path.basename(p)))
    player.animation_data.action = clips["Idle"][0]

    for spec in CHARACTERS:
        name = spec['name']
        obj, tris, probs = meshes[name]
        rig = rigs[name]
        path = os.path.join(OUT, name + ".fbx")
        lods = []
        if tris > 2000:
            E.deselect_all()
            for lo in E.make_lods(obj, (0.40, 0.15)):
                # LODs are skinned copies: keep the armature modifier
                lp = os.path.join(OUT, lo.name + ".fbx")
                E.export_fbx([lo, rig], lp, anim=False)
                lods.append((lp, E.tri_count(lo)))
                E.delete_obj(lo)
        E.export_fbx([obj, rig], path, anim=False)
        part.append({
            "name": name, "family": FAM, "subfamily": "Character",
            "path": os.path.relpath(path, E.REPO).replace("\\", "/"),
            "triangles": tris,
            "lods": [{"level": i + 1,
                      "path": os.path.relpath(p, E.REPO).replace("\\", "/"),
                      "triangles": t} for i, (p, t) in enumerate(lods)],
            "pivot": "between the feet at ground level, facing +Z",
            "textures": [os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
                         os.path.relpath(ap["normal"], E.REPO).replace("\\", "/")],
            "windVertexColors": False,
            "rig": "Humanoid-compatible; T-pose rest; bones Hips/Spine/Chest/"
                   "UpperChest/Neck/Head + Shoulder,UpperArm,LowerArm,Hand and "
                   "UpperLeg,LowerLeg,Foot,Toes in .L/.R",
            "anchors": ["Anchor_Head", "Anchor_Body", "Anchor_HandR",
                        "Anchor_HandL"],
            "clips": [{"name": c,
                       "path": os.path.relpath(clip_paths[c], E.REPO).replace("\\", "/")}
                      for c in CLIP_ORDER],
            "notes": "Clips are authored once on the player rig; all four "
                     "characters share identical bone names and proportions, "
                     "so the same clips drive every one of them.",
        })

    if "--poses" in sys.argv:
        m = meshes["Env_Char_Player"][0]
        render_pose_sheet(player, m, clips)
        render_pose_sheet(player, m, clips, samples=8,
                          only=["ThrowBall", "RaiseScanner"],
                          out="poses_player_hero.png", cell_y=3.4)

    E.write_part(FAM, part)
    E.log("---- %d characters, %d clips, %d with problems"
          % (len(part), len(CLIP_ORDER), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))
    if "--keep" in sys.argv:
        bpy.ops.wm.save_as_mainfile(
            filepath=os.path.join(E.PREVIEWS, "_characters.blend"))


if __name__ == "__main__":
    main()
