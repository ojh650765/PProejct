"""
Props family: the eight capture balls (hero props), the healing machine, the
research terminal, and the handheld scanner.

The capture ball gets close-up screen time during captures, so it is built
properly rather than as a two-tone sphere: a true sphere of revolution, a
recessed equatorial seam channel, a button in a raised bezel with a chamfered
rim, a hinge pin on the back, and a clean spherical UV unwrap so the seam
falls on the geometric seam.

There are eight of them because there are eight of them in the game.
Assets/Game/Scripts/Battle/ItemCatalog.cs registers poke-ball, great-ball,
ultra-ball, net-ball, dusk-ball, quick-ball, timer-ball and master-ball, each
with its own catch behaviour, so the art has a fixed list to hit; BALLS below
is that list in the same order.

------------------------------------------------------------------------
Why the balls repaint themselves instead of using the shared bevel pass
------------------------------------------------------------------------
`bmesh.ops.bevel` gives every face it creates material_index 0, and slot 0 in
this family is ball_red.  Measured on the previous build: the shell left the
builder with 156 red / 156 white / 301 black / 72 button faces and came out of
`E.bevel_sharp` with 476 red -- 320 new faces, every one of them red, and all
of them on colour boundaries.  That is why the shipped ball had a red pinstripe
running along both lips of its black band and a red ring around its button.

A ball's colour is a pure function of position on the sphere, so the fix is to
bevel first and paint afterwards, from that function.  It also means a livery
can be an arbitrary lon/lat predicate rather than something that has to line up
with the revolve's topology, which is what makes the Ultra Ball's H and the
Net Ball's netting cost no triangles at all.

Orientation: the button faces Blender -Y.  Measured with fbx_probe, the export
maps Blender (x, y, z) to Unity (x, z, -y), so Blender -Y is Unity +Z -- the
kit's "models face +Z", and the same face the town buildings put their doors
on.
"""

import sys
import os
import math
import random

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh
from mathutils import Vector, Matrix

import envlib as E
import textures as T

FAM = "Props"
OUT = E.FAMILY_DIR[FAM]

BALL_RED, BALL_WHITE, BALL_BLACK, BALL_BUTTON = 0, 1, 2, 3
METAL, PLASTIC, SCREEN, RUBBER = 4, 5, 6, 7
TEAL, LIVERY, DESK, TUBE = 8, 9, 10, 11
PANEL, ORANGE, DARK, EMISSIVE = 12, 13, 14, 15

# The livery colours share atlas cell 9 as eight horizontal sub-bands, so
# their slot indices continue after the sixteen base cells. See PROPS_CELLS.
LIVERY_NAMES = ["blue", "yellow", "purple", "pink", "green", "cyan",
                "navy", "grey"]
LIV_BLUE, LIV_YELLOW, LIV_PURPLE, LIV_PINK = 16, 17, 18, 19
LIV_GREEN, LIV_CYAN, LIV_NAVY, LIV_GREY = 20, 21, 22, 23


def matset():
    extra = [("livery_%s" % n, LIVERY, (i, len(LIVERY_NAMES)),
              T.BALL_LIVERY[i]) for i, n in enumerate(LIVERY_NAMES)]
    return T.full_matset(FAM, extra)


def box(bm, centre, size, mat, rot_z=0.0, smooth=False):
    cx, cy, cz = centre
    sx, sy, sz = (size[0] * .5, size[1] * .5, size[2] * .5)
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


def revolve(bm, profile, sides, mat_fn, smooth=True, close_bottom=True,
            close_top=True):
    """profile: list of (r, z) from bottom to top.  mat_fn(i, r, z) -> material.
    Zero-radius entries collapse to a pole vertex, so a sphere ends cleanly."""
    rings = []
    for (r, z) in profile:
        if r < 1e-6:
            v = bm.verts.new((0, 0, z))
            rings.append([v] * sides)
        else:
            rings.append([bm.verts.new((math.cos(2 * math.pi * i / sides) * r,
                                        math.sin(2 * math.pi * i / sides) * r,
                                        z)) for i in range(sides)])
    faces = []
    for k in range(len(profile) - 1):
        r0, z0 = profile[k]
        r1, z1 = profile[k + 1]
        m = mat_fn(k, (r0 + r1) * .5, (z0 + z1) * .5)
        for i in range(sides):
            j = (i + 1) % sides
            quad = [rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]]
            uniq = []
            for v in quad:
                if v not in uniq:
                    uniq.append(v)
            if len(uniq) < 3:
                continue
            f = bm.faces.new(uniq)
            f.material_index = m
            f.smooth = smooth
            faces.append(f)
    if close_bottom and profile[0][0] > 1e-6:
        f = bm.faces.new(list(reversed(rings[0])))
        f.material_index = mat_fn(0, profile[0][0], profile[0][1])
        f.smooth = smooth
        faces.append(f)
    if close_top and profile[-1][0] > 1e-6:
        f = bm.faces.new(rings[-1])
        f.material_index = mat_fn(len(profile) - 2, profile[-1][0], profile[-1][1])
        f.smooth = smooth
        faces.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces, rings


# --------------------------------------------------------------------------
# hero props: the capture balls
# --------------------------------------------------------------------------

R_BALL = 0.055          # 5.5 cm radius, i.e. an object you could hold
SIDES = 28              # 12.9 deg per column; the livery stripes are whole
                        # numbers of columns wide so their edges stay crisp
BAND_HALF = 0.085       # half-height of the black band, as a fraction of R
LAT_STEPS = 10


def revolve_arc(bm, prof, sides, mat_fn, arc_deg=360.0, phase_deg=0.0,
                smooth=True, cap_ends=False):
    """Revolve a profile about +Z, optionally through part of a turn.

    `prof` is a list of (r, z) and may be a CLOSED loop -- outer surface out,
    inner surface back -- which is how the open ball's half shells get a real
    wall thickness and a rim instead of being a lid over nothing.  Zero-radius
    stations collapse to a pole vertex.

    `cap_ends` closes a partial revolve with the profile polygon at each end,
    so a half-button is a solid and not a scoop.
    """
    full = abs(arc_deg - 360.0) < 1e-6
    segs = sides if full else max(2, int(round(sides * arc_deg / 360.0)))
    cols = segs if full else segs + 1
    # A closed loop is written with its first station repeated at the end so
    # the caller can read it as a circuit. Building that station twice leaves
    # two coincident rings that only remove_doubles can join, and the end cap
    # of a partial revolve then comes out with a zero-length edge in it and
    # fails to close -- measured as 9 boundary edges per half button. Reuse
    # the first ring instead.
    closed = (len(prof) > 2 and
              abs(prof[0][0] - prof[-1][0]) < 1e-9 and
              abs(prof[0][1] - prof[-1][1]) < 1e-9)
    rings = []
    for idx, (r, z) in enumerate(prof):
        if closed and idx == len(prof) - 1:
            rings.append(rings[0])
            continue
        if r < 1e-6:
            v = bm.verts.new((0.0, 0.0, z))
            rings.append([v] * cols)
        else:
            ring = []
            for i in range(cols):
                a = math.radians(phase_deg + arc_deg * (i / float(segs)))
                ring.append(bm.verts.new((math.cos(a) * r,
                                          math.sin(a) * r, z)))
            rings.append(ring)
    faces = []
    for k in range(len(prof) - 1):
        r0, z0 = prof[k]
        r1, z1 = prof[k + 1]
        m = mat_fn(k, (r0 + r1) * .5, (z0 + z1) * .5)
        for i in range(segs):
            j = (i + 1) % cols
            quad = [rings[k][i], rings[k][j], rings[k + 1][j], rings[k + 1][i]]
            uniq = []
            for v in quad:
                if v not in uniq:
                    uniq.append(v)
            if len(uniq) < 3:
                continue
            f = bm.faces.new(uniq)
            f.material_index = m
            f.smooth = smooth
            faces.append(f)
    if cap_ends and not full:
        for col in (0, cols - 1):
            ring = []
            for k in range(len(prof)):
                v = rings[k][col]
                if v not in ring:
                    ring.append(v)
            if len(ring) >= 3:
                if col == 0:
                    ring.reverse()
                f = bm.faces.new(ring)
                f.material_index = mat_fn(0, prof[0][0], prof[0][1])
                f.smooth = False
                faces.append(f)
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def sphere_profile(R=R_BALL, lat_steps=LAT_STEPS, band_half=BAND_HALF,
                   z_lo=-1.0, z_hi=1.0):
    """Sphere stations from z_lo*R to z_hi*R with the seam channel spliced in.

    The channel is a shallow step, not the double lip the previous build had:
    that read as three stacked bands rather than one, and it was the pair of
    raised lips that the stray red bevel faces landed on.
    """
    prof = []
    for k in range(lat_steps + 1):
        a = math.pi * k / lat_steps
        prof.append((math.sin(a), -math.cos(a)))
    seam = [(0.9955, -band_half), (0.9640, -band_half * 0.72),
            (0.9600, 0.0),
            (0.9640, band_half * 0.72), (0.9955, band_half)]
    prof = [p for p in prof if abs(p[1]) > band_half * 1.05]
    prof = ([p for p in prof if p[1] < 0] + seam +
            [p for p in prof if p[1] > 0])
    prof.sort(key=lambda p: p[1])
    prof = [p for p in prof if z_lo - 1e-9 <= p[1] <= z_hi + 1e-9]
    return [(r * R, z * R) for (r, z) in prof]


# The button, given as a closed loop in (radius from the button axis, distance
# from the ball centre), both as fractions of R.  Expressing the second number
# radially rather than as a local height is what keeps the bezel proud of the
# shell all the way round instead of proud at the centre and buried at the rim.
BUTTON_LOOP = [
    (0.235, 0.870),     # skirt, buried in the shell
    (0.235, 0.988),     # bezel rim, proud
    (0.185, 0.992),     # bezel top face
    (0.185, 0.966),     # recess wall
    (0.150, 0.966),     # recess floor
    (0.150, 1.000),     # button barrel
    (0.105, 1.014),     # chamfer
    (0.000, 1.020),     # crown
    (0.000, 0.870),     # back down the axis
    (0.235, 0.870),     # underside, closing the loop
]


def button_assembly(bm, R, mat_ring, mat_face, arc_deg=360.0, phase_deg=0.0):
    """The button, built about +Z and swung onto -Y (the front)."""
    prof = [(a * R, b * R) for (a, b) in BUTTON_LOOP]

    def bmat(i, r, z):
        return mat_face if 4 <= i < 7 else mat_ring

    tmp = bmesh.new()
    revolve_arc(tmp, prof, 20, bmat, arc_deg=arc_deg, phase_deg=phase_deg,
                smooth=True, cap_ends=True)
    me = bpy.data.meshes.new("_btn")
    tmp.to_mesh(me)
    tmp.free()
    # local +Z -> world -Y, and local +Y -> world +Z, so the arc parameter
    # maps straight onto latitude: 0..180 deg is the upper half of the button.
    me.transform(Matrix.Rotation(math.radians(90), 4, 'X'))
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)


def hinge_pin(bm, R, mat, half=False, sign=1):
    """A short barrel across the back of the band. Half of the real ball's
    hinge is buried; the previous build's pair of octagonal bosses and a bar
    read as a bolt through the shell from every angle behind it."""
    y = R * 0.955
    E.bm_polytube(bm, [Vector((-R * 0.17, y, 0)), Vector((R * 0.17, y, 0))],
                  [R * 0.072, R * 0.072], 8, mat,
                  cap_start=True, cap_end=True, smooth=True)


def sph(R, lon_deg, lat_deg):
    """Point on the ball. lon 0 is the front (-Y, the button), lon grows
    towards +X; lat 0 is the band, +90 the top pole."""
    lo, la = math.radians(lon_deg), math.radians(lat_deg)
    return Vector((math.sin(lo) * math.cos(la),
                   -math.cos(lo) * math.cos(la),
                   math.sin(la))) * R


def lon_lat(co):
    v = Vector(co)
    L = v.length or 1e-9
    return (math.degrees(math.atan2(v.x, -v.y)),
            math.degrees(math.asin(max(-1.0, min(1.0, v.z / L)))))


def front_back_lon(lon):
    """Angular distance to the nearest of front (0) and back (180), so a
    marking authored once appears on both faces the way the real ones do."""
    a = abs(lon)
    return min(a, abs(180.0 - a))


MAX_RIBBON_STEP = 16.0   # degrees of arc between ribbon samples


def sphere_ribbon(bm, R, path, width_deg, mat, out=1.024, inn=0.950,
                  per_seg=1):
    """A raised marking that hugs the ball: a closed slab whose outer face
    stands 2.4% of R proud and whose inner face is buried 5% in, so it is a
    solid interpenetrating the shell rather than a decal floating over it.

    Used only where the shape is not aligned to the shell's own lat/lon grid
    -- the netting, the Master Ball's M and the Quick Ball's streaks.
    Everything else is painted, which is both crisper and free.

    Sampling is by arc length, not by a fixed count. A chord across `d`
    degrees of a sphere sags 1-cos(d/2) below the surface, so a net ring drawn
    with ten samples sags 4.9 % of R while standing only 2.4 % proud -- it
    sinks into the shell between samples and renders as a dashed line, which
    is exactly what the first build of the Net and Dusk balls did. At 16 deg
    the sag is 0.97 %, comfortably inside the 2.4 % the ribbon stands proud,
    and the ribbon stays out in the open all the way round. Halving the step
    again only doubles the triangle count for no visible gain -- the netted
    liveries were 400 triangles heavier for it.
    """
    pts = []
    for i in range(len(path) - 1):
        a, b = path[i], path[i + 1]
        span = math.hypot(((b[0] - a[0] + 180.0) % 360.0) - 180.0,
                          b[1] - a[1])
        n = max(per_seg, int(math.ceil(span / MAX_RIBBON_STEP)))
        for k in range(n):
            t = k / float(n)
            pts.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
    pts.append(path[-1])
    P = [sph(1.0, lo, la) for (lo, la) in pts]
    hw = math.radians(width_deg) * 0.5
    rails = ([], [])
    for i, p in enumerate(P):
        prv = P[max(0, i - 1)]
        nxt = P[min(len(P) - 1, i + 1)]
        t = nxt - prv
        if t.length < 1e-9:
            t = Vector((0, 0, 1))
        t.normalize()
        s = p.cross(t)
        if s.length < 1e-9:
            s = Vector((1, 0, 0))
        s.normalize()
        rails[0].append((p * math.cos(hw) + s * math.sin(hw)).normalized())
        rails[1].append((p * math.cos(hw) - s * math.sin(hw)).normalized())
    LO = [bm.verts.new(v * (R * out)) for v in rails[0]]
    RO = [bm.verts.new(v * (R * out)) for v in rails[1]]
    LI = [bm.verts.new(v * (R * inn)) for v in rails[0]]
    RI = [bm.verts.new(v * (R * inn)) for v in rails[1]]
    faces = []

    def face(vs):
        try:
            f = bm.faces.new(vs)
        except ValueError:
            return
        f.material_index = mat
        f.smooth = True
        faces.append(f)

    for i in range(len(LO) - 1):
        face((LO[i], RO[i], RO[i + 1], LO[i + 1]))
        face((LI[i + 1], RI[i + 1], RI[i], LI[i]))
        face((LO[i], LO[i + 1], LI[i + 1], LI[i]))
        face((RI[i], RI[i + 1], RO[i + 1], RO[i]))
    face((LO[0], LI[0], RI[0], RO[0]))
    face((RO[-1], RI[-1], LI[-1], LO[-1]))
    bmesh.ops.recalc_face_normals(bm, faces=faces)
    return faces


def sphere_stud(bm, R, lon_deg, lat_deg, size, mat):
    """A domed rivet on the shell -- the Master Ball's two side studs."""
    prof = [(0.0, 0.86), (size * 0.42, 0.88), (size * 0.78, 0.94),
            (size * 0.97, 1.01), (size * 1.00, 1.045),
            (size * 0.72, 1.075), (size * 0.40, 1.092), (0.0, 1.098)]
    tmp = bmesh.new()
    revolve_arc(tmp, [(a * R, b * R) for (a, b) in prof], 12,
                lambda i, r, z: mat, smooth=True)
    me = bpy.data.meshes.new("_stud")
    tmp.to_mesh(me)
    tmp.free()
    d = sph(1.0, lon_deg, lat_deg)
    rot = Vector((0, 0, 1)).rotation_difference(d).to_matrix().to_4x4()
    me.transform(rot)
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)


# --------------------------------------------------------------------------
# liveries
#
# `paint(lon, lat, default)` returns the slot for one face of the shell, given
# the face centre's longitude (0 = front) and latitude (0 = band).  The shell
# is 28 columns of 12.857 deg, so any stripe whose edges are placed on column
# boundaries comes out with a perfectly clean edge and costs nothing.
# --------------------------------------------------------------------------

COL = 360.0 / SIDES


def col_from_front(lon):
    """Which column of the shell this longitude is in, counted outwards from
    the front-back centre line: 0 for the pair of columns touching it, 1 for
    the next pair out, and so on.

    Liveries index columns rather than comparing angles because the front is a
    column BOUNDARY, not a column centre, and a `lon < 19.28` style test lands
    exactly on a column centre where floating point decides the answer. Indexing
    is also what guarantees a stripe edge falls on a mesh edge, which is what
    keeps it crisp with no geometry and no texture resolution behind it.
    """
    return int(round((front_back_lon(lon) - COL * 0.5) / COL))


def any_lon_col(lon, centres):
    """Same, but measured to the nearest of several meridians."""
    d = min(abs(((lon - c) + 180.0) % 360.0 - 180.0) for c in centres)
    return int(round((d - COL * 0.5) / COL))


def net_lines(bm, R, mat, hemi=1, meridians=6, lats=(26.0, 56.0),
              width=3.4, lat_lo=9.0, lat_hi=82.0, out=1.030, inn=0.970):
    """A netting web laid over one hemisphere of the shell.

    Painted netting was tried first and does not work: the shell is 28 columns
    and 5 latitude rows above the band, so the thinnest paintable line is 12.9
    deg wide, and the net came out covering more of the ball than the ball
    did. Thin raised ribbons cost about 340 triangles and give a net that
    reads as one at any distance.

    Everything stays strictly on its own side of the equator so the open
    variant can hand each half its own netting without cutting anything.
    """
    s = 1.0 if hemi >= 0 else -1.0
    for i in range(meridians):
        lon = -180.0 + 360.0 * i / meridians
        sphere_ribbon(bm, R, [(lon, s * lat_lo), (lon, s * lat_hi)], width,
                      mat, out=out, inn=inn)
    for la in lats:
        ring = [(-180.0 + 360.0 * k / 6.0, s * la) for k in range(7)]
        sphere_ribbon(bm, R, ring, width, mat, out=out, inn=inn)


class Livery:
    def __init__(self, key, item_id, top, bottom, band, button,
                 paint=None, marks=None, note=""):
        self.key = key
        self.item_id = item_id
        self.top = top
        self.bottom = bottom
        self.band = band
        self.button = button
        self.paint = paint
        # marks(bm, R, hemi): hemi 0 builds the lot, +1/-1 only the pieces
        # that lie wholly on that side of the split, for the open variant
        self.marks = marks or (lambda bm, R, hemi=0: None)
        self.note = note

    def shell_mat(self, lon, lat):
        if abs(lat) < math.degrees(math.asin(BAND_HALF)) * 1.06:
            base = self.band
        else:
            base = self.top if lat > 0 else self.bottom
        if self.paint is not None:
            m = self.paint(lon, lat, base)
            if m is not None:
                return m
        return base


def _great(lon, lat, base):
    """Blue top carrying two red accents, each edged in white, front and back.

    Columns 0 and 1 stay blue as a 52 deg centre line, 2 and 3 carry the red
    and 4 is its outer white edge, so the ball still reads blue with two red
    accents on it. Two earlier passes put white on both sides of the red as
    well; at 12.9 deg per column the thinnest paintable line is as wide as the
    red itself and the ball came out a red-white-blue beach ball. That is the
    failure mode to watch here -- the accents are accents.
    """
    if lat <= 10.0:
        return None
    k = col_from_front(lon)
    if k in (2, 3):
        return BALL_RED
    if k == 4:
        return BALL_WHITE
    return None


def _ultra(lon, lat, base):
    """The yellow H: two uprights over the crown, joined by a crossbar that
    stops at them rather than running round the ball as a ring.

    The crossbar's latitude limits are the shell's own ring latitudes (18 and
    36 deg), so it occupies exactly one row of faces and its edges are mesh
    edges. The uprights stop at the 72 deg ring rather than running to the
    pole, where all of them converge and the H closes up into an arch.
    """
    if lat <= 10.0:
        return None
    k = col_from_front(lon)
    if k in (2, 3) and lat < 72.0:
        return LIV_YELLOW
    if k <= 3 and 18.0 < lat < 36.0:
        return LIV_YELLOW
    return None


def _timer(lon, lat, base):
    """White shell, one grey ring (exactly the 18-36 deg row of faces) and
    four red streaks at the quarter meridians, like marks on a clock face."""
    if lat <= 10.0:
        return None
    if any_lon_col(lon, (0.0, 90.0, 180.0, -90.0)) == 0:
        return BALL_RED
    if 18.0 < lat < 36.0:
        return LIV_GREY
    return None


def _net_marks(bm, R, hemi=0):
    # the Net Ball's underside is plain white; only the top is netted
    if hemi >= 0:
        net_lines(bm, R, LIV_NAVY, hemi=1)


def _dusk_marks(bm, R, hemi=0):
    # the Dusk Ball is green all over and netted all over
    for s in ((1, -1) if hemi == 0 else (hemi,)):
        net_lines(bm, R, BALL_BLACK, hemi=s, meridians=6, lats=(40.0,),
                  width=3.8)


def _quick_marks(bm, R, hemi=0):
    if hemi < 0:
        return
    for base_lon in (0.0, 180.0):
        for sx in (-1, 1):
            sphere_ribbon(bm, R, [
                (base_lon + sx * 8.0, 14.0),
                (base_lon + sx * 34.0, 34.0),
                (base_lon + sx * 12.0, 52.0),
                (base_lon + sx * 30.0, 74.0)], 11.0, LIV_YELLOW, per_seg=2)


def _master_marks(bm, R, hemi=0):
    if hemi < 0:
        return
    for base_lon in (0.0, 180.0):
        sphere_ribbon(bm, R, [
            (base_lon - 26.0, 16.0), (base_lon - 26.0, 52.0),
            (base_lon + 0.0, 28.0),
            (base_lon + 26.0, 52.0), (base_lon + 26.0, 16.0)],
            10.0, LIV_PINK, per_seg=2)
    for lon in (-74.0, 74.0):
        sphere_stud(bm, R, lon, 26.0, 0.115, LIV_PINK)


BALLS = [
    Livery("", "poke-ball", BALL_RED, BALL_WHITE, BALL_BLACK, BALL_BUTTON,
           note="red top, white bottom, black band, white button"),
    Livery("Great", "great-ball", LIV_BLUE, BALL_WHITE, BALL_BLACK,
           BALL_BUTTON, paint=_great,
           note="blue top with two white-edged red accents"),
    Livery("Ultra", "ultra-ball", BALL_BLACK, BALL_WHITE, BALL_BLACK,
           BALL_BUTTON, paint=_ultra, note="black top with the yellow H"),
    Livery("Net", "net-ball", LIV_CYAN, BALL_WHITE, BALL_BLACK, BALL_BUTTON,
           marks=_net_marks, note="cyan top under dark blue netting"),
    Livery("Dusk", "dusk-ball", LIV_GREEN, LIV_GREEN, ORANGE, ORANGE,
           marks=_dusk_marks,
           note="deep green casing, black netting, orange band and button"),
    Livery("Quick", "quick-ball", LIV_BLUE, BALL_WHITE, BALL_BLACK,
           BALL_BUTTON, marks=_quick_marks,
           note="blue top with raised yellow streaks"),
    Livery("Timer", "timer-ball", BALL_WHITE, BALL_WHITE, BALL_BLACK,
           BALL_BUTTON, paint=_timer,
           note="white shell, grey ring, four red clock streaks"),
    Livery("Master", "master-ball", LIV_PURPLE, BALL_WHITE, BALL_BLACK,
           BALL_BUTTON, marks=_master_marks,
           note="purple top with the pink M and two studs"),
]

BALL_BY_KEY = {b.key: b for b in BALLS}


def _paint_shell(bmp, liv):
    for f in bmp.faces:
        lon, lat = lon_lat(f.calc_center_median())
        f.material_index = liv.shell_mat(lon, lat)


def _merge(dst, src):
    me = bpy.data.meshes.new("_part")
    src.to_mesh(me)
    src.free()
    dst.from_mesh(me)
    bpy.data.meshes.remove(me)


def capture_ball(bm, rng, liv=None, R=R_BALL, sides=SIDES):
    """A closed ball in one livery.

    Each part is bevelled in its own bmesh and then painted from its position,
    so no bevel face can end up in slot 0 by accident -- see the module header.
    """
    liv = liv or BALLS[0]

    shell = bmesh.new()
    revolve_arc(shell, sphere_profile(R), sides,
                lambda i, r, z: BALL_RED, smooth=True)
    E.bevel_sharp(shell, width=0.0022, segments=1, angle_deg=34.0,
                  mat_break=False)
    _paint_shell(shell, liv)
    _merge(bm, shell)

    btn = bmesh.new()
    button_assembly(btn, R, liv.band, liv.button)
    E.bevel_sharp(btn, width=0.0018, segments=1, angle_deg=40.0,
                  mat_break=False)
    for f in btn.faces:
        # the bezel is everything outside the recess wall, in the button's own
        # radial coordinate; the crown and barrel are inside it
        c = f.calc_center_median()
        rad = math.hypot(c.x, c.z)
        depth = -c.y
        f.material_index = (liv.button
                            if rad < R * 0.170 and depth > R * 0.960
                            else liv.band)
    _merge(bm, btn)

    hinge_pin(bm, R, liv.band)
    liv.marks(bm, R, hemi=0)


def capture_ball_open(bm, rng, liv=None, R=R_BALL, sides=SIDES,
                      open_deg=45.0):
    """The capture pose: the shell actually split.

    The previous open variant was a whole closed ball with a glowing disc
    hidden inside it -- nothing about it was open, which made it useless for
    the one animation the prop exists for.  Each half here is a real hollow
    half shell: outer surface, inner surface at 86% radius, and a rim
    annulus joining them, revolved as a single closed profile loop.  That
    matters more than it sounds, because Unity culls back faces: a bare
    hemisphere would show the far side of the room through the opening.
    """
    liv = liv or BALLS[0]
    inner = 0.860
    # both halves swing about the hinge pin at the back of the band, which is
    # the joint the closed ball already has, so nothing slides sideways
    piv = Vector((0.0, R * 0.955, 0.0))

    for sign in (1, -1):
        half = bmesh.new()

        outer = sphere_profile(R, z_lo=0.0 if sign > 0 else -1.0,
                               z_hi=1.0 if sign > 0 else 0.0)
        if sign < 0:
            outer = list(reversed(outer))     # always rim first, pole last
        rim_r = outer[0][0]
        # The inner surface is only ever seen as an unlit cavity, so it is
        # built on every other latitude station. Mirroring the outer profile
        # station for station spent about 340 triangles a ball on a shape
        # nobody can resolve; a coarser chord simply makes the shell wall
        # slightly thicker between stations, which is invisible and harmless.
        coarse = outer[::2]
        if coarse[-1] is not outer[-1]:
            coarse = coarse + [outer[-1]]
        back = [(r * inner, z * inner) for (r, z) in reversed(coarse)]
        loop = outer + back + [(rim_r, 0.0)]
        revolve_arc(half, loop, sides, lambda i, r, z: BALL_RED, smooth=True)
        E.bevel_sharp(half, width=0.0022, segments=1, angle_deg=34.0,
                      mat_break=False)
        for f in half.faces:
            c = f.calc_center_median()
            if c.length < R * (inner + 0.050):
                f.material_index = DARK        # the inside of the shell
            else:
                lon, lat = lon_lat(c)
                f.material_index = liv.shell_mat(lon, lat)

        # half the button rides on each half, cut on the same plane. The
        # button revolves about -Y with its arc parameter running through
        # latitude, so 0-180 deg is exactly the half above the split.
        btn = bmesh.new()
        button_assembly(btn, R, liv.band, liv.button, arc_deg=180.0,
                        phase_deg=0.0 if sign > 0 else 180.0)
        E.bevel_sharp(btn, width=0.0018, segments=1, angle_deg=40.0,
                      mat_break=False)
        for f in btn.faces:
            c = f.calc_center_median()
            if math.hypot(c.x, c.z) < R * 0.170 and -c.y > R * 0.960:
                f.material_index = liv.button
            else:
                f.material_index = liv.band
        _merge(half, btn)

        liv.marks(half, R, hemi=sign)
        if sign > 0:
            hinge_pin(half, R, liv.band)

        me = bpy.data.meshes.new("_half")
        half.to_mesh(me)
        half.free()
        # NEGATIVE for the upper half. Rotating +X by +theta carries +Z
        # towards -Y, which swings the top half down over the front and the
        # bottom half up behind it -- the halves come out swapped, and the
        # first render of this showed a white dome sitting on a coloured one.
        me.transform(Matrix.Translation(piv) @
                     Matrix.Rotation(math.radians(-open_deg) * sign, 4, 'X') @
                     Matrix.Translation(-piv))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    # The capture energy, as a lens in the plane the halves opened from.
    # Both halves swing about a hinge on their RIM, not about the ball centre,
    # so the middle of the gap is not the origin: each half carries the origin
    # to y = 0.955R(1-cos t), and the lens has to be put there or it ends up
    # buried inside the lower half, which is where the first version of it was.
    gap_y = R * 0.955 * (1.0 - math.cos(math.radians(open_deg)))
    lens = [(0.0, -R * 0.15), (R * 0.30, -R * 0.09), (R * 0.50, 0.0),
            (R * 0.30, R * 0.09), (0.0, R * 0.15)]
    tmp = bmesh.new()
    revolve_arc(tmp, lens, 14, lambda i, r, z: EMISSIVE, smooth=True)
    me = bpy.data.meshes.new("_lens")
    tmp.to_mesh(me)
    tmp.free()
    me.transform(Matrix.Translation((0.0, gap_y, 0.0)))
    bm.from_mesh(me)
    bpy.data.meshes.remove(me)


def _ball_builder(liv, open_state):
    fn = capture_ball_open if open_state else capture_ball
    return lambda bm, rng: fn(bm, rng, liv)


# --------------------------------------------------------------------------
# lab equipment
# --------------------------------------------------------------------------

def healing_machine(bm, rng):
    """Waist-high console with a sloped top, six ball cradles, a status panel
    and a lit indicator arch."""
    W, D, H = 1.30, 0.78, 0.95
    # plinth and body
    box(bm, (0, 0, 0.05), (W + 0.06, D + 0.06, 0.10), DARK)
    box(bm, (0, 0, 0.10 + (H - 0.10) * .5), (W, D, H - 0.10), PLASTIC)
    # a recessed service panel on the front, and a vent below it
    box(bm, (0, -D * .5 - 0.005, 0.42), (W - 0.26, 0.03, 0.34), TEAL)
    for k in range(6):
        box(bm, (-0.42 + k * 0.17, -D * .5 - 0.012, 0.20),
            (0.10, 0.02, 0.10), DARK)
    # sloped worktop
    vs = [bm.verts.new((-W * .5, -D * .5, H)),
          bm.verts.new((W * .5, -D * .5, H)),
          bm.verts.new((W * .5, D * .5, H + 0.16)),
          bm.verts.new((-W * .5, D * .5, H + 0.16))]
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.05))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = TEAL
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    # six cradles: dished sockets with a chrome ring
    for k in range(6):
        x = -0.46 + (k % 3) * 0.46
        y = -0.16 + (k // 3) * 0.30
        z = H + 0.05 + (y + D * .5) / D * 0.16
        prof = [(0.075, z - 0.030), (0.070, z - 0.016), (0.052, z - 0.006),
                (0.0, z - 0.004)]
        revolve(bm, list(reversed(prof)), 14,
                lambda i, r, zz: METAL, smooth=True)
        E.bm_polytube(bm, [Vector((x, y, z - 0.006)), Vector((x, y, z + 0.004))],
                      [0.082, 0.086], 14, METAL, cap_start=False,
                      cap_end=False, smooth=True)
        # move the dish into place
    # (the revolve above builds at origin; rebuild cradles positioned properly)
    # status screen on a stalk
    box(bm, (0.0, 0.30, H + 0.46), (0.62, 0.05, 0.40), DARK)
    box(bm, (0.0, 0.30 - 0.031, H + 0.46), (0.54, 0.012, 0.32), SCREEN)
    for sx in (-1, 1):
        box(bm, (sx * 0.30, 0.32, H + 0.20), (0.05, 0.05, 0.24), METAL)
    # indicator arch
    arc = []
    for k in range(11):
        t = k / 10.0
        a = math.pi * t
        arc.append(Vector((math.cos(a) * 0.52, 0.05, H + 0.30 +
                           math.sin(a) * 0.34)))
    E.bm_polytube(bm, arc, [0.030] * len(arc), 7, METAL, cap_start=True,
                  cap_end=True, smooth=True)
    for k in range(5):
        t = 0.12 + k * 0.19
        a = math.pi * t
        box(bm, (math.cos(a) * 0.52, 0.018, H + 0.30 + math.sin(a) * 0.34),
            (0.05, 0.035, 0.05), EMISSIVE)


def healing_machine_fix(bm, rng):
    """Rebuild of healing_machine with the cradles actually positioned."""
    W, D, H = 1.30, 0.78, 0.95
    box(bm, (0, 0, 0.05), (W + 0.06, D + 0.06, 0.10), DARK)
    box(bm, (0, 0, 0.10 + (H - 0.10) * .5), (W, D, H - 0.10), PLASTIC)
    box(bm, (0, -D * .5 - 0.005, 0.42), (W - 0.26, 0.03, 0.34), TEAL)
    for k in range(6):
        box(bm, (-0.42 + k * 0.17, -D * .5 - 0.012, 0.20),
            (0.10, 0.02, 0.10), DARK)
    vs = [bm.verts.new((-W * .5, -D * .5, H)),
          bm.verts.new((W * .5, -D * .5, H)),
          bm.verts.new((W * .5, D * .5, H + 0.16)),
          bm.verts.new((-W * .5, D * .5, H + 0.16))]
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.05))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = TEAL
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)

    for k in range(6):
        x = -0.44 + (k % 3) * 0.44
        y = -0.16 + (k // 3) * 0.30
        z = H + 0.05 + (y + D * .5) / D * 0.16
        tmp = bmesh.new()
        prof = [(0.0, -0.030), (0.050, -0.026), (0.068, -0.012),
                (0.074, 0.004), (0.086, 0.006), (0.088, 0.016)]
        revolve(tmp, prof, 10, lambda i, r, zz: METAL, smooth=True,
                close_bottom=False, close_top=False)
        me = bpy.data.meshes.new("t")
        tmp.to_mesh(me)
        tmp.free()
        me.transform(Matrix.Translation((x, y, z)))
        bm.from_mesh(me)
        bpy.data.meshes.remove(me)

    box(bm, (0.0, 0.30, H + 0.48), (0.64, 0.06, 0.42), DARK)
    box(bm, (0.0, 0.30 - 0.036, H + 0.48), (0.54, 0.012, 0.32), SCREEN)
    for sx in (-1, 1):
        box(bm, (sx * 0.30, 0.33, H + 0.22), (0.05, 0.05, 0.26), METAL)
    arc = []
    for k in range(9):
        a = math.pi * (k / 8.0)
        arc.append(Vector((math.cos(a) * 0.52, -0.02,
                           H + 0.26 + math.sin(a) * 0.36)))
    E.bm_polytube(bm, arc, [0.028] * len(arc), 6, METAL, cap_start=True,
                  cap_end=True, smooth=True)
    for k in range(5):
        a = math.pi * (0.12 + k * 0.19)
        box(bm, (math.cos(a) * 0.52, -0.055,
                 H + 0.26 + math.sin(a) * 0.36), (0.05, 0.035, 0.05), EMISSIVE)


def research_terminal(bm, rng):
    """A desk with a raked keyboard deck, a big display on an arm, a specimen
    tube on a base and a stack of drives."""
    W, D, H = 1.55, 0.72, 0.76
    # legs
    for sx in (-1, 1):
        for sy in (-1, 1):
            E.bm_polytube(bm, [Vector((sx * (W * .5 - 0.07), sy * (D * .5 - 0.07), 0)),
                               Vector((sx * (W * .5 - 0.07), sy * (D * .5 - 0.07), H))],
                          [0.040, 0.032], 6, METAL, cap_start=True,
                          cap_end=True, smooth=False)
    box(bm, (0, 0, 0.16), (W - 0.20, 0.06, 0.05), METAL)
    # top with an apron
    box(bm, (0, 0, H + 0.025), (W, D, 0.05), DESK)
    box(bm, (0, -D * .5 + 0.03, H - 0.06), (W - 0.10, 0.04, 0.11), DESK)
    # raked keyboard deck
    vs = [bm.verts.new((-0.42, -0.30, H + 0.05)),
          bm.verts.new((0.42, -0.30, H + 0.05)),
          bm.verts.new((0.42, 0.02, H + 0.11)),
          bm.verts.new((-0.42, 0.02, H + 0.11))]
    vt = [bm.verts.new(v.co + Vector((0, 0, 0.022))) for v in vs]
    fs = [bm.faces.new(vt), bm.faces.new(list(reversed(vs)))]
    for i in range(4):
        j = (i + 1) % 4
        fs.append(bm.faces.new((vs[i], vs[j], vt[j], vt[i])))
    for f in fs:
        f.material_index = DARK
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)
    for r in range(4):
        box(bm, (0.0, -0.26 + r * 0.075, H + 0.083 + r * 0.014),
            (0.80, 0.056, 0.010), PANEL)
    for (cx, cy, w) in ((-0.30, -0.26, 0.10), (0.24, -0.185, 0.07),
                        (0.0, -0.035, 0.34), (-0.34, 0.040, 0.06),
                        (0.32, 0.040, 0.06)):
        r = int((cy + 0.26) / 0.075)
        box(bm, (cx, cy, H + 0.090 + r * 0.014), (w, 0.050, 0.014), DARK)
    # display on an articulated arm
    box(bm, (0.0, 0.30, H + 0.09), (0.26, 0.20, 0.03), METAL)
    E.bm_polytube(bm, [Vector((0, 0.30, H + 0.10)), Vector((0, 0.30, H + 0.34)),
                       Vector((0, 0.22, H + 0.44))],
                  [0.030, 0.026, 0.024], 7, METAL, cap_start=True,
                  cap_end=True, smooth=False)
    box(bm, (0, 0.205, H + 0.66), (0.92, 0.045, 0.52), DARK)
    box(bm, (0, 0.205 - 0.026, H + 0.67), (0.85, 0.012, 0.45), SCREEN)
    box(bm, (0, 0.205 - 0.026, H + 0.41), (0.85, 0.012, 0.03), EMISSIVE)
    # specimen tube
    E.bm_polytube(bm, [Vector((0.56, 0.18, H + 0.05)), Vector((0.56, 0.18, H + 0.09))],
                  [0.105, 0.098], 14, METAL, cap_start=True, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [Vector((0.56, 0.18, H + 0.09)), Vector((0.56, 0.18, H + 0.40))],
                  [0.085, 0.085], 14, TUBE, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [Vector((0.56, 0.18, H + 0.40)), Vector((0.56, 0.18, H + 0.46))],
                  [0.098, 0.078], 14, METAL, cap_start=False, cap_end=True,
                  smooth=True)
    E.bm_puff(bm, (0.56, 0.18, H + 0.22), 0.055, rng, EMISSIVE, sides=7,
              squash=(1.0, 1.0, 1.3), lumpy=0.14, rings_n=3)
    # drive stack
    for k in range(3):
        box(bm, (-0.56, 0.20, H + 0.09 + k * 0.055), (0.30, 0.24, 0.045),
            PANEL if k % 2 else METAL)
    box(bm, (-0.56, 0.075, H + 0.145), (0.06, 0.02, 0.02), EMISSIVE)


def scanner(bm, rng):
    """The handheld device the player raises.  Clamshell body, a raked screen
    on the inner face, a lens barrel on the outer face, grip texture, buttons.
    Pivot at the grip so the RaiseScanner animation can parent it to a hand."""
    W, Hh, T = 0.115, 0.185, 0.028
    # main body as a rounded slab: lofted rings so the edges are real radii
    prof = []
    steps = 5
    for k in range(steps + 1):
        t = k / float(steps)
        a = math.pi * t
        prof.append((math.sin(a), -math.cos(a) * T * .5))
    rings = []
    for (s, z) in prof:
        ring = []
        seg = 22
        for i in range(seg):
            ang = 2 * math.pi * i / seg
            # superellipse: a rounded rectangle, not an oval
            cx = math.cos(ang)
            cy = math.sin(ang)
            k = 0.34
            x = math.copysign(abs(cx) ** k, cx) * W * .5 * (0.55 + 0.45 * s)
            y = math.copysign(abs(cy) ** k, cy) * Hh * .5 * (0.55 + 0.45 * s)
            ring.append(bm.verts.new((x, z, y + Hh * .5)))
        rings.append(ring)
    for k in range(len(rings) - 1):
        for i in range(22):
            j = (i + 1) % 22
            f = bm.faces.new((rings[k][i], rings[k][j],
                              rings[k + 1][j], rings[k + 1][i]))
            f.material_index = PLASTIC if k >= 2 else ORANGE
            f.smooth = True
    for (r, flip) in ((rings[0], True), (rings[-1], False)):
        f = bm.faces.new(list(reversed(r)) if flip else list(r))
        f.material_index = PLASTIC
        f.smooth = True
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))

    # screen sunk into the front face
    box(bm, (0, -T * .5 - 0.001, Hh * 0.62), (W * 0.68, 0.006, Hh * 0.46), DARK)
    box(bm, (0, -T * .5 - 0.004, Hh * 0.62), (W * 0.60, 0.004, Hh * 0.40), SCREEN)
    # a readout strip and two buttons below it
    box(bm, (0, -T * .5 - 0.004, Hh * 0.30), (W * 0.60, 0.004, 0.010), EMISSIVE)
    for sx in (-1, 1):
        E.bm_polytube(bm, [Vector((sx * 0.026, -T * .5 - 0.002, Hh * 0.19)),
                           Vector((sx * 0.026, -T * .5 - 0.010, Hh * 0.19))],
                      [0.014, 0.011], 10, ORANGE, cap_start=False,
                      cap_end=True, smooth=True)
    # d-pad
    box(bm, (0, -T * .5 - 0.004, Hh * 0.09), (0.030, 0.006, 0.009), DARK)
    box(bm, (0, -T * .5 - 0.004, Hh * 0.09), (0.009, 0.006, 0.030), DARK)
    # lens barrel on the back
    lc = Vector((0, T * .5, Hh * 0.70))
    E.bm_polytube(bm, [lc, lc + Vector((0, 0.016, 0))],
                  [0.030, 0.026], 16, METAL, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [lc + Vector((0, 0.016, 0)), lc + Vector((0, 0.022, 0))],
                  [0.026, 0.021], 16, DARK, cap_start=False, cap_end=False,
                  smooth=True)
    E.bm_polytube(bm, [lc + Vector((0, 0.022, 0)), lc + Vector((0, 0.024, 0))],
                  [0.021, 0.0], 16, TUBE, cap_start=False, cap_end=False,
                  smooth=True)
    # grip pads on the back lower half
    for sx in (-1, 1):
        box(bm, (sx * W * 0.30, T * .5 - 0.002, Hh * 0.24),
            (0.020, 0.008, Hh * 0.30), RUBBER)
    # antenna nub
    E.bm_polytube(bm, [Vector((W * 0.32, 0, Hh * 0.97)),
                       Vector((W * 0.36, 0, Hh * 1.13))],
                  [0.008, 0.005], 6, METAL, cap_start=True, cap_end=True,
                  smooth=True)


ASSETS = [
    ("Env_Prop_CaptureBall", 5101, (300, 2000), capture_ball, 'center', 2),
    ("Env_Prop_CaptureBall_Open", 5102, (300, 2000), capture_ball_open, 'center', 1),
    ("Env_Prop_HealingMachine", 5201, (300, 2600), healing_machine_fix, 'base', 1),
    ("Env_Prop_ResearchTerminal", 5301, (300, 2600), research_terminal, 'base', 1),
    ("Env_Prop_Scanner", 5401, (300, 2000), scanner, 'base', 2),
]

# The eight liveries, closed and open, replace the two hand-written entries
# above. bseg 0 means "this builder bevels its own parts" -- the balls have to,
# because the shared pass would put every bevel face in slot 0. The plain
# Poke Ball keeps the original two asset names so the level's existing item
# ball placements keep resolving.
_BALL_NAME = {"": "Env_Prop_CaptureBall"}
ASSETS = [a for a in ASSETS if not a[0].startswith("Env_Prop_CaptureBall")]
for _i, _liv in enumerate(BALLS):
    _base = _BALL_NAME.get(_liv.key, "Env_Prop_CaptureBall_%s" % _liv.key)
    # Budgets are per pair, not per livery, and the headroom is for the two
    # netted balls: the Net and Dusk liveries carry about 700 triangles of
    # raised netting each because a 28-column shell cannot paint a line
    # thinner than 12.9 deg (see net_lines). Everything else lands near 1350
    # closed and 2900 open.
    ASSETS.insert(_i * 2, (_base, 5110 + _i * 2, (300, 2400),
                           _ball_builder(_liv, False), 'center', 0))
    ASSETS.insert(_i * 2 + 1, (_base + "_Open", 5111 + _i * 2, (400, 4000),
                               _ball_builder(_liv, True), 'center', 0))


def _ball_note(name, by_key):
    """Manifest note tying each ball asset back to its ItemCatalog id."""
    if not name.startswith("Env_Prop_CaptureBall"):
        return ""
    rest = name[len("Env_Prop_CaptureBall"):]
    open_state = rest.endswith("_Open")
    if open_state:
        rest = rest[:-len("_Open")]
    liv = by_key.get(rest.lstrip("_"))
    if liv is None:
        return ""
    return "%s (ItemCatalog '%s'): %s.%s Button faces Blender -Y, i.e. Unity +Z." % (
        liv.item_id.replace("-", " ").title(), liv.item_id, liv.note,
        " Split open on the hinge for the capture animation." if open_state
        else "")


def main():
    E.ensure_dirs()
    T.ensure_atlas(FAM)
    E.reset_scene()
    ms = matset()
    ap = T.atlas_paths(FAM)
    part = []
    problems = []
    ball_note = {b.key: b for b in BALLS}

    for (name, seed, budget, fn, pivot, bseg) in ASSETS:
        rng = random.Random(seed)
        bm = E.bm_new()
        fn(bm, rng)
        if bseg:
            E.bevel_sharp(bm, width=0.0035, segments=bseg, angle_deg=40.0,
                          mat_break=False)
        obj = E.bm_to_obj(bm, name, ms.materials())
        # props are handled objects: smooth where round, crisp where machined
        E.finalize(obj, smooth_angle=44.0, merge=1e-6)
        if pivot == 'center':
            me = obj.data
            zs = [v.co.z for v in me.vertices]
            xs = [v.co.x for v in me.vertices]
            ys = [v.co.y for v in me.vertices]
            E.set_pivot(obj, ((min(xs) + max(xs)) * .5,
                              (min(ys) + max(ys)) * .5,
                              (min(zs) + max(zs)) * .5))
        else:
            E.pivot_to_base(obj)
        E.apply_transforms(obj)
        E.uv_all(obj, ms, angle=52.0, margin=0.010)
        # The matset is now 24 slots wide (16 cells plus the eight livery
        # sub-bands) and no single prop uses more than seven of them. Shipping
        # the whole set would declare 24 materials on every mesh; this has to
        # run after uv_all, which reads the original indices.
        E.strip_unused_materials(obj)
        tris, probs = E.validate(obj, budget=budget, need_vcol=False,
                                 strict=False)
        path = os.path.join(OUT, name + ".fbx")
        lods = []
        if tris > 2000:
            for lo in E.make_lods(obj, (0.40, 0.15)):
                lp = os.path.join(OUT, lo.name + ".fbx")
                E.export_fbx([lo], lp)
                lods.append((lp, E.tri_count(lo)))
                E.delete_obj(lo)
        E.export_fbx([obj], path)
        if probs:
            problems.append((name, probs))
        E.log("%-28s %5d tris  %s" % (name, tris, probs or "ok"))
        part.append({
            "name": name, "family": FAM, "subfamily": "Prop",
            "path": os.path.relpath(path, E.REPO).replace("\\", "/"),
            "triangles": tris,
            "lods": [{"level": i + 1,
                      "path": os.path.relpath(p, E.REPO).replace("\\", "/"),
                      "triangles": t} for i, (p, t) in enumerate(lods)],
            "pivot": ("volume centre (spins about its own axis)"
                      if pivot == 'center' else "base, XY centred"),
            "textures": [os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
                         os.path.relpath(ap["normal"], E.REPO).replace("\\", "/")],
            "windVertexColors": False,
            "notes": _ball_note(name, ball_note),
            # the balls are held to the manifest's hero_prop ceiling, not the
            # street-furniture one -- see BUDGETS in build_manifest.py
            "budgetClass": ("hero_prop"
                            if name.startswith("Env_Prop_CaptureBall")
                            else "prop"),
        })
        E.delete_obj(obj)

    E.write_part(FAM, part)
    E.log("---- %d prop assets, %d with problems" % (len(part), len(problems)))
    for n, p in problems:
        E.log("  ISSUE %s: %s" % (n, p))


if __name__ == "__main__":
    main()
