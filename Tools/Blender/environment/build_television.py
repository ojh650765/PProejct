"""Run through the local Blender MCP execute_code endpoint."""
import bpy, sys, json
from pathlib import Path
from mathutils import Vector
HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[2]
sys.path.insert(0,str(HERE))
import envlib as E
import townlib as T
import textures
textures.ensure_atlas('Town')
mats=textures.full_matset('Town')
originalScene=bpy.context.window.scene
for old in list(bpy.data.objects):
 if old.name.startswith(('Env_Interior_Television','TV_Screen')):bpy.data.objects.remove(old,do_unlink=True)
scene=bpy.data.scenes.new('PlayerHome_Television')
bm=E.bm_new()
def box(x,y,z,w,d,h,mat): T.solid_box(bm,(x,y,z),(w,d,h),mat)
# Low cabinet, CRT housing, inset bezel, speakers and controls. Screen stays
# separate so its UVs are the broadcast image rather than the furniture atlas.
box(0,0,.28,1.65,.70,.56,3)
box(0,-.356,.28,1.5,.025,.36,1)
box(0,0,.57,1.75,.78,.08,3)
box(0,.06,1.06,1.40,.65,.88,13)
box(0,-.30,1.08,1.45,.09,.86,8)
box(0,-.355,1.09,1.02,.055,.78,13)
for x in (-.64,.64):
 for z in (.86,.94,1.02,1.10,1.18):box(x,-.36,z,.07,.018,.027,11)
box(.61,-.371,.75,.055,.04,.055,2)
obj=E.bm_to_obj(bm,'Env_Interior_Television',mats.materials())
E.finalize(obj,smooth_angle=20,merge=0);E.apply_transforms(obj);E.uv_all(obj,mats,angle=58,margin=.012)
material=obj.data.materials[0];obj.data.materials.clear();obj.data.materials.append(material)
for p in obj.data.polygons:p.material_index=0
scene.collection.objects.link(obj)
mesh=bpy.data.meshes.new('TelevisionScreen')
mesh.from_pydata([(-.48,-.39,.73),(.48,-.39,.73),(.48,-.39,1.45),(-.48,-.39,1.45)],[],[(0,1,2,3)])
mesh.uv_layers.new(name='UVMap')
for loop,uv in zip(mesh.uv_layers.active.data,[(0,0),(1,0),(1,1),(0,1)]):loop.uv=uv
screen=bpy.data.objects.new('TV_Screen',mesh);scene.collection.objects.link(screen)
screenMat=bpy.data.materials.new('TV_Broadcast');screenMat.use_nodes=True
nodes=screenMat.node_tree.nodes;nodes.clear()
tex=nodes.new('ShaderNodeTexImage');tex.image=bpy.data.images.load(str(ROOT/'Assets/Game/Art/Environment/Interior/Textures/TV_Broadcast.png'));tex.interpolation='Closest'
em=nodes.new('ShaderNodeEmission');out=nodes.new('ShaderNodeOutputMaterial')
screenMat.node_tree.links.new(tex.outputs['Color'],em.inputs['Color']);screenMat.node_tree.links.new(em.outputs[0],out.inputs[0]);mesh.materials.append(screenMat)
outpath=ROOT/'Assets/Game/Art/Environment/Interior/Env_Interior_Television.fbx'
bpy.context.window.scene=scene
E.export_fbx([obj,screen],str(outpath))
camera=bpy.data.objects.new('TV_ReviewCamera',bpy.data.cameras.new('TV_ReviewCamera'));scene.collection.objects.link(camera)
camera.location=(2.8,-4.8,2.7);camera.rotation_euler=(Vector((0,0,.8))-camera.location).to_track_quat('-Z','Y').to_euler();camera.data.type='ORTHO';camera.data.ortho_scale=2.8;scene.camera=camera
light=bpy.data.objects.new('TV_Key',bpy.data.lights.new('TV_Key','AREA'));scene.collection.objects.link(light);light.location=(1,-3,5);light.data.energy=700;light.data.size=4
scene.world=bpy.data.worlds.new('TV_World');scene.world.color=(.25,.25,.25);scene.render.engine='BLENDER_EEVEE';scene.render.resolution_x=1000;scene.render.resolution_y=850;scene.render.resolution_percentage=100
scene.render.filepath=str(ROOT/'previews/television_blender.png');bpy.ops.render.render(write_still=True,scene=scene.name)
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT/'Tools/Blender/projects/Television.blend'),copy=True)
bpy.context.window.scene=originalScene
print(json.dumps({'asset':str(outpath),'screen_vertices':len(mesh.vertices),'body_triangles':sum(len(p.vertices)-2 for p in obj.data.polygons)}))
