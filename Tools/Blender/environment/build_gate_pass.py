"""Run through BlenderMCP execute_code. Build the town ramp's continuous stone banks.

The source height grid and road spline own every world coordinate. Exports a blend,
FBX and Unity mesh data; never replaces the current Blender scene or its objects.
"""
import bpy
import json
import math
from pathlib import Path
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / 'Assets/Game/Art/Environment/Terrain/GatePass'
OUT.mkdir(parents=True, exist_ok=True)
layout = json.loads((ROOT / 'Assets/Game/Data/Levels/slice_layout_unity.json').read_text())
design = json.loads((ROOT / 'Assets/Game/Data/Levels/slice_layout.json').read_text())
grid = layout['heightGrid']


def height(x, z):
    fx = max(0, min(grid['countX'] - 1.001, (x - grid['originX']) / grid['step']))
    fz = max(0, min(grid['countZ'] - 1.001, (z - grid['originZ']) / grid['step']))
    ix, iz = int(fx), int(fz)
    tx, tz = fx - ix, fz - iz
    h = grid['heights']
    a = h[iz * grid['countX'] + ix] * (1-tx) + h[iz * grid['countX'] + ix+1] * tx
    b = h[(iz+1) * grid['countX'] + ix] * (1-tx) + h[(iz+1) * grid['countX'] + ix+1] * tx
    return a * (1-tz) + b * tz


previous = bpy.data.collections.get('GatePass_Review')
if previous:
    for item in list(previous.objects): bpy.data.objects.remove(item, do_unlink=True)
    bpy.data.collections.remove(previous)
collection = bpy.data.collections.new('GatePass_Review')
bpy.context.scene.collection.children.link(collection)
mat = bpy.data.materials.new('GatePass_WeatheredStone')
mat.diffuse_color = (0.30, 0.34, 0.30, 1)
mat.use_nodes = True
bsdf = mat.node_tree.nodes.get('Principled BSDF')
bsdf.inputs['Base Color'].default_value = (0.30, 0.34, 0.30, 1)
bsdf.inputs['Roughness'].default_value = 0.93
road = next(p['centreline'] for p in design['paths'] if p['name'] == 'Path_GateRamp')
export = []
objects = []


def bank(name, line):
    verts, faces = [], []
    for i, (x, y, z) in enumerate(line):
        a, b = line[max(0, i-1)], line[min(len(line)-1, i+1)]
        dx, dz = b[0]-a[0], b[2]-a[2]
        length = max(math.hypot(dx, dz), 0.001)
        nx, nz = dz/length, -dx/length
        # Closed cross-section. The low skirt sinks below both terrain edges;
        # the near vertical face cannot be climbed by a 0.45 m step controller.
        low = min(height(x+nx*0.9, z+nz*0.9), height(x-nx*0.9, z-nz*0.9), y)-0.65
        top = max(y, height(x, z))+0.72 + math.sin(i*1.7)*0.035
        ring = [(-0.92,low), (0.92,low), (0.72,top-0.30),
                (0.53,top), (-0.53,top), (-0.72,top-0.30)]
        for w, yy in ring:
            verts.append((x+nx*w, yy, z+nz*w))
        if i:
            for j in range(6):
                a0=(i-1)*6+j; b0=(i-1)*6+(j+1)%6
                faces.append((a0,b0,b0+6,a0+6))
    faces.extend([tuple(reversed(range(6))), tuple((len(line)-1)*6+j for j in range(6))])
    mesh=bpy.data.meshes.new(name)
    mesh.from_pydata([(x,-z,y) for x,y,z in verts], [], faces)
    mesh.update()
    obj=bpy.data.objects.new(name,mesh); collection.objects.link(obj)
    obj.data.materials.append(mat)
    # Recalculate winding in Blender before exporting the exact same collision surface.
    import bmesh
    bm=bmesh.new();bm.from_mesh(mesh);bmesh.ops.recalc_face_normals(bm,faces=list(bm.faces));bm.to_mesh(mesh);bm.free()
    mesh.calc_loop_triangles()
    tris=[int(v) for tri in mesh.loop_triangles for v in tri.vertices]
    # Conversion is a proper rotation, so triangle winding is unchanged.
    colors=[]
    for i,v in enumerate(verts):
        grass=0.24 if i%6 in (3,4) else 0.03
        colors.extend([grass,0.06,0,1-grass-0.06])
    export.append(dict(name=name,vertices=[c for v in verts for c in v],triangles=tris,
                       colors=colors,uvs=[c for x,y,z in verts for c in (x*0.2,z*0.2)]))
    objects.append(obj)


for side,label in [(-1,'West'),(1,'East')]:
    line=[]
    for i,(x,y,z) in enumerate(road):
        a,b=road[max(0,i-1)],road[min(len(road)-1,i+1)]
        dx,dz=b[0]-a[0],b[2]-a[2]; length=math.hypot(dx,dz)
        # Inner faces leave at least 4 m clear throughout the curved ramp.
        line.append((x+side*dz/length*3.0,y,z-side*dx/length*3.0))
    wing=[(-22, height(-22,-0.6), -0.6),(-16,height(-16,-0.6),-0.6),(-12.4,height(-12.4,-1.6),-1.6)] if side<0 else [(2.4,height(2.4,-2),-2),(-0.6,height(-0.6,-0.4),-0.4),(-4,height(-4,-0.2),-0.2)]
    # Connecting wings close the old gap between the perimeter and the ramp.
    # The northwest route crosses the old west bank. End closed bank sections
    # beside that road instead of leaving a solid wall across its new surface.
    joined = wing + line
    if side > 0:
        bank('GatePass_'+label, joined)
        continue
    route = [(-11,0),(-12,4),(-28,4),(-32,8)]
    def route_distance(x,z):
        distances=[]
        for a,b in zip(route,route[1:]):
            dx,dz=b[0]-a[0],b[1]-a[1]
            t=max(0,min(1,((x-a[0])*dx+(z-a[1])*dz)/(dx*dx+dz*dz)))
            distances.append(math.hypot(x-a[0]-dx*t,z-a[1]-dz*t))
        return min(distances)
    dense=[]
    for a,b in zip(joined,joined[1:]):
        count=max(1,math.ceil(math.hypot(b[0]-a[0],b[2]-a[2])/.25))
        dense.extend(tuple(a[k]+(b[k]-a[k])*i/count for k in range(3)) for i in range(count))
    dense.append(joined[-1])
    section=[];number=0
    for point in dense+[None]:
        if point is not None and route_distance(point[0],point[2])>3.2:
            section.append(point)
        else:
            if len(section)>1:
                bank('GatePass_'+label+'_'+str(number),section);number+=1
            section=[]

(OUT/'gate_pass_meshes.json').write_text(json.dumps({'meshes':export},separators=(',',':')))
bpy.ops.object.select_all(action='DESELECT')
for obj in objects: obj.select_set(True)
bpy.context.view_layer.objects.active=objects[0]
bpy.ops.export_scene.fbx(filepath=str(OUT/'GatePass.fbx'),use_selection=True,
    object_types={'MESH'},axis_forward='-Z',axis_up='Y',apply_unit_scale=True,
    bake_space_transform=True,add_leaf_bones=False)

# Ground context uses the actual grid, with the road picked out for review.
groundmat=bpy.data.materials.new('GatePass_ContextGround');groundmat.diffuse_color=(0.19,0.29,0.14,1)
verts=[];faces=[]
for z in range(-7,15):
    for x in range(-25,6): verts.append((x,-z,height(x,z)))
for j in range(21):
    for i in range(30):
        a=j*31+i;faces.append((a,a+31,a+32,a+1))
mesh=bpy.data.meshes.new('GatePass_Context');mesh.from_pydata(verts,[],faces);mesh.update()
obj=bpy.data.objects.new('GatePass_Context',mesh);collection.objects.link(obj);obj.data.materials.append(groundmat)

scene=bpy.context.scene
for obj in list(scene.objects):
    if obj.name in ('Cube','Light','Camera'): obj.hide_render=True
camdata=bpy.data.cameras.new('GatePass_ReviewCamera');cam=bpy.data.objects.new('GatePass_ReviewCamera',camdata);collection.objects.link(cam)
cam.location=(-26,22,23);aim=Vector((-8,-3,1.5));cam.rotation_euler=(aim-cam.location).to_track_quat('-Z','Y').to_euler();camdata.type='ORTHO';camdata.ortho_scale=29;scene.camera=cam
lightdata=bpy.data.lights.new('GatePass_SoftSun','AREA');lightdata.energy=2100;lightdata.shape='DISK';lightdata.size=16
light=bpy.data.objects.new('GatePass_SoftSun',lightdata);collection.objects.link(light);light.location=(-12,5,18)
scene.render.engine='BLENDER_EEVEE';scene.world.color=(0.25,0.25,0.25)
scene.view_settings.view_transform='AgX';scene.view_settings.exposure=0
scene.render.resolution_x=1440;scene.render.resolution_y=1080;scene.render.resolution_percentage=100
scene.render.filepath=str(ROOT/'previews/gate_pass_blender.png')
source_dir=ROOT/'Tools/Blender/projects'
source_dir.mkdir(exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=str(source_dir/'GatePass.blend'))
bpy.ops.render.render(write_still=True)
print(json.dumps({'meshes':len(export),'triangles':sum(len(m['triangles'])//3 for m in export),'output':str(OUT)}))
