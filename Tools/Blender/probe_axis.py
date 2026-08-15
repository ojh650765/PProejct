"""Determine exactly what Blender 4.0's FBX exporter does to geometry and to the
FBX axis metadata under different axis_forward/axis_up settings.

Run: blender --background --python Tools/Blender/probe_axis.py

The answer decides which way creatures are modelled, so it is worth proving rather
than assuming. Findings are printed and also written to previews/axis_probe.txt.
"""

import bpy
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "previews")
os.makedirs(OUT, exist_ok=True)


def clean():
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=True, confirm=False)


def marker():
    """Apex at +Y (nose), a tall fin at +Z (up), a stub at +X (left)."""
    verts = [
        (0.0, 0.0, 0.0),
        (0.0, 2.0, 0.0),   # 1 nose  +Y
        (0.0, 0.0, 3.0),   # 2 up    +Z
        (4.0, 0.0, 0.0),   # 3 left  +X
        (0.1, 0.1, 0.1),
    ]
    faces = [(0, 1, 2), (0, 2, 3), (0, 3, 1), (1, 3, 2)]
    me = bpy.data.meshes.new("Marker")
    me.from_pydata(verts, [], faces)
    me.update()
    ob = bpy.data.objects.new("Marker", me)
    bpy.context.collection.objects.link(ob)
    return ob


def find_int_prop(data, key):
    idx = data.find(key.encode('ascii'))
    if idx < 0:
        return None
    chunk = data[idx:idx + 160]
    j = chunk.find(b'Integer')
    if j < 0:
        return None
    k = j + len(b'Integer')
    # skip the empty string record: <len:4><bytes>
    while k < len(chunk) - 5:
        if chunk[k:k + 1] == b'I':
            return struct.unpack('<i', chunk[k + 1:k + 5])[0]
        k += 1
    return None


def probe(axis_forward, axis_up, tag, bake=False):
    clean()
    ob = marker()
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    path = os.path.join(OUT, "axis_%s.fbx" % tag)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, apply_unit_scale=True, global_scale=1.0,
        apply_scale_options='FBX_SCALE_NONE', bake_space_transform=bake,
        object_types={'MESH'}, use_mesh_modifiers=False, mesh_smooth_type='FACE',
        add_leaf_bones=False, bake_anim=False, path_mode='STRIP',
        axis_forward=axis_forward, axis_up=axis_up)
    with open(path, 'rb') as fh:
        data = fh.read()
    info = {k: find_int_prop(data, k) for k in
            ("UpAxis", "UpAxisSign", "FrontAxis", "FrontAxisSign",
             "CoordAxis", "CoordAxisSign")}
    # pull the vertex array: node 'Vertices' followed by 'd' array
    vi = data.find(b'Vertices')
    verts = None
    if vi > 0:
        j = data.find(b'd', vi, vi + 200)
        if j > 0:
            n, enc, clen = struct.unpack('<III', data[j + 1:j + 13])
            if enc == 0:
                raw = data[j + 13:j + 13 + n * 8]
                vals = struct.unpack('<%dd' % n, raw)
                verts = [tuple(round(v, 4) for v in vals[i:i + 3])
                         for i in range(0, len(vals), 3)]
            else:
                import zlib
                raw = zlib.decompress(data[j + 13:j + 13 + clen])
                vals = struct.unpack('<%dd' % n, raw)
                verts = [tuple(round(v, 4) for v in vals[i:i + 3])
                         for i in range(0, len(vals), 3)]
    return dict(tag=tag, forward=axis_forward, up=axis_up, axes=info, verts=verts)


lines = []
for fwd, up, tag, bake in (('-Z', 'Y', 'default_negZ_Y', False),
                           ('-Y', 'Z', 'contract_negY_Z', False),
                           ('-Z', 'Y', 'default_baked', True),
                           ('-Y', 'Z', 'contract_baked', True)):
    r = probe(fwd, up, tag, bake)
    lines.append("=== axis_forward=%s axis_up=%s bake_space_transform=%s ===" % (fwd, up, bake))
    lines.append("  metadata: %s" % r['axes'])
    lines.append("  blender nose(0,2,0) up(0,0,3) right(4,0,0) ->")
    for v in (r['verts'] or [])[:5]:
        lines.append("    %s" % (v,))
    lines.append("")

txt = "\n".join(lines)
print(txt)
with open(os.path.join(OUT, "axis_probe.txt"), 'w') as fh:
    fh.write(txt)
