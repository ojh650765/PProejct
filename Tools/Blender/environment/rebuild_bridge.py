"""Blender MCP: widen the crossing and export separately classified deck/structure."""
import importlib, json, os, random, sys
from pathlib import Path
import bpy, bmesh
HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
import envlib as E
import textures as T
import gen_terrain as G
from asset_records import record, collapse_atlas
importlib.reload(G)
name = 'Env_Bridge_Wood'
for obj in list(bpy.data.objects):
    if obj.name.startswith(name): bpy.data.objects.remove(obj, do_unlink=True)
T.ensure_atlas('Terrain')
materials = T.full_matset('Terrain')
bm = E.bm_new()
G.a_bridge(bm, random.Random(3431), 19)
for vertex in bm.verts: vertex.co.y *= 2
objects = []
for part, is_deck in [('Deck', True), ('Structure', False)]:
    copy = bm.copy()
    layer = copy.faces.layers.int.get('bridge_deck')
    bmesh.ops.delete(copy, geom=[f for f in copy.faces if bool(f[layer]) != is_deck], context='FACES')
    obj = E.bm_to_obj(copy, name + '_' + part, materials.materials())
    E.finalize(obj, smooth_angle=24, merge=0)
    E.uv_all(obj, materials, angle=58, margin=.010)
    collapse_atlas(obj)
    objects.append(obj)
bm.free()
staging = ROOT/'Tools/Blender/projects/exports'
staging.mkdir(parents=True, exist_ok=True)
destination = ROOT/'Assets/Game/Art/Environment/Terrain'/f'{name}.fbx'
E.export_fbx(objects, str(staging/destination.name))
os.replace(staging/destination.name, destination)
lod_records=[]
for index, ratio in enumerate((.4,.15), 1):
    lods=[]
    for obj in objects:
        lod = E.make_lods(obj, (ratio,))[0]
        lods.append(lod)
    output = destination.with_name(f'{name}_LOD{index}.fbx')
    E.export_fbx(lods, str(staging/output.name))
    os.replace(staging/output.name, output)
    lod_records.append({'level':index,'path':str(output.relative_to(ROOT)).replace('\\','/'),
                        'triangles':sum(E.tri_count(obj) for obj in lods)})
    for lod in lods: E.delete_obj(lod)
bounds_path = ROOT/'Tools/Level/asset_bounds.json'
bounds = json.loads(bounds_path.read_text(encoding='utf-8'))
bounds[name]['min'][2] = -1.6
bounds[name]['max'][2] = 1.6
bounds[name]['size'][2] = 3.2
bounds_path.write_text(json.dumps(bounds, indent=1)+'\n', encoding='utf-8')
record(name, 'Terrain', sum(E.tri_count(obj) for obj in objects), lod_records,
       ['Assets/Game/Art/Environment/Terrain/Textures/Env_Terrain_Atlas_BaseColor.png',
        'Assets/Game/Art/Environment/Terrain/Textures/Env_Terrain_Atlas_Normal.png'],
       '9 x 3.2 m bridge; Ground deck separate from non-walkable structure.')
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'Tools/Blender/projects/Bridge.blend'))
print('Exported 9 x 3.2 m bridge with separate walkable deck and solid structure.')
