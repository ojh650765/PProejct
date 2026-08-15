"""
Poke Lab creature framework - preview rendering.

Headless studio setup used to actually look at what the scripts produced: a
four-view contact sheet and a one-frame-per-clip pose sheet for every creature.
Nothing ships without being reviewed through these.
"""

import bpy
import math
import os
from mathutils import Vector

import pl_core as C

try:
    import numpy as np
except ImportError:  # pragma: no cover
    np = None

VIEWS = [
    ("front", 0.0, 8.0),
    ("threequarter", -38.0, 12.0),
    ("side", -90.0, 6.0),
    ("back", 180.0, 8.0),
]


def _rgb(mat_name, color, rough=0.9):
    mat = bpy.data.materials.new(mat_name)
    mat.use_nodes = True
    b = mat.node_tree.nodes.get('Principled BSDF')
    b.inputs['Base Color'].default_value = color
    b.inputs['Roughness'].default_value = rough
    return mat


def setup_studio(height, engine='BLENDER_EEVEE', samples=48, res=512, bg=0.30):
    scn = bpy.context.scene
    scn.render.engine = engine
    if engine == 'CYCLES':
        scn.cycles.samples = samples
        scn.cycles.device = 'CPU'
        scn.cycles.use_denoising = True
    else:
        scn.eevee.taa_render_samples = samples
        scn.eevee.use_gtao = True
        scn.eevee.use_soft_shadows = True
    scn.render.resolution_x = res
    scn.render.resolution_y = res
    scn.render.resolution_percentage = 100
    scn.render.film_transparent = False
    scn.view_settings.view_transform = 'Standard'
    scn.render.image_settings.file_format = 'PNG'

    world = bpy.data.worlds.new("Studio") if not scn.world else scn.world
    scn.world = world
    world.use_nodes = True
    bgn = world.node_tree.nodes.get('Background')
    bgn.inputs['Color'].default_value = (bg, bg * 1.03, bg * 1.10, 1.0)
    bgn.inputs['Strength'].default_value = 1.0

    # ground - a soft neutral card so the silhouette reads and shadows land
    bpy.ops.mesh.primitive_plane_add(size=height * 40)
    ground = bpy.context.object
    ground.name = "PreviewGround"
    ground.data.materials.append(_rgb("PreviewGround", (0.34, 0.36, 0.40, 1.0), 0.95))

    h = height

    def area(name, loc, energy, size, color=(1, 1, 1)):
        data = bpy.data.lights.new(name, 'AREA')
        data.energy = energy
        data.size = size
        data.color = color
        ob = bpy.data.objects.new(name, data)
        bpy.context.collection.objects.link(ob)
        ob.location = Vector(loc)
        ob.rotation_euler = (-Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
        return ob

    # One hard key sun (no inverse-square, so exposure is independent of creature
    # scale - the earlier distance-based area key blew every render to white), a
    # large weak area fill tinted to the key's complement, and a cold rim so the
    # silhouette separates from the card. Shadows are never black.
    sun_data = bpy.data.lights.new("Key", 'SUN')
    sun_data.energy = 2.3
    sun_data.angle = math.radians(6.0)
    sun_data.color = (1.0, 0.96, 0.90)
    sun = bpy.data.objects.new("Key", sun_data)
    bpy.context.collection.objects.link(sun)
    sun.location = Vector((-2.0 * h, -2.4 * h, 3.0 * h))
    sun.rotation_euler = (-sun.location).to_track_quat('-Z', 'Y').to_euler()

    area("Fill", (2.6 * h, -2.0 * h, 1.1 * h), 12.0 * h * h, h * 6.0,
         (0.80, 0.87, 1.0))
    area("Rim", (0.9 * h, 2.9 * h, 2.2 * h), 16.0 * h * h, h * 3.0,
         (0.86, 0.93, 1.0))

    cam_data = bpy.data.cameras.new("PreviewCam")
    cam_data.lens = 62.0
    cam = bpy.data.objects.new("PreviewCam", cam_data)
    bpy.context.collection.objects.link(cam)
    scn.camera = cam
    return cam


def frame_camera(cam, objs, yaw_deg, pitch_deg, margin=1.12):
    """Derive the camera from the scene bounding sphere, never by hand.

    Hand-placed cameras end up inside geometry, and a camera inside geometry is
    both wrong and pathologically slow to render.
    """
    lo, hi = subject_bounds(objs)
    ctr = (lo + hi) * 0.5
    pts = [Vector((x, y, z)) for x in (lo.x, hi.x) for y in (lo.y, hi.y)
           for z in (lo.z, hi.z)]
    rad = max((p - ctr).length for p in pts)
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    direction = Vector((math.sin(yaw) * math.cos(pitch),
                        -math.cos(yaw) * math.cos(pitch),
                        math.sin(pitch)))
    dist = rad / math.tan(cam.data.angle * 0.5) * margin
    cam.location = ctr + direction * dist
    cam.rotation_euler = (ctr - cam.location).to_track_quat('-Z', 'Y').to_euler()
    return ctr, rad


def aim_camera(cam, target, distance, yaw_deg, pitch_deg):
    t = Vector(target)
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    off = Vector((math.sin(yaw) * math.cos(pitch),
                  -math.cos(yaw) * math.cos(pitch),
                  math.sin(pitch))) * distance
    cam.location = t + off
    cam.rotation_euler = (t - cam.location).to_track_quat('-Z', 'Y').to_euler()


def render_to(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    return path


def subject_bounds(objs):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in objs:
        if o.type != 'MESH':
            continue
        for corner in o.bound_box:
            w = o.matrix_world @ Vector(corner)
            lo = Vector((min(lo.x, w.x), min(lo.y, w.y), min(lo.z, w.z)))
            hi = Vector((max(hi.x, w.x), max(hi.y, w.y), max(hi.z, w.z)))
    return lo, hi


def tile_sheet(paths, cols, out_path, gap=6):
    if np is None:
        return None
    imgs = []
    for p in paths:
        img = bpy.data.images.load(p)
        imgs.append(img)
    w, h = imgs[0].size
    rows = int(math.ceil(len(imgs) / float(cols)))
    W = cols * w + gap * (cols + 1)
    H = rows * h + gap * (rows + 1)
    buf = np.zeros((H, W, 4), dtype=np.float32)
    buf[:, :, 3] = 1.0
    buf[:, :, 0:3] = 0.02
    for i, img in enumerate(imgs):
        arr = np.zeros(w * h * 4, dtype=np.float32)
        img.pixels.foreach_get(arr)
        arr = arr.reshape(h, w, 4)
        r, c = divmod(i, cols)
        y0 = (rows - 1 - r) * h + gap * (rows - r)
        x0 = c * w + gap * (c + 1)
        buf[y0:y0 + h, x0:x0 + w] = arr
    out = bpy.data.images.new("sheet", W, H, alpha=True)
    out.pixels.foreach_set(buf.reshape(-1))
    out.filepath_raw = out_path
    out.file_format = 'PNG'
    out.save()
    for img in imgs:
        bpy.data.images.remove(img)
    bpy.data.images.remove(out)
    for p in paths:
        try:
            os.remove(p)
        except OSError:
            pass
    return out_path


def render_contact_sheet(subject_objs, base_name, out_dir, height, res=512,
                         engine='BLENDER_EEVEE', samples=48):
    os.makedirs(out_dir, exist_ok=True)
    cam = setup_studio(height, engine=engine, samples=samples, res=res)
    tmp = []
    for name, yaw, pitch in VIEWS:
        frame_camera(cam, subject_objs, yaw, pitch)
        tmp.append(render_to(os.path.join(out_dir, "_tmp_%s_%s.png" % (base_name, name))))
    return tile_sheet(tmp, 2, os.path.join(out_dir, "%s_views.png" % base_name))


def render_pose_sheet(subject_objs, arm, clips, base_name, out_dir, height,
                      res=340, engine='BLENDER_EEVEE', samples=32, cols=5):
    os.makedirs(out_dir, exist_ok=True)
    cam = setup_studio(height, engine=engine, samples=samples, res=res)
    # framed loose so a big attack pose cannot leave the frame
    frame_camera(cam, subject_objs, -34.0, 11.0, margin=1.85)
    tmp = []
    arm.animation_data_create()
    for clip in clips:
        action = bpy.data.actions.get(clip['name'])
        if action is None:
            continue
        arm.animation_data.action = action
        # a frame that shows the clip's character, not its rest pose
        f = int(round(clip['frames'] * clip.get('preview_at', 0.42)))
        bpy.context.scene.frame_set(f)
        bpy.context.view_layer.update()
        tmp.append(render_to(os.path.join(out_dir, "_tmp_%s_%s.png"
                                          % (base_name, clip['name']))))
    arm.animation_data.action = None
    bpy.context.scene.frame_set(0)
    return tile_sheet(tmp, cols, os.path.join(out_dir, "%s_poses.png" % base_name))


def render_portrait(subject_objs, base_name, out_path, height, res=512,
                    engine='BLENDER_EEVEE', samples=64):
    """UI portrait for ICreatureArtRegistry.GetPortrait - transparent background."""
    cam = setup_studio(height, engine=engine, samples=samples, res=res, bg=0.0)
    g = bpy.data.objects.get("PreviewGround")
    if g:
        bpy.data.objects.remove(g, do_unlink=True)
    bpy.context.scene.render.film_transparent = True
    frame_camera(cam, subject_objs, -26.0, 8.0, margin=1.06)
    bpy.context.scene.render.image_settings.color_mode = 'RGBA'
    return render_to(out_path)
