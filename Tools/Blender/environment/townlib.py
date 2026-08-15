"""
townlib -- building shells with real wall thickness and real openings.

Why this module exists
----------------------
The first town pass built a house as a zero-thickness extruded footprint with
window and door boxes glued onto the surface.  Seen in engine from two metres,
that produced every defect at once: frames sitting proud of an unbroken wall,
beams running straight across the windows, z-fighting wherever two slabs shared
a depth, a door lying flat on the ground, corner posts missing the corners, a
roof overhang with no underside, and -- because the extrusion was never capped
-- no rear wall at all.

Those are not seven bugs.  They are one bug: the building was a pile of slabs
rather than a solid with holes in it.  So this module builds the solid.

The rules it enforces
---------------------
* A wall is a SHELL: an outer skin, an inner skin offset by a real thickness
  (0.20-0.30 m), a top plate and a bottom plate, all welded into one closed
  volume.  There is no orientation you can look from and see inside.

* An opening is a HOLE.  The wall skins are decomposed on the opening's own
  cut lines and the cells inside the opening are simply never emitted, so the
  hole is in the geometry, not implied by a frame.  The outer and inner rims
  are joined by four reveal faces, which is what gives a window depth.

* Openings are cut BEFORE anything is decorated, and `Shell.openings` is then
  the authority every later pass consults: beams stop at openings, sills and
  lintels are placed from the opening's own rectangle, and nothing is allowed
  to be positioned by eye.

* Anything applied to a surface OVERLAPS it by a centimetre or more.  Two
  surfaces at the same depth are the definition of z-fighting; a trim board
  that sinks 15 mm into the plaster cannot fight with it.

* Every component is a closed solid.  Components may interpenetrate -- a
  chimney passing through a roof is fine -- but none of them may be a shell
  with a missing side, because that is the failure that renders perfectly from
  the front.
"""

import math

import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

import envlib as E

WELD = 1e-4


def key(p):
    return (round(p[0] / WELD) * WELD, round(p[1] / WELD) * WELD,
            round(p[2] / WELD) * WELD)


class VertCache:
    """Shared vertex pool so adjacent wall panels weld at the corners instead
    of leaving a seam the light gets through."""

    def __init__(self, bm):
        self.bm = bm
        self.map = {}

    def get(self, p):
        k = key(p)
        v = self.map.get(k)
        if v is None:
            v = self.bm.verts.new(k)
            self.map[k] = v
        return v


def quad(bm, cache, pts, mat, smooth=False):
    vs = [cache.get(p) for p in pts]
    uniq = []
    for v in vs:
        if not uniq or uniq[-1] is not v:
            uniq.append(v)
    if len(uniq) > 2 and uniq[0] is uniq[-1]:
        uniq.pop()
    if len(uniq) < 3:
        return None
    try:
        f = bm.faces.new(uniq)
    except ValueError:
        return None
    f.material_index = mat
    f.smooth = smooth
    return f


def poly_area2(pts):
    a = 0.0
    n = len(pts)
    for i in range(n):
        x0, y0 = pts[i]
        x1, y1 = pts[(i + 1) % n]
        a += x0 * y1 - x1 * y0
    return 0.5 * a


def ccw(poly):
    return list(poly) if poly_area2(poly) > 0 else list(reversed(poly))


def offset_in(poly, d):
    """Offset a CCW footprint inward by d, re-intersecting the corners."""
    p = ccw(poly)
    n = len(p)
    lines = []
    for i in range(n):
        ax, ay = p[i]
        bx, by = p[(i + 1) % n]
        dx, dy = bx - ax, by - ay
        L = math.hypot(dx, dy) or 1.0
        nx, ny = -dy / L, dx / L        # inward normal of a CCW polygon
        lines.append((ax + nx * d, ay + ny * d, dx / L, dy / L))
    out = []
    for i in range(n):
        px, py, ux, uy = lines[i - 1]
        qx, qy, vx, vy = lines[i]
        den = ux * vy - uy * vx
        if abs(den) < 1e-7:
            out.append((qx, qy))
            continue
        t = ((qx - px) * vy - (qy - py) * vx) / den
        out.append((px + ux * t, py + uy * t))
    return out


class Opening:
    """A hole in one wall segment, in that segment's own coordinates.

    s runs 0-1 along the segment, z is absolute height.  Everything that is
    later placed in or around this opening -- frame, glazing, sill, lintel,
    the beams that have to stop short of it -- reads its numbers from here, so
    nothing can drift out of alignment with the hole it belongs to.
    """

    def __init__(self, seg, s0, s1, z0, z1, kind="window"):
        self.seg = seg
        self.s0, self.s1 = min(s0, s1), max(s0, s1)
        self.z0, self.z1 = min(z0, z1), max(z0, z1)
        self.kind = kind
        self.centre = None      # world centre of the outer rim, filled by build
        self.normal = None      # outward wall normal
        self.right = None       # along-wall direction
        self.width = 0.0
        self.height = 0.0


class Shell:
    """A closed building shell: outer skin, inner skin, top and bottom plates,
    with every opening cut clean through and its reveal built."""

    def __init__(self, bm, poly, z0, height, thickness=0.24,
                 mat_out=0, mat_in=0, cap_top=True, cap_bottom=True):
        self.bm = bm
        self.cache = VertCache(bm)
        self.poly = ccw(poly)
        self.inner = offset_in(self.poly, thickness)
        self.z0 = z0
        self.z1 = z0 + height
        self.thickness = thickness
        self.mat_out = mat_out
        self.mat_in = mat_in
        self.cap_top = cap_top
        self.cap_bottom = cap_bottom
        self.openings = []
        self.faces = []

    # -- authoring ---------------------------------------------------------

    def n_segments(self):
        return len(self.poly)

    def seg_length(self, i):
        ax, ay = self.poly[i]
        bx, by = self.poly[(i + 1) % len(self.poly)]
        return math.hypot(bx - ax, by - ay)

    def seg_frame(self, i):
        """(origin, along, outward normal) of segment i, all world space."""
        ax, ay = self.poly[i]
        bx, by = self.poly[(i + 1) % len(self.poly)]
        d = Vector((bx - ax, by - ay, 0.0))
        L = d.length or 1.0
        d = d / L
        n = Vector((d.y, -d.x, 0.0))     # outward for a CCW footprint
        return Vector((ax, ay, 0.0)), d, n

    def add_opening(self, seg, width, height, sill_z, centre_s=0.5,
                    kind="window"):
        """Cut an opening `width` wide and `height` tall, its base at sill_z,
        centred at centre_s along segment `seg`."""
        L = self.seg_length(seg)
        # An opening wider than the wall facet it is cut into does not fail
        # loudly: the cell decomposition simply emits panel cells outside the
        # segment and deletes the whole facet, and the shell quietly loses a
        # wall. That is how the Poke Lab's 1.85 m entrance ended up cutting a
        # 1.33 m drum facet clean away. Catch it here instead.
        if width > L - 0.12:
            raise ValueError(
                "opening %.2f m wide does not fit segment %d of this shell "
                "(%.2f m). Widen the facet, or split the opening." %
                (width, seg, L))
        half = (width * 0.5) / L
        # An opening that runs exactly to the shell's own base leaves the
        # bottom rim of the wall open along the doorway: there is no wall face
        # for the floor plate's outer edge to meet, and the jamb reveal lands
        # on the plate's division line making it non-manifold. A door has a
        # threshold in reality for the same reason it needs one here, so the
        # sill is lifted clear rather than the topology being special-cased.
        sill_z = max(sill_z, self.z0 + 0.05)
        o = Opening(seg, centre_s - half, centre_s + half,
                    sill_z, sill_z + height, kind)
        self.openings.append(o)
        return o

    # -- geometry ----------------------------------------------------------

    def _outer(self, seg, s, z):
        ax, ay = self.poly[seg]
        bx, by = self.poly[(seg + 1) % len(self.poly)]
        return (ax + (bx - ax) * s, ay + (by - ay) * s, z)

    def _inner(self, seg, s, z):
        ax, ay = self.inner[seg]
        bx, by = self.inner[(seg + 1) % len(self.inner)]
        return (ax + (bx - ax) * s, ay + (by - ay) * s, z)

    def _zcuts(self):
        """Horizontal cut lines, shared by EVERY panel in the shell.

        They have to be global.  With per-segment cuts, a wall carrying a door
        is split at the door head while the wall next to it is not, so the two
        panels meet at the corner with a T-junction: one side has an edge from
        0 to 2.05 to 2.55, the other a single edge from 0 to 2.55.  That is a
        crack, and it is exactly the sort of thing that renders fine and then
        shows daylight when the camera moves.
        """
        cuts = {self.z0, self.z1}
        for o in self.openings:
            cuts.add(o.z0)
            cuts.add(o.z1)
        return sorted(c for c in cuts if self.z0 - 1e-6 <= c <= self.z1 + 1e-6)

    def _panel(self, seg, pt, mat, flip):
        """One wall skin, decomposed on the shell's cut lines."""
        ops = [o for o in self.openings if o.seg == seg]
        scuts = sorted({0.0, 1.0} | {o.s0 for o in ops} | {o.s1 for o in ops})
        zcuts = self._zcuts()
        made = []
        for i in range(len(scuts) - 1):
            for j in range(len(zcuts) - 1):
                sm = (scuts[i] + scuts[i + 1]) * 0.5
                zm = (zcuts[j] + zcuts[j + 1]) * 0.5
                if any(o.s0 < sm < o.s1 and o.z0 < zm < o.z1 for o in ops):
                    continue        # this cell is the hole
                a = pt(seg, scuts[i], zcuts[j])
                b = pt(seg, scuts[i + 1], zcuts[j])
                c = pt(seg, scuts[i + 1], zcuts[j + 1])
                d = pt(seg, scuts[i], zcuts[j + 1])
                ring = [a, b, c, d] if not flip else [d, c, b, a]
                f = quad(self.bm, self.cache, ring, mat)
                if f:
                    made.append(f)
        return made, scuts

    def build(self):
        bm = self.bm
        n = len(self.poly)
        for seg in range(n):
            out_faces, scuts = self._panel(seg, self._outer, self.mat_out,
                                           False)
            in_faces, _ = self._panel(seg, self._inner, self.mat_in, True)
            self.faces += out_faces + in_faces
            # top and bottom plates, subdivided on the same cuts so no
            # T-junctions appear along the wall head
            for (z, flip, cap) in ((self.z1, False, self.cap_top),
                                   (self.z0, True, self.cap_bottom)):
                if not cap:
                    continue
                for i in range(len(scuts) - 1):
                    a = self._outer(seg, scuts[i], z)
                    b = self._outer(seg, scuts[i + 1], z)
                    c = self._inner(seg, scuts[i + 1], z)
                    d = self._inner(seg, scuts[i], z)
                    ring = [a, b, c, d] if not flip else [d, c, b, a]
                    f = quad(bm, self.cache, ring, self.mat_out)
                    if f:
                        self.faces.append(f)
            # reveals
            for o in (x for x in self.openings if x.seg == seg):
                self._reveal(o)
        self._record_openings()
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        return self.faces

    def _reveal(self, o):
        """The four faces joining the outer rim of a hole to the inner rim.

        This is the whole point of a wall having thickness: without it a window
        is a hole in a sheet of paper and reads as a sticker.
        """
        # The reveal has to be cut on the SAME lines as the panel around it.
        # The panel's hole rim is subdivided by every global z-cut and by every
        # s-cut on this wall, so a reveal built as four whole quads meets it at
        # a T-junction on each intermediate cut -- an invisible crack that the
        # closed-hull check catches and a render does not.
        zc = [z for z in self._zcuts() if o.z0 - 1e-9 < z < o.z1 + 1e-9]
        zc = sorted({o.z0, o.z1} | set(zc))
        ops = [x for x in self.openings if x.seg == o.seg]
        sc = sorted({o.s0, o.s1} |
                    {v for x in ops for v in (x.s0, x.s1)
                     if o.s0 < v < o.s1})
        # sill (skipped when the opening sits on the shell's own base plate,
        # which already is that quad -- a door threshold)
        if not (abs(o.z0 - self.z0) < 1e-6 and self.cap_bottom):
            for i in range(len(sc) - 1):
                self._rev_quad((sc[i], o.z0), (sc[i + 1], o.z0), o.seg, True)
        if not (abs(o.z1 - self.z1) < 1e-6 and self.cap_top):
            for i in range(len(sc) - 1):
                self._rev_quad((sc[i + 1], o.z1), (sc[i], o.z1), o.seg, True)
        for i in range(len(zc) - 1):
            self._rev_quad((o.s1, zc[i]), (o.s1, zc[i + 1]), o.seg, True)
            self._rev_quad((o.s0, zc[i + 1]), (o.s0, zc[i]), o.seg, True)

    def _rev_quad(self, a, b, seg, _):
        f = quad(self.bm, self.cache,
                 [self._outer(seg, a[0], a[1]), self._inner(seg, a[0], a[1]),
                  self._inner(seg, b[0], b[1]), self._outer(seg, b[0], b[1])],
                 self.mat_in)
        if f:
            self.faces.append(f)

    def _record_openings(self):
        for o in self.openings:
            org, d, nrm = self.seg_frame(o.seg)
            L = self.seg_length(o.seg)
            sc = (o.s0 + o.s1) * 0.5
            o.centre = org + d * (sc * L) + Vector((0, 0, (o.z0 + o.z1) * 0.5))
            o.normal = nrm
            o.right = d
            o.width = (o.s1 - o.s0) * L
            o.height = o.z1 - o.z0

    # -- verification ------------------------------------------------------

    def assert_openings(self, tol=0.02):
        """Fire a ray through every opening and confirm it goes through.

        Called on the bare shell, before a single frame or pane is placed, so
        there is nothing for the ray to hit but wall.  If an opening was never
        actually cut -- the original bug -- the ray hits the outer skin at
        essentially zero distance and this raises.
        """
        bm = self.bm
        bm.faces.ensure_lookup_table()
        tree = BVHTree.FromBMesh(bm)
        problems = []
        lead = 0.45
        span = lead + self.thickness + 0.25
        for o in self.openings:
            n = o.normal
            # 1. the wall band in front of the opening must be empty. Casting
            #    the whole way through the building is not a test: the ray can
            #    leave by a different opening and "miss", or be stopped by the
            #    far wall and look like a pass.
            start = o.centre + n * lead
            hit = tree.ray_cast(start, -n, span)
            if hit[0] is not None:
                problems.append(
                    "%s at %s: wall not pierced -- the opening centre is solid "
                    "%.3f m in" % (o.kind, tuple(round(v, 2) for v in o.centre),
                                   (hit[0] - start).length - lead))
                continue
            # 2. and the wall must actually be there beside it, or the "hole"
            #    is just a missing wall.
            side = o.centre + o.right * (o.width * 0.5 + 0.12)
            beside = tree.ray_cast(side + n * lead, -n, span)
            if beside[0] is None:
                problems.append(
                    "%s at %s: no wall beside the opening -- this is a gap, "
                    "not a window" % (o.kind,
                                      tuple(round(v, 2) for v in o.centre)))
        return problems


# --------------------------------------------------------------------------
# solids.  Everything applied to a shell is one of these, and every one of them
# is closed.
# --------------------------------------------------------------------------

def solid_box(bm, centre, size, mat, rot_z=0.0, smooth=False):
    cx, cy, cz = centre
    sx, sy, sz = size[0] * .5, size[1] * .5, size[2] * .5
    ca, sa = math.cos(rot_z), math.sin(rot_z)
    vs = []
    for (dz, dy, dx) in ((-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1),
                         (1, -1, -1), (1, -1, 1), (1, 1, 1), (1, 1, -1)):
        x, y = dx * sx, dy * sy
        vs.append(bm.verts.new((cx + x * ca - y * sa, cy + x * sa + y * ca,
                                cz + dz * sz)))
    faces = []
    for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
              (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
        f = bm.faces.new([vs[i] for i in q])
        f.material_index = mat
        f.smooth = smooth
        faces.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def solid_from_quad(bm, corners, thickness, mat, up=None):
    """Give a planar quad real thickness along its own normal.

    This is what a roof pitch is built from.  A pitch authored as a single quad
    has no underside, so from below the overhang is a zero-thickness card --
    exactly the defect reported on the eaves.  Extruding it gives fascia at the
    eave, a verge board at the gable and a soffit under the overhang, all for
    free and all correctly aligned, because they are the same solid.
    """
    pts = [Vector(c) for c in corners]
    nrm = (pts[1] - pts[0]).cross(pts[2] - pts[0])
    if nrm.length < 1e-9:
        return []
    nrm.normalize()
    if up is not None and nrm.dot(Vector(up)) < 0:
        nrm = -nrm
    top = [p for p in pts]
    bot = [p - nrm * thickness for p in pts]
    vt = [bm.verts.new(p) for p in top]
    vb = [bm.verts.new(p) for p in bot]
    faces = [bm.faces.new(vt), bm.faces.new(list(reversed(vb)))]
    for i in range(4):
        j = (i + 1) % 4
        faces.append(bm.faces.new((vt[i], vt[j], vb[j], vb[i])))
    for f in faces:
        f.material_index = mat
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def solid_prism(bm, poly, z0, z1, mat, smooth=False):
    """Closed vertical prism from a footprint -- both caps, always."""
    p = ccw(poly)
    bot = [bm.verts.new((x, y, z0)) for (x, y) in p]
    top = [bm.verts.new((x, y, z1)) for (x, y) in p]
    faces = []
    n = len(p)
    for i in range(n):
        j = (i + 1) % n
        faces.append(bm.faces.new((bot[i], bot[j], top[j], top[i])))
    faces.append(bm.faces.new(top))
    faces.append(bm.faces.new(list(reversed(bot))))
    for f in faces:
        f.material_index = mat
        f.smooth = smooth
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def gable_roof(bm, shell_poly, eave_z, ridge_rise, overhang, thickness,
               mat_roof, mat_trim, ridge_along_x=True, gable_mat=None):
    """Two thick pitches, two closed gable-end walls and a ridge cap.

    Sized from the footprint it sits on rather than from loose numbers, so the
    eave line and the wall it overhangs cannot drift apart.
    """
    xs = [p[0] for p in shell_poly]
    ys = [p[1] for p in shell_poly]
    x0, x1 = min(xs), max(xs)
    y0, y1 = min(ys), max(ys)
    faces = []
    ridge_z = eave_z + ridge_rise
    if ridge_along_x:
        rx0, rx1 = x0 - overhang, x1 + overhang
        for sy in (-1, 1):
            ey = (y1 + overhang) if sy > 0 else (y0 - overhang)
            faces += solid_from_quad(bm, [
                (rx0, ey, eave_z), (rx1, ey, eave_z),
                (rx1, (y0 + y1) * 0.5, ridge_z),
                (rx0, (y0 + y1) * 0.5, ridge_z)], thickness, mat_roof,
                up=(0, 0, 1))
        # gable ends: closed wedges, so the roof is not open at the ends
        for sx, ex in ((-1, x0), (1, x1)):
            tri = [(ex, y0, eave_z), (ex, y1, eave_z),
                   (ex, (y0 + y1) * 0.5, ridge_z)]
            vs = [bm.verts.new(p) for p in tri]
            vb = [bm.verts.new((p[0] - sx * 0.14, p[1], p[2])) for p in tri]
            fs = [bm.faces.new(vs), bm.faces.new(list(reversed(vb)))]
            for i in range(3):
                j = (i + 1) % 3
                fs.append(bm.faces.new((vs[i], vs[j], vb[j], vb[i])))
            for f in fs:
                f.material_index = gable_mat if gable_mat is not None else mat_trim
                f.smooth = False
            bmesh.ops.recalc_face_normals(bm, faces=fs)
            faces += fs
        faces += solid_box(bm, ((x0 + x1) * 0.5, (y0 + y1) * 0.5,
                                ridge_z + 0.045),
                           (x1 - x0 + overhang * 2 + 0.06, 0.20, 0.11),
                           mat_trim)
    else:
        ry0, ry1 = y0 - overhang, y1 + overhang
        for sx in (-1, 1):
            ex = (x1 + overhang) if sx > 0 else (x0 - overhang)
            faces += solid_from_quad(bm, [
                (ex, ry0, eave_z), (ex, ry1, eave_z),
                ((x0 + x1) * 0.5, ry1, ridge_z),
                ((x0 + x1) * 0.5, ry0, ridge_z)], thickness, mat_roof,
                up=(0, 0, 1))
        for sy, ey in ((-1, y0), (1, y1)):
            tri = [(x0, ey, eave_z), (x1, ey, eave_z),
                   ((x0 + x1) * 0.5, ey, ridge_z)]
            vs = [bm.verts.new(p) for p in tri]
            vb = [bm.verts.new((p[0], p[1] - sy * 0.14, p[2])) for p in tri]
            fs = [bm.faces.new(vs), bm.faces.new(list(reversed(vb)))]
            for i in range(3):
                j = (i + 1) % 3
                fs.append(bm.faces.new((vs[i], vs[j], vb[j], vb[i])))
            for f in fs:
                f.material_index = gable_mat if gable_mat is not None else mat_trim
                f.smooth = False
            bmesh.ops.recalc_face_normals(bm, faces=fs)
            faces += fs
        faces += solid_box(bm, ((x0 + x1) * 0.5, (y0 + y1) * 0.5,
                                ridge_z + 0.045),
                           (0.20, y1 - y0 + overhang * 2 + 0.06, 0.11),
                           mat_trim)
    return faces


# --------------------------------------------------------------------------
# opening furniture.  All of it positioned from the Opening, never by eye.
# --------------------------------------------------------------------------

EMBED = 0.018     # how far applied trim sinks into the surface it sits on


def window_furniture(bm, o, mat_frame, mat_glass, thickness,
                     mullion=True, sill=True, shutters=False,
                     mat_shutter=None):
    """Frame, glazing, sill and lintel, all set INSIDE the hole.

    The frame is a ring of four bars sitting in the reveal, one third of the
    wall thickness back from the outer face -- it cannot sit on the wall,
    because at that depth there is no wall, only the hole.
    """
    n, r = o.normal, o.right
    up = Vector((0, 0, 1))
    c = o.centre
    back = n * (thickness * 0.34)
    faces = []

    def plate(cc, ww, hh, dd, mat):
        vs = []
        for (a, b, s) in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                          (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            vs.append(bm.verts.new(cc + r * (a * ww * .5) + up * (b * hh * .5) +
                                   n * (s * dd * .5)))
        fs = []
        for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                  (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
            f = bm.faces.new([vs[i] for i in q])
            f.material_index = mat
            f.smooth = False
            fs.append(f)
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        faces.extend(fs)
        return fs

    fw = 0.085
    # frame ring, inset into the reveal so the bars overlap the jambs
    plate(c - back + up * ((o.height - fw) * 0.5), o.width + 0.02, fw,
          thickness * 0.5, mat_frame)
    plate(c - back - up * ((o.height - fw) * 0.5), o.width + 0.02, fw,
          thickness * 0.5, mat_frame)
    for sx in (-1, 1):
        plate(c - back + r * (sx * (o.width - fw) * 0.5), fw,
              o.height - fw * 2 + 0.02, thickness * 0.5, mat_frame)
    # glazing, behind the frame
    plate(c - back - n * 0.012, o.width - fw * 1.6, o.height - fw * 1.6, 0.02,
          mat_glass)
    if mullion:
        plate(c - back - n * 0.004, 0.036, o.height - fw * 1.6, 0.05,
              mat_frame)
        plate(c - back - n * 0.004, o.width - fw * 1.6, 0.036, 0.05, mat_frame)
    if sill:
        # sits ON the wall, so it sinks into it by EMBED and projects outward
        plate(c - up * (o.height * 0.5 + 0.035) + n * (0.10 - EMBED),
              o.width + 0.30, 0.075, 0.22, mat_frame)
        plate(c + up * (o.height * 0.5 + 0.045) + n * (0.06 - EMBED),
              o.width + 0.26, 0.085, 0.16, mat_frame)
    if shutters:
        m = mat_shutter if mat_shutter is not None else mat_frame
        for sx in (-1, 1):
            plate(c + r * (sx * (o.width * 0.5 + 0.135)) + n * (0.035 - EMBED),
                  0.26, o.height - 0.02, 0.06, m)
    return faces


def door_furniture(bm, o, mat_leaf, mat_frame, mat_step, thickness,
                   ground_z=0.0):
    """Leaf inside the opening, architrave in the reveal, threshold on the
    ground.  The step is a solid under the opening, not a plate on the lawn."""
    n, r = o.normal, o.right
    up = Vector((0, 0, 1))
    c = o.centre
    faces = []

    def plate(cc, ww, hh, dd, mat):
        vs = []
        for (a, b, s) in ((-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                          (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)):
            vs.append(bm.verts.new(cc + r * (a * ww * .5) + up * (b * hh * .5) +
                                   n * (s * dd * .5)))
        fs = []
        for q in ((0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1),
                  (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)):
            f = bm.faces.new([vs[i] for i in q])
            f.material_index = mat
            f.smooth = False
            fs.append(f)
        bmesh.ops.recalc_face_normals(bm, faces=fs)
        faces.extend(fs)
        return fs

    back = n * (thickness * 0.30)
    # the leaf fills the hole
    plate(c - back, o.width - 0.03, o.height - 0.02, 0.055, mat_leaf)
    # raised panels, proud of the leaf
    for dz in (-o.height * 0.20, o.height * 0.22):
        plate(c - back + up * dz - n * 0.030, o.width - 0.26,
              o.height * 0.30, 0.024, mat_leaf)
    # handle
    plate(c - back - n * 0.055 + r * (o.width * 0.30), 0.05, 0.05, 0.10,
          mat_frame)
    # architrave: three bars round the hole, sunk into the wall
    aw = 0.10
    plate(c + up * (o.height * 0.5 + aw * 0.5) + n * (0.045 - EMBED),
          o.width + aw * 2.4, aw, 0.09, mat_frame)
    for sx in (-1, 1):
        plate(c + r * (sx * (o.width * 0.5 + aw * 0.5)) + n * (0.045 - EMBED),
              aw, o.height + aw, 0.09, mat_frame)
    # Threshold: a solid step on the ground under the opening. The offset is
    # measured DOWN FROM THE OPENING CENTRE, not from the sill height, because
    # o.centre already carries the height -- getting that wrong put the step
    # half way up the doorway as a bar across the door, which is what the
    # close-range render caught.
    solid_box(bm, tuple(c + n * 0.30 - up * (o.height * 0.5 + 0.055)),
              (o.width + 0.42, 0.62, 0.13), mat_step)
    solid_box(bm, tuple(c + n * 0.52 - up * (o.height * 0.5 + 0.155)),
              (o.width + 0.66, 0.80, 0.11), mat_step)
    return faces


def beams_around(bm, shell, seg, z, thickness_z, depth, mat, margin=0.10,
                 inset=EMBED):
    """A horizontal timber band that STOPS at every opening on its wall.

    The reported defect was beams running straight through the windows: they
    were placed on the wall with no knowledge of what was already in it. This
    takes its runs from the shell's own opening list, so a beam physically
    cannot cross one.
    """
    L = shell.seg_length(seg)
    org, d, nrm = shell.seg_frame(seg)
    blocked = []
    for o in shell.openings:
        if o.seg != seg:
            continue
        if o.z0 - margin < z + thickness_z * 0.5 and \
                o.z1 + margin > z - thickness_z * 0.5:
            blocked.append((o.s0 * L - margin, o.s1 * L + margin))
    blocked.sort()
    runs = []
    cursor = 0.0
    for (a, b) in blocked:
        if a > cursor:
            runs.append((cursor, min(a, L)))
        cursor = max(cursor, b)
    if cursor < L:
        runs.append((cursor, L))
    faces = []
    for (a, b) in runs:
        if b - a < 0.12:
            continue
        mid = org + d * ((a + b) * 0.5) + Vector((0, 0, z)) + \
            nrm * (depth * 0.5 - inset)
        ang = math.atan2(d.y, d.x)
        faces += solid_box(bm, tuple(mid), (b - a, depth, thickness_z), mat,
                           rot_z=ang)
    return faces



def cart_wheel(bm, centre, r_out, r_in, width, mat_rim, mat_hub, seg=12,
               spokes=6):
    """A wheel lying in the XZ plane, turning about Y.

    solid_box only rotates about Z, so a rim assembled from boxes leaves every
    segment axis-aligned and the wheel reads as a ring of loose blocks. This
    builds the rim as a closed annulus and puts the spokes in the wheel's own
    plane, which is the only way it looks like a wheel.
    """
    cx, cy, cz = centre
    faces = []
    loops = []
    for r in (r_out, r_in):
        for sy in (-1, 1):
            ring = []
            for i in range(seg):
                a = 2.0 * math.pi * i / seg
                ring.append(bm.verts.new((cx + math.cos(a) * r,
                                          cy + sy * width * 0.5,
                                          cz + math.sin(a) * r)))
            loops.append(ring)
    o_lo, o_hi, i_lo, i_hi = loops

    def strip(a, b):
        for i in range(seg):
            j = (i + 1) % seg
            f = bm.faces.new((a[i], a[j], b[j], b[i]))
            f.material_index = mat_rim
            f.smooth = False
            faces.append(f)

    strip(o_lo, o_hi)          # tread
    strip(i_hi, i_lo)          # inner face of the rim
    strip(o_hi, i_hi)          # cheek
    strip(i_lo, o_lo)          # cheek
    bmesh.ops.recalc_face_normals(bm, faces=faces)

    hub_r = r_in * 0.42
    for k in range(spokes):
        a = math.pi * k / spokes
        dx, dz = math.cos(a), math.sin(a)
        px, pz = -dz, dx
        t = 0.035
        quad_pts = [(cx - dx * r_in + px * t, cy, cz - dz * r_in + pz * t),
                    (cx + dx * r_in + px * t, cy, cz + dz * r_in + pz * t),
                    (cx + dx * r_in - px * t, cy, cz + dz * r_in - pz * t),
                    (cx - dx * r_in - px * t, cy, cz - dz * r_in - pz * t)]
        faces += solid_from_quad(bm, quad_pts, width * 0.55, mat_rim,
                                 up=(0, 1, 0))
    faces += solid_box(bm, (cx, cy, cz), (hub_r * 2, width * 1.5, hub_r * 2),
                       mat_hub)
    return faces

def corner_posts(bm, poly, z0, z1, size, mat, embed=0.06):
    """Posts on the actual plan corners.

    The reported defect was corner posts offset from the wall edges, leaving a
    seam at every corner. They are placed on the footprint's own vertices here
    and oversized by `embed` so they bite into both walls instead of touching
    them.
    """
    faces = []
    p = ccw(poly)
    n = len(p)
    for i in range(n):
        ax, ay = p[i]
        # bisector of the two walls meeting here, so the post leans into the
        # corner rather than sticking out of it
        px, py = p[i - 1]
        qx, qy = p[(i + 1) % n]
        v1 = Vector((ax - px, ay - py, 0))
        v2 = Vector((qx - ax, qy - ay, 0))
        if v1.length < 1e-6 or v2.length < 1e-6:
            continue
        ang = math.atan2((v1.normalized() + v2.normalized()).y,
                         (v1.normalized() + v2.normalized()).x)
        faces += solid_box(bm, (ax, ay, (z0 + z1) * 0.5),
                           (size + embed, size + embed, z1 - z0), mat,
                           rot_z=ang)
    return faces
