"""Keep regenerated mesh records in the same manifest parts as their source family."""
import json
from pathlib import Path
ROOT = Path(__file__).resolve().parents[3]

def record(name, family, triangles, lods=(), textures=(), note=''):
    path = Path(__file__).parent/'_manifest_parts'/f'{family}.json'
    rows = json.loads(path.read_text(encoding='utf-8')) if path.exists() else []
    entry = next((row for row in rows if row['name'] == name), None)
    if entry is None:
        entry = {'name': name, 'family': family, 'subfamily': name.split('_')[1]}
        rows.append(entry)
    entry.update(path=f'Assets/Game/Art/Environment/{family}/{name}.fbx',
                 triangles=triangles, lods=list(lods), textures=list(textures),
                 pivot='base, footprint centred', windVertexColors=False, notes=note)
    if family == 'Interior':
        entry['budgetClass'] = 'furniture'
    path.write_text(json.dumps(rows, indent=2)+'\n', encoding='utf-8')

def collapse_atlas(obj):
    material = obj.data.materials[0]
    obj.data.materials.clear()
    obj.data.materials.append(material)
    for polygon in obj.data.polygons: polygon.material_index = 0
