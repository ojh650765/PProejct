"""Close-range inspection renders of an exported FBX, with BACK FACES CULLED.

Why this exists as its own tool.  Every other render script in this folder
draws with EEVEE's default two-sided shading, and that hides the one class of
defect that costs the most time downstream: a solid that is really a set of
open surfaces.  Env_Well shipped see-through because its drum was two open
cylinders with no inner wall, and every preview of it looked correct, because
Blender happily drew the inside of the far wall where the near wall should
have been.  Unity culls back faces and does not.

So every camera here renders through materials with `use_backface_culling`
forced on.  A hole in a hull shows up as a window straight through the object
onto the background, which is deliberately a flat mid tone so it is obvious.

Usage (from the repo root):

    blender -b -P Tools/Blender/environment/render_inspect.py -- \
        --fbx Assets/Game/Art/Props/Env_Prop_CaptureBall.fbx \
        --out Tools/Blender/environment/previews/inspect \
        --tag ball_before --preset prop

Presets pick sensible camera distances and eye heights:

    prop      small handled object; eye level = object centre
    building  eye level 1.65 m, plus a straight-on 1.70 m shot and a
              look-into-the-doorway shot
    field     the object seen as a tiny thing on the ground from the game
              camera, i.e. what an item ball actually looks like in play

Every preset also renders the game's real camera: pitch 38 deg, yaw 45 deg,
40 deg vertical FOV.
"""

import sys
import os
import math
import argparse

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
from mathutils import Vector, Euler

import envlib as E


# --------------------------------------------------------------------------

GAME_PITCH = 38.0
GAME_YAW = 45.0
GAME_FOV_V = 40.0


def cull_everything(state=True):
    """Force back-face culling on every material in the file.

    This is the whole point of the module, so it is applied to imported
    materials, generated ones and the ground alike -- an uncalled-out
    exception is how a hole gets missed.
    """
    n = 0
    for mat in bpy.data.materials:
        mat.use_backface_culling = state
        n += 1
    return n


def import_fbx(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.abspath(path))
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == 'MESH']
    # the FBX carries the export's axis conversion baked in, so re-importing
    # it lands the model back on Blender's own axes; nothing to undo here.
    return meshes


def look_camera(loc, target, fov_v=None, lens=50.0):
    cam = bpy.data.cameras.new("Cam")
    if fov_v is not None:
        cam.sensor_fit = 'VERTICAL'
        cam.angle_y = math.radians(fov_v)
    else:
        cam.lens = lens
    cam.clip_start = 0.005
    cam.clip_end = 500.0
    co = bpy.data.objects.new("Cam", cam)
    co.location = Vector(loc)
    bpy.context.scene.collection.objects.link(co)
    d = Vector(target) - Vector(loc)
    co.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
    bpy.context.scene.camera = co
    return co


def drop_cameras():
    for o in list(bpy.data.objects):
        if o.type == 'CAMERA':
            bpy.data.objects.remove(o, do_unlink=True)


def orbit_loc(target, azimuth_deg, pitch_deg, dist):
    """azimuth 0 looks at the model's -Y face, i.e. the front by the kit's
    convention (Blender -Y exports to Unity +Z)."""
    a = math.radians(azimuth_deg)
    p = math.radians(pitch_deg)
    # start on -Y and rotate anticlockwise about +Z
    dirn = Vector((math.sin(a), -math.cos(a), 0.0))
    return Vector(target) + dirn * (dist * math.cos(p)) + \
        Vector((0, 0, dist * math.sin(p)))


# --------------------------------------------------------------------------

def run(fbxs, out_dir, tag, preset, extra_scale=1.0, label_bg=(0.42, 0.46, 0.52)):
    E.reset_scene()
    sc = E.setup_render(res=(1100, 800), samples=48, world_rgb=label_bg,
                        strength=1.0)
    objs = []
    for p in fbxs:
        objs += import_fbx(p)
    if not objs:
        raise SystemExit("no meshes imported from %s" % (fbxs,))

    lo, hi = E.obj_bounds(objs)
    size = hi - lo
    centre = (lo + hi) * 0.5
    radius = max(size.x, size.y, size.z) * 0.5 or 0.1

    E.add_studio_lights(key_energy=3.1, scale=max(1.0, radius * 1.2))
    ground = E.ground_plane(size=max(60.0, radius * 40), rgb=(0.34, 0.40, 0.30))
    ground.location.z = lo.z - 0.0005

    culled = cull_everything(True)
    print("[inspect] back-face culling forced on %d materials" % culled)
    print("[inspect] bbox min %s" % ([round(v, 4) for v in lo],))
    print("[inspect] bbox max %s" % ([round(v, 4) for v in hi],))
    print("[inspect] bbox size %s" % ([round(v, 4) for v in size],))

    os.makedirs(out_dir, exist_ok=True)
    shots = []

    if preset == "building":
        eye = 1.65
        dist = max(radius * 2.5, size.length * 0.85)
        aim = Vector((centre.x, centre.y, min(centre.z, lo.z + size.z * 0.45)))
        for (name, az) in (("front", 0), ("right", 90), ("back", 180),
                           ("left", 270)):
            loc = orbit_loc(aim, az, 4.0, dist)
            loc.z = lo.z + eye
            shots.append((name, loc, aim, None, 42.0))
        # straight on at 1.70 m eye height, close, to judge the door
        shots.append(("door_eye",
                      Vector((centre.x, lo.y - max(3.4, size.y * 0.55),
                              lo.z + 1.70)),
                      Vector((centre.x, centre.y, lo.z + 1.05)), None, 38.0))
        # nose right up to the doorway, looking in
        shots.append(("door_into",
                      Vector((centre.x, lo.y - 1.30, lo.z + 1.35)),
                      Vector((centre.x, centre.y + size.y * 0.3, lo.z + 1.05)),
                      55.0, None))
    elif preset == "field":
        # an item ball sitting on the ground, seen from the game camera at
        # walking distance -- the size it is actually read at in play
        aim = Vector((centre.x, centre.y, lo.z + size.z * 0.5))
        for d in (5.5, 2.6):
            loc = orbit_loc(aim, GAME_YAW, GAME_PITCH, d)
            shots.append(("game_%.1fm" % d, loc, aim, GAME_FOV_V, None))
    else:   # prop
        eye = centre.z
        dist = radius * 3.2
        aim = Vector((centre.x, centre.y, eye))
        for (name, az) in (("front", 0), ("right", 90), ("back", 180),
                           ("left", 270)):
            shots.append((name, orbit_loc(aim, az, 6.0, dist), aim, None, 46.0))
        shots.append(("top", Vector((centre.x, centre.y - radius * 0.4,
                                     centre.z + dist)), centre, None, 46.0))

    # the game's own camera, on every preset
    if preset != "field":
        gaim = Vector((centre.x, centre.y, lo.z + size.z * 0.42))
        gdist = 5.5 if preset == "prop" else max(5.5, radius * 3.4)
        loc = orbit_loc(gaim, GAME_YAW, GAME_PITCH, gdist * extra_scale)
        shots.append(("game", loc, gaim, GAME_FOV_V, None))

    written = []
    for (name, loc, aim, fov, lens) in shots:
        drop_cameras()
        look_camera(loc, aim, fov_v=fov, lens=lens or 50.0)
        p = os.path.join(out_dir, "%s_%s.png" % (tag, name))
        E.render_to(p)
        written.append(p)
        print("[inspect] wrote %s" % p)
    return written


def main():
    argv = sys.argv
    argv = argv[argv.index("--") + 1:] if "--" in argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument("--fbx", action="append", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--preset", default="prop",
                    choices=("prop", "building", "field"))
    ap.add_argument("--scale", type=float, default=1.0)
    a = ap.parse_args(argv)
    run(a.fbx, a.out, a.tag, a.preset, a.scale)


if __name__ == "__main__":
    main()
