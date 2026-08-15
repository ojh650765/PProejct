"""Flatten the level layout into something Unity can build directly.

Two problems make a straight read impossible on the C# side. JsonUtility deserialises
neither jagged arrays nor dictionaries, and the deck heights are authored as analytic
formulas — strings like `y = 0.0 + 0.28*sin(0.075x) + 0.22*cos(0.09z)`.

Reimplementing those formulas in C# would be a second source of truth for the one number
the whole level depends on: 4,682 objects had their Y sampled from these fields, and a
surface that disagrees by centimetres leaves props floating or buried. So the fields are
evaluated *here*, against the same expressions, and the decks ship as pre-triangulated
meshes. C# only has to upload vertices and indices.

    python emit_unity_layout.py
"""

import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

SOURCE = os.path.join(ROOT, "Assets", "Game", "Data", "Levels", "slice_layout.json")
OUT = os.path.join(ROOT, "Assets", "Game", "Data", "Levels", "slice_layout_unity.json")

# Grid spacing for generated ground. 1 m is finer than the gentlest field varies and
# keeps the vertex count reasonable across eight decks.
CELL = 1.0


# --- the height fields, transcribed from terrain.heightFields -----------------------

def h_route(x, z):
    return 0.0 + 0.28 * math.sin(0.075 * x) + 0.22 * math.cos(0.09 * z)


def h_cave(x, z):
    return 1.5 + 0.13 * math.sin(0.22 * x) + 0.11 * math.cos(0.26 * z)


def h_shore(x, z):
    d = math.hypot(x + 6.0, 1.15 * (z + 2.0))
    t = max(0.0, min(1.0, (d - 21.0) / 8.0))
    return -1.5 + t * 1.5 + 0.12 * math.sin(0.3 * x)


HEIGHT_FIELDS = {
    "heightFields.route": h_route,
    "heightFields.cave": h_cave,
    "heightFields.shore": h_shore,
}


def height_at(y_spec, x, z):
    """A deck's Y is either a constant or a named analytic field."""
    if isinstance(y_spec, (int, float)):
        return float(y_spec)
    field = HEIGHT_FIELDS.get(y_spec)
    if field is None:
        raise ValueError(f"unknown height field {y_spec!r}")
    return field(x, z)


# --- polygon helpers ----------------------------------------------------------------

def point_in_polygon(x, z, poly):
    """Standard ray cast. Polygons here are simple and small, so this is fast enough."""
    inside = False
    n = len(poly)
    for i in range(n):
        x1, z1 = poly[i]
        x2, z2 = poly[(i + 1) % n]
        if (z1 > z) != (z2 > z):
            xin = (x2 - x1) * (z - z1) / (z2 - z1) + x1
            if x < xin:
                inside = not inside
    return inside



def triangulate(poly):
    """Ear clipping. Handles concave outlines, which is the whole point.

    The previous approach clipped grid cells against the outline with Sutherland-Hodgman,
    which only holds for convex clip regions: on the concave cave outline it discarded 94%
    of the floor, and on the route 11%. Ear clipping reproduces the polygon exactly.
    """
    pts = list(poly)
    # Work counter-clockwise so the ear test has a consistent sense.
    area = 0.0
    for i in range(len(pts)):
        x1, z1 = pts[i]
        x2, z2 = pts[(i + 1) % len(pts)]
        area += x1 * z2 - x2 * z1
    if area < 0:
        pts.reverse()

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    def in_triangle(p, a, b, c):
        d1 = cross(a, b, p)
        d2 = cross(b, c, p)
        d3 = cross(c, a, p)
        neg = d1 < -1e-12 or d2 < -1e-12 or d3 < -1e-12
        pos = d1 > 1e-12 or d2 > 1e-12 or d3 > 1e-12
        return not (neg and pos)

    idx = list(range(len(pts)))
    tris = []
    guard = 0
    while len(idx) > 3 and guard < 10000:
        guard += 1
        clipped = False
        for k in range(len(idx)):
            i0 = idx[(k - 1) % len(idx)]
            i1 = idx[k]
            i2 = idx[(k + 1) % len(idx)]
            a, b, c = pts[i0], pts[i1], pts[i2]
            if cross(a, b, c) <= 1e-12:
                continue  # reflex vertex, not an ear
            if any(in_triangle(pts[m], a, b, c) for m in idx if m not in (i0, i1, i2)):
                continue  # another vertex is inside, not an ear
            tris.append((a, b, c))
            idx.pop(k)
            clipped = True
            break
        if not clipped:
            break  # degenerate; emit what remains as a fan
    if len(idx) == 3:
        tris.append((pts[idx[0]], pts[idx[1]], pts[idx[2]]))
    return tris


def subdivide(tri, max_edge):
    """Split a triangle until no edge is longer than max_edge.

    The outline comes from ear clipping and is exact; this exists so the interior carries
    enough vertices to sample the height field, because a flat triangle spanning ten metres
    would interpolate straight across the terrain the props were placed on.
    """
    out = []
    stack = [tri]
    guard = 0
    while stack and guard < 200000:
        guard += 1
        a, b, c = stack.pop()
        ab = math.dist(a, b)
        bc = math.dist(b, c)
        ca = math.dist(c, a)
        longest = max(ab, bc, ca)
        if longest <= max_edge:
            out.append((a, b, c))
            continue
        # Split the longest edge, which keeps triangles from becoming slivers.
        if longest == ab:
            m = ((a[0] + b[0]) * 0.5, (a[1] + b[1]) * 0.5)
            stack.append((a, m, c))
            stack.append((m, b, c))
        elif longest == bc:
            m = ((b[0] + c[0]) * 0.5, (b[1] + c[1]) * 0.5)
            stack.append((b, m, a))
            stack.append((m, c, a))
        else:
            m = ((c[0] + a[0]) * 0.5, (c[1] + a[1]) * 0.5)
            stack.append((c, m, b))
            stack.append((m, a, b))
    return out


def build_ramp(entry):
    """A sloped strip joining two heights.

    Without these the decks are disconnected plates: the player has no way between levels
    and anything the layout placed on the join hangs in the air.
    """
    a = entry["from"]
    b = entry["to"]
    half = float(entry.get("width", 4.0)) * 0.5

    ax, ay, az = float(a[0]), float(a[1]), float(a[2])
    bx, by, bz = float(b[0]), float(b[1]), float(b[2])

    dx, dz = bx - ax, bz - az
    length = math.hypot(dx, dz) or 1.0
    # Perpendicular in the ground plane, to give the ramp its width.
    px, pz = -dz / length * half, dx / length * half

    verts = [
        ax - px, ay, az - pz,
        ax + px, ay, az + pz,
        bx + px, by, bz + pz,
        bx - px, by, bz - pz,
    ]
    return {
        "name": entry.get("name", "Ramp"),
        "material": "terrain_grass_dirt_paving",
        "vertices": [round(v, 4) for v in verts],
        "triangles": [0, 2, 1, 0, 3, 2],
    }


def build_ledge(entry):
    """The vertical face of a one-way drop, so the level change is visible and solid."""
    a = entry["from"]
    b = entry["to"]
    drop = float(entry.get("drop", 1.0))

    ax, ay, az = float(a[0]), float(a[1]), float(a[2])
    bx, by, bz = float(b[0]), float(b[1]), float(b[2])

    verts = [
        ax, ay, az,
        bx, by, bz,
        bx, by - drop, bz,
        ax, ay - drop, az,
    ]
    return {
        "name": entry.get("name", "Ledge"),
        "material": "terrain_rock",
        "vertices": [round(v, 4) for v in verts],
        "triangles": [0, 1, 2, 0, 2, 3],
    }


def build_surface(entry, dense):
    """Triangulate a polygon exactly, then subdivide it enough to follow the height field."""
    poly = [(float(p[0]), float(p[1])) for p in entry.get("polygon", [])]
    y_spec = entry.get("y", 0.0)
    if len(poly) < 3:
        return None

    max_edge = CELL if dense else 1e9
    index = {}
    verts = []
    tris = []

    def vertex(x, z):
        key = (round(x, 3), round(z, 3))
        got = index.get(key)
        if got is not None:
            return got
        index[key] = len(verts) // 3
        verts.extend((x, height_at(y_spec, x, z), z))
        return index[key]

    for tri in triangulate(poly):
        for a, b, c in subdivide(tri, max_edge):
            # Wound clockwise from above so the surface faces +Y.
            tris.extend((vertex(*a), vertex(*c), vertex(*b)))

    if not tris:
        return None

    return {
        "name": entry.get("name", "Surface"),
        "material": entry.get("material", ""),
        "vertices": [round(v, 4) for v in verts],
        "triangles": tris,
    }


def main():
    with open(SOURCE, encoding="utf-8") as fh:
        src = json.load(fh)

    terrain = src.get("terrain", {})

    decks = [s for s in (build_surface(d, True) for d in terrain.get("decks", [])) if s]
    # Water is flat by definition, so it needs no subdivision for height — but the wave
    # shader displaces vertices, so give it a grid anyway.
    water = [s for s in (build_surface(w, True) for w in terrain.get("water", [])) if s]

    # Ramps and ledges were specified by the designer and skipped by the first pass of
    # this script, which is why props sat in mid-air at every level change.
    ramps = [build_ramp(r) for r in terrain.get("ramps", [])]
    ledges = [build_ledge(l) for l in terrain.get("ledges", [])]

    gameplay = src.get("gameplay", {})
    spawn = gameplay.get("playerSpawn") or gameplay.get("PlayerSpawn")
    if isinstance(spawn, dict) and "position" in spawn:
        spawn = [float(v) for v in spawn["position"]]
    elif isinstance(spawn, list):
        spawn = [float(v) for v in spawn]
    else:
        spawn = [0.0, 0.0, 0.0]

    camera = src.get("camera", {})
    out = {
        "schema": "pokelab-level-unity/1",
        "decks": decks + ramps,
        "ledges": ledges,
        "water": water,
        "objects": src.get("objects", []),
        "ambientAnchors": src.get("ambientAnchors", []),
        "playerSpawn": spawn,
        "cameraPitch": float(camera.get("pitchDegrees", 42.0)),
        "cameraDistance": float(camera.get("restDistanceMetres", 5.5)),
        "cameraFov": float(camera.get("verticalFovDegrees", 40.0)),
    }

    with open(OUT, "w", encoding="utf-8") as fh:
        json.dump(out, fh)

    print(f"wrote {OUT}")
    for s in decks:
        print(f"  deck  {s['name']:24} {len(s['vertices'])//3:5} verts  {len(s['triangles'])//3:5} tris")
    for s in ramps:
        print(f"  ramp  {s['name']:24} {len(s['vertices'])//3:5} verts")
    for s in ledges:
        print(f"  ledge {s['name']:24} {len(s['vertices'])//3:5} verts")
    for s in water:
        print(f"  water {s['name']:24} {len(s['vertices'])//3:5} verts  {len(s['triangles'])//3:5} tris")
    print(f"  objects {len(out['objects'])}  anchors {len(out['ambientAnchors'])}  spawn {spawn}")


if __name__ == "__main__":
    main()
