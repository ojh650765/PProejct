"""
Dressed-scene render: composes a route section, a town corner and a cave
mouth out of the shipped FBXs, exactly as the integrator would dress them in
Unity -- modules snapped to the 0.5 m grid, everything else scattered with a
seeded RNG.

This is the honest test.  A contact sheet flatters a kit; a dressed scene
shows whether the pieces share a language.

    blender --background --python render_dressed.py
"""

import sys
import os
import math
import random

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Euler, Matrix

import envlib as E
import textures as T
import render_contact as RC

GRID = 0.5
_CACHE = {}


def load(family, name):
    """Import an FBX once and keep it off-camera as a template."""
    key = (family, name)
    if key in _CACHE:
        return _CACHE[key]
    path = os.path.join(E.FAMILY_DIR[family], name + ".fbx")
    if not os.path.exists(path):
        E.log("  !! missing %s" % path)
        return None
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path, axis_forward='-Y', axis_up='Z',
                             automatic_bone_orientation=True)
    new = [o for o in bpy.context.scene.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    RC.apply_atlas(meshes, family)
    for o in new:
        o.hide_render = True
        o.hide_viewport = True
        o.location = (0, 0, -900)
    _CACHE[key] = (new, meshes)
    return _CACHE[key]


AX = {'x': Vector((1, 0, 0)), 'y': Vector((0, 1, 0)), 'z': Vector((0, 0, 1))}


def pose_bone(rig, name, rx=0.0, ry=0.0, rz=0.0):
    """Set a pose rotation about ARMATURE axes.  Converting through
    bone.matrix_local means this works whatever local axes the FBX importer
    decided to give the bone, which raw euler assignment does not."""
    pb = rig.pose.bones.get(name)
    if pb is None:
        return
    Minv = pb.bone.matrix_local.to_3x3().inverted()
    from mathutils import Quaternion
    q = Quaternion((1, 0, 0, 0))
    for (ax, ang) in (('y', ry), ('x', rx), ('z', rz)):
        if abs(ang) < 1e-9:
            continue
        q = Quaternion((Minv @ AX[ax]).normalized(), math.radians(ang)) @ q
    pb.rotation_mode = 'QUATERNION'
    pb.rotation_quaternion = q


def stand_pose(rig, rng):
    """A relaxed standing pose so the cast is not crucified in every render."""
    k = rng.uniform(-1.0, 1.0)
    pose_bone(rig, "UpperArm.L", rx=k * 3 - 3, ry=73 - abs(k) * 2)
    pose_bone(rig, "UpperArm.R", rx=-k * 3 - 3, ry=-73 + abs(k) * 2)
    pose_bone(rig, "LowerArm.L", rz=-(16 + abs(k) * 8))
    pose_bone(rig, "LowerArm.R", rz=(16 + abs(k) * 8))
    pose_bone(rig, "Hips", rx=-1.0, ry=-k * 2.0, rz=-k * 2.0)
    pose_bone(rig, "Spine", rx=2.0, ry=k * 1.0)
    pose_bone(rig, "Chest", rx=-1.0, rz=k * 1.5)
    pose_bone(rig, "Neck", rx=-1.5)
    pose_bone(rig, "Head", rx=-1.0, rz=k * 6.0)
    pose_bone(rig, "UpperLeg.L", rx=-1.0)
    pose_bone(rig, "UpperLeg.R", rx=1.0)
    pose_bone(rig, "LowerLeg.L", rx=2.0)
    pose_bone(rig, "LowerLeg.R", rx=2.0)


def place_character(name, loc, rot_z, rng):
    """Characters need their armature, so they get their own import path:
    a fresh copy of mesh + rig, posed, rather than a shared template."""
    path = os.path.join(E.FAMILY_DIR["Characters"], name + ".fbx")
    if not os.path.exists(path):
        return []
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path, axis_forward='-Y', axis_up='Z',
                             automatic_bone_orientation=True)
    new = [o for o in bpy.context.scene.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    rig = next((o for o in new if o.type == 'ARMATURE'), None)
    RC.apply_atlas(meshes, "Characters")
    if rig:
        stand_pose(rig, rng)
    for o in new:
        if o.parent is None:
            o.location = Vector(loc)
            o.rotation_euler = Euler((0, 0, rot_z))
    return new


def place(family, name, loc, rot_z=0.0, scale=1.0, tilt=0.0, jitter=None,
          rng=None):
    got = load(family, name)
    if not got:
        return []
    new, meshes = got
    out = []
    for m in meshes:
        o = m.copy()
        o.data = m.data
        bpy.context.scene.collection.objects.link(o)
        # a character mesh is parented to its template armature, which is
        # parked off-camera; keeping the parent drags the copy down with it
        # and blows the scene bounding box out to nothing.
        o.parent = None
        o.matrix_parent_inverse = Matrix.Identity(4)
        for mod in list(o.modifiers):
            if mod.type == 'ARMATURE':
                o.modifiers.remove(mod)
        o.hide_render = False
        o.hide_viewport = False
        loc = Vector(loc)
        if jitter and rng:
            loc = loc + Vector((rng.uniform(-jitter, jitter),
                                rng.uniform(-jitter, jitter), 0))
        o.location = loc
        o.rotation_euler = Euler((tilt, 0, rot_z))
        o.scale = (scale, scale, scale)
        out.append(o)
    return out


def snap(v):
    return round(v / GRID) * GRID


# --------------------------------------------------------------------------
# terrain shell
# --------------------------------------------------------------------------

def build_ground(rng):
    """A single hand-shaped ground mesh: the route dips into a valley, the
    lake sits at the far end, the town platform is raised and level."""
    W, D = 74.0, 64.0
    nx, ny = 92, 80
    bm = bmesh.new()
    grid = []
    for i in range(nx + 1):
        col = []
        x = -W * .5 + W * i / nx
        for j in range(ny + 1):
            y = -D * .5 + D * j / ny
            z = 0.0
            # rolling base
            z += E.fbm((x * 0.045, y * 0.045, 0.0), 4, seed=3.0) * 2.6
            z += E.fbm((x * 0.16, y * 0.16, 5.0), 3, seed=9.0) * 0.55
            # the route: a worn corridor running north-south around x = -6
            d = abs(x + 6.0 + math.sin(y * 0.10) * 3.4)
            z -= math.exp(-(d / 5.0) ** 2) * 1.5
            # town platform, raised and flat, north-east
            tx, ty = 15.0, 15.0
            td = max(abs(x - tx) / 15.0, abs(y - ty) / 12.0)
            plat = 1.0 - min(1.0, max(0.0, (td - 0.72) / 0.30))
            z = z * (1 - plat) + (2.35 + E.fbm((x * 0.3, y * 0.3, 2.0), 2,
                                               seed=1.0) * 0.06) * plat
            # river: an east-west channel that feeds the lake
            ry = -6.0 + math.sin(x * 0.09) * 1.8
            z -= math.exp(-(abs(y - ry) / 2.4) ** 2) * 2.1
            # lake basin, south-west
            lx, ly = -22.0, -21.0
            ld = math.hypot((x - lx) / 15.0, (y - ly) / 12.0)
            lake = 1.0 - min(1.0, max(0.0, (ld - 0.55) / 0.45))
            z = z * (1 - lake) + (-1.5) * lake
            # cliff shelf, north-west (the cave sits against it)
            cd = max(0.0, min(1.0, (y - 20.0) / 6.0))
            z += cd * 1.4
            col.append(bm.verts.new((x, y, z)))
        grid.append(col)
    for i in range(nx):
        for j in range(ny):
            f = bm.faces.new((grid[i][j], grid[i + 1][j],
                              grid[i + 1][j + 1], grid[i][j + 1]))
            f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, "Ground")
    obj.data.use_auto_smooth = True
    obj.data.auto_smooth_angle = math.radians(60)

    mat = bpy.data.materials.new("M_Scene_Ground")
    mat.use_nodes = True
    nt = mat.node_tree
    b = nt.nodes["Principled BSDF"]
    b.inputs["Roughness"].default_value = 0.94
    # grass with a worn dirt corridor and a sandy lake shore, driven by the
    # same functions the height uses so the paint follows the form
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(geo.outputs["Position"], sep.inputs["Vector"])
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 2.2
    noise.inputs["Detail"].default_value = 6.0
    nt.links.new(geo.outputs["Position"], noise.inputs["Vector"])
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].position = 0.36
    ramp.color_ramp.elements[0].color = (0.18, 0.32, 0.12, 1)
    ramp.color_ramp.elements[1].position = 0.68
    ramp.color_ramp.elements[1].color = (0.36, 0.50, 0.17, 1)
    nt.links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], b.inputs["Base Color"])
    obj.data.materials.append(mat)
    return obj


def height_at(x, y):
    """Same field as build_ground, for placing props on the surface."""
    z = 0.0
    z += E.fbm((x * 0.045, y * 0.045, 0.0), 4, seed=3.0) * 2.6
    z += E.fbm((x * 0.16, y * 0.16, 5.0), 3, seed=9.0) * 0.55
    d = abs(x + 6.0 + math.sin(y * 0.10) * 3.4)
    z -= math.exp(-(d / 5.0) ** 2) * 1.5
    ry = -6.0 + math.sin(x * 0.09) * 1.8
    z -= math.exp(-(abs(y - ry) / 2.4) ** 2) * 2.1
    tx, ty = 15.0, 15.0
    td = max(abs(x - tx) / 15.0, abs(y - ty) / 12.0)
    plat = 1.0 - min(1.0, max(0.0, (td - 0.72) / 0.30))
    z = z * (1 - plat) + (2.35 + E.fbm((x * 0.3, y * 0.3, 2.0), 2,
                                       seed=1.0) * 0.06) * plat
    lx, ly = -22.0, -21.0
    ld = math.hypot((x - lx) / 15.0, (y - ly) / 12.0)
    lake = 1.0 - min(1.0, max(0.0, (ld - 0.55) / 0.45))
    z = z * (1 - lake) + (-1.5) * lake
    cd = max(0.0, min(1.0, (y - 20.0) / 6.0))
    z += cd * 1.4
    return z


def water_plane(level=-0.55):
    bm = bmesh.new()
    # lake: an ellipse cut to the basin rather than a rectangle sticking out
    ring = []
    for i in range(40):
        a = 2 * math.pi * i / 40
        ring.append(bm.verts.new((-22.0 + math.cos(a) * 14.6,
                                  -21.0 + math.sin(a) * 11.6, level)))
    c = bm.verts.new((-22.0, -21.0, level))
    for i in range(40):
        bm.faces.new((ring[i], ring[(i + 1) % 40], c))
    # river ribbon following the channel, meeting the lake
    prev = None
    for k in range(41):
        x = -34.0 + k * 1.35
        yy = -6.0 + math.sin(x * 0.09) * 1.8
        a = bm.verts.new((x, yy - 2.15, level - 0.55))
        b = bm.verts.new((x, yy + 2.15, level - 0.55))
        if prev:
            bm.faces.new((prev[0], prev[1], b, a))
        prev = (a, b)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    obj = E.bm_to_obj(bm, "Water")
    obj.location = (0, 0, 0)
    mat = bpy.data.materials.new("M_Scene_Water")
    mat.use_nodes = True
    b = mat.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (0.09, 0.30, 0.40, 1)
    b.inputs["Roughness"].default_value = 0.08
    if "Transmission" in b.inputs:
        b.inputs["Transmission"].default_value = 0.25
    obj.data.materials.append(mat)
    return obj


# --------------------------------------------------------------------------
# dressing
# --------------------------------------------------------------------------

TREES = ["Env_Tree_Broadleaf_A", "Env_Tree_Broadleaf_B", "Env_Tree_Broadleaf_C",
         "Env_Tree_Conifer_A", "Env_Tree_Conifer_B", "Env_Tree_Conifer_C",
         "Env_Tree_Birch_A", "Env_Tree_Birch_B", "Env_Tree_Birch_C"]
GRASS = ["Env_Grass_Clump_A", "Env_Grass_Clump_B", "Env_Grass_Clump_C",
         "Env_Grass_Clump_D"]
BUSHES = ["Env_Bush_A", "Env_Bush_B", "Env_Bush_C"]
FLOWERS = ["Env_Flower_Red", "Env_Flower_Yellow", "Env_Flower_Purple",
           "Env_Flower_White"]
ROCKS = ["Env_Rock_Boulder_A", "Env_Rock_Boulder_B", "Env_Rock_Boulder_C",
         "Env_Rock_Boulder_D", "Env_Rock_Boulder_Mossy_E",
         "Env_Rock_Boulder_Wet_F", "Env_Rock_Scatter_A", "Env_Rock_Scatter_B"]


def dress(rng):
    made = []

    # ---- route corridor: a worn path with dense verge planting ----------
    for k in range(30):
        t = k / 29.0
        y = -30.0 + t * 56.0
        x = -6.0 + math.sin(y * 0.10) * 3.4
        z = height_at(x, y)
        made += place("Town", "Env_Path_Paved_2m", (snap(x), snap(y), z - 0.06),
                      rot_z=math.sin(y * 0.10) * 0.35, rng=rng, jitter=0.10)
    # verge: grass, flowers, bushes thinning away from the path
    for k in range(950):
        y = rng.uniform(-30, 30)
        px = -6.0 + math.sin(y * 0.10) * 3.4
        side = rng.choice((-1, 1))
        off = 1.4 + abs(rng.gauss(0, 5.5))
        x = px + side * off
        if x > 4.0 and y > 4.0:
            continue                       # keep the town platform clear
        z = height_at(x, y)
        if z < -0.4:
            continue                       # do not plant in the lake
        r = rng.random()
        if r < 0.62:
            nm = rng.choice(GRASS)
            s = rng.uniform(0.75, 1.45)
        elif r < 0.76:
            nm = rng.choice(FLOWERS)
            s = rng.uniform(0.85, 1.25)
        elif r < 0.90:
            nm = rng.choice(BUSHES)
            s = rng.uniform(0.7, 1.3)
        else:
            nm = "Env_Fern_A" if rng.random() < 0.5 else "Env_Fern_B"
            s = rng.uniform(0.8, 1.4)
        made += place("Foliage", nm, (x, y, z), rot_z=rng.uniform(0, 6.28),
                      scale=s, rng=rng, jitter=0.25)

    # ---- trees: clumped, never on a grid, never the same scale ----------
    clumps = [(-14, 6, 5), (-13, -11, 4), (2, -18, 5), (-2, 13, 3),
              (-20, 8, 4), (8, -26, 5), (-26, 3, 4), (4, 24, 3),
              (26, -6, 5), (23, 25, 4), (-30, -9, 3), (12, 29, 4),
              (-31, 12, 4), (31, 8, 4), (0, -30, 4), (-9, -27, 3),
              (18, -20, 4), (30, 22, 3), (-33, -22, 3), (7, 6, 2)]
    for (cx, cy, n) in clumps:
        for k in range(n + rng.randint(0, 3)):
            a = rng.uniform(0, 6.28)
            d = abs(rng.gauss(0, 3.0))
            x, y = cx + math.cos(a) * d, cy + math.sin(a) * d
            if math.hypot((x - 15.0) / 11.0, (y - 15.0) / 9.5) < 1.0:
                continue          # the town square stays open
            z = height_at(x, y)
            if z < -0.3:
                continue
            made += place("Foliage", rng.choice(TREES), (x, y, z),
                          rot_z=rng.uniform(0, 6.28),
                          scale=rng.uniform(0.82, 1.30),
                          tilt=rng.uniform(-0.035, 0.035))
    # willows at the lakeside only
    for k in range(6):
        a = rng.uniform(2.2, 4.4)
        x = -22.0 + math.cos(a) * rng.uniform(11.5, 14.0)
        y = -21.0 + math.sin(a) * rng.uniform(9.5, 11.5)
        z = height_at(x, y)
        made += place("Foliage", rng.choice(["Env_Tree_Willow_A",
                                             "Env_Tree_Willow_B",
                                             "Env_Tree_Willow_C"]),
                      (x, y, z), rot_z=rng.uniform(0, 6.28),
                      scale=rng.uniform(0.9, 1.25))
    # reeds and lily pads at the water line
    for k in range(90):
        a = rng.uniform(0, 6.28)
        rr = rng.uniform(0.86, 1.06)
        x = -22.0 + math.cos(a) * 14.5 * rr
        y = -21.0 + math.sin(a) * 11.5 * rr
        z = height_at(x, y)
        nm = rng.choice(["Env_Reed_A", "Env_Reed_B"])
        made += place("Foliage", nm, (x, y, max(z, -0.85)),
                      rot_z=rng.uniform(0, 6.28), scale=rng.uniform(0.8, 1.3))
    for k in range(26):
        a = rng.uniform(0, 6.28)
        rr = rng.uniform(0.25, 0.85)
        made += place("Foliage",
                      "Env_Lilypad_A" if rng.random() < 0.6 else "Env_Lilypad_B",
                      (-22.0 + math.cos(a) * 14.0 * rr,
                       -21.0 + math.sin(a) * 11.0 * rr, -0.53),
                      rot_z=rng.uniform(0, 6.28), scale=rng.uniform(0.8, 1.4))

    # ---- rock scatter along the route and the shore --------------------
    for k in range(95):
        y = rng.uniform(-30, 30)
        px = -6.0 + math.sin(y * 0.10) * 3.4
        x = px + rng.choice((-1, 1)) * rng.uniform(2.4, 16.0)
        z = height_at(x, y)
        if z < -0.3:
            continue
        made += place("Terrain", rng.choice(ROCKS), (x, y, z),
                      rot_z=rng.uniform(0, 6.28),
                      scale=rng.uniform(0.55, 1.25),
                      tilt=rng.uniform(-0.10, 0.10))

    # ---- cliff run + cave mouth, north-west -----------------------------
    cliff_y = 21.0
    x = -34.0
    while x < 2.0:
        for (nm, ln) in (("Env_Cliff_Wall_6m", 6.0), ("Env_Cliff_Wall_4m", 4.0),
                         ("Env_Cliff_Wall_Tall_4m", 4.0),
                         ("Env_Cliff_Wall_2m", 2.0)):
            if rng.random() < 0.4:
                break
        nm, ln = rng.choice([("Env_Cliff_Wall_6m", 6.0),
                             ("Env_Cliff_Wall_4m", 4.0),
                             ("Env_Cliff_Wall_Tall_4m", 4.0),
                             ("Env_Cliff_Wall_2m", 2.0)])
        if -14.0 < x < -8.0:          # leave the gap for the cave mouth
            x = -8.0
            continue
        made += place("Terrain", nm, (snap(x), snap(cliff_y),
                                      height_at(x, cliff_y) - 0.35))
        x += ln
    cave_x, cave_y = -11.0, 20.6
    cz = height_at(cave_x, cave_y)
    made += place("Terrain", "Env_Cave_Arch", (cave_x, cave_y, cz - 0.12),
                  rot_z=0.0, scale=1.15)
    for k in range(7):
        made += place("Terrain",
                      rng.choice(["Env_Cave_Stalagmite_A",
                                  "Env_Cave_Stalagmite_B"]),
                      (cave_x + rng.uniform(-1.7, 1.7),
                       cave_y - rng.uniform(0.9, 2.2), cz - 0.05),
                      rot_z=rng.uniform(0, 6.28),
                      scale=rng.uniform(0.7, 1.3))
    for k in range(5):
        made += place("Terrain", "Env_Cave_Stalactite_A",
                      (cave_x + rng.uniform(-0.9, 0.9),
                       cave_y - rng.uniform(0.9, 1.9), cz + 3.05),
                      rot_z=rng.uniform(0, 6.28), scale=rng.uniform(0.6, 1.1))
    for k in range(9):
        made += place("Terrain", "Env_Cave_Rubble",
                      (cave_x + rng.uniform(-3.2, 3.2),
                       cave_y + rng.uniform(-0.8, 2.4), cz),
                      rot_z=rng.uniform(0, 6.28), scale=rng.uniform(0.7, 1.3))
    for k in range(14):
        made += place("Foliage",
                      rng.choice(["Env_Moss_Cave_A", "Env_Moss_Cave_B"]),
                      (cave_x + rng.uniform(-3.6, 3.6),
                       cave_y + rng.uniform(-1.0, 2.6), cz + 0.02),
                      rot_z=rng.uniform(0, 6.28), scale=rng.uniform(0.8, 1.5))
    for k in range(6):
        made += place("Foliage",
                      rng.choice(["Env_Vine_Hanging_A", "Env_Vine_Hanging_B"]),
                      (cave_x + rng.uniform(-1.1, 1.1),
                       cave_y + 0.9 + rng.uniform(-0.2, 0.2), cz + 2.95),
                      rot_z=rng.uniform(0, 6.28), scale=rng.uniform(0.8, 1.2))

    # ---- town corner, north-east ---------------------------------------
    tz = height_at(15, 15)
    # paved square
    for i in range(-4, 5):
        for j in range(-4, 5):
            if abs(i) + abs(j) > 6:
                continue
            made += place("Town", "Env_Path_Paved_2m",
                          (15 + i * 2.0, 15 + j * 2.0, tz - 0.02),
                          rot_z=rng.choice((0, 1.5708, 3.1416, 4.7124)))
    made += place("Town", "Env_Building_PokeLab", (15.5, 21.0, tz),
                  rot_z=math.radians(184))
    made += place("Town", "Env_House_Cottage_A", (8.0, 12.5, tz),
                  rot_z=math.radians(64))
    made += place("Town", "Env_House_Townhouse_B", (21.5, 12.0, tz),
                  rot_z=math.radians(-70))
    made += place("Town", "Env_House_Farmhouse_C", (14.5, 6.5, tz),
                  rot_z=math.radians(2))
    made += place("Town", "Env_Well", (15.0, 15.5, tz))
    made += place("Town", "Env_Market_Stall", (10.5, 17.5, tz),
                  rot_z=math.radians(-38))
    made += place("Town", "Env_Market_Stall", (19.8, 17.8, tz),
                  rot_z=math.radians(36))
    for (px, py, rz) in ((11.6, 12.0, 0.4), (18.6, 12.2, -0.4),
                         (12.0, 19.4, 2.6), (19.2, 19.6, -2.4)):
        made += place("Town", "Env_Lamp_Post", (px, py, tz), rot_z=rz)
    for (px, py) in ((12.6, 13.6), (17.4, 13.6), (13.2, 18.0)):
        made += place("Town", "Env_Bench", (px, py, tz),
                      rot_z=rng.uniform(0, 6.28))
    made += place("Town", "Env_Signpost", (11.2, 10.4, tz), rot_z=0.6)
    for k in range(9):
        made += place("Town", "Env_Planter",
                      (15 + math.cos(k * 0.70) * 7.6,
                       15 + math.sin(k * 0.70) * 6.4, tz),
                      rot_z=rng.uniform(0, 6.28))
    for k in range(7):
        made += place("Town", rng.choice(["Env_Crate", "Env_Barrel"]),
                      (rng.uniform(9.5, 20.5), rng.uniform(9.5, 20.0), tz),
                      rot_z=rng.uniform(0, 6.28))
    # fence run along the south edge of the platform
    fx = 6.0
    while fx < 24.0:
        made += place("Town", "Env_Fence_Picket_2m", (fx, 4.0, tz),
                      rot_z=0.0)
        fx += 2.0

    # ---- river crossing on the route ------------------------------------
    bx = -6.0 + math.sin(-6.0 * 0.10) * 3.4
    by = -6.0 + math.sin(bx * 0.09) * 1.8
    made += place("Terrain", "Env_Bridge_Wood", (bx, by, -1.35),
                  rot_z=math.radians(90))
    # bank edging down both sides of the channel
    for k in range(13):
        x = -30.0 + k * 4.0
        yy = -6.0 + math.sin(x * 0.09) * 1.8
        made += place("Terrain", "Env_Riverbank_4m", (snap(x), yy - 2.0, -0.55),
                      rot_z=0.0)
        made += place("Terrain", "Env_Riverbank_4m", (snap(x) + 4.0, yy + 2.0,
                                                      -0.55),
                      rot_z=math.radians(180))
    for k in range(3):
        x = 2.0 + k * 3.0
        yy = -6.0 + math.sin(x * 0.09) * 1.8
        made += place("Terrain", "Env_Stepping_Stones", (x, yy, -1.55),
                      rot_z=math.radians(90))
    made += place("Terrain", "Env_Waterfall_Shelf", (-31.0, -6.6, -1.1),
                  rot_z=math.radians(90))

    # ---- characters and props ------------------------------------------
    for (nm, px, py, rz) in (("Env_Char_Player", -6.4, -1.2, math.radians(4)),
                             ("Env_Char_NPC_Rival", -5.2, 2.2, math.radians(188)),
                             ("Env_Char_NPC_Townsfolk_A", 13.2, 13.4,
                              math.radians(120)),
                             ("Env_Char_NPC_Townsfolk_B", 16.9, 16.4,
                              math.radians(-60))):
        made += place_character(nm, (px, py, height_at(px, py)), rz, rng)
    made += place("Props", "Env_Prop_CaptureBall",
                  (-6.0, -0.6, height_at(-6.0, -0.6) + 1.15))
    made += place("Props", "Env_Prop_ResearchTerminal", (11.4, 20.4, tz),
                  rot_z=math.radians(-30))
    made += place("Props", "Env_Prop_HealingMachine", (19.6, 20.6, tz),
                  rot_z=math.radians(28))
    return made


# --------------------------------------------------------------------------

def main():
    rng = random.Random(20260815)
    E.reset_scene()
    E.setup_render(res=(2400, 1350), samples=72, world_rgb=(0.48, 0.63, 0.86),
                   strength=0.62)
    sc = bpy.context.scene
    sc.eevee.gtao_distance = 1.2
    sc.eevee.bloom_intensity = 0.030
    sun, fill, rim = E.add_studio_lights(key_energy=3.4, scale=9.0)
    sun.rotation_euler = Euler((math.radians(48), 0, math.radians(28)))

    ground = build_ground(rng)
    water = water_plane()
    made = dress(rng)
    E.log("dressed %d objects" % len(made))

    tris = sum(E.tri_count(o) for o in bpy.context.scene.objects
               if o.type == 'MESH' and not o.hide_render)
    E.log("scene triangles: %d" % tris)

    shots = [
        ("dressed_overview", (-0.55, -1.0, 0.48), 0.86, None),
        ("dressed_route", (-0.10, -1.0, 0.28), 0.78, Vector((-6.0, -3.0, 1.2))),
        ("dressed_town", (-0.50, -0.90, 0.34), 1.06, Vector((15.0, 15.0, 3.2))),
        ("dressed_cave", (0.10, -1.0, 0.24), 0.80, Vector((-11.0, 20.0, 2.0))),
    ]
    cam = E.add_camera((0, -60, 30), (0, 0, 0), lens=45)
    for (name, direction, margin, target) in shots:
        d = Vector(direction).normalized()
        if target is None:
            E.fit_camera(cam, [o for o in bpy.context.scene.objects
                               if o.type == 'MESH' and not o.hide_render and
                               o.matrix_world.translation.z > -100.0],
                         d, margin=margin)
        else:
            # frame a local region: fit to the objects near the target
            near = [o for o in bpy.context.scene.objects
                    if o.type == 'MESH' and not o.hide_render and
                    (o.matrix_world.translation - target).length < 13.0]
            E.fit_camera(cam, near or [ground], d, margin=margin)
            cam.location = cam.location + Vector((0, 0, 0.0))
        cam.data.lens = 45 if target is None else 55
        bpy.context.view_layer.update()
        E.render_to(os.path.join(E.PREVIEWS, name + ".png"))
        E.log("wrote %s.png" % name)


if __name__ == "__main__":
    main()
