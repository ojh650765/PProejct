"""
Assembled renders of the world-space terrain: the decks, the water, the ramps,
the ledges and the waterfall, all imported back from their shipped FBXs and
placed at the origin, which is where they belong.

A contact sheet is the wrong tool for these.  Every other family is a prop you
can put in a grid cell; a ground deck is 68 m across and only means anything
next to the deck it joins.  So this renders the level instead, at the shipped
camera angle (yaw 35, pitch 42) and at eye level, and the point of it is to be
opened and looked at.

    blender --background --python render_ground.py -- [shot ...]
"""

import sys
import os
import math
import glob

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Euler

import envlib as E
import gen_ground as G

OUT = E.PREVIEWS

# Blender-space colour keyed off the Unity material each asset declares
LOOK = {
    "PokeLab/TerrainBlend": None,          # keep the vertex-colour blend
    "PokeLab/Water": None,
}


def import_fbx(path):
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path,
                             axis_forward=E.FBX_AXIS_FORWARD,
                             axis_up=E.FBX_AXIS_UP)
    return [o for o in bpy.context.scene.objects if o not in before]


def load(patterns, family="Terrain"):
    objs = []
    base = E.FAMILY_DIR[family]
    for pat in patterns:
        for f in sorted(glob.glob(os.path.join(base, pat))):
            if "_LOD" in os.path.basename(f):
                continue
            objs.extend(import_fbx(f))
    return [o for o in objs if o.type == 'MESH']


def restyle(objs):
    """The FBXs carry placeholder materials by name only; rebuild the preview
    look from the name so the render shows the terrain blend and the water."""
    ground = G.blend_preview_material("M_Preview_TerrainBlend")
    water = G.water_preview_material("M_Preview_Water")
    fall = G.water_preview_material("M_Preview_Waterfall",
                                    (0.70, 0.86, 0.92), 0.7)
    for o in objs:
        n = o.name
        if n.startswith("Env_Water"):
            m = water
        elif n.startswith("Env_Waterfall"):
            m = fall
        else:
            m = ground
        o.data.materials.clear()
        o.data.materials.append(m)


def sun(strength=3.4, angle=(math.radians(52), 0, math.radians(35))):
    d = bpy.data.lights.new("Sun", type='SUN')
    d.energy = strength
    d.angle = math.radians(2.0)
    d.color = (1.0, 0.96, 0.88)
    ob = bpy.data.objects.new("Sun", d)
    ob.rotation_euler = Euler(angle)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def place_camera(target, distance, yaw_deg, pitch_deg, lens=50.0):
    """target is in LAYOUT (x, y, z); the scene is in Blender (x, z, y)."""
    tx, ty, tz = target
    bt = Vector((tx, tz, ty))
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    # Blender: +Z up, +Y is the layout's +Z
    horiz = math.cos(pitch) * distance
    eye = bt + Vector((math.sin(yaw) * horiz,
                       -math.cos(yaw) * horiz,
                       math.sin(pitch) * distance))
    cam = E.add_camera(eye, bt, lens=lens)
    return cam


SHOTS = {
    # name: (patterns, target(layout xyz), distance, yaw, pitch, lens, res)
    "ground_overview": (["Env_Ground_*.fbx", "Env_Water_*.fbx",
                         "Env_Ramp_*.fbx", "Env_Ledge_*.fbx",
                         "Env_Waterfall_*.fbx"],
                        (10.0, 0.0, 14.0), 200.0, 35.0, 42.0, 45.0,
                        (2000, 1250)),
    "ground_lakeside": (["Env_Ground_ShoreBand.fbx", "Env_Ground_RouteFloor.fbx",
                         "Env_Ground_RiverChannel.fbx", "Env_Water_Lake.fbx",
                         "Env_Water_Outflow.fbx", "Env_Ramp_LakeSpur.fbx",
                         "Env_Ledge_*.fbx", "Env_Waterfall_*.fbx"],
                        (-6.0, -1.5, 4.0), 52.0, 35.0, 42.0, 46.0,
                        (2000, 1250)),
    "ground_townedge": (["Env_Ground_TownTerrace.fbx",
                         "Env_Ground_RouteFloor.fbx",
                         "Env_Ramp_TownFromRoute.fbx"],
                        (36.0, 1.5, 43.0), 46.0, 35.0, 36.0, 40.0,
                        (2000, 1250)),
    "ground_cave": (["Env_Ground_CaveFloor.fbx", "Env_Ground_CaveCeiling.fbx",
                     "Env_Water_CavePool.fbx", "Env_Ramp_CaveFromRoute.fbx"],
                    (-46.0, 3.0, 34.0), 52.0, 35.0, 28.0, 40.0,
                    (2000, 1250)),
    "ground_spur": (["Env_Ground_ShoreBand.fbx", "Env_Ground_RouteFloor.fbx",
                     "Env_Water_Lake.fbx", "Env_Ramp_LakeSpur.fbx",
                     "Env_Ledge_*.fbx"],
                    (6.0, -1.0, 22.0), 42.0, 35.0, 34.0, 42.0,
                    (2000, 1250)),
    "ground_fall": (["Env_Ground_ShoreBand.fbx", "Env_Water_Lake.fbx",
                     "Env_Waterfall_*.fbx"],
                    (-25.0, 0.5, 4.5), 17.0, 300.0, 14.0, 50.0,
                    (2000, 1250)),
    "route_tallgrass": None,
    "ground_waterline": (["Env_Ground_ShoreBand.fbx", "Env_Water_Lake.fbx",
                          "Env_Ledge_*.fbx", "Env_Ramp_LakeSpur.fbx"],
                         (6.0, -1.6, 14.0), 13.0, 35.0, 16.0, 50.0,
                         (2000, 1250)),
}



# --------------------------------------------------------------------------
# The assembled route shot.
#
# The question this has to answer is not "did the mesh export" but "does the
# grass read as something a creature would hide in", and that is only decidable
# at the shipped camera (yaw 35, pitch 42) with the ground under it and
# something of known height standing in it. So the shot places a 1.70 m trainer
# block and a 0.45 m creature block in the patch, and the tall grass is placed
# at the density it is meant to be used at -- one cluster per square metre of
# patch, which is roughly a tenth of the 834 lawn clumps the layout is burning.
# --------------------------------------------------------------------------

import json
import random as _random


def _layout():
    with open(G.LAYOUT, "r", encoding="utf-8") as f:
        return json.load(f)


def scatter_tallgrass(centre, radius, count, seed=7):
    """Place the four cluster meshes over a patch, with the per-instance
    rotation and scale the runtime instancer will apply."""
    base = E.FAMILY_DIR["Foliage"]
    protos = []
    for k in "ABCD":
        p = os.path.join(base, "Env_TallGrass_Cluster_%s.fbx" % k)
        objs = [o for o in import_fbx(p) if o.type == 'MESH']
        if objs:
            protos.append(objs[0])
    rng = _random.Random(seed)
    made = []
    for i in range(count):
        a = rng.uniform(0, 6.2832)
        r = radius * math.sqrt(rng.random())
        x = centre[0] + math.cos(a) * r
        z = centre[2] + math.sin(a) * r
        y = G.ground_route(x, z)
        src = protos[rng.randrange(len(protos))]
        ob = src.copy()
        ob.data = src.data
        bpy.context.scene.collection.objects.link(ob)
        ob.location = (x, z, y)                      # blender (x, layoutZ, h)
        ob.rotation_euler = (0, 0, rng.uniform(0, 6.2832))
        sc = rng.uniform(0.86, 1.16)
        ob.scale = (sc, sc, rng.uniform(0.92, 1.12))
        made.append(ob)
    for p in protos:
        p.hide_render = True
    return made


def _block(name, x, z, height, width, rgb):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= width
        v.co.y *= width * 0.6
        v.co.z = (v.co.z + 0.5) * height
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new(name, me)
    ob.location = (x, z, G.ground_route(x, z))
    bpy.context.scene.collection.objects.link(ob)
    m = G.flat_material("M_Ref_" + name, rgb, 0.6)
    ob.data.materials.append(m)
    return ob


def render_route_tallgrass():
    """Route section with the new tall grass and the new ground, at the
    shipped camera angle, with scale references standing in the patch."""
    E.reset_scene()
    E.setup_render(res=(2000, 1250), samples=48, world_rgb=(0.60, 0.72, 0.88),
                   strength=0.85)
    sun()
    ground = load(["Env_Ground_RouteFloor.fbx"])
    restyle(ground)
    # the layout's own tall-grass patch on the route, at the replacement density
    # one cluster per square metre, which is the density the asset is sized
    # for and roughly a tenth of the layout's 834 lawn clumps
    centre = (-6.0, 0.0, 36.0)
    scatter_tallgrass(centre, 11.0, 420, seed=11)
    trees = []
    rng = _random.Random(3)
    for (tx, tz) in ((-17.0, 38.0), (4.0, 39.5), (-13.0, 22.0)):
        for o in import_fbx(os.path.join(E.FAMILY_DIR["Foliage"],
                                         "Env_Tree_Broadleaf_B.fbx")):
            if o.type != 'MESH':
                continue
            o.location = (tx, tz, G.ground_route(tx, tz))
            o.rotation_euler = (0, 0, rng.uniform(0, 6.28))
            trees.append(o)
    _block("Trainer", -3.0, 34.0, 1.70, 0.42, (0.20, 0.24, 0.34))
    _block("Creature", -8.0, 37.5, 0.45, 0.40, (0.72, 0.30, 0.22))
    place_camera((-6.0, 0.6, 36.0), 15.0, 35.0, 42.0, 44.0)
    out = os.path.join(OUT, "route_tallgrass.png")
    E.render_to(out)
    E.log("rendered %s" % out)
    return out


def render_shot(name):
    pats, target, dist, yaw, pitch, lens, res = SHOTS[name]
    E.reset_scene()
    E.setup_render(res=res, samples=42, world_rgb=(0.60, 0.72, 0.88),
                   strength=0.85)
    sun()
    objs = load(pats)
    restyle(objs)
    place_camera(target, dist, yaw, pitch, lens)
    path = os.path.join(OUT, "%s.png" % name)
    E.render_to(path)
    E.log("rendered %s (%d meshes)" % (path, len(objs)))
    return path


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    want = argv or list(SHOTS)
    G.load_water_polys()
    for name in want:
        if name == "route_tallgrass":
            render_route_tallgrass()
        else:
            render_shot(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
