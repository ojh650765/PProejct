"""Blender MCP entry point: reusable, metre-scale furniture using the Town atlas."""
import sys, json, os
from pathlib import Path
import bpy
from mathutils import Vector

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
import envlib as E
import townlib as T
import textures
from asset_records import record

def box(bm, x,y,z, w,d,h, mat=3):
    T.solid_box(bm, (x,-y,z), (w,d,h), mat)

def table(bm):
    box(bm,0,0,.76,1.65,.95,.12)
    for x in (-.67,.67):
        for y in (-.32,.32): box(bm,x,y,.36,.12,.12,.72)
    box(bm,0,0,.828,1.12,.55,.016,15)

def chair(bm):
    box(bm,0,0,.43,.5,.5,.12,1)
    for x in (-.19,.19):
        for y in (-.19,.19): box(bm,x,y,.21,.07,.07,.42)
    box(bm,0,-.22,.72,.5,.09,.55)

def bed(bm):
    box(bm,0,0,.25,1.45,2.15,.32)
    box(bm,0,0,.48,1.34,2.02,.22,15)
    box(bm,0,-.32,.605,1.34,1.38,.06,1)
    box(bm,0,.72,.635,.95,.4,.18,15)
    box(bm,0,1.04,.65,1.5,.14,1.25)

def shelf(bm):
    for x in (-.81,.81): box(bm,x,0,1.12,.12,.48,2.24)
    box(bm,0,-.21,1.12,1.6,.06,2.24)
    for z in (.1,.7,1.3,1.9,2.21): box(bm,0,0,z,1.68,.48,.1)
    for row,z in enumerate((.4,1.,1.6)):
        for i in range(9): box(bm,-.66+i*.16,.005,z,.115,.33,.43, (1,2,6,15)[(i+row)%4])

def cabinet(bm):
    box(bm,0,0,.48,1.9,.72,.96,15)
    box(bm,0,0,1.02,2,.8,.12,8)
    for x in (-.46,.46):
        box(bm,x,.367,.49,.86,.035,.76,1)
        box(bm,x,.4,.73,.26,.055,.045,11)

def counter(bm):
    box(bm,0,0,.5,3.6,.85,1,15)
    box(bm,0,0,1.04,3.8,1,.12,2)
    box(bm,0,.445,.57,3.4,.03,.23,2)

def sofa(bm):
    box(bm,0,0,.22,1.85,.78,.3)
    box(bm,0,0,.44,1.7,.73,.24,1)
    box(bm,0,-.34,.72,1.85,.15,.64,1)
    for x in (-.88,.88): box(bm,x,0,.57,.17,.84,.42,1)

def kitchen(bm):
    cabinet(bm)
    box(bm,-.48,0,1.09,.67,.53,.025,11)
    box(bm,-.48,.03,1.105,.48,.32,.025,8)
    box(bm,-.48,-.25,1.22,.05,.05,.3,11)
    for x in (.25,.64):
        for y in (-.17,.17): box(bm,x,y,1.105,.24,.24,.025,13)
    box(bm,.95,-.29,1.47,.12,.12,.9,15)

GENERATORS = [('Table',table),('Chair',chair),('Bed',bed),('Bookshelf',shelf),
              ('LabCabinet',cabinet),('Reception',counter),('Sofa',sofa),('Kitchen',kitchen)]
textures.ensure_atlas('Town')
mats=textures.full_matset('Town')
output=ROOT/'Assets/Game/Art/Environment/Interior';output.mkdir(parents=True,exist_ok=True)
staging=ROOT/'Tools/Blender/projects/exports';staging.mkdir(parents=True,exist_ok=True)
scene=bpy.data.scenes.new('Interior_Furniture_Review')
scene.render.engine='BLENDER_EEVEE'
scene.world=bpy.data.worlds.new('Interior_Review_World');scene.world.color=(.35,.35,.35)
report=[]
for index,(label,generate) in enumerate(GENERATORS):
    name='Env_Interior_'+label
    for old in list(bpy.data.objects):
        if old.name.startswith(name): bpy.data.objects.remove(old,do_unlink=True)
    bm=E.bm_new();generate(bm)
    obj=E.bm_to_obj(bm,name,mats.materials())
    E.finalize(obj,smooth_angle=20,merge=0)
    E.apply_transforms(obj);E.uv_all(obj,mats,angle=58,margin=.012)
    tris,issues=E.validate(obj,budget=(10,2500),need_vcol=False,strict=False,closed=True,max_coplanar_dupes=0)
    assert not issues,(name,issues)
    # UVs already reference the shared atlas; one submesh avoids a draw per swatch.
    material=obj.data.materials[0]
    obj.data.materials.clear();obj.data.materials.append(material)
    for polygon in obj.data.polygons: polygon.material_index=0
    temporary=staging/(name+'.fbx');E.export_fbx([obj],str(temporary))
    os.replace(temporary,output/temporary.name)
    record(name,'Interior',tris, textures=[
        'Assets/Game/Art/Environment/Town/Textures/Env_Town_Atlas_BaseColor.png',
        'Assets/Game/Art/Environment/Town/Textures/Env_Town_Atlas_Normal.png'],
        note='One atlas submesh; solid furniture excluded from walkable ground.')
    scene.collection.objects.link(obj);obj.location=((index%4)*3.7,(index//4)*4,0)
    report.append({'asset':name,'triangles':tris})
camera=bpy.data.objects.new('Furniture_Camera',bpy.data.cameras.new('Furniture_Camera'))
scene.collection.objects.link(camera);camera.location=(14,-17,15)
camera.rotation_euler=(Vector((5.3,1.8,.6))-camera.location).to_track_quat('-Z','Y').to_euler()
camera.data.type='ORTHO';camera.data.ortho_scale=17;scene.camera=camera
light=bpy.data.objects.new('Furniture_Key',bpy.data.lights.new('Furniture_Key','AREA'))
scene.collection.objects.link(light);light.location=(4,-5,12);light.data.energy=2300;light.data.size=12
scene.view_settings.view_transform='AgX'
scene.render.resolution_x=1400;scene.render.resolution_y=850;scene.render.resolution_percentage=100
scene.render.filepath=str(ROOT/'previews/interior_furniture_blender.png')
bpy.ops.render.render(write_still=True,scene=scene.name)
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'Tools/Blender/projects/Interiors.blend'))
print(json.dumps(report))
