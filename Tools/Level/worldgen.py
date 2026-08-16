"""Geometry kernel for the Poke Lab slice layout.

Split out of build_layout.py so the design file stays readable. Nothing in here knows
anything about Pokemon; it is polylines, signed distance fields, a composed height field
and a seeded scatterer.

Conventions match Assets/Game/Art/Environment/environment_manifest.json:
  1 unit = 1 metre, Y up, models face +Z, pivots at the base.
"""

from __future__ import annotations

import math


# ---------------------------------------------------------------------------
# scalar helpers
# ---------------------------------------------------------------------------
def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v


def lerp(a, b, t):
    return a + (b - a) * t


def smootherstep(t):
    """Ken Perlin's C2 sigmoid. Used for every terrain shoulder, so slopes have no crease."""
    t = clamp(t)
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def yaw_towards(dx, dz):
    """Degrees of Y rotation so a +Z-facing model looks along (dx, dz)."""
    return math.degrees(math.atan2(dx, dz)) % 360.0


# ---------------------------------------------------------------------------
# polyline helpers
# ---------------------------------------------------------------------------
def chaikin(points, iterations=3, closed=False):
    """Corner cutting. Turns hand-authored control points into a smooth centreline.

    Roads in Sword/Shield and BDSP are continuous curves, never chains of straight
    segments meeting at visible corners, so every centreline goes through this.
    """
    pts = [tuple(p) for p in points]
    for _ in range(iterations):
        out = []
        n = len(pts)
        rng = range(n) if closed else range(n - 1)
        if not closed:
            out.append(pts[0])
        for i in rng:
            a = pts[i]
            b = pts[(i + 1) % n]
            q = tuple(0.75 * a[k] + 0.25 * b[k] for k in range(len(a)))
            r = tuple(0.25 * a[k] + 0.75 * b[k] for k in range(len(a)))
            out.append(q)
            out.append(r)
        if not closed:
            out.append(pts[-1])
        pts = out
    return pts


def resample(points, spacing):
    """Even arc-length resampling, keeping the endpoints."""
    if len(points) < 2:
        return [tuple(p) for p in points]
    dim = len(points[0])
    out = [tuple(points[0])]
    carry = 0.0
    for i in range(len(points) - 1):
        a, b = points[i], points[i + 1]
        seg = math.dist((a[0], a[-1]), (b[0], b[-1]))
        if seg <= 1e-9:
            continue
        t = (spacing - carry) / seg
        while t <= 1.0:
            out.append(tuple(lerp(a[k], b[k], t) for k in range(dim)))
            t += spacing / seg
        carry = (1.0 - (t - spacing / seg)) * seg
    if math.dist((out[-1][0], out[-1][-1]), (points[-1][0], points[-1][-1])) > spacing * 0.4:
        out.append(tuple(points[-1]))
    else:
        out[-1] = tuple(points[-1])
    return out


def polyline_length(points):
    return sum(math.dist((points[i][0], points[i][-1]), (points[i + 1][0], points[i + 1][-1]))
               for i in range(len(points) - 1))


def closest_on_polyline(x, z, points):
    """(distance, interpolated point, tangent) to a polyline in the XZ plane."""
    best = (1e18, points[0], (0.0, 1.0))
    for i in range(len(points) - 1):
        ax, az = points[i][0], points[i][-1]
        bx, bz = points[i + 1][0], points[i + 1][-1]
        vx, vz = bx - ax, bz - az
        L2 = vx * vx + vz * vz
        t = 0.0 if L2 <= 1e-12 else clamp(((x - ax) * vx + (z - az) * vz) / L2)
        px, pz = ax + vx * t, az + vz * t
        d = math.hypot(x - px, z - pz)
        if d < best[0]:
            pt = tuple(lerp(points[i][k], points[i + 1][k], t) for k in range(len(points[i])))
            n = math.hypot(vx, vz) or 1.0
            best = (d, pt, (vx / n, vz / n))
    return best


def offset_point(points, t, side, distance):
    """A point `distance` metres to one side of the polyline at normalised arc length t."""
    total = polyline_length(points)
    target = clamp(t) * total
    run = 0.0
    for i in range(len(points) - 1):
        ax, az = points[i][0], points[i][-1]
        bx, bz = points[i + 1][0], points[i + 1][-1]
        seg = math.hypot(bx - ax, bz - az)
        if run + seg >= target or i == len(points) - 2:
            u = 0.0 if seg <= 1e-9 else (target - run) / seg
            px = lerp(ax, bx, u)
            pz = lerp(az, bz, u)
            nx, nz = (bz - az), -(bx - ax)
            n = math.hypot(nx, nz) or 1.0
            return px + side * distance * nx / n, pz + side * distance * nz / n
        run += seg
    return points[-1][0], points[-1][-1]


# ---------------------------------------------------------------------------
# polygons
# ---------------------------------------------------------------------------
def point_in_poly(x, z, poly):
    inside = False
    n = len(poly)
    for i in range(n):
        ax, az = poly[i][0], poly[i][1]
        bx, bz = poly[(i + 1) % n][0], poly[(i + 1) % n][1]
        if (az > z) != (bz > z):
            xx = (bx - ax) * (z - az) / ((bz - az) or 1e-12) + ax
            if x < xx:
                inside = not inside
    return inside


def poly_sdf(x, z, poly):
    """Signed distance to a polygon: negative inside, positive outside."""
    d = 1e18
    for i in range(len(poly)):
        ax, az = poly[i][0], poly[i][1]
        bx, bz = poly[(i + 1) % len(poly)][0], poly[(i + 1) % len(poly)][1]
        vx, vz = bx - ax, bz - az
        L2 = vx * vx + vz * vz
        t = 0.0 if L2 <= 1e-12 else clamp(((x - ax) * vx + (z - az) * vz) / L2)
        d = min(d, math.hypot(x - (ax + vx * t), z - (az + vz * t)))
    return -d if point_in_poly(x, z, poly) else d


def poly_area(poly):
    a = 0.0
    for i in range(len(poly)):
        x0, z0 = poly[i][0], poly[i][1]
        x1, z1 = poly[(i + 1) % len(poly)][0], poly[(i + 1) % len(poly)][1]
        a += x0 * z1 - x1 * z0
    return abs(a) * 0.5


def poly_bounds(poly):
    xs = [p[0] for p in poly]
    zs = [p[1] for p in poly]
    return min(xs), max(xs), min(zs), max(zs)


def poly_centroid(poly):
    a = 0.0
    cx = cz = 0.0
    for i in range(len(poly)):
        x0, z0 = poly[i][0], poly[i][1]
        x1, z1 = poly[(i + 1) % len(poly)][0], poly[(i + 1) % len(poly)][1]
        cross = x0 * z1 - x1 * z0
        a += cross
        cx += (x0 + x1) * cross
        cz += (z0 + z1) * cross
    a *= 0.5
    if abs(a) < 1e-9:
        return poly[0][0], poly[0][1]
    return cx / (6 * a), cz / (6 * a)


def ribbon_polygon(points, half_width):
    """Closed outline of a constant-width ribbon around a polyline. Used for stream beds."""
    left, right = [], []
    n = len(points)
    for i in range(n):
        if i == 0:
            ax, az = points[0][0], points[0][-1]
            bx, bz = points[1][0], points[1][-1]
        elif i == n - 1:
            ax, az = points[-2][0], points[-2][-1]
            bx, bz = points[-1][0], points[-1][-1]
        else:
            ax, az = points[i - 1][0], points[i - 1][-1]
            bx, bz = points[i + 1][0], points[i + 1][-1]
        nx, nz = (bz - az), -(bx - ax)
        L = math.hypot(nx, nz) or 1.0
        nx, nz = nx / L * half_width, nz / L * half_width
        px, pz = points[i][0], points[i][-1]
        left.append((px + nx, pz + nz))
        right.append((px - nx, pz - nz))
    return left + right[::-1]


# ---------------------------------------------------------------------------
# marching squares -- extracts the waterline from the height field, so the water
# polygon can never disagree with the terrain that holds it
# ---------------------------------------------------------------------------
def contour(grid, x0, z0, step, level, seed_cell=None):
    """Marching squares on grid[iz][ix], returning the longest closed XZ ring at `level`.

    Interpolated, so the waterline is a smooth curve rather than a staircase of cell edges.
    The longest ring is the lake shore; smaller ones are puddles in the micro-relief and are
    discarded.
    """
    nz, nx = len(grid), len(grid[0])
    segs = []

    def interp(pa, va, pb, vb):
        t = 0.5 if abs(vb - va) < 1e-9 else (level - va) / (vb - va)
        t = clamp(t)
        return (pa[0] + (pb[0] - pa[0]) * t, pa[1] + (pb[1] - pa[1]) * t)

    for iz in range(nz - 1):
        for ix in range(nx - 1):
            v = [grid[iz][ix], grid[iz][ix + 1], grid[iz + 1][ix + 1], grid[iz + 1][ix]]
            p = [(x0 + ix * step, z0 + iz * step),
                 (x0 + (ix + 1) * step, z0 + iz * step),
                 (x0 + (ix + 1) * step, z0 + (iz + 1) * step),
                 (x0 + ix * step, z0 + (iz + 1) * step)]
            case = sum((1 << i) for i in range(4) if v[i] < level)
            if case in (0, 15):
                continue
            e = {0: interp(p[0], v[0], p[1], v[1]), 1: interp(p[1], v[1], p[2], v[2]),
                 2: interp(p[2], v[2], p[3], v[3]), 3: interp(p[3], v[3], p[0], v[0])}
            table = {1: [(3, 0)], 2: [(0, 1)], 3: [(3, 1)], 4: [(1, 2)],
                     5: [(3, 0), (1, 2)], 6: [(0, 2)], 7: [(3, 2)], 8: [(2, 3)],
                     9: [(2, 0)], 10: [(0, 1), (2, 3)], 11: [(2, 1)], 12: [(1, 3)],
                     13: [(1, 0)], 14: [(0, 3)]}
            for a, bq in table.get(case, []):
                segs.append((e[a], e[bq]))

    # chain the segments into rings, undirected: marching-squares segment orientation is
    # not consistent enough to walk one way round
    def key(pt):
        return (round(pt[0], 3), round(pt[1], 3))

    adj = {}
    for si, (a, bq) in enumerate(segs):
        adj.setdefault(key(a), []).append((si, key(bq), bq))
        adj.setdefault(key(bq), []).append((si, key(a), a))
    used_seg = set()
    rings = []
    for si, (a, _b) in enumerate(segs):
        if si in used_seg:
            continue
        ring = [a]
        cur = key(a)
        guard = 0
        while guard < len(segs) + 4:
            guard += 1
            nxt = None
            for sj, kb, pb in adj.get(cur, []):
                if sj not in used_seg:
                    nxt = (sj, kb, pb)
                    break
            if nxt is None:
                break
            used_seg.add(nxt[0])
            ring.append(nxt[2])
            cur = nxt[1]
        if len(ring) > 12:
            rings.append(ring)
    if not rings:
        return []
    ring = max(rings, key=len)
    _ = seed_cell
    out = simplify(ring, step * 0.55)
    return out if len(out) >= 4 else ring


def simplify(ring, tolerance):
    """Ramer-Douglas-Peucker on a closed ring."""
    if len(ring) < 4:
        return ring

    def rdp(pts):
        if len(pts) < 3:
            return pts
        ax, az = pts[0]
        bx, bz = pts[-1]
        L = math.hypot(bx - ax, bz - az) or 1e-9
        worst, wi = 0.0, 0
        for i in range(1, len(pts) - 1):
            px, pz = pts[i]
            dist = abs((bx - ax) * (az - pz) - (ax - px) * (bz - az)) / L
            if dist > worst:
                worst, wi = dist, i
        if worst <= tolerance:
            return [pts[0], pts[-1]]
        return rdp(pts[:wi + 1])[:-1] + rdp(pts[wi:])

    out = rdp(ring + [ring[0]])
    return out[:-1]


# ---------------------------------------------------------------------------
# the height field
# ---------------------------------------------------------------------------
class Mass:
    """A raised or lowered landform.

    `amount` is the plateau height at the polygon's core; `shoulder` is how many metres
    the ground takes to get there. A 1 m shoulder on a 3 m rise is a cliff; a 12 m
    shoulder on the same rise is a hillside you can walk up. That single number is what
    distinguishes the town's retaining wall from the meadow it sits above.
    """

    def __init__(self, name, polygon, amount, shoulder, edge="slope", note=""):
        self.name = name
        self.polygon = [(float(p[0]), float(p[1])) for p in polygon]
        self.amount = float(amount)
        self.shoulder = float(shoulder)
        self.edge = edge
        self.note = note

    def height(self, x, z):
        d = poly_sdf(x, z, self.polygon)
        if d >= self.shoulder:
            return 0.0
        if d <= 0.0:
            return self.amount
        return self.amount * smootherstep(1.0 - d / self.shoulder)

    def to_json(self):
        return {"name": self.name, "amount": round(self.amount, 3),
                "shoulderMetres": round(self.shoulder, 3), "edge": self.edge,
                "polygon": [[round(p[0], 2), round(p[1], 2)] for p in self.polygon],
                "note": self.note}


class Channel:
    """A cut trench -- the stream bed. Removes height along a centreline."""

    def __init__(self, name, points, half_width, depth, shoulder):
        self.name = name
        self.points = points
        self.half_width = half_width
        self.depth = depth
        self.shoulder = shoulder

    def height(self, x, z):
        d, _, _ = closest_on_polyline(x, z, self.points)
        if d >= self.half_width + self.shoulder:
            return 0.0
        if d <= self.half_width:
            return -self.depth
        return -self.depth * smootherstep(1.0 - (d - self.half_width) / self.shoulder)


class HeightField:
    """Regional base + landform masses + trench cuts + path conform + micro relief.

    Path conform is the important one: the terrain is pulled onto each road's authored
    elevation inside its width and feathered out over `blend`. That is why the road looks
    worn into the hillside instead of laid on top of it, and why nothing on it floats.
    """

    def __init__(self, base_fn, masses, channels, micro):
        self.base_fn = base_fn
        self.masses = masses
        self.channels = channels
        self.micro = micro
        self.conform = []          # (points, half_width, blend, skippable)
        self.pads = []             # (cx, cz, half_x, half_z, height, blend)

    def add_conform(self, points, half_width, blend, skippable=True):
        """`skippable` marks a conform that a bridge span is allowed to suspend.

        Roads are skippable: under a bridge the road must stop dragging the ground up
        with it, or the water has nothing to pass through. The stream channel is not.
        Suspending the channel as well leaves the bed higher than the water surface,
        and the stream disappears into the ground exactly where the bridge is -- which
        reads as a bridge standing on dry dirt between two disconnected pools.
        """
        self.conform.append((points, half_width, blend, skippable))

    def add_pad(self, cx, cz, half_x, half_z, height, blend):
        """A level platform cut for a building.

        Buildings are rigid boxes and the ground under them is not. A cottage on
        4 degrees of fall has one corner in the air, and seating it lower only buries
        the opposite corner — the repair is to make the ground flat, which is what
        anyone actually building a house does. Rectangular rather than radial so the
        pad follows the building rather than bulging past its corners.
        """
        self.pads.append((cx, cz, half_x, half_z, height, max(blend, 0.01)))

    def raw(self, x, z):
        y = self.base_fn(x, z)
        for m in self.masses:
            y += m.height(x, z)
        for c in self.channels:
            y += c.height(x, z)
        y += self.micro(x, z)
        return y

    def height(self, x, z, skippable=True):
        """Terrain height. With `skippable` false, only the conforms a bridge may not
        suspend are applied -- in practice, the stream channel but not the roads."""
        y = self.raw(x, z)
        # Tracked separately so a watercourse can win against a road. Both are
        # conforms and the strongest-weight rule made them compete on equal terms,
        # which let the route's elevation fill in the stream bed wherever the two run
        # close: 11 of the stream's 77 centreline points had their bed standing up to
        # 0.90 m *above* their own waterline, and the stream rendered as a chain of
        # disconnected puddles. A road can be carried over water on a bridge; water
        # cannot be carried over a road.
        best_w, best_y = 0.0, 0.0
        cut_w, cut_y = 0.0, 0.0
        for points, hw, blend, is_skippable in self.conform:
            if is_skippable and not skippable:
                continue
            d, pt, _ = closest_on_polyline(x, z, points)
            if d >= hw + blend:
                continue
            w = 1.0 if d <= hw else smootherstep(1.0 - (d - hw) / blend)
            if is_skippable:
                if w > best_w:
                    best_w, best_y = w, pt[1]
            elif w > cut_w:
                cut_w, cut_y = w, pt[1]

        if cut_w >= 0.5:
            best_w, best_y = cut_w, cut_y
        if best_w > 0.0:
            y = lerp(y, best_y, best_w)

        # Pads last: a house's platform wins over the road that runs past it.
        for cx, cz, hx, hz, height, blend in self.pads:
            dx = max(abs(x - cx) - hx, 0.0)
            dz = max(abs(z - cz) - hz, 0.0)
            d = math.hypot(dx, dz)
            if d >= blend:
                continue
            w = 1.0 if d <= 0.0 else smootherstep(1.0 - d / blend)
            y = lerp(y, height, w)
        return y


class BakedField:
    """A sampled copy of a HeightField on a regular grid.

    Every Y in the emitted layout is read from this, never from the analytic function, so
    that whatever the builder does -- evaluate the formula or triangulate the grid -- the
    props and the ground agree to the last decimal.
    """

    def __init__(self, field, x0, x1, z0, z1, step):
        self.x0, self.z0, self.step = x0, z0, step
        self.nx = int(round((x1 - x0) / step)) + 1
        self.nz = int(round((z1 - z0) / step)) + 1
        self.grid = [[field.height(x0 + ix * step, z0 + iz * step) for ix in range(self.nx)]
                     for iz in range(self.nz)]

    def at(self, x, z):
        fx = clamp((x - self.x0) / self.step, 0.0, self.nx - 1.001)
        fz = clamp((z - self.z0) / self.step, 0.0, self.nz - 1.001)
        ix, iz = int(fx), int(fz)
        tx, tz = fx - ix, fz - iz
        g = self.grid
        a = lerp(g[iz][ix], g[iz][ix + 1], tx)
        b = lerp(g[iz + 1][ix], g[iz + 1][ix + 1], tx)
        return lerp(a, b, tz)

    def slope_degrees(self, x, z):
        h = self.step
        dx = (self.at(x + h, z) - self.at(x - h, z)) / (2 * h)
        dz = (self.at(x, z + h) - self.at(x, z - h)) / (2 * h)
        return math.degrees(math.atan(math.hypot(dx, dz)))

    def rows(self, ndigits=3):
        return [[round(v, ndigits) for v in row] for row in self.grid]
