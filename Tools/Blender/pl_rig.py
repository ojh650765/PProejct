"""
Poke Lab creature framework - rigging layer.

Standard armature template shared by every creature. Bone names are stable across
the cast so one animation library drives all twelve:

    Root
      Hips -> Spine -> Chest -> Neck -> Head -> Jaw
      Thigh_L/R -> Shin_L/R -> Foot_L/R
      Shoulder_L/R -> UpperArm_L/R -> Forearm_L/R -> Hand_L/R
      Tail_1..n, Wing_L/R_1..n, Ear_L/R, plus creature specific extras

Not every creature has every chain; the animation layer keys whatever exists.
Anchors (Anchor_Head / Anchor_Body / Anchor_Muzzle) are empties bone-parented into
the armature, exactly as the integration contract requires.
"""

import bpy
import math
from mathutils import Vector, Matrix, Quaternion

from pl_core import activate, link, apply_transforms


class Rig(object):
    def __init__(self, obj, roles):
        self.obj = obj
        self.roles = roles

    @property
    def name(self):
        return self.obj.name

    def has(self, role):
        v = self.roles.get(role)
        return bool(v)

    def bones(self, role):
        v = self.roles.get(role)
        if not v:
            return []
        if isinstance(v, str):
            return [v]
        return list(v)

    def pose(self, name):
        return self.obj.pose.bones.get(name)


def build_armature(name, bone_specs, roles):
    """bone_specs: list of dicts with keys name, head, tail, parent, connect, deform, roll."""
    arm_data = bpy.data.armatures.new(name)
    arm = bpy.data.objects.new(name, arm_data)
    link(arm)
    activate(arm)
    bpy.ops.object.mode_set(mode='EDIT')
    eb = arm_data.edit_bones
    created = {}
    for spec in bone_specs:
        b = eb.new(spec['name'])
        b.head = Vector(spec['head'])
        b.tail = Vector(spec['tail'])
        b.roll = spec.get('roll', 0.0)
        b.use_deform = spec.get('deform', True)
        created[spec['name']] = b
    for spec in bone_specs:
        p = spec.get('parent')
        if p:
            created[spec['name']].parent = created[p]
            created[spec['name']].use_connect = bool(spec.get('connect', False))
    bpy.ops.object.mode_set(mode='OBJECT')
    for b in arm_data.bones:
        b.use_inherit_rotation = True
    return Rig(arm, roles)


def bone_chain(prefix, points, parent=None, connect=True, deform=True, count_from=1):
    """Build a connected chain of bones through a polyline of points."""
    specs = []
    prev = parent
    for i in range(len(points) - 1):
        nm = "%s_%d" % (prefix, i + count_from) if len(points) > 2 else prefix
        specs.append(dict(name=nm, head=points[i], tail=points[i + 1], parent=prev,
                          connect=connect and prev is not None, deform=deform))
        prev = nm
    return specs


def _bone_segments(arm):
    out = []
    for b in arm.data.bones:
        if not b.use_deform:
            continue
        out.append((b.name, Vector(b.head_local), Vector(b.tail_local),
                    max(1e-5, b.length)))
    return out


def _dist_to_segment(p, a, b):
    ab = b - a
    L2 = ab.length_squared
    if L2 < 1e-12:
        return (p - a).length
    t = max(0.0, min(1.0, (p - a).dot(ab) / L2))
    return (p - (a + ab * t)).length


def distance_weights(mesh_obj, rig, max_influences=4, reach=1.9, floor=0.10,
                     smooth_iterations=6, smooth_factor=0.55):
    """Deterministic smooth skinning by distance to each bone segment.

    Blender's heat weighting needs a watertight single-shell mesh; a creature
    assembled from a body, a head, eyes, leaves and shell plates is none of those,
    and heat weighting bails out ("failed to find solution for one or more bones"),
    leaving rigid single-bone limbs. This does the job robustly instead:

      * a smooth radial falloff per bone, with neighbouring bones deliberately
        overlapping so every joint blends across at least two bones,
      * top-N influences kept and normalised,
      * several Laplacian smoothing passes over the mesh graph, which is what
        actually kills the faceted-deformation look.
    """
    arm = rig.obj
    segs = _bone_segments(arm)
    if not segs:
        return mesh_obj
    me = mesh_obj.data
    nv = len(me.vertices)
    names = [s[0] for s in segs]
    scale = max(mesh_obj.dimensions) or 1.0

    weights = [dict() for _ in range(nv)]
    for vi, v in enumerate(me.vertices):
        p = Vector(v.co)
        raw = []
        for (nm, a, b, L) in segs:
            R = L * reach + scale * floor
            dd = _dist_to_segment(p, a, b)
            if dd >= R:
                continue
            w = (1.0 - dd / R)
            raw.append((w * w * w, nm))
        if not raw:
            best = min(segs, key=lambda s: _dist_to_segment(p, s[1], s[2]))
            raw = [(1.0, best[0])]
        raw.sort(reverse=True)
        raw = raw[:max_influences]
        tot = sum(w for w, _ in raw) or 1.0
        weights[vi] = {nm: w / tot for w, nm in raw}

    # Laplacian smoothing over the edge graph
    if smooth_iterations:
        adj = [[] for _ in range(nv)]
        for e in me.edges:
            a, b = e.vertices
            adj[a].append(b)
            adj[b].append(a)
        for _ in range(smooth_iterations):
            new = []
            for vi in range(nv):
                acc = dict(weights[vi])
                for k in acc:
                    acc[k] *= (1.0 - smooth_factor)
                nb = adj[vi]
                if nb:
                    share = smooth_factor / len(nb)
                    for j in nb:
                        for k, w in weights[j].items():
                            acc[k] = acc.get(k, 0.0) + w * share
                items = sorted(acc.items(), key=lambda kv: -kv[1])[:max_influences]
                tot = sum(w for _, w in items) or 1.0
                new.append({k: w / tot for k, w in items})
            weights = new

    for nm in names:
        if nm not in mesh_obj.vertex_groups:
            mesh_obj.vertex_groups.new(name=nm)
    buckets = {nm: [] for nm in names}
    for vi in range(nv):
        for nm, w in weights[vi].items():
            if w > 0.002:
                buckets[nm].append((vi, w))
    for nm, items in buckets.items():
        vg = mesh_obj.vertex_groups[nm]
        for vi, w in items:
            vg.add([vi], w, 'REPLACE')
    return mesh_obj


def skin_mesh(mesh_obj, rig, **kwargs):
    """Parent the mesh to the armature and weight it."""
    arm = rig.obj
    activate(mesh_obj)
    mesh_obj.parent = arm
    # leave the parent inverse at identity - setting it to the parent's inverted
    # world matrix is the "keep transform" idiom and cancels the parent
    mesh_obj.matrix_parent_inverse = Matrix.Identity(4)
    mod = mesh_obj.modifiers.new("Armature", 'ARMATURE')
    mod.object = arm
    mod.use_vertex_groups = True
    distance_weights(mesh_obj, rig, **kwargs)
    _assign_jaw(mesh_obj, rig)
    try:
        bpy.ops.object.vertex_group_normalize_all(lock_active=False)
    except Exception:
        pass
    return mesh_obj


def _assign_jaw(mesh_obj, rig):
    jaw = rig.roles.get('jaw')
    if not jaw:
        return
    src = mesh_obj.vertex_groups.get("Mouth")
    if src is None:
        return
    dst = mesh_obj.vertex_groups.get(jaw) or mesh_obj.vertex_groups.new(name=jaw)
    head_g = mesh_obj.vertex_groups.get(rig.roles.get('head', ''))
    for v in mesh_obj.data.vertices:
        w = 0.0
        for g in v.groups:
            if g.group == src.index:
                w = g.weight
        if w <= 0.01:
            continue
        w = min(1.0, w * 1.15)
        dst.add([v.index], w, 'REPLACE')
        if head_g is not None:
            cur = 0.0
            for g in v.groups:
                if g.group == head_g.index:
                    cur = g.weight
            head_g.add([v.index], max(0.0, cur * (1.0 - w)), 'REPLACE')


def _smooth_weights(mesh_obj, iterations=2, factor=0.5):
    activate(mesh_obj)
    try:
        bpy.ops.object.mode_set(mode='WEIGHT_PAINT')
        bpy.ops.object.vertex_group_smooth(group_select_mode='ALL',
                                           factor=factor, repeat=iterations)
        bpy.ops.object.mode_set(mode='OBJECT')
    except Exception as exc:
        print("  ! weight smooth: %s" % exc)
        try:
            bpy.ops.object.mode_set(mode='OBJECT')
        except Exception:
            pass
    try:
        bpy.ops.object.vertex_group_normalize_all(lock_active=False)
    except Exception:
        pass


def add_anchors(rig, head_bone, body_bone, muzzle_bone, head_co, body_co, muzzle_co,
                extra=None):
    """Empties bone-parented into the armature. Health bars, hit VFX and projectiles
    attach to these - a missing one breaks battle framing."""
    arm = rig.obj
    specs = [("Anchor_Head", head_bone, head_co),
             ("Anchor_Body", body_bone, body_co),
             ("Anchor_Muzzle", muzzle_bone, muzzle_co)]
    for nm, bn, co in (extra or []):
        specs.append((nm, bn, co))
    out = []
    for nm, bone_name, co in specs:
        e = bpy.data.objects.new(nm, None)
        e.empty_display_type = 'PLAIN_AXES'
        e.empty_display_size = 0.03
        link(e)
        e.parent = arm
        if bone_name and bone_name in arm.data.bones:
            e.parent_type = 'BONE'
            e.parent_bone = bone_name
            bone = arm.data.bones[bone_name]
            # bone-parenting is relative to the bone tail in bone space
            mat = arm.matrix_world @ bone.matrix_local
            tail_off = Vector((0, bone.length, 0))
            local = mat.inverted() @ Vector(co)
            e.location = local - tail_off
        else:
            e.parent_type = 'OBJECT'
            e.location = Vector(co)
        out.append(e)
    bpy.context.view_layer.update()
    return out


def verify_anchors_follow(rig, anchors, test_bone=None, angle_deg=35.0):
    """Prove the anchors actually inherit bone motion.

    Symptom of getting the parent inverse wrong is that posing the rig does not
    move the anchors, which would silently attach every health bar and hit VFX to
    the wrong place. So pose a bone, measure, and put it back.
    """
    arm = rig.obj
    bone = test_bone or rig.roles.get('head')
    pb = arm.pose.bones.get(bone) if bone else None
    if pb is None:
        return {}
    bpy.context.view_layer.update()
    before = {a.name: a.matrix_world.translation.copy() for a in anchors}
    old_mode = pb.rotation_mode
    pb.rotation_mode = 'QUATERNION'
    old = pb.rotation_quaternion.copy()
    pb.rotation_quaternion = Quaternion((1, 0, 0), __import__('math').radians(angle_deg))
    bpy.context.view_layer.update()
    after = {a.name: a.matrix_world.translation.copy() for a in anchors}
    pb.rotation_quaternion = old
    pb.rotation_mode = old_mode
    bpy.context.view_layer.update()
    return {k: round((after[k] - before[k]).length, 6) for k in before}


# ---------------------------------------------------------------------------
# standard skeleton generators
# ---------------------------------------------------------------------------

def biped_skeleton(hips_z, spine_pts, head_top, arm_pts, leg_pts, tail_pts=None,
                   ear_pts=None, extra=None, jaw=True, muzzle_y=None):
    """Returns (bone_specs, roles) for a two-legged creature.

    spine_pts: [hips, spine, chest, neck, head] world positions (Z up, +Y is front)
    arm_pts:   [shoulder, elbow, wrist, tip] for the LEFT side (x > 0)
    leg_pts:   [hip, knee, ankle, toe] for the LEFT side
    """
    specs = []
    roles = {}
    specs.append(dict(name='Root', head=(0, 0, 0), tail=(0, 0, hips_z * 0.35),
                      parent=None, deform=False))
    roles['root'] = 'Root'

    names = ['Hips', 'Spine', 'Chest', 'Neck', 'Head']
    chain_parent = 'Root'
    for i in range(len(spine_pts) - 1):
        nm = names[i] if i < len(names) else 'Spine_%d' % i
        specs.append(dict(name=nm, head=spine_pts[i], tail=spine_pts[i + 1],
                          parent=chain_parent, connect=(i > 0)))
        chain_parent = nm
    head_name = names[min(len(spine_pts) - 2, len(names) - 1)]
    roles['hips'] = 'Hips'
    roles['spine'] = [n for n in ('Spine', 'Chest') if any(s['name'] == n for s in specs)]
    roles['neck'] = 'Neck' if any(s['name'] == 'Neck' for s in specs) else None
    roles['head'] = head_name

    if jaw:
        hp = Vector(spine_pts[-1])
        my = muzzle_y if muzzle_y is not None else hp.y - 0.12
        specs.append(dict(name='Jaw', head=(0, hp.y, hp.z),
                          tail=(0, my, hp.z - 0.02), parent=head_name))
        roles['jaw'] = 'Jaw'

    arms = []
    if arm_pts:
        for side, sx in (('L', 1.0), ('R', -1.0)):
            pts = [(p[0] * sx, p[1], p[2]) for p in arm_pts]
            chain = ['Shoulder_%s' % side, 'UpperArm_%s' % side,
                     'Forearm_%s' % side, 'Hand_%s' % side]
            parent = roles['spine'][-1] if roles['spine'] else 'Hips'
            seq = []
            for i in range(len(pts) - 1):
                nm = chain[i] if i < len(chain) else 'Arm_%s_%d' % (side, i)
                specs.append(dict(name=nm, head=pts[i], tail=pts[i + 1],
                                  parent=parent, connect=(i > 1)))
                parent = nm
                seq.append(nm)
            arms.append(seq)
    roles['arms'] = arms

    legs = []
    if leg_pts:
        for side, sx in (('L', 1.0), ('R', -1.0)):
            pts = [(p[0] * sx, p[1], p[2]) for p in leg_pts]
            chain = ['Thigh_%s' % side, 'Shin_%s' % side, 'Foot_%s' % side,
                     'Toe_%s' % side]
            parent = 'Hips'
            seq = []
            for i in range(len(pts) - 1):
                nm = chain[i] if i < len(chain) else 'Leg_%s_%d' % (side, i)
                specs.append(dict(name=nm, head=pts[i], tail=pts[i + 1],
                                  parent=parent, connect=(i > 0)))
                parent = nm
                seq.append(nm)
            legs.append(seq)
    roles['legs'] = legs

    if tail_pts:
        specs.extend(bone_chain('Tail', tail_pts, parent='Hips'))
        roles['tail'] = ["Tail_%d" % (i + 1) for i in range(len(tail_pts) - 1)]
    else:
        roles['tail'] = []

    if ear_pts:
        ears = []
        for side, sx in (('L', 1.0), ('R', -1.0)):
            pts = [(p[0] * sx, p[1], p[2]) for p in ear_pts]
            nm = 'Ear_%s' % side
            sub = bone_chain(nm, pts, parent=head_name)
            for s in sub:
                specs.append(s)
            ears.append([s['name'] for s in sub])
        roles['ears'] = ears
    else:
        roles['ears'] = []

    if extra:
        for spec_list, role_key in extra:
            specs.extend(spec_list)
            if role_key:
                roles.setdefault(role_key, []).append([s['name'] for s in spec_list])

    roles['plan'] = 'biped'
    return specs, roles


def quadruped_skeleton(spine_pts, front_leg_pts, back_leg_pts, head_pts,
                       tail_pts=None, ear_pts=None, extra=None, jaw=True,
                       muzzle_y=None):
    """spine_pts run back-to-front: [hips, spine, chest]; head_pts: [neck_base, head]."""
    specs = []
    roles = {}
    root_z = spine_pts[0][2]
    specs.append(dict(name='Root', head=(0, 0, 0), tail=(0, 0, root_z * 0.4),
                      parent=None, deform=False))
    roles['root'] = 'Root'

    names = ['Hips', 'Spine', 'Chest']
    parent = 'Root'
    for i in range(len(spine_pts) - 1):
        nm = names[i] if i < len(names) else 'Spine_%d' % i
        specs.append(dict(name=nm, head=spine_pts[i], tail=spine_pts[i + 1],
                          parent=parent, connect=(i > 0)))
        parent = nm
    chest = parent
    roles['hips'] = 'Hips'
    roles['spine'] = [s['name'] for s in specs if s['name'] in ('Spine', 'Chest')]

    specs.append(dict(name='Neck', head=head_pts[0], tail=head_pts[1], parent=chest))
    specs.append(dict(name='Head', head=head_pts[1], tail=head_pts[2], parent='Neck',
                      connect=True))
    roles['neck'] = 'Neck'
    roles['head'] = 'Head'

    if jaw:
        hp = Vector(head_pts[1])
        tp = Vector(head_pts[2])
        my = muzzle_y if muzzle_y is not None else tp.y
        specs.append(dict(name='Jaw', head=(0, hp.y, hp.z), tail=(0, my, tp.z - 0.02),
                          parent='Head'))
        roles['jaw'] = 'Jaw'

    legs = []
    arms = []
    for label, pts_l, parent_bone, chain in (
            ('front', front_leg_pts, chest,
             ['Shoulder_%s', 'UpperArm_%s', 'Forearm_%s', 'Hand_%s']),
            ('back', back_leg_pts, 'Hips',
             ['Thigh_%s', 'Shin_%s', 'Foot_%s', 'Toe_%s'])):
        if not pts_l:
            continue
        for side, sx in (('L', 1.0), ('R', -1.0)):
            pts = [(p[0] * sx, p[1], p[2]) for p in pts_l]
            parent = parent_bone
            seq = []
            for i in range(len(pts) - 1):
                nm = (chain[i] % side) if i < len(chain) else '%s_%s_%d' % (label, side, i)
                specs.append(dict(name=nm, head=pts[i], tail=pts[i + 1],
                                  parent=parent, connect=(i > 0 and label == 'back')))
                parent = nm
                seq.append(nm)
            (arms if label == 'front' else legs).append(seq)
    roles['arms'] = arms
    roles['legs'] = legs

    if tail_pts:
        specs.extend(bone_chain('Tail', tail_pts, parent='Hips'))
        roles['tail'] = ["Tail_%d" % (i + 1) for i in range(len(tail_pts) - 1)]
    else:
        roles['tail'] = []

    if ear_pts:
        ears = []
        for side, sx in (('L', 1.0), ('R', -1.0)):
            pts = [(p[0] * sx, p[1], p[2]) for p in ear_pts]
            sub = bone_chain('Ear_%s' % side, pts, parent='Head')
            specs.extend(sub)
            ears.append([s['name'] for s in sub])
        roles['ears'] = ears
    else:
        roles['ears'] = []

    if extra:
        for spec_list, role_key in extra:
            specs.extend(spec_list)
            if role_key:
                roles.setdefault(role_key, []).append([s['name'] for s in spec_list])

    roles['plan'] = 'quadruped'
    return specs, roles


def floater_skeleton(core_z, head_pts, tendrils=None, extra=None, jaw=True,
                     muzzle_y=None):
    """For creatures with no contact with the ground - gas clouds, hoverers."""
    specs = [dict(name='Root', head=(0, 0, 0), tail=(0, 0, core_z * 0.4), parent=None,
                  deform=False),
             dict(name='Hips', head=head_pts[0], tail=head_pts[1], parent='Root')]
    roles = {'root': 'Root', 'hips': 'Hips', 'spine': [], 'plan': 'floater',
             'arms': [], 'legs': [], 'tail': [], 'ears': []}
    specs.append(dict(name='Head', head=head_pts[1], tail=head_pts[2], parent='Hips',
                      connect=True))
    roles['head'] = 'Head'
    roles['neck'] = None
    if jaw:
        hp = Vector(head_pts[1])
        my = muzzle_y if muzzle_y is not None else head_pts[2][1]
        specs.append(dict(name='Jaw', head=(0, hp.y, hp.z), tail=(0, my, hp.z - 0.03),
                          parent='Head'))
        roles['jaw'] = 'Jaw'
    if tendrils:
        groups = []
        for i, pts in enumerate(tendrils):
            sub = bone_chain('Tendril%d' % (i + 1), pts, parent='Hips')
            specs.extend(sub)
            groups.append([s['name'] for s in sub])
        roles['tendrils'] = groups
    if extra:
        for spec_list, role_key in extra:
            specs.extend(spec_list)
            if role_key:
                roles.setdefault(role_key, []).append([s['name'] for s in spec_list])
    return specs, roles


def avian_skeleton(spine_pts, head_pts, wing_pts, leg_pts, tail_pts=None, extra=None,
                   jaw=True, muzzle_y=None):
    specs, roles = quadruped_skeleton(spine_pts, None, leg_pts, head_pts,
                                      tail_pts=tail_pts, jaw=jaw, muzzle_y=muzzle_y)
    wings = []
    chest = roles['spine'][-1] if roles['spine'] else 'Hips'
    for side, sx in (('L', 1.0), ('R', -1.0)):
        pts = [(p[0] * sx, p[1], p[2]) for p in wing_pts]
        parent = chest
        seq = []
        for i in range(len(pts) - 1):
            nm = 'Wing_%s_%d' % (side, i + 1)
            specs.append(dict(name=nm, head=pts[i], tail=pts[i + 1], parent=parent,
                              connect=(i > 0)))
            parent = nm
            seq.append(nm)
        wings.append(seq)
    roles['wings'] = wings
    roles['plan'] = 'avian'
    if extra:
        for spec_list, role_key in extra:
            specs.extend(spec_list)
            if role_key:
                roles.setdefault(role_key, []).append([s['name'] for s in spec_list])
    return specs, roles
