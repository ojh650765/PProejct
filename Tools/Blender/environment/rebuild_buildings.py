"""Run through Blender MCP; replace all five building meshes and measured anchors."""
import importlib
import json
import random
import sys
import os
import time
from pathlib import Path
import bpy
from mathutils import Vector

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
import envlib as E
import textures as T
import gen_town as G
import building_kit as K
from asset_records import record, collapse_atlas
importlib.reload(K)
importlib.reload(G)

GENERATORS = [('Env_House_Cottage_A', K.cottage),
              ('Env_House_Townhouse_B', K.townhouse),
              ('Env_House_Farmhouse_C', K.farmhouse),
              ('Env_Building_PokeLab', G.poke_lab),
              ('Env_Building_PokeCentre', K.pokemon_centre)]
for old in list(bpy.data.objects):
    if any(old.name.startswith(name) for name, _ in GENERATORS):
        bpy.data.objects.remove(old, do_unlink=True)

def export(obj, destination):
    staging = ROOT/'Tools/Blender/projects/exports'
    staging.mkdir(parents=True, exist_ok=True)
    temporary = staging/destination.name
    E.export_fbx([obj], str(temporary))
    for attempt in range(20):
        try:
            os.replace(temporary, destination)
            return
        except OSError:
            if attempt == 19: raise
            time.sleep(.25)
T.ensure_atlas('Town')
materials = T.full_matset('Town')
bounds_path = ROOT/'Tools/Level/asset_bounds.json'
bounds = json.loads(bounds_path.read_text(encoding='utf-8'))
anchors, report = {}, []
scene = bpy.data.scenes.new('Buildings_Review')
scene.render.engine = 'BLENDER_EEVEE'
scene.world = bpy.data.worlds.new('Buildings_World')
scene.world.color = (.3, .3, .3)
for index, (name, generate) in enumerate(GENERATORS):
    G.HOLE_CHECKS.clear()
    bm = E.bm_new()
    door = generate(bm, random.Random(4101+index))
    assert not G.HOLE_CHECKS, G.HOLE_CHECKS
    obj = E.bm_to_obj(bm, name, materials.materials())
    E.finalize(obj, smooth_angle=22, merge=0)
    before = obj.data.vertices[0].co.copy()
    E.pivot_to_base(obj, xy='center')
    offset = before-obj.data.vertices[0].co
    E.apply_transforms(obj)
    E.uv_all(obj, materials, angle=58, margin=.010)
    # Roof and door faces receive a complete, upright atlas tile. Smart packing
    # squeezed them into narrow scraps and stretched shingles into broad stripes.
    uv = obj.data.uv_layers.active.data
    for polygon in obj.data.polygons:
        slot = polygon.material_index
        if slot not in (4,5,6,9) or polygon.area < .6: continue
        axes = (0,2) if slot == 9 else (0,1)
        points = [obj.data.vertices[obj.data.loops[i].vertex_index].co for i in polygon.loop_indices]
        low = [min(p[a] for p in points) for a in axes]
        span = [max(p[a] for p in points)-low[j] for j,a in enumerate(axes)]
        if min(span) < .05: continue
        x,y,w,h = materials.rect_of_slot(slot)
        for loop,p in zip(polygon.loop_indices, points):
            uv[loop].uv=(x+(p[axes[0]]-low[0])/span[0]*w,
                         y+(p[axes[1]]-low[1])/span[1]*h)
    tris, issues = E.validate(obj, budget=(100,6000), need_vcol=False,
                             strict=False, closed=True, max_coplanar_dupes=0)
    assert not issues, (name, issues)
    collapse_atlas(obj)
    path = ROOT/'Assets/Game/Art/Environment/Town'/f'{name}.fbx'
    export(obj, path)
    # Existing LOD assets must follow the replacement, even for a cheaper base mesh.
    lod_records = []
    for index_lod, lod in enumerate(E.make_lods(obj, (.40, .15)), 1):
        export(lod, path.parent/f'{lod.name}.fbx')
        lod_records.append({'level': index_lod, 'path': str((path.parent/f'{lod.name}.fbx').relative_to(ROOT)).replace('\\','/'), 'triangles': E.tri_count(lod)})
        E.delete_obj(lod)
    record(name, 'Town', tris, lod_records,
           ['Assets/Game/Art/Environment/Town/Textures/Env_Town_Atlas_BaseColor.png',
            'Assets/Game/Art/Environment/Town/Textures/Env_Town_Atlas_Normal.png'],
           'Open door shell, low doorstep, measured entrance anchors, one atlas submesh.')
    p = door.centre-door.normal*.02-offset
    p.z = door.z0-offset.z
    p += door.normal*.55
    anchors[name] = {'position': [round(p.x,4),round(p.z,4),round(-p.y,4)],
                     'forward': [door.normal.x,door.normal.z,-door.normal.y],
                     'width': door.width, 'source': 'Blender shell opening'}
    lamp_x = max(v.co.x for v in obj.data.vertices)-.56
    if name == 'Env_Building_PokeLab': lamp_x = 1.5
    found, wall, _, _ = obj.ray_cast(Vector((lamp_x,-100,2.35)), Vector((0,1,0)))
    if found:
        anchors[name]['wallLamp'] = [round(wall.x,4),1.8,round(-wall.y+.32,4)]
    points = [(v.co.x,v.co.z,-v.co.y) for v in obj.data.vertices]
    low = [min(p[i] for p in points) for i in range(3)]
    high = [max(p[i] for p in points) for i in range(3)]
    entry = bounds.setdefault(name, {'family':'Town', 'subfamily':name.split('_')[1],
              'path':str(path.relative_to(ROOT)).replace('\\','/'),
              'pivot':'ground level, footprint centred'})
    entry.update(min=[round(v,4) for v in low], max=[round(v,4) for v in high],
                 size=[round(high[i]-low[i],4) for i in range(3)])
    scene.collection.objects.link(obj)
    obj.location = ((index%3)*9, (index//3)*11, 0)
    report.append({'asset':name, 'triangles':tris, 'entrance':anchors[name]})

(ROOT/'Assets/Game/Data/Levels/building_entrances.json').write_text(
    json.dumps(anchors,indent=2)+'\n', encoding='utf-8')
bounds_path.write_text(json.dumps(bounds,indent=1)+'\n',encoding='utf-8')
camera = bpy.data.objects.new('Buildings_Camera', bpy.data.cameras.new('Buildings_Camera'))
scene.collection.objects.link(camera)
camera.location=(24,-32,26)
camera.rotation_euler=(Vector((8,5,2))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO'; camera.data.ortho_scale=31; scene.camera=camera
lamp=bpy.data.objects.new('Buildings_Key',bpy.data.lights.new('Buildings_Key','AREA'))
scene.collection.objects.link(lamp); lamp.location=(2,-7,20)
lamp.data.energy=5000; lamp.data.size=20
scene.view_settings.view_transform='AgX'
scene.render.resolution_x=1600;scene.render.resolution_y=1100
scene.render.resolution_percentage=100
scene.render.filepath=str(ROOT/'previews/buildings_blender.png')
bpy.ops.render.render(write_still=True, scene=scene.name)
source=ROOT/'Tools/Blender/projects';source.mkdir(exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(source/'Buildings.blend'))
(ROOT/'previews/buildings_report.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report))
