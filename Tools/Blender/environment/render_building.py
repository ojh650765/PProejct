"""
Close-range building inspection sheets.

The previous town pass was signed off on EEVEE contact sheets and structural
checks, and every one of the seven defects the integrator found in engine
survived both.  The sheets were too far away and the lighting was too kind.

So this renders each building at EYE LEVEL, 6 m out, from four yaws INCLUDING
directly behind, under a hard sun with visible shadows and no fill, four panels
to a sheet.  It exists to be opened and looked at, and the specific things to
look for are printed into the sheet's own filename order:

    front   -- is there a hole behind every frame? does a beam cross a window?
    side    -- do the corner posts meet the wall edges? does the wall flicker?
    back    -- is there a wall at all?
    low     -- does the roof overhang have an underside?

    blender --background --python render_building.py -- [Name ...]
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

FAM = "Town"
OUT = E.PREVIEWS

# (name, yaw, distance from the building, eye height, focal length mm)
VIEWS = [
    ("front", 0.0, 7.0, 1.65, 34.0),
    ("side", 90.0, 7.0, 1.65, 34.0),
    ("back", 180.0, 7.0, 1.65, 34.0),
    ("low", 35.0, 4.2, 0.50, 26.0),
]


def import_fbx(path):
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path,
                             axis_forward=E.FBX_AXIS_FORWARD,
                             axis_up=E.FBX_AXIS_UP)
    return [o for o in bpy.context.scene.objects if o not in before]


def apply_atlas(objs):
    ap = T.atlas_paths(FAM)
    cols = T.load_colors(FAM)
    names = [n for (n, _) in T.FAMILY_CELLS[FAM]]
    for o in objs:
        if o.type != 'MESH':
            continue
        for slot in o.material_slots:
            m = slot.material
            if m is None:
                continue
            base = m.name.split("M_%s_" % FAM)[-1]
            key = base if base in cols else names[0]
            cell = names.index(key) if key in names else 0
            slot.material = E.get_material(FAM, base, cell,
                                           cols.get(key, (0.5, 0.5, 0.5)),
                                           T._ROUGH.get(base, 0.74), ap)


def hard_sun(yaw_deg):
    d = bpy.data.lights.new("Sun", type='SUN')
    d.energy = 4.2
    d.angle = math.radians(0.6)        # hard: crisp shadows, no forgiving blur
    d.color = (1.0, 0.97, 0.90)
    ob = bpy.data.objects.new("Sun", d)
    ob.rotation_euler = Euler((math.radians(46), 0,
                               math.radians(yaw_deg + 40)))
    bpy.context.scene.collection.objects.link(ob)
    return ob


def shoot(name, path):
    """One render per view. reset_scene wipes the datablocks, so envlib's
    material cache has to go with it or the next view reuses a freed
    Material."""
    for (view, yaw, dist, eye, lens) in VIEWS:
        E.reset_scene()
        E._MAT_CACHE.clear()
        E.setup_render(res=(1500, 1000), samples=52,
                       world_rgb=(0.55, 0.66, 0.82), strength=0.28)
        hard_sun(yaw)
        objs = import_fbx(path)
        apply_atlas(objs)
        lo, hi = E.obj_bounds(objs)
        cx, cy = (lo.x + hi.x) * 0.5, (lo.y + hi.y) * 0.5
        # scale the stand-off with the building, or the Poke Lab's 9 m podium
        # puts the camera inside the steps
        reach = max(hi.x - lo.x, hi.y - lo.y) * 0.5
        dist = max(dist, reach * 2.1 + 2.6)
        # ground so the shadows land on something and the base is readable
        E.ground_plane(size=60.0, rgb=(0.34, 0.40, 0.26))
        a = math.radians(yaw)
        eye_pos = Vector((cx + math.sin(a) * dist,
                          cy - math.cos(a) * dist, eye))
        target = Vector((cx, cy, min(eye + 0.6, (lo.z + hi.z) * 0.5)))
        E.add_camera(eye_pos, target, lens=lens)
        out = os.path.join(OUT, "building_%s_%s.png" % (name, view))
        E.render_to(out)
        E.log("rendered %s" % out)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    base = E.FAMILY_DIR[FAM]
    files = []
    for f in sorted(glob.glob(os.path.join(base, "*.fbx"))):
        n = os.path.splitext(os.path.basename(f))[0]
        if "_LOD" in n:
            continue
        if not (n.startswith("Env_House") or n.startswith("Env_Building")):
            continue
        if argv and not any(a in n for a in argv):
            continue
        files.append((n, f))
    for (n, f) in files:
        shoot(n, f)
    return 0


if __name__ == "__main__":
    sys.exit(main())
