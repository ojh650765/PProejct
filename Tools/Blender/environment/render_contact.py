"""
Contact-sheet renderer.  Imports every FBX of a family back in, lays it out on
a grid scaled so each cell frames its asset, labels it, and renders one PNG.

    blender --background --python render_contact.py -- Foliage
"""

import sys
import os
import math
import glob

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
from mathutils import Vector, Euler

import envlib as E
import textures as T


def import_fbx(path):
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path, axis_forward='-Y', axis_up='Z',
                             automatic_bone_orientation=True)
    new = [o for o in bpy.context.scene.objects if o not in before]
    return new


def apply_atlas(objs, family):
    ap = T.atlas_paths(family)
    cols = T.load_colors(family)
    names = [n for (n, _) in T.FAMILY_CELLS[family]]
    for o in objs:
        if o.type != 'MESH':
            continue
        for slot in o.material_slots:
            m = slot.material
            if m is None:
                continue
            base = m.name.split("M_%s_" % family)[-1]
            key = base if base in cols else names[0]
            cell = names.index(key) if key in names else 0
            slot.material = E.get_material(family, base, cell,
                                           cols.get(key, (0.5, 0.5, 0.5)),
                                           T._ROUGH.get(base, 0.74), ap,
                                           emission=T._EMISSIVE.get(base, 0.0))


def sheet(family, files, out_png, cols=None, cell=3.0, cam_pitch=30.0,
          title=None, res=(2000, 1400)):
    E.reset_scene()
    E.setup_render(res=res, samples=64, world_rgb=(0.56, 0.68, 0.86),
                   strength=0.55)
    E.add_studio_lights(key_energy=3.1, scale=max(1.0, cell / 3.0))

    n = len(files)
    if cols is None:
        cols = int(math.ceil(math.sqrt(n * res[0] / float(res[1]))))
    rows = int(math.ceil(n / float(cols)))

    # rows must clear each other on screen given the camera pitch
    ygap = cell * (0.62 / math.tan(math.radians(cam_pitch)) + 0.42)
    ground = E.ground_plane(size=(cell + ygap) * max(cols, rows) * 6,
                            rgb=(0.46, 0.50, 0.47))

    placed = []
    maxh = 0.0
    for i, f in enumerate(files):
        objs = import_fbx(f)
        meshes = [o for o in objs if o.type == 'MESH']
        if not meshes:
            continue
        apply_atlas(meshes, family)
        lo, hi = E.obj_bounds(meshes)
        size = max(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z, 1e-3)
        s = min(1.0, (cell * 0.62) / size)
        cx = (i % cols) * cell - (cols - 1) * cell * 0.5
        cy = -((i // cols) * ygap) + (rows - 1) * ygap * 0.5
        for o in objs:
            if o.parent is None:
                o.scale = (s, s, s)
                o.location = (cx - (lo.x + hi.x) * 0.5 * s,
                              cy - (lo.y + hi.y) * 0.5 * s,
                              -lo.z * s)
        maxh = max(maxh, (hi.z - lo.z) * s)
        E.text_label(os.path.basename(f).replace(".fbx", ""),
                     (cx, cy - ygap * 0.40, 0.02), size=cell * 0.055)
        placed.append(meshes)

    # orthographic three-quarter view, fitted exactly to the grid extents
    W = cols * cell
    D = rows * ygap
    pitch = math.radians(cam_pitch)
    dist = (W + D + maxh) * 2.0 + 20.0
    cam = E.add_camera((0, -dist * math.cos(pitch), dist * math.sin(pitch)),
                       (0, 0, 0), ortho_scale=10.0)
    bpy.context.view_layer.update()
    inv = cam.matrix_world.inverted()
    pts = []
    for sx in (-1, 1):
        for sy in (-1, 1):
            for z in (0.0, maxh * 1.05):
                pts.append(Vector((sx * (W * 0.5 + cell * 0.10),
                                   sy * (D * 0.5 + ygap * 0.30), z)))
    cps = [inv @ p for p in pts]
    minx = min(p.x for p in cps)
    maxx = max(p.x for p in cps)
    miny = min(p.y for p in cps)
    maxy = max(p.y for p in cps)
    cx, cy = (minx + maxx) * 0.5, (miny + maxy) * 0.5
    # recentre by shifting the camera along its own local axes
    cam.location = cam.matrix_world @ Vector((cx, cy, 0.0))
    aspect = res[0] / float(res[1])
    need_x = (maxx - minx) * 1.03
    need_y = (maxy - miny) * 1.03
    cam.data.ortho_scale = max(need_x, need_y * aspect)
    bpy.context.view_layer.update()
    E.render_to(out_png)
    E.log("wrote %s (%d assets)" % (out_png, len(placed)))


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    family = argv[0] if argv else "Foliage"
    pattern = argv[1] if len(argv) > 1 else "*.fbx"
    out = argv[2] if len(argv) > 2 else os.path.join(
        E.PREVIEWS, "contact_%s.png" % family.lower())
    cell = float(argv[3]) if len(argv) > 3 else 3.0
    cols = int(argv[4]) if len(argv) > 4 else 0

    d = E.FAMILY_DIR[family]
    files = sorted(glob.glob(os.path.join(d, pattern)))
    files = [f for f in files if "_LOD" not in os.path.basename(f)
             and "@" not in os.path.basename(f)]
    if not files:
        E.log("no files for %s/%s" % (family, pattern))
        return
    sheet(family, files, out, cols=(cols or None), cell=cell)


if __name__ == "__main__":
    main()
