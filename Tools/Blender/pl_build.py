"""
Poke Lab creature framework - build pipeline.

One enforced path from a creature script to shipped assets, so no creature can
quietly skip a contract requirement:

    parts -> join -> cleanup -> crease/sharp/smooth -> bevel every hard edge
          -> fit to true metre size and ground the feet -> smart UV
          -> bake base colour + normal at 1024
          -> shape keys (Blink / Squint)
          -> armature from the same plan -> automatic weights -> anchors
          -> 14 animation clips -> validate -> FBX (+ LOD1) -> manifest entry

Run:  blender --background --python Tools/Blender/build_all.py -- [ids...]
"""

import bpy
import importlib
import math
import os
import sys
import traceback
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import pl_core as C
import pl_rig as R
import pl_anim as A
import pl_bodyplans as B
import pl_render as PR

importlib.reload(C)
importlib.reload(R)
importlib.reload(A)
importlib.reload(B)
importlib.reload(PR)


def _plan_transform(plan, s, off):
    def t(co):
        return (co[0] * s + off.x, co[1] * s + off.y, co[2] * s + off.z)

    def tc(chain):
        return [tuple([t(item[0])] + list(item[1:])) for item in chain]

    plan.spine = [tuple([t(i[0])] + [v * s for v in i[1:]]) for i in plan.spine]
    for attr in ('arms', 'legs', 'tail', 'wings'):
        chain = getattr(plan, attr)
        if chain and isinstance(chain[0][0], (tuple, list, Vector)):
            setattr(plan, attr, [tuple([t(i[0])] + [v * s for v in i[1:]])
                                 for i in chain])
    plan.head_co = Vector(t(plan.head_co))
    plan.head_size = tuple(v * s for v in plan.head_size)
    plan.height = plan.height * s
    _ = tc
    return plan


def build_creature(mod, render=True, engine='BLENDER_EEVEE', strict=True,
                   texture_size=1024):
    name = "Creature_%d_%s" % (mod.ID, mod.NAME)
    print("\n=== building %s ===" % name)
    C.reset_scene()
    C.ensure_dirs()

    result = mod.build()
    parts = [p for p in result['parts'] if p is not None]
    plan = result['plan']

    if os.environ.get("PL_PARTS"):
        for p in parts:
            print("    part %-34s %5d tris" % (p.name, C.tri_count(p)))
    mesh = C.join_objects(parts, name)
    C.cleanup_mesh(mesh)
    print("  joined: %d tris" % C.tri_count(mesh))
    B.finish_mesh(mesh, mod.HEIGHT,
                  bevel_scale=result.get('bevel_scale', 0.012),
                  bevel_segments=result.get('bevel_segments', 2),
                  smooth_angle=result.get('smooth_angle', 38.0),
                  sharp_angle=result.get('sharp_angle', 46.0),
                  crease=result.get('crease', False),
                  tri_max=getattr(mod, 'TRI_MAX', 8000))

    size_axis = getattr(mod, 'SIZE_AXIS', 'z')
    s, off = C.fit_size(mesh, mod.HEIGHT, axis=size_axis, ground=True, center_xy=True)
    _plan_transform(plan, s, off)
    eye_centres = [Vector(c) * s + off for c in result.get('eye_centres', [])]
    eye_radius = result.get('eye_radius', 0.0) * s

    mn, mx = C.bbox(mesh)
    true_height = mx.z - mn.z

    C.unwrap(mesh, angle_deg=result.get('uv_angle', 66.0),
             margin=result.get('uv_margin', 0.012))

    tex = C.bake_textures(mesh, name, size=texture_size,
                          albedo_kwargs=result.get('albedo', None),
                          normal_kwargs=result.get('normal', None))

    if eye_centres and eye_radius > 0.0:
        C.shape_key_blink(mesh, eye_centres, eye_radius * 1.55)
        C.shape_key_squint(mesh, eye_centres, eye_radius * 1.75)

    rig = plan.build_rig(name + "_Rig",
                         jaw=result.get('jaw', True),
                         muzzle_y=result.get('muzzle_y', None),
                         ear_pts=result.get('ear_pts', None),
                         extra=result.get('rig_extra', None))
    C.apply_transforms(rig.obj)
    R.skin_mesh(mesh, rig)

    # anchors, derived from the finished geometry so none can be forgotten
    head_bone = result.get('anchor_bones', {}).get('head', rig.roles.get('head'))
    body_bone = result.get('anchor_bones', {}).get(
        'body', (rig.bones('spine') or [rig.roles.get('hips')])[-1])
    muzzle_bone = result.get('anchor_bones', {}).get(
        'muzzle', rig.roles.get('jaw') or rig.roles.get('head'))
    hd = plan.head_size
    default_head = Vector((0.0, plan.head_co.y, mx.z + true_height * 0.015))
    default_body = Vector((0.0, (mn.y + mx.y) * 0.5, (mn.z + mx.z) * 0.5))
    default_muzzle = Vector(plan.head_co) + B.FRONT * (hd[1] * 0.55)
    ov = result.get('anchors', {})
    head_co = Vector(ov['head']) * s + off if 'head' in ov else default_head
    body_co = Vector(ov['body']) * s + off if 'body' in ov else default_body
    muzzle_co = Vector(ov['muzzle']) * s + off if 'muzzle' in ov else default_muzzle
    anchors = R.add_anchors(rig, head_bone, body_bone, muzzle_bone,
                            head_co, body_co, muzzle_co)

    follow = R.verify_anchors_follow(rig, anchors)
    print("  anchors move with the head bone: %s" % follow)
    if follow and follow.get('Anchor_Head', 0.0) < 1e-5:
        print("  !! Anchor_Head does not follow its bone")

    profile = dict(A.DEFAULT_PROFILE)
    profile.update(getattr(mod, 'PROFILE', {}))
    profile['height'] = true_height
    clips = A.build_all_clips(rig, profile)

    report = C.validate(mesh, rig.obj, tri_min=getattr(mod, 'TRI_MIN', 2000),
                        tri_max=getattr(mod, 'TRI_MAX', 8000), strict=False)
    print("  validation: %s" % {k: v for k, v in report.items() if k != 'problems'})
    if report['problems']:
        print("  !! PROBLEMS: %s" % report['problems'])
        if strict:
            raise C.ValidationError("%s: %s" % (name, report['problems']))

    fbx_path = os.path.join(C.ART_DIR, name + ".fbx")
    C.export_fbx([mesh, rig.obj] + anchors, fbx_path, bake_anim=True)

    lod_entry = None
    if report['tris'] > 5000:
        lod = C.make_lod(mesh, rig.obj, 0.40, name + "_LOD1")
        lod.shape_key_clear()
        lod_path = os.path.join(C.ART_DIR, name + "_LOD1.fbx")
        C.export_fbx([lod, rig.obj] + anchors, lod_path, bake_anim=False)
        lod_entry = dict(fbx=os.path.relpath(lod_path, C.REPO_ROOT).replace('\\', '/'),
                         tris=C.tri_count(lod))
        bpy.data.objects.remove(lod, do_unlink=True)

    portrait_rel = None
    if render:
        try:
            sheet_objs = [mesh]
            portrait = os.path.join(C.ART_DIR, name + "_Portrait.png")
            PR.render_portrait(sheet_objs, name, portrait, true_height, engine=engine)
            portrait_rel = os.path.relpath(portrait, C.REPO_ROOT).replace('\\', '/')
            PR.render_contact_sheet(sheet_objs, name, C.PREVIEW_DIR, true_height,
                                    engine=engine)
            PR.render_pose_sheet(sheet_objs, rig.obj, clips, name, C.PREVIEW_DIR,
                                 true_height, engine=engine)
        except Exception:
            print("  ! preview render failed:\n%s" % traceback.format_exc())

    entry = {
        "id": mod.ID,
        "nameEn": mod.NAME,
        "types": list(mod.TYPES),
        "design": getattr(mod, 'DESIGN', ''),
        "bodyPlan": plan.kind,
        "fbx": os.path.relpath(fbx_path, C.REPO_ROOT).replace('\\', '/'),
        "lod1": lod_entry,
        "textures": tex,
        "portrait": portrait_rel,
        "displayHeight": round(true_height, 4),
        "authoredSize": {"axis": size_axis, "metres": round(mod.HEIGHT, 4)},
        "shoulderHeight": round(float(plan.spine[-1][0][2]) if plan.spine else true_height * 0.6, 4),
        "boundsMetres": {"min": [round(v, 4) for v in mn], "max": [round(v, 4) for v in mx]},
        "triangles": report['tris'],
        "quadRatio": report['quad_ratio'],
        "bones": report.get('bones', 0),
        "anchors": report['anchors'],
        "shapeKeys": [k.name for k in (mesh.data.shape_keys.key_blocks if mesh.data.shape_keys else [])][1:],
        "clips": [c['name'] for c in clips],
        "clipDetail": clips,
        "missingClips": [n for n, _, _ in A.CLIPS if n not in {c['name'] for c in clips}],
        "validation": {k: v for k, v in report.items() if k != 'problems'},
        "problems": report['problems'],
    }
    print("  -> %s  (%d tris, %d clips)" % (entry['fbx'], entry['triangles'],
                                            len(entry['clips'])))
    return entry
