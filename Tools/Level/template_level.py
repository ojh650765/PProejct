"""Generate a repeatable dry route from a compact authoring file.

python Tools/Level/template_level.py Tools/Level/templates/meadow.json --write
Then select the emitted JSON in Unity and use Tools/Poké Lab/Level/Build Selected Template.
Ground, road shoulders, placements and navigation all derive from the same height grid.
Water/caves need their own authored geometry; this template never guesses water crossings.
"""
import argparse
import json
import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def generate(config):
    name = config['scene']
    if not re.fullmatch(r'[A-Z][A-Za-z0-9_]{2,48}', name) or name in {'Town', 'Field', 'Route202', 'MainMenu', 'Battle', 'Boot', 'Login', 'Overworld', 'Interior_House', 'Interior_Lab', 'Interior_PlayerHome', 'Interior_PokeCentre'}:
        raise ValueError('Use a new scene name, not an existing story scene.')
    width, length = int(config.get('width', 28)), int(config.get('length', 40))
    if not (16 <= width <= 100 and 16 <= length <= 150):
        raise ValueError('Supported dimensions: width 16–100, length 16–150 metres.')
    road = float(config.get('roadHalfWidth', 2))
    if not 1.5 <= road <= width / 4:
        raise ValueError('Road must leave room for passable shoulders.')
    amplitude = float(config.get('relief', .25))
    if not 0 <= amplitude <= .6:
        raise ValueError('Relief must be 0–0.6 metres to retain walkable shoulders.')
    def height(x, z):
        # A continuous, low slope. No abrupt ownership change at the road edge.
        return round(.24 + amplitude * math.sin(z * .09) * (1-math.exp(-max(0,abs(x)-road)**2*.1)), 4)
    grid = [height(ix-width/2, iz) for iz in range(length+1) for ix in range(width+1)]
    vertices, normals, colors, uvs, triangles = [], [], [], [], []
    for z in range(length+1):
        for ix in range(width+1):
            x = ix-width/2
            vertices += [x, height(x,z), z]
            dx=(height(x+.1,z)-height(x-.1,z))/.2
            dz=(height(x,z+.1)-height(x,z-.1))/.2
            norm=math.sqrt(1+dx*dx+dz*dz)
            normals += [-dx/norm,1/norm,-dz/norm]
            t=min(1,max(0,(abs(x)-road)/1.2));t=t*t*(3-2*t)
            colors += [t,1-t,0,0];uvs += [x,z]
            if ix<width and z<length:
                i=z*(width+1)+ix;n=i+width+1
                triangles += [i,n,i+1,i+1,n,n+1]
    objects=[]; occupied=[]; checkpoints=[]
    def position(value, radius=.6):
        x,z=map(float,value)
        if not (-width/2+radius<=x<=width/2-radius and radius<=z<=length-radius):
            raise ValueError(f'Placement {value} leaves the terrain.')
        if any(math.hypot(x-a,z-b)<radius+r+.5 for a,b,r in occupied):
            raise ValueError(f'Placement {value} overlaps scenery or another interaction.')
        occupied.append((x,z,radius))
        return [x,height(x,z),z]
    for prop in config.get('props',[]):
        radius=float(prop.get('radius',1.3))
        if abs(float(prop['at'][0]))<road+radius+.5:
            raise ValueError('Solid scenery must leave the road and its shoulders clear.')
        pos=position(prop['at'],radius)
        prefab=prop['prefab']
        if not prefab.startswith('Assets/Game/Art/') or not (ROOT/prefab).is_file():
            raise ValueError('Missing project prefab: '+prefab)
        objects.append(dict(name=prop['name'],prefab=prefab,parent='Scenery',position=pos,
                            rotation=[0,prop.get('yaw',0),0],scale=[1,1,1],layer='Environment',
                            tag='Untagged',static=True,collider='mesh'))
    for item in config.get('items',[]):
        pos=position(item['at']);pos[1]+=.3
        objects.append(dict(name=item['name'],prefab='Assets/Game/Art/Props/Env_Prop_CaptureBall.fbx',
                            parent='Items',position=pos,rotation=[0,0,0],scale=[2.6]*3,
                            layer='Interactable',tag='Interactable',static=True,collider='mesh',pickup=item['id']))
        checkpoints.append(dict(name=item['name'],position=pos))
    npcs=[]
    for npc in config.get('npcs',[]):
        pos=position(npc['at'])
        npcs.append(dict(name=npc['name'],npcId=npc['id'],displayName=npc['label'],position=pos,
                         rotation=[0,180,0],schedule=[dict(startHour=0,activity='Walk',waypoint=pos,wanderRadius=npc.get('wanderRadius',1.5))]))
        checkpoints.append(dict(name=npc['name'],position=pos))
    spawn=[0,height(0,3)+.04,3]
    links=[]
    for link in config.get('exits',[]):
        if not (ROOT/'Assets/Game/Scenes'/f"{link['scene']}.unity").is_file():
            raise ValueError('Exit destination scene does not exist.')
        if not link.get('arrivalSpawn'):
            raise ValueError('An exit needs an explicit destination arrival marker.')
        z=1 if link.get('end')=='south' else length-1
        pos=[0,height(0,z),z]
        links.append(dict(name='To_'+link['scene'],scene=link['scene'],arrivalSpawn=link['arrivalSpawn'],
                          position=pos,facingYaw=180 if z==1 else 0,size=[road*2,3,1]))
        checkpoints.append(dict(name='To_'+link['scene'],position=pos))
    return dict(schema='pokelab-level-unity/2',templateVersion=1,scene=name,
                biome=config.get('biome','route_meadow'),displayName=config.get('displayName',name),
                heightGrid=dict(originX=-width/2,originZ=0,step=1,countX=width+1,countZ=length+1,heights=grid),
                ground=[dict(name='Ground_'+name,vertices=vertices,normals=normals,colors=colors,uvs=uvs,triangles=triangles)],
                objects=objects,npcs=npcs,trainers=[],water=[],foliage=[],ambientAnchors=[],tallGrass=[],
                caveEntrances=[],barrierVolumes=[],buildingDoors=[],sceneLinks=links,playerSpawn=spawn,
                cameraYaw=0,cameraPitch=38,cameraDistance=11,cameraFov=48,checkpoints=checkpoints)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('config',type=Path);parser.add_argument('--write',action='store_true')
    args=parser.parse_args();result=generate(json.loads(args.config.read_text(encoding='utf-8')))
    target=ROOT/'Assets/Game/Data/Levels'/('template_'+result['scene'].lower()+'.json')
    if args.write: target.write_text(json.dumps(result,ensure_ascii=False,separators=(',',':'))+'\n',encoding='utf-8')
    print(json.dumps(dict(scene=result['scene'],output=str(target),written=args.write,
                         vertices=len(result['ground'][0]['vertices'])//3,checks=len(result['checkpoints'])),indent=2))
