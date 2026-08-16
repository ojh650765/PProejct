"""
Tree canopy review renderer -- the game's camera, not a flattering one.

The kit's contact sheets frame every asset from far away at 30 deg, which
flatters a canopy: a ball of leaves and a real tree look the same at 8 px.
This renders the trees at the shipping camera instead:

    pitch 38 deg, yaw 45 deg, vertical FOV 40 deg, at 5 / 12 / 25 m

plus a backlit pass (the foliage shader has a translucency term, and a blobby
canopy is at its most obvious with the sun behind it) and a grove of four trees
(a canopy that reads solo can still turn into one green mass in a group).

The preview material mirrors PokeLab/Foliage where it matters for this
judgement: vertex colour B drives ambient occlusion at _OcclusionStrength 0.8,
and the shading normal is bent 0.45 toward the object-space outward direction
(_NormalSpherify). Without those the preview lies about the shading.

    blender --background --python render_trees.py -- --tag before Env_Tree_Broadleaf_A ...
"""

import sys
import os
import math

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Euler

import envlib as E
import textures as T

FAM = "Foliage"
OUTDIR = os.path.join(E.PREVIEWS, "trees")

PITCH = 38.0
YAW = 45.0
VFOV = 40.0

LEAFY = {"leaf_a", "leaf_b", "leaf_autumn", "needle", "bush_leaf", "vine",
         "grass", "fern", "reed", "lilypad", "moss"}


def import_fbx(path):
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path,
                             axis_forward=E.FBX_AXIS_FORWARD,
                             axis_up=E.FBX_AXIS_UP,
                             automatic_bone_orientation=True)
    return [o for o in bpy.context.scene.objects if o not in before]


def preview_material(family, surface, cols, ap):
    """Atlas colour x vertex-colour-B occlusion, spherified normal, and a
    translucent lobe for anything with leaves in it."""
    name = "MP_%s_%s" % (family, surface)
    m = bpy.data.materials.get(name)
    if m:
        return m
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (600, 0)

    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.location = (-900, 200)
    img = bpy.data.images.get(os.path.basename(ap["base"]))
    if img is None:
        img = bpy.data.images.load(ap["base"])
    tex.image = img

    # --- vertex colour B as ambient occlusion -----------------------------
    attr = nt.nodes.new("ShaderNodeAttribute")
    attr.attribute_name = "Col"
    attr.location = (-900, -120)
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    sep.location = (-720, -120)
    nt.links.new(attr.outputs["Color"], sep.inputs["Color"])
    aoMix = nt.nodes.new("ShaderNodeMapRange")   # lerp(1, B, 0.8)
    aoMix.location = (-560, -120)
    aoMix.inputs["From Min"].default_value = 0.0
    aoMix.inputs["From Max"].default_value = 1.0
    aoMix.inputs["To Min"].default_value = 0.2
    aoMix.inputs["To Max"].default_value = 1.0
    nt.links.new(sep.outputs["Blue"], aoMix.inputs["Value"])

    mul = nt.nodes.new("ShaderNodeMixRGB")
    mul.blend_type = 'MULTIPLY'
    mul.inputs["Fac"].default_value = 1.0
    mul.location = (-360, 100)
    nt.links.new(tex.outputs["Color"], mul.inputs[1])
    nt.links.new(aoMix.outputs["Result"], mul.inputs[2])

    # --- spherified normal (matches _NormalSpherify = 0.45) ---------------
    geo = nt.nodes.new("ShaderNodeNewGeometry")
    geo.location = (-900, -420)
    oinfo = nt.nodes.new("ShaderNodeObjectInfo")
    oinfo.location = (-900, -600)
    sub = nt.nodes.new("ShaderNodeVectorMath")
    sub.operation = 'SUBTRACT'
    sub.location = (-700, -500)
    nt.links.new(geo.outputs["Position"], sub.inputs[0])
    nt.links.new(oinfo.outputs["Location"], sub.inputs[1])
    nrm = nt.nodes.new("ShaderNodeVectorMath")
    nrm.operation = 'NORMALIZE'
    nrm.location = (-540, -500)
    nt.links.new(sub.outputs["Vector"], nrm.inputs[0])
    blend = nt.nodes.new("ShaderNodeMix")
    blend.data_type = 'VECTOR'
    blend.location = (-380, -500)
    blend.inputs["Factor"].default_value = 0.45
    nt.links.new(geo.outputs["Normal"], blend.inputs[4])
    nt.links.new(nrm.outputs["Vector"], blend.inputs[5])
    nrm2 = nt.nodes.new("ShaderNodeVectorMath")
    nrm2.operation = 'NORMALIZE'
    nrm2.location = (-220, -500)
    nt.links.new(blend.outputs[1], nrm2.inputs[0])

    leafy = surface in LEAFY
    diff = nt.nodes.new("ShaderNodeBsdfDiffuse")
    diff.location = (0, 150)
    diff.inputs["Roughness"].default_value = 0.9
    nt.links.new(mul.outputs["Color"], diff.inputs["Color"])
    if leafy:
        nt.links.new(nrm2.outputs["Vector"], diff.inputs["Normal"])

    if leafy:
        tr = nt.nodes.new("ShaderNodeBsdfTranslucent")
        tr.location = (0, -80)
        tint = nt.nodes.new("ShaderNodeMixRGB")
        tint.blend_type = 'MULTIPLY'
        tint.inputs["Fac"].default_value = 1.0
        tint.inputs[2].default_value = (0.55, 0.95, 0.30, 1)
        tint.location = (-180, -80)
        nt.links.new(mul.outputs["Color"], tint.inputs[1])
        nt.links.new(tint.outputs["Color"], tr.inputs["Color"])
        mix = nt.nodes.new("ShaderNodeMixShader")
        mix.location = (300, 0)
        mix.inputs["Fac"].default_value = 0.34
        nt.links.new(diff.outputs["BSDF"], mix.inputs[1])
        nt.links.new(tr.outputs["BSDF"], mix.inputs[2])
        nt.links.new(mix.outputs["Shader"], out.inputs["Surface"])
    else:
        nt.links.new(diff.outputs["BSDF"], out.inputs["Surface"])
    return m


def apply_preview(objs, family):
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
            prefix = "M_%s_" % family
            base = m.name[len(prefix):] if m.name.startswith(prefix) else names[0]
            base = base.split(".")[0]
            if base not in names and base not in cols:
                base = names[0]
            slot.material = preview_material(family, base, cols, ap)


def sky(strength=1.0, rgb=(0.60, 0.74, 0.92)):
    sc = bpy.context.scene
    w = bpy.data.worlds.get("World") or bpy.data.worlds.new("World")
    sc.world = w
    w.use_nodes = True
    bg = w.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (rgb[0], rgb[1], rgb[2], 1)
    bg.inputs[1].default_value = strength


def sun(energy, elev_deg, azim_deg, colour=(1.0, 0.96, 0.88), angle=2.0):
    li = bpy.data.lights.new("Sun", 'SUN')
    li.energy = energy
    li.angle = math.radians(angle)
    li.color = colour
    o = bpy.data.objects.new("Sun", li)
    # a sun aims along its -Z; build the rotation from the direction it shines
    d = Vector((math.cos(math.radians(elev_deg)) * math.cos(math.radians(azim_deg)),
                math.cos(math.radians(elev_deg)) * math.sin(math.radians(azim_deg)),
                math.sin(math.radians(elev_deg))))
    o.rotation_euler = d.to_track_quat('Z', 'Y').to_euler()
    bpy.context.scene.collection.objects.link(o)
    return o


def game_camera(target, dist, yaw=YAW, pitch=PITCH):
    cam = bpy.data.cameras.new("Cam")
    cam.sensor_fit = 'VERTICAL'
    cam.angle_y = math.radians(VFOV)
    co = bpy.data.objects.new("Cam", cam)
    y, p = math.radians(yaw), math.radians(pitch)
    off = Vector((math.cos(p) * math.cos(y), math.cos(p) * math.sin(y),
                  math.sin(p))) * dist
    co.location = Vector(target) + off
    co.rotation_euler = (Vector(target) - co.location).to_track_quat('-Z', 'Y').to_euler()
    bpy.context.scene.collection.objects.link(co)
    bpy.context.scene.camera = co
    return co


def base_render(res=(1200, 1200), samples=64):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'
    sc.eevee.taa_render_samples = samples
    sc.eevee.use_gtao = False          # judge the vertex-colour AO, not GTAO
    sc.eevee.use_soft_shadows = True
    sc.eevee.shadow_cube_size = '2048'
    sc.eevee.shadow_cascade_size = '4096'
    sc.eevee.use_bloom = False
    sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = 'PNG'
    sc.render.film_transparent = False
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'
    return sc


# --------------------------------------------------------------------------
# Wind preview
#
# A port of PL_FoliageWind (Assets/Game/Shaders/Library/PokeLabCommon.hlsl) to
# Python, driving the mesh directly, with the amplitudes the shipping material
# actually carries (M_Env_Foliage.mat: sway 0.24 @ 1.1, flutter 0.062 @ 7.4,
# gust 1.7).  Blender is Z-up where Unity is Y-up, so the shader's world XZ is
# this scene's XY and the shader's -Y bob is a -Z bob here.
#
# The point of it is to answer one question with a picture rather than a claim:
# does the trunk move.
# --------------------------------------------------------------------------

MAT_SWAY_AMP, MAT_SWAY_SPD = 0.24, 1.1
MAT_FLUT_AMP, MAT_FLUT_SPD = 0.062, 7.4
MAT_GUST = 1.7


def _frac(x):
    return x - math.floor(x)


def _hash21(x, y):
    p = [_frac(x * 0.1031), _frac(y * 0.1031), _frac(x * 0.1031)]
    d = p[0] * (p[1] + 33.33) + p[1] * (p[2] + 33.33) + p[2] * (p[0] + 33.33)
    p = [p[0] + d, p[1] + d, p[2] + d]
    return _frac((p[0] + p[1]) * p[2])


def _value_noise(x, y):
    ix, iy = math.floor(x), math.floor(y)
    fx, fy = x - ix, y - iy
    fx = fx * fx * (3.0 - 2.0 * fx)
    fy = fy * fy * (3.0 - 2.0 * fy)
    a = _hash21(ix, iy)
    b = _hash21(ix + 1, iy)
    c = _hash21(ix, iy + 1)
    d = _hash21(ix + 1, iy + 1)
    return (a + (b - a) * fx) + ((c + (d - c) * fx) - (a + (b - a) * fx)) * fy


def foliage_wind(co, r, g, t, seed=0.0):
    if r <= 0.001:
        return (0.0, 0.0, 0.0)
    dx, dy = 0.70710678, 0.70710678
    phase = g * 6.2831853 + seed * 6.2831853
    travel = (co.x * dx + co.y * dy) * 0.12
    sway = math.sin(t * MAT_SWAY_SPD + travel + phase)
    gf = _value_noise(co.x * 0.035 - dx * t * 0.35, co.y * 0.035 - dy * t * 0.35)
    gust = gf * gf * MAT_GUST
    tip = r * r * r
    flutter = math.sin(t * MAT_FLUT_SPD + phase * 3.1 + travel * 4.0) * tip
    amp = (sway * MAT_SWAY_AMP * (1.0 + gust) + flutter * MAT_FLUT_AMP) * r
    return (dx * amp, dy * amp, -abs(amp) * 0.25)


def apply_wind(objs, t):
    for o in objs:
        if o.type != 'MESH':
            continue
        me = o.data
        attr = me.color_attributes[0] if me.color_attributes else None
        if attr is None:
            continue
        # colour lives on corners; collapse to a per-vertex value
        rg = {}
        for poly in me.polygons:
            for li in poly.loop_indices:
                vi = me.loops[li].vertex_index
                c = attr.data[li].color
                rg[vi] = (c[0], c[1])
        M = o.matrix_world
        Minv = M.inverted()
        for vi, v in enumerate(me.vertices):
            r, g = rg.get(vi, (0.0, 0.0))
            world = M @ v.co
            d = foliage_wind(world, r, g, t)
            v.co = Minv @ (world + Vector(d))
        me.update()


def mask_material():
    """Flat readout of vertex colour R -- the sway mask -- as blue(0) to
    red(1).  Anything not blue is something the wind moves."""
    m = bpy.data.materials.get("MP_SwayMask")
    if m:
        return m
    m = bpy.data.materials.new("MP_SwayMask")
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    attr = nt.nodes.new("ShaderNodeAttribute")
    attr.attribute_name = "Col"
    sep = nt.nodes.new("ShaderNodeSeparateColor")
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.interpolation = 'LINEAR'
    ramp.color_ramp.elements[0].position = 0.0
    ramp.color_ramp.elements[0].color = (0.05, 0.12, 0.55, 1)
    ramp.color_ramp.elements[1].position = 1.0
    ramp.color_ramp.elements[1].color = (1.0, 0.15, 0.05, 1)
    e = ramp.color_ramp.elements.new(0.5)
    e.color = (0.95, 0.92, 0.30, 1)
    emit = nt.nodes.new("ShaderNodeEmission")
    nt.links.new(attr.outputs["Color"], sep.inputs["Color"])
    nt.links.new(sep.outputs["Red"], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], emit.inputs["Color"])
    nt.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    return m


def strip_leaves(objs):
    """Delete every face whose UVs land outside the bark row of the atlas, so
    what is left is exactly the wood."""
    for o in objs:
        if o.type != 'MESH':
            continue
        me = o.data
        uv = me.uv_layers[0].data
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.faces.ensure_lookup_table()
        kill = []
        for i, poly in enumerate(me.polygons):
            vs = [uv[li].uv[1] for li in poly.loop_indices]
            if sum(vs) / len(vs) >= 0.25:
                kill.append(bm.faces[i])
        bmesh.ops.delete(bm, geom=kill, context='FACES')
        bm.to_mesh(me)
        bm.free()
        me.update()


def shot_mask(name, tag):
    E.reset_scene()
    base_render()
    sky(strength=0.0, rgb=(0.10, 0.10, 0.12))
    objs = load(name)
    for o in objs:
        if o.type == 'MESH':
            o.data.materials.clear()
            o.data.materials.append(mask_material())
    h = height_of(objs)
    game_camera((0, 0, h * 0.55), 7.0)
    E.render_to(os.path.join(OUTDIR, "%s_maskR.png" % tag))


def shot_wind_bark(name, tag, times=(0.0, 2.35)):
    """Wood only, two times.  If the trunk moves, this is where it shows."""
    for i, t in enumerate(times):
        E.reset_scene()
        base_render()
        sky()
        sun(3.2, 52, 30)
        objs = load(name)
        apply_wind(objs, t)
        strip_leaves(objs)
        h = 3.3
        game_camera((0, 0, h * 0.55), 7.0)
        E.render_to(os.path.join(OUTDIR, "%s_barkwind_t%d.png" % (tag, i)))


def shot_wind(name, tag, times=(0.0, 2.35)):
    """Same tree, same camera, two times.  Anything that moves between the two
    frames is something the wind is moving."""
    for i, t in enumerate(times):
        E.reset_scene()
        base_render()
        sky()
        sun(3.2, 52, 30)
        sun(0.9, 24, 210, colour=(0.62, 0.76, 1.0))
        objs = load(name)
        h = height_of(objs)
        apply_wind(objs, t)
        game_camera((0, 0, h * 0.55), 7.0)
        E.render_to(os.path.join(OUTDIR, "%s_%s_wind_t%d.png" % (name, tag, i)))


def load(name):
    # an absolute path is taken as-is, so an FBX pulled out of git history can
    # be rendered side by side with the current one
    path = name if os.path.isabs(name) else \
        os.path.join(E.FAMILY_DIR[FAM], name + ".fbx")
    objs = import_fbx(path)
    apply_preview(objs, FAM)
    for o in objs:
        o.location = (0, 0, 0)
    return objs


def height_of(objs):
    lo, hi = E.obj_bounds(objs)
    return hi.z - lo.z


def shot_distances(name, tag):
    for d in (5.0, 12.0, 25.0):
        E.reset_scene()
        base_render()
        sky()
        sun(3.2, 52, 30)
        sun(0.9, 24, 210, colour=(0.62, 0.76, 1.0))   # sky fill
        objs = load(name)
        h = height_of(objs)
        game_camera((0, 0, h * 0.55), d)
        E.render_to(os.path.join(OUTDIR, "%s_%s_%02dm.png" % (name, tag, int(d))))


def shot_backlit(name, tag):
    E.reset_scene()
    base_render()
    sky(strength=0.55, rgb=(0.52, 0.62, 0.80))
    # sun low and behind the tree from the camera's point of view: the camera
    # sits at yaw 45, so the sun goes at yaw 45 + 180
    sun(4.4, 18, YAW + 180.0, colour=(1.0, 0.92, 0.74), angle=1.0)
    objs = load(name)
    h = height_of(objs)
    game_camera((0, 0, h * 0.58), 9.0)
    E.render_to(os.path.join(OUTDIR, "%s_%s_backlit.png" % (name, tag)))


def shot_grove(names, tag, label="grove"):
    E.reset_scene()
    base_render(res=(1600, 1000))
    sky()
    sun(3.2, 52, 30)
    sun(0.9, 24, 210, colour=(0.62, 0.76, 1.0))
    g = E.ground_plane(size=200, rgb=(0.34, 0.44, 0.26))
    # Laid out across the camera's line of sight rather than scattered on a
    # guessed grid: at yaw 45 the screen-horizontal axis is (1,-1)/sqrt(2), so
    # stepping along it puts every tree in frame side by side, which is the
    # only arrangement that actually tests whether the canopies merge.
    ax = (0.7071, -0.7071)
    dep = (-0.7071, -0.7071)
    spots = []
    for i, s in enumerate((-8.4, -2.8, 2.8, 8.4, 14.0)):
        d = (1.6 if i % 2 else -1.6)
        spots.append((ax[0] * s + dep[0] * d, ax[1] * s + dep[1] * d))
    hs = []
    for i, n in enumerate(names):
        objs = load(n)
        x, y = spots[i % len(spots)]
        for o in objs:
            o.location = (x, y, 0)
            # ADD the yaw; the FBX import leaves its own axis-conversion
            # rotation on the object, and overwriting it lays the tree on its
            # back -- which is exactly what the first grove render showed.
            o.rotation_euler.z += (i * 1.9) % 6.28
        hs.append(height_of(objs))
    game_camera((0, 0, max(hs) * 0.42), 24.0)
    E.render_to(os.path.join(OUTDIR, "%s_%s.png" % (label, tag)))


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    tag = "x"
    names = []
    mode = "all"
    i = 0
    while i < len(argv):
        if argv[i] == "--tag":
            tag = argv[i + 1]
            i += 2
        elif argv[i] == "--mode":
            mode = argv[i + 1]
            i += 2
        else:
            names.append(argv[i])
            i += 1
    os.makedirs(OUTDIR, exist_ok=True)
    if mode in ("all", "solo"):
        for n in names:
            shot_distances(n, tag)
            shot_backlit(n, tag)
    if mode == "wind":
        for n in names:
            shot_wind(n, tag)
            shot_wind_bark(n, tag)
            shot_mask(n, tag)
    if mode in ("all", "grove"):
        shot_grove(names[:4] if len(names) >= 4 else names, tag)


if __name__ == "__main__":
    main()
