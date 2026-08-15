"""
Poke Lab creature framework - core layer.

Shared modelling / shading / validation / export utilities used by every creature
script under Tools/Blender/creatures/.

Design rules encoded here (see Docs/CONTRACTS.md "Art conventions"):
  * 1 Blender unit = 1 metre, creatures authored at true size.
  * Creatures face Blender +Y, feet at z = 0, exported with -Y forward / Z up so
    they arrive in Unity facing +Z with Y up.
  * Every rig carries Anchor_Head / Anchor_Body / Anchor_Muzzle empties.
  * Quad-dominant organic bodies: skin-modifier limb skeletons + spherified-cube
    heads + lathed hard-surface parts, all smoothed with subsurf and finished with
    an angle-limited bevel + hardened normals. No unioned primitives.

Nothing in this module knows about a specific creature.
"""

import bmesh
import bpy
import json
import math
import os
import random
from mathutils import Vector, Matrix, Euler

TAU = math.pi * 2.0

# ----------------------------------------------------------------------------
# paths
# ----------------------------------------------------------------------------

THIS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(THIS_DIR, "..", ".."))
ART_DIR = os.path.join(REPO_ROOT, "Assets", "Game", "Art", "Creatures")
PREVIEW_DIR = os.path.join(THIS_DIR, "previews")


def ensure_dirs():
    for d in (ART_DIR, PREVIEW_DIR):
        os.makedirs(d, exist_ok=True)


# ----------------------------------------------------------------------------
# scene / object plumbing
# ----------------------------------------------------------------------------

def reset_scene():
    """Wipe everything so a build is deterministic no matter what ran before."""
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete(use_global=True, confirm=False)
    for coll in (bpy.data.meshes, bpy.data.armatures, bpy.data.objects,
                 bpy.data.materials, bpy.data.actions, bpy.data.images,
                 bpy.data.lattices, bpy.data.curves, bpy.data.node_groups):
        for item in list(coll):
            try:
                coll.remove(item)
            except Exception:
                pass
    scn = bpy.context.scene
    scn.unit_settings.system = 'METRIC'
    scn.unit_settings.scale_length = 1.0
    scn.render.fps = 30
    random.seed(20260815)


def activate(obj, deselect_others=True):
    if deselect_others:
        for o in bpy.context.view_layer.objects:
            o.select_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    return obj


def link(obj):
    bpy.context.collection.objects.link(obj)
    return obj


def new_mesh_object(name, verts=None, edges=None, faces=None):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts or [], edges or [], faces or [])
    me.update()
    obj = bpy.data.objects.new(name, me)
    link(obj)
    return obj


def apply_modifier(obj, mod_name):
    activate(obj)
    bpy.ops.object.modifier_apply(modifier=mod_name)


def apply_all_modifiers(obj):
    activate(obj)
    for m in list(obj.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except Exception as exc:  # pragma: no cover - diagnostic path
            print("  ! modifier_apply failed on %s.%s: %s" % (obj.name, m.name, exc))
            obj.modifiers.remove(m)


def apply_transforms(obj):
    activate(obj)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def join_objects(objs, name):
    """Join a list of mesh objects; first one wins the name."""
    objs = [o for o in objs if o is not None]
    for o in objs:
        apply_transforms(o)
    main = objs[0]
    activate(main, deselect_others=True)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = main
    if len(objs) > 1:
        bpy.ops.object.join()
    main.name = name
    main.data.name = name
    return main


def tri_count(obj):
    me = obj.data
    return sum(len(p.vertices) - 2 for p in me.polygons)


def quad_ratio(obj):
    polys = obj.data.polygons
    if not polys:
        return 0.0
    quads = sum(1 for p in polys if len(p.vertices) == 4)
    return quads / float(len(polys))


# ----------------------------------------------------------------------------
# primitive builders (all quad-dominant, none of them shipped raw)
# ----------------------------------------------------------------------------

def spherified_cube(name, cuts=3, radius=1.0):
    """A cube subdivided and pushed onto a sphere.

    Gives an all-quad sphere with even topology and no poles - the right base for
    a head you are going to sculpt a snout and a brow into.
    """
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=2.0)
    if cuts > 0:
        bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=cuts, use_grid_fill=True)
    for v in bm.verts:
        # cube -> sphere mapping that keeps the grid even (Philip Rideout's formula)
        x, y, z = v.co
        x2, y2, z2 = x * x, y * y, z * z
        v.co = Vector((
            x * math.sqrt(max(0.0, 1.0 - y2 / 2.0 - z2 / 2.0 + y2 * z2 / 3.0)),
            y * math.sqrt(max(0.0, 1.0 - z2 / 2.0 - x2 / 2.0 + z2 * x2 / 3.0)),
            z * math.sqrt(max(0.0, 1.0 - x2 / 2.0 - y2 / 2.0 + x2 * y2 / 3.0)),
        )) * radius
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.update()
    obj = bpy.data.objects.new(name, me)
    link(obj)
    return obj


def uv_sphere(name, radius=1.0, segments=16, rings=10):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segments, v_segments=rings,
                              radius=radius)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    link(obj)
    return obj


def lathe(name, profile, segments=16, axis='Z', close_bottom=True, close_top=True):
    """Revolve a 2D profile [(radius, height), ...] into an all-quad solid.

    This is how the shells, horns, gems and bulbs get made - a considered profile
    curve rather than a scaled primitive.
    """
    verts = []
    faces = []
    n = len(profile)
    ring_index = []
    for r, h in profile:
        if abs(r) < 1e-6:
            ring_index.append(("point", len(verts)))
            verts.append((0.0, 0.0, h))
        else:
            ring_index.append(("ring", len(verts)))
            for s in range(segments):
                a = TAU * s / segments
                verts.append((math.cos(a) * r, math.sin(a) * r, h))
    for i in range(n - 1):
        kind_a, base_a = ring_index[i]
        kind_b, base_b = ring_index[i + 1]
        if kind_a == "ring" and kind_b == "ring":
            for s in range(segments):
                s2 = (s + 1) % segments
                faces.append((base_a + s, base_a + s2, base_b + s2, base_b + s))
        elif kind_a == "point" and kind_b == "ring":
            for s in range(segments):
                s2 = (s + 1) % segments
                faces.append((base_a, base_b + s2, base_b + s))
        elif kind_a == "ring" and kind_b == "point":
            for s in range(segments):
                s2 = (s + 1) % segments
                faces.append((base_a + s, base_a + s2, base_b))
    # caps as triangle fans around a centre vertex - never an n-gon
    if close_bottom and ring_index[0][0] == "ring":
        c = len(verts)
        verts.append((0.0, 0.0, profile[0][1]))
        for s in range(segments):
            faces.append((c, (s + 1) % segments, s))
    if close_top and ring_index[-1][0] == "ring":
        base = ring_index[-1][1]
        c = len(verts)
        verts.append((0.0, 0.0, profile[-1][1]))
        for s in range(segments):
            faces.append((c, base + s, base + (s + 1) % segments))
    obj = new_mesh_object(name, verts, [], faces)
    if axis == 'Y':
        rotate_obj(obj, (math.radians(90), 0, 0))
    elif axis == 'X':
        rotate_obj(obj, (0, math.radians(90), 0))
    return obj


def lobed_loft(name, stations, lobes=5, lobe_depth=0.14, phase=0.0, segments=None,
               **kwargs):
    """A loft whose rings are scalloped by a cosine, so the form is grooved into
    one continuous skin.

    This is how a bulb of overlapping leaf lobes, a ridged shell or a segmented
    body gets made: the grooves are the geometry, not separate flaps stuck on.
    """
    seg = segments if segments else max(12, lobes * 6)
    out = []
    for s in stations:
        out.append(s)
    obj = loft_path(name, out, segments=seg, **kwargs)
    # scallop radially about the object's own Z axis
    for v in obj.data.vertices:
        co = Vector(v.co)
        r = math.hypot(co.x, co.y)
        if r < 1e-6:
            continue
        ang = math.atan2(co.y, co.x)
        k = 1.0 + lobe_depth * math.cos(lobes * (ang + phase))
        v.co = Vector((co.x * k, co.y * k, co.z))
    obj.data.update()
    return obj


def superellipse_ring(a, b, segments, square=2.0):
    """Points on a superellipse. square=2 is a true ellipse, >2 squares off toward
    a rounded box, <2 pinches toward a diamond.

    Circular/superelliptical rings are the creature equivalent of the hard-surface
    octagon ring: sweeping the squareness along a body lets a round belly run into
    a flatter plastron or a keeled back with no seam.
    """
    pts = []
    e = 2.0 / max(0.2, square)
    for i in range(segments):
        t = TAU * i / segments
        c, s = math.cos(t), math.sin(t)
        pts.append((a * math.copysign(abs(c) ** e, c),
                    b * math.copysign(abs(s) ** e, s)))
    return pts


def _frames(points, up=Vector((0, 0, 1))):
    """Parallel-transport frames along a polyline - no flipping, no twist pops."""
    pts = [Vector(p) for p in points]
    out = []
    ref = None
    for i, p in enumerate(pts):
        if len(pts) == 1:
            tangent = Vector((0, 1, 0))
        elif i == 0:
            tangent = (pts[1] - pts[0]).normalized()
        elif i == len(pts) - 1:
            tangent = (pts[-1] - pts[-2]).normalized()
        else:
            tangent = (pts[i + 1] - pts[i - 1]).normalized()
        if ref is None:
            ref = up if abs(tangent.dot(up)) < 0.94 else Vector((0, 1, 0))
        side = tangent.cross(ref)
        if side.length < 1e-5:
            side = tangent.cross(Vector((1, 0, 0)))
        side.normalize()
        binorm = side.cross(tangent).normalized()
        ref = binorm
        out.append((p, side, binorm, tangent))
    return out


def _catmull(p0, p1, p2, p3, t):
    t2 = t * t
    t3 = t2 * t
    return 0.5 * ((2 * p1) + (-p0 + p2) * t
                  + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2
                  + (-p0 + 3 * p1 - 3 * p2 + p3) * t3)


def resample_stations(st, per_segment):
    """Catmull-Rom subdivision along a lofted path.

    Adding stations is far cheaper than a subdivision surface: it smooths the
    silhouette lengthwise (which is where faceting shows) without multiplying the
    ring count, so the creature stays inside the triangle budget by construction
    instead of being decimated back into triangles afterwards.
    """
    if per_segment <= 0 or len(st) < 2:
        return st
    ext = [st[0]] + list(st) + [st[-1]]
    out = []
    for i in range(1, len(ext) - 2):
        a, b, c, dd = ext[i - 1], ext[i], ext[i + 1], ext[i + 2]
        for k in range(per_segment + 1):
            t = k / float(per_segment + 1)
            co = _catmull(Vector(a[0]), Vector(b[0]), Vector(c[0]), Vector(dd[0]), t)
            vals = []
            for j in range(1, 5):
                vals.append(_catmull(a[j], b[j], c[j], dd[j], t))
            out.append((co, max(0.0, vals[0]), max(0.0, vals[1]), vals[2], vals[3]))
    out.append(st[-1])
    return out


def loft_path(name, stations, segments=12, cap_start=True, cap_end=True,
              up=Vector((0, 0, 1)), round_start=0.0, round_end=0.0, resample=0):
    """Sweep a varying superelliptical cross-section along a path.

    stations: [(co, half_width, half_height[, square[, roll_deg]]), ...]

    This is the single most important modelling primitive in the kit. A torso that
    tapers into a neck, a tail with real taper and curve, a limb with volume - all
    of it is one continuous quad skin with edge loops exactly where the rig bends,
    which is what stacking primitives can never give you.
    """
    st = []
    for s in stations:
        co = Vector(s[0])
        w = s[1]
        h = s[2] if len(s) > 2 else s[1]
        sq = s[3] if len(s) > 3 else 2.0
        roll = math.radians(s[4]) if len(s) > 4 else 0.0
        st.append((co, w, h, sq, roll))

    # dome the ends instead of leaving a flat disc
    def dome(front):
        base = st[0] if front else st[-1]
        nxt = st[1] if front else st[-2]
        d = (base[0] - nxt[0])
        L = d.length
        if L < 1e-7:
            return []
        d = d / L
        amount = round_start if front else round_end
        extra = []
        steps = 3
        for k in range(1, steps + 1):
            f = k / float(steps)
            ang = f * math.pi * 0.5
            extra.append((base[0] + d * (max(base[1], base[2]) * amount * math.sin(ang)),
                          base[1] * math.cos(ang), base[2] * math.cos(ang),
                          base[3], base[4]))
        return extra

    st = resample_stations(st, resample)
    if round_start > 0.0 and len(st) > 1:
        st = list(reversed(dome(True))) + st
    if round_end > 0.0 and len(st) > 1:
        st = st + dome(False)

    frames = _frames([s[0] for s in st], up=up)
    verts = []
    faces = []
    ring_bases = []
    degenerate = []
    for idx, ((p, side, binorm, tangent), (co, w, h, sq, roll)) in enumerate(
            zip(frames, st)):
        if w < 1e-6 and h < 1e-6:
            ring_bases.append(len(verts))
            degenerate.append(True)
            verts.append(tuple(p))
            continue
        degenerate.append(False)
        ring_bases.append(len(verts))
        cr, sr = math.cos(roll), math.sin(roll)
        for (u, v) in superellipse_ring(w, h, segments, sq):
            uu = u * cr - v * sr
            vv = u * sr + v * cr
            verts.append(tuple(p + side * uu + binorm * vv))

    for i in range(len(st) - 1):
        a, b = ring_bases[i], ring_bases[i + 1]
        da, db = degenerate[i], degenerate[i + 1]
        if da and db:
            continue
        if da:
            for s2 in range(segments):
                faces.append((a, b + (s2 + 1) % segments, b + s2))
        elif db:
            for s2 in range(segments):
                faces.append((b, a + s2, a + (s2 + 1) % segments))
        else:
            for s2 in range(segments):
                s3 = (s2 + 1) % segments
                faces.append((a + s2, a + s3, b + s3, b + s2))

    if cap_start and not degenerate[0]:
        c = len(verts)
        verts.append(tuple(frames[0][0]))
        for s2 in range(segments):
            faces.append((c, ring_bases[0] + (s2 + 1) % segments, ring_bases[0] + s2))
    if cap_end and not degenerate[-1]:
        c = len(verts)
        verts.append(tuple(frames[-1][0]))
        base = ring_bases[-1]
        for s2 in range(segments):
            faces.append((c, base + s2, base + (s2 + 1) % segments))
    return new_mesh_object(name, verts, [], faces)


def edit_bm(obj, fn):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.verts.ensure_lookup_table()
    fn(bm)
    bm.normal_update()
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return obj


def recess(obj, pick, thickness, depth, color=None):
    """Cut detail into the skin instead of gluing it on: shell plate divisions,
    head ridges, rock facets. Inset the picked faces, then push them along their
    own normal."""
    if color is not None:
        ensure_color_layer(obj)

    def go(bm):
        sel = [f for f in bm.faces if pick(f)]
        if not sel:
            return
        bmesh.ops.inset_region(bm, faces=sel, thickness=thickness, depth=0.0,
                               use_even_offset=True, use_boundary=True,
                               use_interpolate=True)
        cl = bm.loops.layers.color.get(COLOR_LAYER) if color is not None else None
        for f in sel:
            bmesh.ops.translate(bm, verts=list(f.verts),
                                vec=f.normal.normalized() * depth)
            if cl is not None:
                lin = to_linear_rgba(color)
                for lp in f.loops:
                    lp[cl] = mix(tuple(lp[cl]), lin, 0.8)

    return edit_bm(obj, go)


def tube_along(name, points, radii, segments=10, cap=True, up=Vector((0, 0, 1))):
    """Sweep a circle along a polyline with per-point radius.

    Used for horns, vines, antennae, tails that need an exact curve. Produces a
    clean quad grid with edge loops perpendicular to the sweep.
    """
    pts = [Vector(p) for p in points]
    verts = []
    faces = []
    prev_ref = None
    for i, p in enumerate(pts):
        if i == 0:
            tangent = (pts[1] - pts[0]).normalized()
        elif i == len(pts) - 1:
            tangent = (pts[-1] - pts[-2]).normalized()
        else:
            tangent = (pts[i + 1] - pts[i - 1]).normalized()
        ref = up if abs(tangent.dot(up)) < 0.95 else Vector((0, 1, 0))
        if prev_ref is not None:
            ref = prev_ref
        side = tangent.cross(ref)
        if side.length < 1e-5:
            side = tangent.cross(Vector((1, 0, 0)))
        side.normalize()
        binorm = side.cross(tangent).normalized()
        prev_ref = binorm
        r = radii[i]
        for s in range(segments):
            a = TAU * s / segments
            verts.append(tuple(p + side * (math.cos(a) * r) + binorm * (math.sin(a) * r)))
    for i in range(len(pts) - 1):
        b0 = i * segments
        b1 = (i + 1) * segments
        for s in range(segments):
            s2 = (s + 1) % segments
            faces.append((b0 + s, b0 + s2, b1 + s2, b1 + s))
    if cap:
        c0 = len(verts)
        verts.append(tuple(pts[0]))
        for s in range(segments):
            faces.append((c0, (s + 1) % segments, s))
        base = (len(pts) - 1) * segments
        c1 = len(verts)
        verts.append(tuple(pts[-1]))
        for s in range(segments):
            faces.append((c1, base + s, base + (s + 1) % segments))
    return new_mesh_object(name, verts, [], faces)


def leaf_blade(name, length, width, thickness, curl=0.35, segments=7, taper=2.2):
    """A tapered, curled, slightly thick leaf/fin/wing-membrane blade."""
    verts = []
    faces = []
    rows = segments
    cols = 5
    for i in range(rows + 1):
        t = i / float(rows)
        # never let a row collapse to zero width - that makes degenerate faces,
        # which cleanup then deletes, which leaves holes in the shell
        w = max(width * 0.06,
                width * math.sin(math.pi * min(1.0, t * 1.08)) ** (1.0 / taper))
        droop = -curl * length * t * t
        for j in range(cols + 1):
            u = j / float(cols) * 2.0 - 1.0
            bow = (1.0 - u * u) * thickness
            verts.append((u * w, t * length, droop + bow))
    for i in range(rows):
        for j in range(cols):
            a = i * (cols + 1) + j
            b = a + 1
            c = a + cols + 2
            d = a + cols + 1
            faces.append((a, b, c, d))
    obj = new_mesh_object(name, verts, [], faces)
    solidify(obj, thickness * 0.9)
    return obj


def solidify(obj, thickness, offset=0.0):
    m = obj.modifiers.new("Solidify", 'SOLIDIFY')
    m.thickness = thickness
    m.offset = offset
    m.use_even_offset = True
    apply_modifier(obj, m.name)
    return obj


# ----------------------------------------------------------------------------
# skin-skeleton body builder
# ----------------------------------------------------------------------------

class SkinSkeleton(object):
    """A graph of spheres-on-a-wire turned into a continuous organic body.

    You add points with a radius (optionally elliptical) and connect them; the
    Skin modifier builds a quad tube network with real edge loops at every joint,
    then subdivision smooths it. This is the base mesh every body plan starts from -
    never a union of primitives.
    """

    def __init__(self):
        self.co = []
        self.radius = []
        self.edges = []
        self.root = 0

    def add(self, co, r, ry=None, parent=None):
        idx = len(self.co)
        self.co.append(Vector(co))
        self.radius.append((r, ry if ry is not None else r))
        if parent is not None:
            self.edges.append((parent, idx))
        return idx

    def chain(self, samples, parent=None):
        """samples: [(co, r) or (co, r, ry), ...] connected head to tail."""
        prev = parent
        out = []
        for s in samples:
            co = s[0]
            r = s[1]
            ry = s[2] if len(s) > 2 else None
            prev = self.add(co, r, ry, parent=prev)
            out.append(prev)
        return out

    def connect(self, a, b):
        self.edges.append((a, b))

    def build(self, name, subsurf=1, smooth_shade=True, use_x_symmetry=False):
        obj = new_mesh_object(name, [tuple(c) for c in self.co], self.edges, [])
        activate(obj)
        mod = obj.modifiers.new("Skin", 'SKIN')
        mod.use_smooth_shade = smooth_shade
        mod.branch_smoothing = 0.35
        me = obj.data
        if not len(me.skin_vertices):
            # fall back to the operator, which always creates the CD layer
            obj.modifiers.remove(mod)
            bpy.ops.object.modifier_add(type='SKIN')
            mod = obj.modifiers[-1]
            mod.use_smooth_shade = smooth_shade
            mod.branch_smoothing = 0.35
        layer = me.skin_vertices[0].data
        for i, (rx, ry) in enumerate(self.radius):
            layer[i].radius = (rx, ry)
            layer[i].use_root = (i == self.root)
        if subsurf:
            sub = obj.modifiers.new("Subsurf", 'SUBSURF')
            sub.levels = subsurf
            sub.render_levels = subsurf
        apply_all_modifiers(obj)
        # skin output can carry a few stray tris at branch points; tidy up
        cleanup_mesh(obj, merge_dist=1e-5)
        return obj


# ----------------------------------------------------------------------------
# welding a bag of shells into one continuous skin
# ----------------------------------------------------------------------------

# Parts that must stay their own shell through the weld: thin decals, teeth,
# whiskers, membranes and the eyes. Voxel remeshing at the resolution the body
# needs would either swallow them or inflate them into sausages, and a hard
# accessory sitting on the surface is normal for a stylised creature. Everything
# else - head, neck, torso, limbs, tail, shell, bulb, ears, crest - is body, and
# body has to be one skin.
DETAIL_PATTERNS = ("_eye", "_hl", "mouth", "claw", "blush", "brow", "tooth",
                   "fang", "whisker", "pupil", "flame", "spiral", "earin")
# "beak" deliberately absent: Pidgey's beak halves are open shells, so keeping
# them out of the weld leaves boundary edges in the shipped mesh. Welding them
# softens the beak slightly, which is the cheaper of the two costs.


def drop_small_islands(obj, min_share=0.02):
    """Delete disconnected shells that are a negligible share of the mesh.

    Voxel remeshing sheds any feature thinner than a voxel, and a curling tail
    tip or a whisker root can come off as a free-floating shard. It passes every
    manifold check - it is a closed shell, just not attached to anything - so it
    has to be found by connectivity, not by the usual loose-geometry test. This
    caught a fragment hovering above Rattata's head.
    """
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    seen = set()
    islands = []
    for f in bm.faces:
        if f.index in seen:
            continue
        stack, comp = [f], []
        seen.add(f.index)
        while stack:
            cur = stack.pop()
            comp.append(cur)
            for e in cur.edges:
                for nb in e.link_faces:
                    if nb.index not in seen:
                        seen.add(nb.index)
                        stack.append(nb)
        islands.append(comp)
    if len(islands) > 1:
        biggest = max(len(i) for i in islands)
        doomed = [f for i in islands
                  if len(i) < max(4, biggest * min_share) for f in i]
        if doomed:
            print("  weld: dropped %d orphan shell(s), %d faces"
                  % (sum(1 for i in islands
                         if len(i) < max(4, biggest * min_share)), len(doomed)))
            bmesh.ops.delete(bm, geom=doomed, context='FACES')
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return obj


def is_detail_part(obj):
    n = obj.name.lower()
    return any(p in n for p in DETAIL_PATTERNS)


def transfer_colors(src_obj, dst_obj, layer=None):
    """Nearest-surface vertex-colour transfer.

    Remeshing throws colour away - `quadriflow_remesh` drops the attribute
    outright - and re-deriving every creature's paint after the weld would mean
    rewriting all twelve scripts. Sampling the pre-weld shells instead keeps
    every belly, spot and stripe exactly where the creature script put it.

    Values are copied straight across: what is stored in a colour attribute is
    already linear, so running them back through `to_linear_rgba` here would
    darken the whole cast.
    """
    from mathutils.bvhtree import BVHTree
    layer = layer or COLOR_LAYER
    src = src_obj.data
    if layer not in src.color_attributes:
        return dst_obj
    sl = src.color_attributes[layer]
    verts = [v.co.copy() for v in src.vertices]
    polys = [tuple(p.vertices) for p in src.polygons]
    if not polys:
        return dst_obj
    bvh = BVHTree.FromPolygons(verts, polys, all_triangles=False)
    corners = [(tuple(p.vertices), tuple(p.loop_indices)) for p in src.polygons]
    src_col = [tuple(c.color) for c in sl.data]

    dl = ensure_color_layer(dst_obj)
    dst = dst_obj.data
    cache = {}
    out = []
    for lp in dst.loops:
        vi = lp.vertex_index
        col = cache.get(vi)
        if col is None:
            co = dst.vertices[vi].co
            loc, _nrm, idx, _d = bvh.find_nearest(co)
            if idx is None:
                col = (1.0, 1.0, 1.0, 1.0)
            else:
                vids, lids = corners[idx]
                # Inverse-distance blend across the hit face's corners. Straight
                # nearest-corner sampling quantises every marking to the source
                # face grid, which showed up as a dithered mess along Pikachu's
                # back stripes; this stays sharp near a corner and interpolates
                # in between.
                tot = 0.0
                acc = [0.0, 0.0, 0.0, 0.0]
                for k, v in enumerate(vids):
                    d2 = (verts[v] - loc).length_squared
                    # inverse fourth power: near-nearest close to a corner, so a
                    # hard-edged marking keeps its edge, but still continuous in
                    # between instead of quantising to the source vertex grid
                    w = 1.0 / (d2 * d2 + 1e-14)
                    tot += w
                    c = src_col[lids[k]]
                    for j in range(4):
                        acc[j] += c[j] * w
                col = tuple(a / tot for a in acc) if tot > 0 else (1, 1, 1, 1)
            cache[vi] = col
        out.extend(col)
    dl.data.foreach_set("color", out)
    dst.update()
    return dst_obj


def weld_skin(obj, height, target_quads=2400, voxel_ratio=1.0 / 150.0,
              smooth_iters=2, smooth_factor=0.45, symmetry=True):
    """Turn a bag of interpenetrating shells into ONE continuous skin.

    `join_objects` merges objects, not surfaces: a head ball jammed into a body
    ball stays two balls with a hard interpenetration line down the join, and no
    amount of care inside each individual loft fixes that. The user's
    requirement is that a creature reads as a single mesh.

    Route: voxel remesh (which is a union by construction - overlapping shells
    become one watertight volume, all quads), smooth off the voxel stair-
    stepping, then Quadriflow back to a clean quad field at an exact face
    budget. Colour is carried across from the pre-weld shells afterwards.

    Alternatives considered: a Boolean union alone welds the topology but leaves
    a hard crease along the intersection curve, which is the very seam we are
    trying to lose; bridging edge loops gives the best topology but cannot be
    scripted generically across twelve different body plans.
    """
    activate(obj)
    snapshot = obj.copy()
    snapshot.data = obj.data.copy()
    snapshot.name = obj.name + "_PreWeld"
    link(snapshot)

    me = obj.data
    me.remesh_mode = 'VOXEL'
    me.remesh_voxel_size = max(1e-4, height * voxel_ratio)
    me.remesh_voxel_adaptivity = 0.0
    me.use_remesh_fix_poles = True
    for flag in ("use_remesh_preserve_volume", "use_remesh_preserve_vertex_colors"):
        if hasattr(me, flag):
            setattr(me, flag, True)
    activate(obj)
    bpy.ops.object.voxel_remesh()
    print("  weld: voxel %.4f m -> %d faces" % (me.remesh_voxel_size,
                                                len(me.polygons)))
    drop_small_islands(obj)

    if smooth_iters:
        m = obj.modifiers.new("WeldSmooth", 'SMOOTH')
        m.factor = smooth_factor
        m.iterations = smooth_iters
        apply_modifier(obj, m.name)

    if symmetry:
        for axis in ("symmetry_x",):
            if hasattr(me, axis):
                setattr(me, axis, True)

    ok = False
    try:
        activate(obj)
        bpy.ops.object.quadriflow_remesh(mode='FACES',
                                         target_faces=max(400, int(target_quads)),
                                         use_preserve_sharp=False,
                                         use_preserve_boundary=False,
                                         preserve_paint_mask=False,
                                         smooth_normals=False,
                                         use_mesh_symmetry=True, seed=0)
        ok = len(obj.data.polygons) > 100
    except Exception as exc:
        print("  ! quadriflow failed (%s)" % exc)
    if not ok:
        # unsubdivide keeps quads, unlike a collapse decimate
        while tri_count(obj) > target_quads * 2 * 1.15:
            before = tri_count(obj)
            m = obj.modifiers.new("WeldUnsub", 'DECIMATE')
            m.decimate_type = 'UNSUBDIV'
            m.iterations = 1
            apply_modifier(obj, m.name)
            if tri_count(obj) >= before:
                break
    print("  weld: quadriflow -> %d faces (%d tris, %.0f%% quads)"
          % (len(obj.data.polygons), tri_count(obj), 100.0 * quad_ratio(obj)))

    # Again after quadriflow: a tail tip thinner than a voxel can survive the
    # voxel pass as a hairline bridge and only come away during the retopology,
    # which is how Rattata ended up with a fragment hovering over its head.
    drop_small_islands(obj, min_share=0.06)
    cleanup_mesh(obj)
    transfer_colors(snapshot, obj)
    bpy.data.objects.remove(snapshot, do_unlink=True)
    return obj


def triangulate_ngons(obj):
    """The contract bans n-gons on deforming surfaces. Bevel mitres and sweep caps
    can still produce a few, so any survivor is split into triangles - quads are
    untouched, so the mesh stays quad-dominant."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    ngons = [f for f in bm.faces if len(f.verts) > 4]
    if ngons:
        bmesh.ops.triangulate(bm, faces=ngons, quad_method='BEAUTY',
                              ngon_method='BEAUTY')
    bm.to_mesh(me)
    bm.free()
    me.update()
    return len(ngons)


def fill_holes(obj):
    """Close any boundary loops left behind after degenerate faces were removed.

    Aggressive shaping (flattening a rock's base, squashing a spike) can collapse
    a ring to zero area; deleting it leaves an open shell, which shows as a black
    rim in a render and fails the contract's no-holes check."""
    me = obj.data
    total = 0
    for _ in range(4):
        bm = bmesh.new()
        bm.from_mesh(me)
        boundary = [e for e in bm.edges if len(e.link_faces) == 1]
        if not boundary:
            bm.free()
            break
        total += len(boundary)
        bmesh.ops.holes_fill(bm, edges=boundary, sides=0)
        still = [e for e in bm.edges if len(e.link_faces) == 1]
        if still:
            bmesh.ops.triangle_fill(bm, edges=still, use_beauty=True)
        # anything left is a sliver too degenerate to fill - collapse it away
        still = [e for e in bm.edges if len(e.link_faces) == 1]
        if still:
            verts = set()
            for e in still:
                verts.update(e.verts)
            bmesh.ops.dissolve_verts(bm, verts=list(verts))
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
        bm.to_mesh(me)
        bm.free()
        me.update()
    return total


def cleanup_mesh(obj, merge_dist=1e-5, recalc=True):
    activate(obj)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=merge_dist)
    # drop wire / loose geometry
    loose_v = [v for v in bm.verts if not v.link_faces]
    if loose_v:
        bmesh.ops.delete(bm, geom=loose_v, context='VERTS')
    loose_e = [e for e in bm.edges if not e.link_faces]
    if loose_e:
        bmesh.ops.delete(bm, geom=loose_e, context='EDGES')
    # zero-area faces
    degen = [f for f in bm.faces if f.calc_area() < 1e-9]
    if degen:
        bmesh.ops.delete(bm, geom=degen, context='FACES')
    if recalc:
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return obj


# ----------------------------------------------------------------------------
# sculpt-like deformation (proportional-editing equivalents, in script)
# ----------------------------------------------------------------------------

def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3.0 - 2.0 * t)


def falloff(dist, radius):
    if radius <= 0.0:
        return 0.0
    return smoothstep(1.0 - dist / radius)


def deform(obj, fn):
    """fn(Vector) -> Vector, applied to every vertex in object space."""
    for v in obj.data.vertices:
        v.co = fn(Vector(v.co))
    obj.data.update()
    return obj


def bulge(obj, center, radius, amount, direction=None, ellipse=(1, 1, 1)):
    """Push a region outward (radially, or along `direction`) with smooth falloff.

    The scripted stand-in for a proportional-edit grab: bellies, cheeks, brows,
    shoulder mass.
    """
    c = Vector(center)
    e = Vector(ellipse)

    def fn(co):
        d = Vector(((co.x - c.x) / e.x, (co.y - c.y) / e.y, (co.z - c.z) / e.z))
        w = falloff(d.length, radius)
        if w <= 0.0:
            return co
        if direction is None:
            n = (co - c)
            n = n.normalized() if n.length > 1e-6 else Vector((0, 0, 1))
        else:
            n = Vector(direction).normalized()
        return co + n * (amount * w)

    return deform(obj, fn)


def scale_region(obj, center, radius, factors, pivot=None, ellipse=(1, 1, 1)):
    c = Vector(center)
    p = Vector(pivot) if pivot is not None else c
    f = Vector(factors)
    e = Vector(ellipse)

    def fn(co):
        d = Vector(((co.x - c.x) / e.x, (co.y - c.y) / e.y, (co.z - c.z) / e.z))
        w = falloff(d.length, radius)
        if w <= 0.0:
            return co
        rel = co - p
        s = Vector((1 + (f.x - 1) * w, 1 + (f.y - 1) * w, 1 + (f.z - 1) * w))
        return p + Vector((rel.x * s.x, rel.y * s.y, rel.z * s.z))

    return deform(obj, fn)


def taper_axis(obj, axis, a, b, sa, sb, other=('x', 'y')):
    """Linearly scale the cross-section between two positions along `axis`."""
    ai = 'xyz'.index(axis)
    idx = ['xyz'.index(o) for o in other]

    def fn(co):
        t = (co[ai] - a) / (b - a) if abs(b - a) > 1e-9 else 0.0
        t = max(0.0, min(1.0, t))
        s = sa + (sb - sa) * smoothstep(t)
        out = co.copy()
        for i in idx:
            out[i] = co[i] * s
        return out

    return deform(obj, fn)


def bend(obj, axis, from_v, to_v, angle, pivot_axis='x'):
    """Bend geometry about `pivot_axis` proportionally to position along `axis`."""
    ai = 'xyz'.index(axis)

    def fn(co):
        t = (co[ai] - from_v) / (to_v - from_v) if abs(to_v - from_v) > 1e-9 else 0.0
        t = max(0.0, min(1.0, t))
        a = angle * smoothstep(t)
        m = Matrix.Rotation(a, 3, pivot_axis.upper())
        return m @ co

    return deform(obj, fn)


def twist(obj, axis, from_v, to_v, angle):
    ai = 'xyz'.index(axis)

    def fn(co):
        t = (co[ai] - from_v) / (to_v - from_v) if abs(to_v - from_v) > 1e-9 else 0.0
        t = max(0.0, min(1.0, t))
        m = Matrix.Rotation(angle * t, 3, axis.upper())
        return m @ co

    return deform(obj, fn)


def flatten_axis(obj, axis, factor, center=0.0):
    ai = 'xyz'.index(axis)

    def fn(co):
        out = co.copy()
        out[ai] = center + (co[ai] - center) * factor
        return out

    return deform(obj, fn)


def noise_displace(obj, scale, amount, seed=0):
    """Fine surface break-up so silhouettes are not mathematically clean."""
    rnd = random.Random(seed)
    ox, oy, oz = rnd.random() * 10, rnd.random() * 10, rnd.random() * 10

    def n(p):
        return (math.sin(p.x * scale + ox) * math.sin(p.y * scale * 1.31 + oy)
                * math.sin(p.z * scale * 0.87 + oz))

    obj.data.calc_normals_split()
    normals = {}
    for poly in obj.data.polygons:
        for vi in poly.vertices:
            normals.setdefault(vi, Vector((0, 0, 0)))
            normals[vi] += poly.normal
    for i, v in enumerate(obj.data.vertices):
        nrm = normals.get(i, Vector((0, 0, 1)))
        if nrm.length > 1e-6:
            nrm.normalize()
        v.co = Vector(v.co) + nrm * (n(Vector(v.co)) * amount)
    obj.data.update()
    return obj


def lattice_deform(obj, resolution, fn, bounds=None, name="Lat"):
    """Real lattice deform: build a lattice over the object, move its points via
    fn(u, v, w, co) -> offset, then apply. Good for whole-body proportion moves
    (pear-shaped torso, drooping mass) that vertex maths alone makes fiddly.
    """
    if bounds is None:
        bounds = obj.bound_box
        xs = [p[0] for p in bounds]
        ys = [p[1] for p in bounds]
        zs = [p[2] for p in bounds]
        bmin = Vector((min(xs), min(ys), min(zs)))
        bmax = Vector((max(xs), max(ys), max(zs)))
    else:
        bmin, bmax = Vector(bounds[0]), Vector(bounds[1])
    size = bmax - bmin
    pad = size * 0.06
    bmin -= pad
    bmax += pad
    size = bmax - bmin

    lat_data = bpy.data.lattices.new(name)
    lat = bpy.data.objects.new(name, lat_data)
    link(lat)
    u, v, w = resolution
    lat_data.points_u, lat_data.points_v, lat_data.points_w = u, v, w
    lat.location = (bmin + bmax) * 0.5
    lat.scale = size
    bpy.context.view_layer.update()

    for i, pt in enumerate(lat_data.points):
        iu = i % u
        iv = (i // u) % v
        iw = i // (u * v)
        fu = iu / float(u - 1) if u > 1 else 0.5
        fv = iv / float(v - 1) if v > 1 else 0.5
        fw = iw / float(w - 1) if w > 1 else 0.5
        world = Vector((bmin.x + size.x * fu, bmin.y + size.y * fv, bmin.z + size.z * fw))
        off = fn(fu, fv, fw, world)
        if off is not None:
            pt.co_deform = Vector(pt.co_deform) + Vector((
                off[0] / size.x, off[1] / size.y, off[2] / size.z))

    m = obj.modifiers.new(name, 'LATTICE')
    m.object = lat
    apply_modifier(obj, m.name)
    bpy.data.objects.remove(lat, do_unlink=True)
    bpy.data.lattices.remove(lat_data)
    return obj


def mirror_x(obj, merge_threshold=1e-4):
    m = obj.modifiers.new("Mirror", 'MIRROR')
    m.use_axis = (True, False, False)
    m.use_clip = True
    m.merge_threshold = merge_threshold
    apply_modifier(obj, m.name)
    return obj


def subsurf(obj, levels=1, crease_angle=None):
    m = obj.modifiers.new("Subsurf", 'SUBSURF')
    m.levels = levels
    m.render_levels = levels
    apply_modifier(obj, m.name)
    return obj


def crease_sharp_edges(obj, angle_deg=48.0, crease=0.9):
    """Creases stop subdivision from melting shell rims, horn ridges and plates."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    # Blender 4.0 moved edge crease off bm.edges.layers.crease and onto a generic
    # float attribute named 'crease_edge'
    try:
        layer = bm.edges.layers.crease.verify()
    except AttributeError:
        layer = (bm.edges.layers.float.get('crease_edge')
                 or bm.edges.layers.float.new('crease_edge'))
    a = math.radians(angle_deg)
    for e in bm.edges:
        if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > a:
            e[layer] = crease
    bm.to_mesh(me)
    bm.free()
    return obj


def mark_sharp_by_angle(obj, angle_deg=42.0):
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    a = math.radians(angle_deg)
    for e in bm.edges:
        if len(e.link_faces) == 2 and e.calc_face_angle(0.0) > a:
            e.smooth = False
    bm.to_mesh(me)
    bm.free()
    return obj


def bevel_hard_edges(obj, width, segments=2, angle_deg=32.0, harden=True):
    """An unbevelled hard edge is the clearest tell of procedural art. This is the
    last modelling step before shading, applied to every creature."""
    m = obj.modifiers.new("Bevel", 'BEVEL')
    m.width = width
    m.segments = segments
    m.limit_method = 'ANGLE'
    m.angle_limit = math.radians(angle_deg)
    m.harden_normals = harden and obj.data.use_auto_smooth
    m.miter_outer = 'MITER_ARC'
    m.use_clamp_overlap = True
    try:
        apply_modifier(obj, m.name)
    except Exception as exc:
        print("  ! bevel failed (%s), retrying without harden" % exc)
        m.harden_normals = False
        apply_modifier(obj, m.name)
    return obj


def shade_smooth_with_sharp(obj, angle_deg=40.0):
    activate(obj)
    bpy.ops.object.shade_smooth()
    me = obj.data
    me.use_auto_smooth = True
    me.auto_smooth_angle = math.radians(angle_deg)
    return obj


def add_custom_split_normals(obj):
    activate(obj)
    me = obj.data
    me.calc_normals_split()
    try:
        bpy.ops.mesh.customdata_custom_splitnormals_add()
    except Exception:
        pass
    return obj


def rotate_obj(obj, euler):
    obj.rotation_euler = Euler(euler, 'XYZ')
    apply_transforms(obj)
    return obj


def place(obj, location=(0, 0, 0), rotation=(0, 0, 0), scale=(1, 1, 1)):
    obj.location = Vector(location)
    obj.rotation_euler = Euler(rotation, 'XYZ')
    obj.scale = Vector(scale)
    apply_transforms(obj)
    return obj


def duplicate(obj, name, mirror=False):
    new = obj.copy()
    new.data = obj.data.copy()
    new.name = name
    new.data.name = name
    link(new)
    if mirror:
        new.scale = (-1, 1, 1)
        apply_transforms(new)
        cleanup_mesh(new)
    return new


# ----------------------------------------------------------------------------
# vertex colours -> the source of every baked albedo
# ----------------------------------------------------------------------------

COLOR_LAYER = "Col"


def ensure_color_layer(obj):
    me = obj.data
    if COLOR_LAYER not in me.color_attributes:
        me.color_attributes.new(name=COLOR_LAYER, type='BYTE_COLOR', domain='CORNER')
    idx = me.color_attributes.find(COLOR_LAYER)
    if idx >= 0:
        me.color_attributes.active_color_index = idx
        me.color_attributes.render_color_index = idx
    return me.color_attributes[COLOR_LAYER]


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def to_linear_rgba(c):
    """Colours are authored in sRGB (that is what a hex code off the reference art
    means), but Blender's colour-attribute RNA takes *linear* floats. Writing sRGB
    values straight in bleaches every creature - the first bake came out pastel
    mint instead of the reference's teal. Convert at every write site."""
    return (srgb_to_linear(c[0]), srgb_to_linear(c[1]), srgb_to_linear(c[2]),
            c[3] if len(c) > 3 else 1.0)


def linear_to_srgb_rgba(c):
    def f(v):
        return v * 12.92 if v <= 0.0031308 else 1.055 * (v ** (1 / 2.4)) - 0.055
    return (f(c[0]), f(c[1]), f(c[2]), c[3] if len(c) > 3 else 1.0)


def hexcol(h, alpha=1.0):
    h = h.lstrip('#')
    r, g, b = (int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))
    return (r, g, b, alpha)


def paint(obj, fn):
    """fn(co, normal, vertex_index) -> (r,g,b,a) in 0..1, per loop.

    Normals are pulled into plain Python *before* the colour layer is created:
    calc_normals_split reallocates the mesh CustomData, which invalidates any RNA
    reference taken before it (that is an access violation, not an exception).
    """
    me = obj.data
    me.calc_normals_split()
    loop_data = [(l.vertex_index, tuple(l.normal)) for l in me.loops]
    coords = [tuple(v.co) for v in me.vertices]
    layer = ensure_color_layer(obj)
    colors = []
    for vi, nrm in loop_data:
        colors.extend(to_linear_rgba(fn(Vector(coords[vi]), Vector(nrm), vi)))
    layer.data.foreach_set("color", colors)
    me.update()
    return obj


def paint_flat(obj, color):
    return paint(obj, lambda co, n, i: color)


def mix(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(4))


def body_gradient(top, bottom, zmin, zmax, belly=None, belly_axis_y=None,
                  patches=None, noise_amt=0.0, seed=1, patch_edge=0.30):
    """Standard creature albedo: darker along the back, lighter under the belly,
    optional patch overrides, plus a touch of noise so nothing reads as flat."""
    rnd = random.Random(seed)
    ox = rnd.random() * 9.0

    def fn(co, n, i):
        t = (co.z - zmin) / max(1e-6, (zmax - zmin))
        c = mix(bottom, top, smoothstep(t))
        if belly is not None:
            # front-and-under facing surfaces get the belly colour
            w = max(0.0, -n.z) * 0.75 + max(0.0, -n.y) * 0.45
            if belly_axis_y is not None:
                w *= smoothstep((belly_axis_y - co.z) / max(1e-6, belly_axis_y))
            c = mix(c, belly, min(1.0, w))
        if patches:
            for patch in patches:
                pc, cen, rad, ell = patch[0], patch[1], patch[2], patch[3]
                edge = patch[4] if len(patch) > 4 else patch_edge
                e = Vector(ell)
                d = Vector(((co.x - cen[0]) / e.x, (co.y - cen[1]) / e.y,
                            (co.z - cen[2]) / e.z)).length
                # mostly solid with a soft rim - reference markings are graphic
                # shapes, not airbrushed gradients
                c = mix(c, pc, smoothstep((1.0 - d / max(1e-6, rad)) / max(1e-3, edge)))
        if noise_amt:
            s = math.sin(co.x * 37.0 + ox) * math.sin(co.y * 31.0) * math.sin(co.z * 41.0)
            c = tuple(max(0.0, min(1.0, c[k] + s * noise_amt)) for k in range(3)) + (c[3],)
        return c

    return fn


# ----------------------------------------------------------------------------
# eyes - the single biggest readability lever at small size
# ----------------------------------------------------------------------------

EYE_DARK = hexcol('16161d')
MOUTH_DARK = hexcol('20141a')


def make_eye(name, center, radius, look=Vector((0, -1, 0)), dark=EYE_DARK,
             squash=(1.0, 0.55, 1.15), tilt=0.0, highlight=0.30,
             highlight_dir=(-0.42, 0.0, 0.44), rim=0.10):
    """The shared face idiom for the whole cast: a simple dark oval eye with one
    soft highlight. No sclera, no iris rings.

    Deliberately iconic - it makes twelve independently modelled creatures read as
    one cast, it survives being 40 px tall in the overworld, and it keeps the
    budget for silhouette and animation where the quality actually shows.
    """
    eye = uv_sphere(name, radius=1.0, segments=14, rings=9)
    d = Vector(look).normalized()

    def paint_fn(co, n, i):
        v = Vector(co).normalized()
        a = math.degrees(math.acos(max(-1.0, min(1.0, v.dot(d)))))
        # a whisper of lift at the very edge so the oval has form, not a flat fill
        edge = smoothstep((a - 62.0) / 34.0)
        return mix(dark, tuple(min(1.0, c + rim) for c in dark[:3]) + (1.0,), edge)

    paint(eye, paint_fn)
    # squash along the view axis, then swing the whole eye so its flattened axis
    # follows `look` - otherwise a non-forward eye gets flattened across the wrong
    # axis and reads as a bulging bead on the side of the skull
    eye.scale = (radius * squash[0], radius * squash[1], radius * squash[2])
    apply_transforms(eye)
    if tilt:
        rotate_obj(eye, (0, tilt, 0))
    swing = Vector((0, -1, 0)).rotation_difference(d).to_matrix().to_4x4()
    for v in eye.data.vertices:
        v.co = swing @ Vector(v.co)
    eye.data.update()
    eye.location = Vector(center)
    apply_transforms(eye)

    hl = None
    if highlight > 0.0:
        hl = uv_sphere(name + "_HL", radius=radius * highlight, segments=8, rings=5)
        hl.scale = (1.0, 0.6, 1.0)
        apply_transforms(hl)
        paint_flat(hl, (1.0, 1.0, 1.0, 1.0))
        side = d.cross(Vector((0, 0, 1)))
        if side.length < 1e-4:
            side = Vector((1, 0, 0))
        side.normalize()
        up = side.cross(d).normalized()
        off = (Vector(center) + d * (radius * squash[1] * 0.80)
               + side * (highlight_dir[0] * radius)
               + up * (highlight_dir[2] * radius))
        hl.location = off
        apply_transforms(hl)
    return eye, hl


def mouth_arc(name, center, width, curve=0.28, thickness=0.02, face_bow=0.0,
              look=Vector((0, -1, 0)), color=MOUTH_DARK, segments=9, ring=7):
    """A simple curved mouth line that hugs the face - the other half of the shared
    facial language. curve > 0 smiles, curve < 0 frowns."""
    # build in the tangent frame at the placement point, so the mouth lies ON the
    # face wherever it is put rather than slicing through the skull
    d = Vector(look).normalized()
    side = d.cross(Vector((0, 0, 1)))
    if side.length < 1e-4:
        side = Vector((1, 0, 0))
    side.normalize()
    up = side.cross(d).normalized()
    pts = []
    radii = []
    n = segments
    for i in range(n):
        u = (i / float(n - 1)) * 2.0 - 1.0
        x = u * width * 0.5
        z = curve * width * 0.5 * (u * u - 0.35)
        p = (Vector(center) + side * x + up * z - d * (face_bow * u * u))
        pts.append(p)
        radii.append(thickness * (0.45 + 0.55 * math.cos(u * math.pi * 0.5) ** 0.7))
    obj = tube_along(name, pts, radii, segments=ring, up=d)
    paint_flat(obj, color)
    return obj


def ray_to_surface(obj, origin, direction):
    """Cast a ray from inside `obj` outward and return (location, normal).

    `obj` must have its transform applied, which every part does by the time this
    is useful, so object space and world space coincide.
    """
    hit, loc, nrm, _ = obj.ray_cast(Vector(origin), Vector(direction).normalized())
    if not hit:
        return None, None
    return Vector(loc), Vector(nrm)


def drape_line(name, obj, origin, look, up=(0, 0, 1), yaw_span=46.0, pitch=-26.0,
               curve=14.0, thickness=0.008, color=MOUTH_DARK, samples=15, ring=6,
               lift=0.6):
    """A mouth (or brow) line laid ON a head, by ray-casting from inside it.

    `mouth_arc` builds its curve inside a single flat tangent frame. That is fine
    on a ball and wrong on anything with a snout or a jaw: the middle of the arc
    ends up buried in the muzzle and only the tip pokes out, which is what the
    first pass shipped on three of the four creatures revised here. Sampling the
    real surface cannot make that mistake.

    curve > 0 lifts the corners (a smile); the sweep is in degrees of yaw either
    side of `look`, so the line wraps a narrow muzzle and a broad face alike.
    """
    look = Vector(look).normalized()
    side = look.cross(Vector(up))
    if side.length < 1e-5:
        side = Vector((1, 0, 0))
    side.normalize()
    upv = side.cross(look).normalized()
    bpy.context.view_layer.update()
    pts, radii = [], []
    for i in range(samples):
        u = (i / float(samples - 1)) * 2.0 - 1.0
        yaw = math.radians(yaw_span * u)
        pit = math.radians(pitch + curve * (u * u - 0.35))
        d = Matrix.Rotation(pit, 3, side) @ (Matrix.Rotation(yaw, 3, upv) @ look)
        loc, nrm = ray_to_surface(obj, origin, d)
        if loc is None:
            continue
        n = nrm if nrm is not None and nrm.length > 1e-6 else d
        pts.append(loc + n.normalized() * (thickness * lift))
        radii.append(thickness * (0.45 + 0.55 * math.cos(u * math.pi * 0.5) ** 0.7))
    if len(pts) < 3:
        print("  ! drape_line %s: only %d surface hits" % (name, len(pts)))
        return None
    ob = tube_along(name, pts, radii, segments=ring, up=look)
    paint_flat(ob, color)
    return ob


def cheek_blush(name, center, radius, color=hexcol('e58a8f'), squash=(1.0, 0.30, 0.85)):
    """A soft raised cheek patch. Cheap, and it does a lot for appeal."""
    ob = uv_sphere(name, radius=radius, segments=14, rings=9)
    ob.scale = squash
    apply_transforms(ob)
    paint(ob, lambda co, n, i: mix(color, tuple(min(1.0, c + 0.10) for c in color[:3])
                                   + (1.0,), smoothstep(-Vector(co).normalized().y)))
    ob.location = Vector(center)
    apply_transforms(ob)
    return ob


# ----------------------------------------------------------------------------
# shape keys - expression without extra bones
# ----------------------------------------------------------------------------

def add_shape_key(obj, name, fn, from_mix=False):
    """fn(co, index) -> new co. Creates Basis first if the mesh has no keys yet."""
    if obj.data.shape_keys is None:
        obj.shape_key_add(name="Basis", from_mix=False)
    key = obj.shape_key_add(name=name, from_mix=from_mix)
    key.value = 0.0
    for i, p in enumerate(key.data):
        new = fn(Vector(p.co), i)
        if new is not None:
            p.co = new
    return key


def shape_key_blink(obj, eye_centers, radius, close=0.86, name="Blink"):
    """Flatten the eye ovals toward their own centre. With a dark eye that reads as
    a closed eye, which is exactly what the simple-eye idiom wants."""
    centres = [Vector(c) for c in eye_centers]

    def fn(co, i):
        for c in centres:
            if (co - c).length < radius:
                return Vector((co.x, co.y, c.z + (co.z - c.z) * (1.0 - close)))
        return None

    return add_shape_key(obj, name, fn)


def shape_key_squint(obj, eye_centers, radius, amount=0.45, name="Squint"):
    centres = [Vector(c) for c in eye_centers]

    def fn(co, i):
        for c in centres:
            d = (co - c).length
            if d < radius:
                w = falloff(d, radius)
                sq = 1.0 - amount * w
                return Vector((co.x, co.y, c.z + (co.z - c.z) * sq
                               + radius * 0.12 * amount * w))
        return None

    return add_shape_key(obj, name, fn)


# ----------------------------------------------------------------------------
# mouth
# ----------------------------------------------------------------------------

def carve_mouth(obj, center, width, height, depth, look=Vector((0, -1, 0)),
                color=hexcol('3a1f28'), corner_lift=0.0):
    """Press a mouth line into a head and darken it. Adds the 'Mouth' vertex group
    used later to weight the jaw bone."""
    c = Vector(center)
    d = Vector(look).normalized()
    vg = obj.vertex_groups.get("Mouth") or obj.vertex_groups.new(name="Mouth")
    weights = {}
    for v in obj.data.vertices:
        co = Vector(v.co)
        rel = co - c
        lift = corner_lift * (rel.x / max(1e-6, width)) ** 2
        u = abs(rel.x) / width
        w_v = abs(rel.z - lift) / height
        if u > 1.0:
            continue
        f = falloff(math.sqrt(u * u + w_v * w_v), 1.0)
        if f <= 0.0:
            continue
        if rel.dot(d) < -0.05 * width:
            continue
        v.co = co - d * (depth * f)
        weights[v.index] = f
    for i, w in weights.items():
        vg.add([i], w, 'REPLACE')
    obj.data.update()

    layer = ensure_color_layer(obj)
    me = obj.data
    lin = to_linear_rgba(color)
    for poly in me.polygons:
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            w = weights.get(vi, 0.0)
            if w > 0.02:
                cur = tuple(layer.data[li].color)
                layer.data[li].color = mix(cur, lin, min(1.0, w * 1.6))
    return obj


# ----------------------------------------------------------------------------
# UVs
# ----------------------------------------------------------------------------

def unwrap(obj, angle_deg=66.0, margin=0.012):
    activate(obj)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(angle_deg),
                             island_margin=margin,
                             correct_aspect=True, scale_to_bounds=False)
    bpy.ops.uv.select_all(action='SELECT')
    try:
        bpy.ops.uv.average_islands_scale()
        bpy.ops.uv.pack_islands(margin=margin, rotate=True)
    except Exception as exc:
        print("  ! uv pack: %s" % exc)
    bpy.ops.object.mode_set(mode='OBJECT')
    return obj


def uv_stats(obj):
    me = obj.data
    if not me.uv_layers:
        return None
    uv = me.uv_layers[0].data
    us = [d.uv[0] for d in uv]
    vs = [d.uv[1] for d in uv]
    return (min(us), min(vs), max(us), max(vs))


# ----------------------------------------------------------------------------
# materials & baking
# ----------------------------------------------------------------------------

def _new_material(name):
    mat = bpy.data.materials.get(name)
    if mat:
        bpy.data.materials.remove(mat)
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    return mat


def build_bake_material(obj, name, detail_scale=28.0, cavity=0.42, ao_strength=0.55,
                        speckle=0.05, roughness=0.62, voronoi_scale=9.0,
                        voronoi_amount=0.09):
    """Shader that turns vertex colours into a rich albedo: cavity darkening from
    pointiness, ambient occlusion in the creases, and a fine speckle so the bake is
    never a flat fill."""
    mat = _new_material(name + "_Bake")
    nt = mat.node_tree
    nt.nodes.clear()
    n = nt.nodes.new
    out = n('ShaderNodeOutputMaterial')
    emit = n('ShaderNodeEmission')
    attr = n('ShaderNodeAttribute')
    attr.attribute_name = COLOR_LAYER
    geo = n('ShaderNodeNewGeometry')
    ao = n('ShaderNodeAmbientOcclusion')
    ao.samples = 8
    ao.inputs['Distance'].default_value = 0.12
    noise = n('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = detail_scale
    noise.inputs['Detail'].default_value = 6.0
    noise.inputs['Roughness'].default_value = 0.62
    # per-area tonal variation: broad Voronoi cells remapped to a narrow band and
    # multiplied in. This is what stops a colour reading as a flat fill.
    tcoord = n('ShaderNodeTexCoord')
    vor = n('ShaderNodeTexVoronoi')
    vor.inputs['Scale'].default_value = voronoi_scale
    bw = n('ShaderNodeRGBToBW')
    remap = n('ShaderNodeMapRange')
    remap.inputs['From Min'].default_value = 0.0
    remap.inputs['From Max'].default_value = 1.0
    remap.inputs['To Min'].default_value = 1.0 - voronoi_amount
    remap.inputs['To Max'].default_value = 1.0 + voronoi_amount
    mul_var = n('ShaderNodeMixRGB')
    mul_var.blend_type = 'MULTIPLY'
    mul_var.inputs['Fac'].default_value = 1.0
    # pointiness -> cavity
    ramp = n('ShaderNodeValToRGB')
    ramp.color_ramp.elements[0].position = 0.42
    ramp.color_ramp.elements[1].position = 0.58
    mul_cav = n('ShaderNodeMixRGB')
    mul_cav.blend_type = 'MULTIPLY'
    mul_cav.inputs['Fac'].default_value = cavity
    mul_ao = n('ShaderNodeMixRGB')
    mul_ao.blend_type = 'MULTIPLY'
    mul_ao.inputs['Fac'].default_value = ao_strength
    add_noise = n('ShaderNodeMixRGB')
    add_noise.blend_type = 'OVERLAY'
    add_noise.inputs['Fac'].default_value = speckle

    nt.links.new(tcoord.outputs['Object'], vor.inputs['Vector'])
    nt.links.new(vor.outputs['Color'], bw.inputs['Color'])
    nt.links.new(bw.outputs['Val'], remap.inputs['Value'])
    nt.links.new(attr.outputs['Color'], mul_var.inputs['Color1'])
    nt.links.new(remap.outputs['Result'], mul_var.inputs['Color2'])
    nt.links.new(geo.outputs['Pointiness'], ramp.inputs['Fac'])
    nt.links.new(mul_var.outputs['Color'], mul_cav.inputs['Color1'])
    nt.links.new(ramp.outputs['Color'], mul_cav.inputs['Color2'])
    nt.links.new(mul_cav.outputs['Color'], mul_ao.inputs['Color1'])
    nt.links.new(ao.outputs['Color'], mul_ao.inputs['Color2'])
    nt.links.new(mul_ao.outputs['Color'], add_noise.inputs['Color1'])
    nt.links.new(noise.outputs['Color'], add_noise.inputs['Color2'])
    nt.links.new(add_noise.outputs['Color'], emit.inputs['Color'])
    nt.links.new(emit.outputs['Emission'], out.inputs['Surface'])
    for i, node in enumerate(nt.nodes):
        node.location = (i * 180 - 900, 0)
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    return mat


def build_normal_bake_material(obj, name, detail_scale=48.0, bump=0.22,
                               pattern_scale=9.0, pattern_bump=0.10):
    mat = _new_material(name + "_NBake")
    nt = mat.node_tree
    nt.nodes.clear()
    n = nt.nodes.new
    out = n('ShaderNodeOutputMaterial')
    diff = n('ShaderNodeBsdfDiffuse')
    noise = n('ShaderNodeTexNoise')
    noise.inputs['Scale'].default_value = detail_scale
    noise.inputs['Detail'].default_value = 8.0
    vor = n('ShaderNodeTexVoronoi')
    vor.feature = 'DISTANCE_TO_EDGE'
    vor.inputs['Scale'].default_value = pattern_scale
    b1 = n('ShaderNodeBump')
    b1.inputs['Strength'].default_value = pattern_bump
    b2 = n('ShaderNodeBump')
    b2.inputs['Strength'].default_value = bump
    nt.links.new(vor.outputs['Distance'], b1.inputs['Height'])
    nt.links.new(b1.outputs['Normal'], b2.inputs['Normal'])
    nt.links.new(noise.outputs['Fac'], b2.inputs['Height'])
    nt.links.new(b2.outputs['Normal'], diff.inputs['Normal'])
    nt.links.new(diff.outputs['BSDF'], out.inputs['Surface'])
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    return mat


def _bake_to_image(obj, mat, image, bake_type, samples=8, normal_space='TANGENT'):
    scn = bpy.context.scene
    scn.render.engine = 'CYCLES'
    scn.cycles.device = 'CPU'
    scn.cycles.samples = samples
    scn.cycles.use_denoising = False
    scn.render.bake.margin = 20
    scn.render.bake.use_clear = True
    nt = mat.node_tree
    tex = nt.nodes.new('ShaderNodeTexImage')
    tex.image = image
    nt.nodes.active = tex
    for node in nt.nodes:
        node.select = False
    tex.select = True
    activate(obj)
    kwargs = dict(type=bake_type, use_clear=True, margin=20)
    if bake_type == 'NORMAL':
        kwargs['normal_space'] = normal_space
    bpy.ops.object.bake(**kwargs)
    nt.nodes.remove(tex)


def bake_textures(obj, base_name, size=1024, albedo_kwargs=None, normal_kwargs=None):
    ensure_dirs()
    albedo_kwargs = albedo_kwargs or {}
    normal_kwargs = normal_kwargs or {}

    alb_path = os.path.join(ART_DIR, base_name + "_BaseColor.png")
    nrm_path = os.path.join(ART_DIR, base_name + "_Normal.png")

    img_a = bpy.data.images.new(base_name + "_BaseColor", size, size, alpha=False)
    img_a.colorspace_settings.name = 'sRGB'
    mat_a = build_bake_material(obj, base_name, **albedo_kwargs)
    _bake_to_image(obj, mat_a, img_a, 'EMIT', samples=12)
    img_a.filepath_raw = alb_path
    img_a.file_format = 'PNG'
    img_a.save()

    img_n = bpy.data.images.new(base_name + "_Normal", size, size, alpha=False,
                                is_data=True)
    img_n.colorspace_settings.name = 'Non-Color'
    mat_n = build_normal_bake_material(obj, base_name, **normal_kwargs)
    _bake_to_image(obj, mat_n, img_n, 'NORMAL', samples=1)
    img_n.filepath_raw = nrm_path
    img_n.file_format = 'PNG'
    img_n.save()

    # final shipping material: baked maps + vertex colours retained on the mesh
    mat = _new_material(base_name)
    nt = mat.node_tree
    bsdf = nt.nodes.get('Principled BSDF')
    out = nt.nodes.get('Material Output')
    tex_a = nt.nodes.new('ShaderNodeTexImage')
    tex_a.image = img_a
    tex_a.location = (-600, 200)
    tex_n = nt.nodes.new('ShaderNodeTexImage')
    tex_n.image = img_n
    tex_n.location = (-600, -200)
    nmap = nt.nodes.new('ShaderNodeNormalMap')
    nmap.location = (-300, -200)
    nmap.inputs['Strength'].default_value = 0.85
    nt.links.new(tex_a.outputs['Color'], bsdf.inputs['Base Color'])
    nt.links.new(tex_n.outputs['Color'], nmap.inputs['Color'])
    nt.links.new(nmap.outputs['Normal'], bsdf.inputs['Normal'])
    bsdf.inputs['Roughness'].default_value = 0.62
    bsdf.inputs['Metallic'].default_value = 0.0
    if out:
        nt.links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    obj.data.materials.clear()
    obj.data.materials.append(mat)

    for m in (mat_a, mat_n):
        bpy.data.materials.remove(m)
    return {
        'baseColor': os.path.relpath(alb_path, REPO_ROOT).replace('\\', '/'),
        'normal': os.path.relpath(nrm_path, REPO_ROOT).replace('\\', '/'),
    }


# ----------------------------------------------------------------------------
# sizing / grounding
# ----------------------------------------------------------------------------

def bbox(obj):
    vs = [Vector(v.co) for v in obj.data.vertices]
    if not vs:
        return Vector((0, 0, 0)), Vector((0, 0, 0))
    mn = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    mx = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    return mn, mx


def fit_size(obj, target, axis='z', ground=True, center_xy=True):
    """Scale to exactly hit the authored size from the design table, drop feet to
    z = 0 and centre on the origin in X/Y. Mesh data is edited directly so the
    object transform stays identity."""
    mn, mx = bbox(obj)
    size = mx - mn
    cur = {'x': size.x, 'y': size.y, 'z': size.z}[axis]
    s = target / cur if cur > 1e-9 else 1.0
    for v in obj.data.vertices:
        v.co = Vector(v.co) * s
    mn, mx = bbox(obj)
    off = Vector((0, 0, 0))
    if ground:
        off.z = -mn.z
    if center_xy:
        off.x = -(mn.x + mx.x) * 0.5
        off.y = -(mn.y + mx.y) * 0.5
    if off.length > 1e-9:
        for v in obj.data.vertices:
            v.co = Vector(v.co) + off
    obj.data.update()
    return s, off


# ----------------------------------------------------------------------------
# validation
# ----------------------------------------------------------------------------

class ValidationError(Exception):
    pass


def validate(obj, arm, tri_min=2000, tri_max=8000, strict=True):
    report = {}
    problems = []

    tris = tri_count(obj)
    report['tris'] = tris
    if tris < tri_min:
        problems.append("tri count %d below budget %d" % (tris, tri_min))
    if tris > tri_max:
        problems.append("tri count %d above budget %d" % (tris, tri_max))

    report['quad_ratio'] = round(quad_ratio(obj), 3)

    me = obj.data
    ngons = sum(1 for p in me.polygons if len(p.vertices) > 4)
    report['ngons'] = ngons
    if ngons:
        problems.append("%d n-gons" % ngons)

    bm = bmesh.new()
    bm.from_mesh(me)
    loose_v = sum(1 for v in bm.verts if not v.link_faces)
    loose_e = sum(1 for e in bm.edges if not e.link_faces)
    zero_a = sum(1 for f in bm.faces if f.calc_area() < 1e-10)
    boundary = sum(1 for e in bm.edges if len(e.link_faces) == 1)
    # signed volume tells us whether normals point outward overall
    vol = 0.0
    for f in bm.faces:
        vs = [v.co for v in f.verts]
        for i in range(1, len(vs) - 1):
            a, b, c = vs[0], vs[i], vs[i + 1]
            vol += a.dot(b.cross(c)) / 6.0
    bm.free()
    report['loose_verts'] = loose_v
    report['loose_edges'] = loose_e
    report['zero_area_faces'] = zero_a
    report['boundary_edges'] = boundary
    report['signed_volume'] = round(vol, 6)
    if loose_v or loose_e:
        problems.append("loose geometry: %d verts / %d edges" % (loose_v, loose_e))
    if zero_a:
        problems.append("%d zero-area faces" % zero_a)
    if boundary:
        problems.append("%d boundary (hole) edges" % boundary)
    if vol <= 0.0:
        problems.append("signed volume %.5f <= 0 - normals are inverted" % vol)

    if not me.uv_layers:
        problems.append("no UV layer")
    else:
        u0, v0, u1, v1 = uv_stats(obj)
        report['uv_bounds'] = [round(x, 4) for x in (u0, v0, u1, v1)]
        if u0 < -0.001 or v0 < -0.001 or u1 > 1.001 or v1 > 1.001:
            problems.append("UVs outside 0..1: %s" % report['uv_bounds'])

    for o in (obj, arm):
        if o is None:
            continue
        if Vector(o.location).length > 1e-6:
            problems.append("%s location not applied" % o.name)
        if any(abs(a) > 1e-6 for a in o.rotation_euler):
            problems.append("%s rotation not applied" % o.name)
        if any(abs(s - 1.0) > 1e-6 for s in o.scale):
            problems.append("%s scale not applied" % o.name)

    anchors = {c.name for c in bpy.data.objects if c.type == 'EMPTY'}
    missing = [a for a in ("Anchor_Head", "Anchor_Body", "Anchor_Muzzle")
               if a not in anchors]
    report['anchors'] = sorted(a for a in anchors if a.startswith("Anchor_"))
    if missing:
        problems.append("missing anchors: %s" % missing)

    if arm is not None:
        bones = [b.name for b in arm.data.bones]
        report['bones'] = len(bones)
        if 'Root' not in bones:
            problems.append("no Root bone")
        # weighting sanity: every deform bone should own some weight, and no
        # vertex should be 100% on one bone across a whole limb
        vg_names = {g.name for g in obj.vertex_groups}
        unused = [b for b in bones if b not in vg_names and b != 'Root']
        if unused:
            report['bones_without_weights'] = unused
        rigid = 0
        for v in obj.data.vertices:
            infl = [g for g in v.groups if g.weight > 0.001]
            if len(infl) <= 1:
                rigid += 1
        report['single_influence_verts'] = rigid
        report['single_influence_pct'] = round(100.0 * rigid / max(1, len(obj.data.vertices)), 1)

    report['problems'] = problems
    if problems and strict:
        raise ValidationError("%s: %s" % (obj.name, "; ".join(problems)))
    return report


# ----------------------------------------------------------------------------
# export
# ----------------------------------------------------------------------------

FBX_KWARGS = dict(
    use_selection=True,
    apply_unit_scale=True,
    global_scale=1.0,
    apply_scale_options='FBX_SCALE_NONE',
    use_space_transform=True,
    bake_space_transform=False,
    object_types={'ARMATURE', 'MESH', 'EMPTY'},
    use_mesh_modifiers=True,
    mesh_smooth_type='FACE',
    use_subsurf=False,
    use_mesh_edges=False,
    use_tspace=True,
    colors_type='SRGB',
    use_custom_props=False,
    add_leaf_bones=False,
    primary_bone_axis='Y',
    secondary_bone_axis='X',
    armature_nodetype='NULL',
    bake_anim=True,
    bake_anim_use_all_bones=True,
    bake_anim_use_nla_strips=False,
    bake_anim_use_all_actions=True,
    bake_anim_force_startend_keying=True,
    bake_anim_step=1.0,
    bake_anim_simplify_factor=0.0,
    path_mode='STRIP',
    embed_textures=False,
    axis_forward='-Y',
    axis_up='Z',
)


def export_fbx(objects, filepath, bake_anim=True):
    """Single enforced export path. Contract: -Y forward / Z up, metres, applied
    transforms, all actions baked as separate takes."""
    ensure_dirs()
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    kwargs = dict(FBX_KWARGS)
    kwargs['bake_anim'] = bake_anim
    if not bake_anim:
        kwargs['bake_anim_use_all_actions'] = False
    bpy.ops.export_scene.fbx(filepath=filepath, **kwargs)
    return filepath


def make_lod(obj, arm, ratio, name):
    """LOD1 for the heavy creatures: collapse-decimate a copy that keeps the same
    armature and vertex groups."""
    lod = obj.copy()
    lod.data = obj.data.copy()
    lod.name = name
    lod.data.name = name
    link(lod)
    activate(lod)
    if lod.data.shape_keys:
        lod.shape_key_clear()
    m = lod.modifiers.new("Decimate", 'DECIMATE')
    m.decimate_type = 'COLLAPSE'
    m.ratio = ratio
    m.use_collapse_triangulate = False
    apply_modifier(lod, m.name)
    return lod


def write_json(path, data):
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(data, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    return path
