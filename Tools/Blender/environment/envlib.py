"""
envlib -- shared authoring library for the Poke Lab stylised environment kit.

Conventions enforced here (see Docs/CONTRACTS.md, "Art conventions"):
  * 1 Blender unit = 1 metre.
  * Models face +Z, Y up, pivot at the base (or at the snapping corner for modules).
  * FBX export uses axis_forward='-Y', axis_up='Z'.
  * One UV set, non-overlapping, inside 0-1, packed into a per-family atlas cell.
  * Wind vertex colours: R = sway mask (0 at trunk base -> 1 at leaf tips),
    G = per-cluster phase offset, B = secondary/flutter mask, A = 1.
    Stored as FLOAT_COLOR corner attribute named "Col" and exported with
    colors_type='LINEAR' so the values survive the round trip un-gamma'd.

Everything is deterministic: every generator seeds its own random.Random.
"""

import os
import sys
import math
import json
import random
import struct
import zlib

import bpy
import bmesh
from mathutils import Vector, Matrix, Euler
from mathutils import noise as mnoise

# --------------------------------------------------------------------------
# paths
# --------------------------------------------------------------------------

THIS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(THIS_DIR, "..", "..", ".."))
ART_ENV = os.path.join(REPO, "Assets", "Game", "Art", "Environment")
ART_PROPS = os.path.join(REPO, "Assets", "Game", "Art", "Props")
PREVIEWS = os.path.join(THIS_DIR, "previews")

FAMILY_DIR = {
    "Foliage": os.path.join(ART_ENV, "Foliage"),
    "Terrain": os.path.join(ART_ENV, "Terrain"),
    "Town": os.path.join(ART_ENV, "Town"),
    "Characters": os.path.join(ART_ENV, "Characters"),
    "Props": ART_PROPS,
}


def ensure_dirs():
    for d in list(FAMILY_DIR.values()) + [PREVIEWS, ART_ENV, ART_PROPS]:
        os.makedirs(d, exist_ok=True)


# --------------------------------------------------------------------------
# tiny PNG writer (avoids Blender colour management round trips entirely)
# --------------------------------------------------------------------------

def write_png(path, rgb):
    """rgb: numpy uint8 array HxWx3 (top row first)."""
    h, w, _ = rgb.shape
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw.extend(rgb[y].tobytes())
    comp = zlib.compress(bytes(raw), 9)

    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    hdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    blob = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", hdr) +
            chunk(b"IDAT", comp) + chunk(b"IEND", b""))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(blob)
    return path


# --------------------------------------------------------------------------
# scene management
# --------------------------------------------------------------------------

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    sc = bpy.context.scene
    sc.unit_settings.system = 'METRIC'
    sc.unit_settings.scale_length = 1.0
    return sc


def deselect_all():
    for o in bpy.context.scene.objects:
        o.select_set(False)


def activate(obj):
    deselect_all()
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def delete_obj(obj):
    data = obj.data
    bpy.data.objects.remove(obj, do_unlink=True)
    if isinstance(data, bpy.types.Mesh) and data.users == 0:
        bpy.data.meshes.remove(data)


# --------------------------------------------------------------------------
# noise helpers -- deterministic fbm
# --------------------------------------------------------------------------

def fbm(p, octaves=4, lac=2.0, gain=0.5, seed=0.0):
    v = Vector(p) + Vector((seed * 13.17, seed * 7.31, seed * 3.77))
    total = 0.0
    amp = 1.0
    freq = 1.0
    norm = 0.0
    for _ in range(octaves):
        total += mnoise.noise(v * freq) * amp
        norm += amp
        amp *= gain
        freq *= lac
    return total / max(norm, 1e-6)


def ridged(p, octaves=4, seed=0.0):
    return 1.0 - abs(fbm(p, octaves, seed=seed))


def smoothstep(a, b, x):
    if b - a < 1e-9:
        return 0.0
    t = max(0.0, min(1.0, (x - a) / (b - a)))
    return t * t * (3.0 - 2.0 * t)


# --------------------------------------------------------------------------
# mesh construction helpers
# --------------------------------------------------------------------------

def bm_new():
    return bmesh.new()


def bm_to_obj(bm, name, mat_slots=None):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    if mat_slots:
        for m in mat_slots:
            obj.data.materials.append(m)
    return obj


def obj_bm(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    return bm


def bm_write(bm, obj):
    bm.to_mesh(obj.data)
    obj.data.update()
    bm.free()


def join(objs, name):
    """Join a list of objects into the first, rename, return it."""
    objs = [o for o in objs if o is not None]
    if not objs:
        return None
    if len(objs) == 1:
        objs[0].name = name
        objs[0].data.name = name
        return objs[0]
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    res = bpy.context.view_layer.objects.active
    res.name = name
    res.data.name = name
    return res


def set_face_mat(bm, faces, index):
    for f in faces:
        f.material_index = index


# ---- primitive builders working straight in bmesh -------------------------

def bm_tube(bm, sections, mat=0, cap_start=False, cap_end=False, smooth=True):
    """sections: list of (centre Vector, radius, axis Vector, sides, twist).
    Builds a swept tube; returns (all_faces, start_ring, end_ring)."""
    rings = []
    for (c, r, axis, sides, twist) in sections:
        axis = Vector(axis).normalized()
        ref = Vector((0, 0, 1)) if abs(axis.z) < 0.9 else Vector((1, 0, 0))
        u = axis.cross(ref).normalized()
        v = axis.cross(u).normalized()
        ring = []
        for i in range(sides):
            a = twist + 2.0 * math.pi * i / sides
            p = c + (u * math.cos(a) + v * math.sin(a)) * r
            ring.append(bm.verts.new(p))
        rings.append(ring)
    bm.verts.ensure_lookup_table()
    faces = []
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        n = min(len(a), len(b))
        for j in range(n):
            k = (j + 1) % n
            try:
                f = bm.faces.new((a[j], a[k], b[k], b[j]))
                f.smooth = smooth
                f.material_index = mat
                faces.append(f)
            except ValueError:
                pass
    if cap_start and len(rings[0]) > 2:
        try:
            f = bm.faces.new(list(reversed(rings[0])))
            f.material_index = mat
            f.smooth = smooth
            faces.append(f)
        except ValueError:
            pass
    if cap_end and len(rings[-1]) > 2:
        try:
            f = bm.faces.new(rings[-1])
            f.material_index = mat
            f.smooth = smooth
            faces.append(f)
        except ValueError:
            pass
    return faces, rings[0], rings[-1]


def bm_quad(bm, p0, p1, p2, p3, mat=0, smooth=True):
    vs = [bm.verts.new(Vector(p)) for p in (p0, p1, p2, p3)]
    f = bm.faces.new(vs)
    f.material_index = mat
    f.smooth = smooth
    return f


def bm_box(bm, centre, size, mat=0, smooth=False):
    cx, cy, cz = centre
    sx, sy, sz = (size[0] / 2.0, size[1] / 2.0, size[2] / 2.0)
    verts = []
    for dz in (-1, 1):
        for dy in (-1, 1):
            for dx in (-1, 1):
                verts.append(bm.verts.new((cx + dx * sx, cy + dy * sy, cz + dz * sz)))
    idx = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1),
           (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    faces = []
    for q in idx:
        f = bm.faces.new([verts[i] for i in q])
        f.material_index = mat
        f.smooth = smooth
        faces.append(f)
    return faces


def bm_prism(bm, profile, height, mat=0, smooth=False, origin=(0, 0, 0)):
    """profile: list of (x,y) CCW. Extrudes +Z by height."""
    ox, oy, oz = origin
    bot = [bm.verts.new((ox + x, oy + y, oz)) for (x, y) in profile]
    top = [bm.verts.new((ox + x, oy + y, oz + height)) for (x, y) in profile]
    faces = []
    n = len(profile)
    for i in range(n):
        j = (i + 1) % n
        f = bm.faces.new((bot[i], bot[j], top[j], top[i]))
        f.material_index = mat
        f.smooth = smooth
        faces.append(f)
    f = bm.faces.new(list(reversed(bot)))
    f.material_index = mat
    faces.append(f)
    f = bm.faces.new(top)
    f.material_index = mat
    faces.append(f)
    return faces


def _frames(pts):
    """Parallel-transport frames along a polyline -> list of (tangent,u,v)."""
    n = len(pts)
    tans = []
    for i in range(n):
        if i == 0:
            t = (pts[1] - pts[0])
        elif i == n - 1:
            t = (pts[-1] - pts[-2])
        else:
            t = (pts[i + 1] - pts[i - 1])
        if t.length < 1e-9:
            t = Vector((0, 0, 1))
        tans.append(t.normalized())
    ref = Vector((0, 0, 1)) if abs(tans[0].z) < 0.9 else Vector((1, 0, 0))
    u = (ref - tans[0] * ref.dot(tans[0]))
    if u.length < 1e-6:
        u = Vector((1, 0, 0))
    u.normalize()
    frames = []
    for i, t in enumerate(tans):
        if i > 0:
            prev_t = tans[i - 1]
            axis = prev_t.cross(t)
            if axis.length > 1e-7:
                ang = math.atan2(axis.length, prev_t.dot(t))
                u = (Matrix.Rotation(ang, 3, axis.normalized()) @ u)
            u = (u - t * u.dot(t))
            if u.length < 1e-7:
                u = t.orthogonal()
            u.normalize()
        v = t.cross(u).normalized()
        frames.append((t, u.copy(), v))
    return frames


def bm_polytube(bm, pts, radii, sides=6, mat=0, cap_start=True, cap_end=True,
                smooth=True, shape=None, twist=0.0):
    """Swept tube along a polyline with parallel-transport frames.
    shape: optional list of (cos,sin) multipliers per side for non-circular
    cross sections.  Returns (faces, rings)."""
    pts = [Vector(p) for p in pts]
    frames = _frames(pts)
    rings = []
    for i, (c, r) in enumerate(zip(pts, radii)):
        t, u, v = frames[i]
        ring = []
        if r < 1e-6:
            ring = [bm.verts.new(c)] * sides
            rings.append(ring)
            continue
        for j in range(sides):
            a = twist * i + 2.0 * math.pi * j / sides
            cu, sv = math.cos(a), math.sin(a)
            if shape:
                cu *= shape[j % len(shape)][0]
                sv *= shape[j % len(shape)][1]
            ring.append(bm.verts.new(c + (u * cu + v * sv) * r))
        rings.append(ring)
    faces = []
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for j in range(sides):
            k = (j + 1) % sides
            vs = [a[j], a[k], b[k], b[j]]
            uniq = []
            for x in vs:
                if x not in uniq:
                    uniq.append(x)
            if len(uniq) < 3:
                continue
            try:
                f = bm.faces.new(uniq)
                f.smooth = smooth
                f.material_index = mat
                faces.append(f)
            except ValueError:
                pass
    if cap_start and len(set(rings[0])) >= 3:
        try:
            f = bm.faces.new(list(reversed(rings[0])))
            f.smooth = smooth
            f.material_index = mat
            faces.append(f)
        except ValueError:
            pass
    if cap_end and len(set(rings[-1])) >= 3:
        try:
            f = bm.faces.new(rings[-1])
            f.smooth = smooth
            f.material_index = mat
            faces.append(f)
        except ValueError:
            pass
    return faces, rings


PUFF_PROFILES = {
    3: [(-0.52, 0.58), (0.06, 1.0), (0.62, 0.60)],
    4: [(-0.62, 0.42), (-0.18, 0.86), (0.30, 1.0), (0.72, 0.64)],
}


def bm_puff(bm, centre, radius, rng, mat=0, sides=6, squash=(1.0, 1.0, 0.78),
            lumpy=0.30, rot=0.0, smooth=True, rings_n=4):
    """Low-poly organic blob used for leaf clusters / bush masses / moss.
    36 tris at sides=6/rings_n=3, 48 at rings_n=4."""
    c = Vector(centre)
    profile = PUFF_PROFILES[rings_n]
    rings = []
    for (zz, rr) in profile:
        ring = []
        for j in range(sides):
            a = rot + 2.0 * math.pi * j / sides
            lump = 1.0 + (rng.random() - 0.5) * 2.0 * lumpy
            x = math.cos(a) * rr * radius * squash[0] * lump
            y = math.sin(a) * rr * radius * squash[1] * lump
            z = zz * radius * squash[2] * (1.0 + (rng.random() - 0.5) * lumpy)
            ring.append(bm.verts.new(c + Vector((x, y, z))))
        rings.append(ring)
    bot = bm.verts.new(c + Vector((0, 0, -radius * squash[2] * 0.92)))
    top = bm.verts.new(c + Vector((0, 0, radius * squash[2] * 0.98)))
    faces = []
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for j in range(sides):
            k = (j + 1) % sides
            try:
                f = bm.faces.new((a[j], a[k], b[k], b[j]))
                f.smooth = smooth
                f.material_index = mat
                faces.append(f)
            except ValueError:
                pass
    for j in range(sides):
        k = (j + 1) % sides
        try:
            f = bm.faces.new((rings[0][k], rings[0][j], bot))
            f.smooth = smooth
            f.material_index = mat
            faces.append(f)
        except ValueError:
            pass
        try:
            f = bm.faces.new((rings[-1][j], rings[-1][k], top))
            f.smooth = smooth
            f.material_index = mat
            faces.append(f)
        except ValueError:
            pass
    verts = [v for r in rings for v in r] + [bot, top]
    return faces, verts


def bm_ribbon(bm, pts, widths, mat=0, double=True, up=None, thickness=0.004,
              smooth=True, taper_tip=True):
    """Flat tapered strip (grass blade, frond, leaf strand).  Built double
    sided by adding a mirrored copy offset along the normal, so it survives
    backface culling under any shader."""
    pts = [Vector(p) for p in pts]
    frames = _frames(pts)
    made = []
    allv = []
    for side in ((1, 0), (-1, 1)) if double else ((1, 0),):
        sgn, flip = side
        rows = []
        for i, (c, w) in enumerate(zip(pts, widths)):
            t, u, v = frames[i]
            side_dir = Vector(up).cross(t).normalized() if up else u
            nrm = t.cross(side_dir).normalized()
            off = nrm * thickness * 0.5 * sgn
            if taper_tip and i == len(pts) - 1:
                rows.append([bm.verts.new(c + off)])
            else:
                rows.append([bm.verts.new(c - side_dir * w * 0.5 + off),
                             bm.verts.new(c + side_dir * w * 0.5 + off)])
        for r in rows:
            allv.extend(r)
        for i in range(len(rows) - 1):
            a, b = rows[i], rows[i + 1]
            if len(a) == 2 and len(b) == 2:
                quad = (a[0], a[1], b[1], b[0])
            elif len(a) == 2 and len(b) == 1:
                quad = (a[0], a[1], b[0])
            else:
                continue
            if flip:
                quad = tuple(reversed(quad))
            try:
                f = bm.faces.new(quad)
                f.smooth = smooth
                f.material_index = mat
                made.append(f)
            except ValueError:
                pass
    return made, allv


def recess(bm, faces, thickness=0.06, depth=-0.03, mat_idx=None, even=True):
    """Panel bays, window reveals, door frames, seams -- cut INTO the skin.
    Boxes glued onto a surface always look glued on.  Adapted from the
    hard-surface skill's recess(); returns the recessed faces."""
    sel = [f for f in faces if f.is_valid]
    if not sel:
        return []
    bmesh.ops.inset_region(bm, faces=sel, thickness=thickness, depth=0.0,
                           use_even_offset=even, use_boundary=True)
    for f in sel:
        if not f.is_valid:
            continue
        bmesh.ops.translate(bm, verts=list(f.verts),
                            vec=f.normal.normalized() * depth)
        if mat_idx is not None:
            f.material_index = mat_idx
    return sel


def greeble(bm, faces, rng, count=40, lo=0.15, hi=0.45, depth=0.03,
            mat_idx=None):
    """Fine tier only, and deliberately zoned: greebling every face reads as
    noise.  Detail only registers against something undetailed."""
    cand = [f for f in faces if f.is_valid]
    rng.shuffle(cand)
    out = []
    for f in cand[:count]:
        if not f.is_valid:
            continue
        bmesh.ops.inset_individual(bm, faces=[f],
                                   thickness=rng.uniform(lo, hi) * math.sqrt(
                                       max(f.calc_area(), 1e-8)),
                                   depth=0.0, use_even_offset=True)
        d = rng.choice((depth, -depth, depth * 0.45, -depth * 0.7))
        bmesh.ops.translate(bm, verts=list(f.verts),
                            vec=f.normal.normalized() * d)
        if mat_idx is not None:
            f.material_index = mat_idx
        out.append(f)
    return out


def faces_by(bm, pick):
    return [f for f in bm.faces if pick(f)]


def hide_from_light(obj):
    """Backdrop geometry must be camera-visible only -- an emissive or large
    backdrop that also bounces light costs an order of magnitude in render
    time (145 s -> 4.3 s in the reference project)."""
    obj.visible_diffuse = False
    obj.visible_glossy = False
    obj.visible_transmission = False
    obj.visible_volume_scatter = False
    obj.visible_shadow = False
    return obj


def fit_camera(cam, objs, direction, margin=1.06, ortho=False):
    """Derive the camera from the scene bounding box, never by hand -- a
    hand-placed camera ends up inside geometry and costs 80x the render time."""
    pts = []
    for o in objs:
        if o.type not in ('MESH', 'FONT', 'CURVE'):
            continue
        for c in o.bound_box:
            pts.append(o.matrix_world @ Vector(c))
    if not pts:
        return cam
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts),
                 min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts),
                 max(p.z for p in pts)))
    ctr = (lo + hi) * 0.5
    rad = max((p - ctr).length for p in pts)
    d = Vector(direction).normalized()
    if ortho:
        cam.data.type = 'ORTHO'
        cam.data.ortho_scale = rad * 2.0 * margin
        cam.location = ctr + d * (rad * 4.0 + 10.0)
    else:
        cam.location = ctr + d * (rad / math.tan(cam.data.angle * 0.5) * margin)
    cam.rotation_euler = (ctr - cam.location).to_track_quat('-Z', 'Y').to_euler()
    return cam


def displace_verts(verts, amount, freq, seed, axis_bias=(1, 1, 1)):
    for v in verts:
        p = v.co * freq
        d = Vector((fbm(p, 3, seed=seed),
                    fbm(p, 3, seed=seed + 11.3),
                    fbm(p, 3, seed=seed + 27.7)))
        v.co += Vector((d.x * axis_bias[0], d.y * axis_bias[1],
                        d.z * axis_bias[2])) * amount


def bevel(bm, edges=None, verts=None, width=0.01, segments=2, profile=0.5,
          clamp=True, affect='EDGES'):
    if affect == 'EDGES':
        geom = edges if edges is not None else list(bm.edges)
        if not geom:
            return
        bmesh.ops.bevel(bm, geom=geom, offset=width, segments=segments,
                        profile=profile, affect='EDGES', clamp_overlap=clamp,
                        offset_type='OFFSET', miter_outer='MITER_ARC')
    else:
        geom = verts if verts is not None else list(bm.verts)
        if not geom:
            return
        bmesh.ops.bevel(bm, geom=geom, offset=width, segments=segments,
                        profile=profile, affect='VERTICES', clamp_overlap=clamp)


def bevel_sharp(bm, width=0.012, segments=2, angle_deg=32.0, mat_break=True):
    """Bevel every edge whose face angle exceeds `angle_deg`.  This is the
    'bevel every hard edge' rule -- run it right before finishing a mesh."""
    thr = math.radians(angle_deg)
    edges = []
    for e in bm.edges:
        if len(e.link_faces) != 2:
            continue
        try:
            a = e.calc_face_angle()
        except ValueError:
            continue
        if a > thr:
            edges.append(e)
        elif mat_break and e.link_faces[0].material_index != e.link_faces[1].material_index:
            edges.append(e)
    if edges:
        bevel(bm, edges=edges, width=width, segments=segments, clamp=True)


def cleanup(bm, merge=1e-5, tri_limit=None):
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=merge)
    # kill zero-area faces and loose geometry
    bad = [f for f in bm.faces if f.calc_area() < 1e-9]
    if bad:
        bmesh.ops.delete(bm, geom=bad, context='FACES')
    loose_e = [e for e in bm.edges if not e.link_faces]
    if loose_e:
        bmesh.ops.delete(bm, geom=loose_e, context='EDGES')
    loose_v = [v for v in bm.verts if not v.link_edges]
    if loose_v:
        bmesh.ops.delete(bm, geom=loose_v, context='VERTS')
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))


def finalize(obj, smooth_angle=38.0, merge=1e-5):
    """Recalc normals outward, remove degenerate/loose geo, enable auto smooth."""
    bm = obj_bm(obj)
    cleanup(bm, merge=merge)
    bm_write(bm, obj)
    me = obj.data
    for p in me.polygons:
        p.use_smooth = True
    me.use_auto_smooth = True
    me.auto_smooth_angle = math.radians(smooth_angle)
    return obj


def apply_transforms(obj):
    activate(obj)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def set_pivot(obj, point):
    """Move mesh data so that `point` (in current object space) becomes origin."""
    me = obj.data
    off = Vector(point)
    for v in me.vertices:
        v.co -= off
    me.update()


def pivot_to_base(obj, xy='center'):
    me = obj.data
    if not me.vertices:
        return
    xs = [v.co.x for v in me.vertices]
    ys = [v.co.y for v in me.vertices]
    zs = [v.co.z for v in me.vertices]
    if xy == 'center':
        px = (min(xs) + max(xs)) * 0.5
        py = (min(ys) + max(ys)) * 0.5
    elif xy == 'corner':
        px, py = min(xs), min(ys)
    else:
        px, py = 0.0, 0.0
    set_pivot(obj, (px, py, min(zs)))


# --------------------------------------------------------------------------
# vertex colours
# --------------------------------------------------------------------------

def add_vcol(obj, func, name="Col"):
    """func(co: Vector, loop_index, poly_index) -> (r,g,b,a) in 0..1."""
    me = obj.data
    attr = me.color_attributes.get(name)
    if attr is None:
        attr = me.color_attributes.new(name=name, type='FLOAT_COLOR', domain='CORNER')
    data = attr.data
    for poly in me.polygons:
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            co = me.vertices[vi].co
            data[li].color = func(co, li, poly.index)
    return attr


def wind_vcol_from_height(obj, base_z=None, top_z=None, phase=0.0, power=1.6,
                          flutter=0.0):
    """Simple height-driven sway mask. R=sway, G=phase, B=flutter, A=1."""
    me = obj.data
    zs = [v.co.z for v in me.vertices]
    lo = base_z if base_z is not None else min(zs)
    hi = top_z if top_z is not None else max(zs)
    span = max(hi - lo, 1e-5)

    def f(co, li, pi):
        t = max(0.0, min(1.0, (co.z - lo) / span)) ** power
        return (t, phase, flutter * t, 1.0)
    return add_vcol(obj, f)


class WindPainter:
    """Accumulates per-vertex wind data while a plant is being built, then
    stamps it onto the finished object.  Keyed by rounded world position so it
    survives bmesh joins and remove_doubles."""

    def __init__(self):
        self.pts = []   # (Vector, sway, phase, flutter)

    def mark(self, co, sway, phase, flutter=0.0):
        self.pts.append((Vector(co), sway, phase, flutter))

    def mark_many(self, cos, sway, phase, flutter=0.0):
        for c in cos:
            self.mark(c, sway, phase, flutter)

    def apply(self, obj, fallback_power=1.5):
        me = obj.data
        if not self.pts:
            return wind_vcol_from_height(obj, power=fallback_power)
        # spatial hash for nearest lookup
        cell = 0.12
        grid = {}
        for (p, s, ph, fl) in self.pts:
            k = (int(p.x / cell), int(p.y / cell), int(p.z / cell))
            grid.setdefault(k, []).append((p, s, ph, fl))
        cache = {}

        def lookup(co):
            key = (round(co.x, 3), round(co.y, 3), round(co.z, 3))
            if key in cache:
                return cache[key]
            k = (int(co.x / cell), int(co.y / cell), int(co.z / cell))
            best = None
            bd = 1e9
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        for item in grid.get((k[0] + dx, k[1] + dy, k[2] + dz), ()):
                            d = (item[0] - co).length_squared
                            if d < bd:
                                bd = d
                                best = item
            if best is None:
                for item in self.pts:
                    d = (item[0] - co).length_squared
                    if d < bd:
                        bd = d
                        best = item
            res = (best[1], best[2], best[3])
            cache[key] = res
            return res

        def f(co, li, pi):
            s, ph, fl = lookup(co)
            return (s, ph, fl, 1.0)
        return add_vcol(obj, f)


def has_vcol(obj, name="Col"):
    return obj.data.color_attributes.get(name) is not None


# --------------------------------------------------------------------------
# materials / atlas
# --------------------------------------------------------------------------

ATLAS_GRID = 4  # 4x4 cells


def cell_rect(index, grid=ATLAS_GRID, inset=0.012):
    col = index % grid
    row = index // grid
    s = 1.0 / grid
    x0 = col * s + inset * s
    y0 = row * s + inset * s
    w = s * (1.0 - 2.0 * inset)
    return (x0, y0, w, w)


_MAT_CACHE = {}


def get_material(family, surface_name, cell_index, base_rgb, rough=0.72,
                 atlas_paths=None, alpha_clip=False, emission=0.0):
    key = (family, surface_name)
    if key in _MAT_CACHE and _MAT_CACHE[key].name in bpy.data.materials:
        return _MAT_CACHE[key]
    mat = bpy.data.materials.new("M_%s_%s" % (family, surface_name))
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (base_rgb[0], base_rgb[1], base_rgb[2], 1.0)
    bsdf.inputs["Roughness"].default_value = rough
    if "Specular" in bsdf.inputs:
        bsdf.inputs["Specular"].default_value = 0.22
    if emission > 0.0:
        if "Emission" in bsdf.inputs:
            bsdf.inputs["Emission"].default_value = (base_rgb[0], base_rgb[1], base_rgb[2], 1)
        if "Emission Strength" in bsdf.inputs:
            bsdf.inputs["Emission Strength"].default_value = emission
    if atlas_paths:
        base_png = atlas_paths.get("base")
        nrm_png = atlas_paths.get("normal")
        if base_png and os.path.exists(base_png):
            img = bpy.data.images.get(os.path.basename(base_png))
            if img is None:
                img = bpy.data.images.load(base_png)
            tex = nt.nodes.new("ShaderNodeTexImage")
            tex.image = img
            tex.location = (-420, 220)
            nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        if nrm_png and os.path.exists(nrm_png):
            img2 = bpy.data.images.get(os.path.basename(nrm_png))
            if img2 is None:
                img2 = bpy.data.images.load(nrm_png)
                img2.colorspace_settings.name = 'Non-Color'
            tex2 = nt.nodes.new("ShaderNodeTexImage")
            tex2.image = img2
            tex2.location = (-420, -160)
            nmap = nt.nodes.new("ShaderNodeNormalMap")
            nmap.location = (-180, -160)
            nmap.inputs["Strength"].default_value = 0.8
            nt.links.new(tex2.outputs["Color"], nmap.inputs["Color"])
            nt.links.new(nmap.outputs["Normal"], bsdf.inputs["Normal"])
    mat["atlas_cell"] = cell_index
    mat["family"] = family
    if alpha_clip:
        mat.blend_method = 'CLIP'
        mat.shadow_method = 'CLIP'
    _MAT_CACHE[key] = mat
    return mat


class MatSet:
    """Ordered material slot set for one object family build."""

    def __init__(self, family, atlas_paths=None):
        self.family = family
        self.atlas = atlas_paths
        self.order = []
        self.index = {}

    def add(self, surface_name, cell, rgb, rough=0.72, alpha_clip=False,
            emission=0.0, sub=None):
        """sub=(band_index, band_count) restricts the material to a horizontal
        band inside its atlas cell -- lets several materials share one cell
        (e.g. the four flower colours in the petal swatch)."""
        if surface_name in self.index:
            return self.index[surface_name]
        m = get_material(self.family, surface_name, cell, rgb, rough,
                         self.atlas, alpha_clip, emission)
        x0, y0, w, h = cell_rect(cell)
        if sub:
            bi, bn = sub
            # atlas rows run bottom-up in UV space; band 0 is the top image band
            h = h / float(bn)
            y0 = y0 + (bn - 1 - bi) * h
        m["atlas_rect"] = [x0, y0, w, h]
        self.index[surface_name] = len(self.order)
        self.order.append(m)
        return self.index[surface_name]

    def slot(self, surface_name):
        return self.index[surface_name]

    def materials(self):
        return list(self.order)

    def cell_of_slot(self, slot):
        return self.order[slot].get("atlas_cell", 0)

    def rect_of_slot(self, slot):
        if slot < len(self.order):
            r = self.order[slot].get("atlas_rect")
            if r is not None:
                return tuple(r)
            return cell_rect(self.order[slot].get("atlas_cell", 0))
        return cell_rect(0)


# --------------------------------------------------------------------------
# UVs
# --------------------------------------------------------------------------

def uv_smart(obj, angle=64.0, margin=0.02):
    activate(obj)
    if not obj.data.uv_layers:
        obj.data.uv_layers.new(name="UVMap")
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    try:
        bpy.ops.uv.smart_project(angle_limit=math.radians(angle),
                                 island_margin=margin,
                                 correct_aspect=True, scale_to_bounds=False)
    except Exception as exc:
        print("  ! smart_project failed (%s), falling back to cube project" % exc)
        bpy.ops.uv.cube_project(cube_size=1.0)
    bpy.ops.object.mode_set(mode='OBJECT')


def uv_pack_into_cells(obj, matset, grid=ATLAS_GRID):
    """Take the globally packed 0-1 UVs and squeeze each material's islands into
    that material's atlas cell.  Islands stay non-overlapping because different
    materials land in disjoint cells and same-material islands share one affine
    transform of an already non-overlapping layout."""
    me = obj.data
    uvl = me.uv_layers.active
    if uvl is None:
        return
    data = uvl.data
    # bbox per material
    boxes = {}
    for poly in me.polygons:
        mi = poly.material_index
        b = boxes.setdefault(mi, [9e9, 9e9, -9e9, -9e9])
        for li in poly.loop_indices:
            u, v = data[li].uv
            b[0] = min(b[0], u)
            b[1] = min(b[1], v)
            b[2] = max(b[2], u)
            b[3] = max(b[3], v)
    for poly in me.polygons:
        mi = poly.material_index
        b = boxes[mi]
        bw = max(b[2] - b[0], 1e-6)
        bh = max(b[3] - b[1], 1e-6)
        x0, y0, w, h = matset.rect_of_slot(mi)
        # preserve aspect inside the cell so texel density stays sane
        sc = min(w / bw, h / bh)
        ox = x0 + (w - bw * sc) * 0.5
        oy = y0 + (h - bh * sc) * 0.5
        for li in poly.loop_indices:
            u, v = data[li].uv
            data[li].uv = (ox + (u - b[0]) * sc, oy + (v - b[1]) * sc)


def uv_all(obj, matset, angle=64.0, margin=0.02):
    uv_smart(obj, angle, margin)
    uv_pack_into_cells(obj, matset)


# --------------------------------------------------------------------------
# LODs
# --------------------------------------------------------------------------

def tri_count(obj):
    n = 0
    for p in obj.data.polygons:
        n += max(0, len(p.vertices) - 2)
    return n


def make_lod(obj, ratio, suffix):
    new = obj.copy()
    new.data = obj.data.copy()
    new.name = obj.name + suffix
    new.data.name = new.name
    bpy.context.scene.collection.objects.link(new)
    activate(new)
    m = new.modifiers.new("dec", 'DECIMATE')
    m.decimate_type = 'COLLAPSE'
    m.ratio = ratio
    m.use_collapse_triangulate = False
    bpy.ops.object.modifier_apply(modifier=m.name)
    new.data.use_auto_smooth = True
    new.data.auto_smooth_angle = obj.data.auto_smooth_angle
    return new


def make_lods(obj, ratios=(0.40, 0.15)):
    out = []
    for i, r in enumerate(ratios):
        out.append(make_lod(obj, r, "_LOD%d" % (i + 1)))
    return out


# --------------------------------------------------------------------------
# validation
# --------------------------------------------------------------------------

class ValidationError(Exception):
    pass


def validate(obj, budget=None, need_vcol=False, need_uv=True, strict=True):
    problems = []
    me = obj.data

    if tuple(round(x, 5) for x in obj.location) != (0.0, 0.0, 0.0):
        problems.append("location not applied: %s" % (tuple(obj.location),))
    if tuple(round(x, 5) for x in obj.scale) != (1.0, 1.0, 1.0):
        problems.append("scale not applied: %s" % (tuple(obj.scale),))
    if any(abs(a) > 1e-5 for a in obj.rotation_euler):
        problems.append("rotation not applied")

    tris = tri_count(obj)
    if budget:
        lo, hi = budget
        if tris > hi:
            problems.append("tri count %d over budget %d" % (tris, hi))
        elif tris < lo:
            problems.append("tri count %d under budget floor %d" % (tris, lo))

    bm = obj_bm(obj)
    loose_v = sum(1 for v in bm.verts if not v.link_edges)
    loose_e = sum(1 for e in bm.edges if not e.link_faces)
    zero = sum(1 for f in bm.faces if f.calc_area() < 1e-9)
    bm.free()
    if loose_v:
        problems.append("%d loose verts" % loose_v)
    if loose_e:
        problems.append("%d loose edges" % loose_e)
    if zero:
        problems.append("%d zero-area faces" % zero)

    if need_uv:
        if not me.uv_layers:
            problems.append("no UV layer")
        else:
            if len(me.uv_layers) > 1:
                problems.append("%d UV layers (want 1)" % len(me.uv_layers))
            d = me.uv_layers[0].data
            out = 0
            for i in range(len(d)):
                u, v = d[i].uv
                if u < -0.001 or u > 1.001 or v < -0.001 or v > 1.001:
                    out += 1
            if out:
                problems.append("%d UV loops outside 0-1" % out)

    if need_vcol and not has_vcol(obj):
        problems.append("missing wind vertex colours")

    if problems and strict:
        raise ValidationError("%s: %s" % (obj.name, "; ".join(problems)))
    return tris, problems


# --------------------------------------------------------------------------
# export
# --------------------------------------------------------------------------

def export_fbx(objs, path, apply_modifiers=True, armature=None, anim=False,
               anim_actions=False):
    objs = [o for o in objs if o]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    deselect_all()
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    kwargs = dict(
        filepath=path,
        use_selection=True,
        apply_unit_scale=True,
        global_scale=1.0,
        apply_scale_options='FBX_SCALE_NONE',
        axis_forward='-Y',
        axis_up='Z',
        object_types={'MESH', 'ARMATURE', 'EMPTY'},
        use_mesh_modifiers=apply_modifiers,
        mesh_smooth_type='FACE',
        use_triangles=True,      # n-gon caps otherwise block tangent export
        use_tspace=True,
        colors_type='LINEAR',
        add_leaf_bones=False,
        primary_bone_axis='Y',
        secondary_bone_axis='X',
        bake_anim=anim,
        bake_anim_use_all_bones=anim,
        bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=anim_actions,
        bake_anim_force_startend_keying=True,
        bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        # STRIP, not COPY: COPY writes a whole atlas pair into a <name>.fbm
        # folder beside every single FBX.  The integrator assigns the shared
        # atlas material in Unity anyway.
        path_mode='STRIP',
        embed_textures=False,
    )
    bpy.ops.export_scene.fbx(**kwargs)
    return path


# --------------------------------------------------------------------------
# manifest
# --------------------------------------------------------------------------

class Manifest:
    def __init__(self):
        self.entries = []

    def add(self, name, family, path, tris, lods=None, pivot="base-centre",
            textures=None, wind=False, notes="", extra=None):
        rel = os.path.relpath(path, REPO).replace("\\", "/")
        e = {
            "name": name,
            "family": family,
            "path": rel,
            "triangles": tris,
            "lods": [
                {"level": i + 1,
                 "path": os.path.relpath(p, REPO).replace("\\", "/"),
                 "triangles": t}
                for i, (p, t) in enumerate(lods or [])
            ],
            "pivot": pivot,
            "textures": [os.path.relpath(t, REPO).replace("\\", "/")
                         for t in (textures or [])],
            "windVertexColors": bool(wind),
            "notes": notes,
        }
        if extra:
            e.update(extra)
        self.entries.append(e)
        return e

    def merge_file(self, path):
        if os.path.exists(path):
            try:
                with open(path, "r", encoding="utf-8") as f:
                    old = json.load(f)
                return old
            except Exception:
                return None
        return None

    def write(self, path, family=None):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(self.entries, f, indent=2)
        return path


def part_manifest_path(family):
    return os.path.join(THIS_DIR, "_manifest_parts", "%s.json" % family)


def write_part(family, entries):
    p = part_manifest_path(family)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8") as f:
        json.dump(entries, f, indent=2)
    return p


# --------------------------------------------------------------------------
# rendering
# --------------------------------------------------------------------------

def setup_render(res=(1600, 1000), samples=48, world_rgb=(0.62, 0.72, 0.85),
                 strength=1.0, transparent=False):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_EEVEE'
    sc.eevee.taa_render_samples = samples
    sc.eevee.use_gtao = True
    sc.eevee.gtao_distance = 0.6
    sc.eevee.use_soft_shadows = True
    sc.eevee.shadow_cube_size = '2048'
    sc.eevee.shadow_cascade_size = '2048'
    sc.eevee.use_bloom = True
    sc.eevee.bloom_intensity = 0.02
    sc.render.resolution_x, sc.render.resolution_y = res
    sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = 'PNG'
    sc.render.film_transparent = transparent
    sc.view_settings.view_transform = 'Standard'
    sc.view_settings.look = 'None'
    sc.view_settings.exposure = 0.0
    sc.view_settings.gamma = 1.0

    world = bpy.data.worlds.get("World")
    if world is None:
        world = bpy.data.worlds.new("World")
    sc.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (world_rgb[0], world_rgb[1], world_rgb[2], 1)
    bg.inputs[1].default_value = strength
    return sc


def add_studio_lights(key_energy=3.1, sun_angle=(math.radians(54), 0, math.radians(34)),
                      scale=1.0):
    """Warm key sun, cool sky fill, warm rim.  Energies are tuned for the
    'Standard' view transform -- raising them washes the atlas colours out."""
    sun = bpy.data.lights.new("Sun", 'SUN')
    sun.energy = key_energy
    sun.angle = math.radians(2.4)
    sun.color = (1.0, 0.95, 0.86)
    so = bpy.data.objects.new("Sun", sun)
    so.rotation_euler = Euler(sun_angle)
    bpy.context.scene.collection.objects.link(so)

    fill = bpy.data.lights.new("Fill", 'AREA')
    fill.energy = 90.0 * scale * scale
    fill.size = 9.0 * scale
    fill.color = (0.66, 0.78, 1.0)
    fo = bpy.data.objects.new("Fill", fill)
    fo.location = (-8 * scale, -9 * scale, 6 * scale)
    fo.rotation_euler = Euler((math.radians(62), 0, math.radians(-42)))
    bpy.context.scene.collection.objects.link(fo)

    rim = bpy.data.lights.new("Rim", 'AREA')
    rim.energy = 120.0 * scale * scale
    rim.size = 6.0 * scale
    rim.color = (1.0, 0.84, 0.60)
    ro = bpy.data.objects.new("Rim", rim)
    ro.location = (7 * scale, 10 * scale, 7 * scale)
    ro.rotation_euler = Euler((math.radians(115), 0, math.radians(150)))
    bpy.context.scene.collection.objects.link(ro)
    return so, fo, ro


def add_camera(loc, look_at, lens=50.0, ortho_scale=None):
    cam = bpy.data.cameras.new("Cam")
    cam.lens = lens
    if ortho_scale:
        cam.type = 'ORTHO'
        cam.ortho_scale = ortho_scale
    co = bpy.data.objects.new("Cam", cam)
    co.location = Vector(loc)
    bpy.context.scene.collection.objects.link(co)
    d = Vector(look_at) - Vector(loc)
    co.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
    bpy.context.scene.camera = co
    return co


def ground_plane(size=200.0, rgb=(0.30, 0.42, 0.20)):
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=size / 2.0)
    obj = bm_to_obj(bm, "Ground")
    mat = bpy.data.materials.new("M_Ground")
    mat.use_nodes = True
    b = mat.node_tree.nodes.get("Principled BSDF")
    b.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1)
    b.inputs["Roughness"].default_value = 0.95
    obj.data.materials.append(mat)
    return obj


def text_label(txt, loc, size=0.12, rgb=(0.06, 0.06, 0.08)):
    cu = bpy.data.curves.new(txt, 'FONT')
    cu.body = txt
    cu.align_x = 'CENTER'
    cu.size = size
    o = bpy.data.objects.new("T_" + txt, cu)
    o.location = Vector(loc)
    o.rotation_euler = Euler((math.radians(90), 0, 0))
    bpy.context.scene.collection.objects.link(o)
    mat = bpy.data.materials.get("M_Label")
    if mat is None:
        mat = bpy.data.materials.new("M_Label")
        mat.use_nodes = True
        b = mat.node_tree.nodes.get("Principled BSDF")
        b.inputs["Base Color"].default_value = (rgb[0], rgb[1], rgb[2], 1)
        b.inputs["Roughness"].default_value = 1.0
    o.data.materials.append(mat)
    return o


def render_to(path):
    path = os.path.abspath(path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    return path


def obj_bounds(objs):
    lo = Vector((9e9, 9e9, 9e9))
    hi = Vector((-9e9, -9e9, -9e9))
    for o in objs:
        for c in o.bound_box:
            w = o.matrix_world @ Vector(c)
            for i in range(3):
                lo[i] = min(lo[i], w[i])
                hi[i] = max(hi[i], w[i])
    return lo, hi


def log(msg):
    print("[env] %s" % msg)
    sys.stdout.flush()
