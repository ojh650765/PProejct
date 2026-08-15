"""Render and audit slice_layout.json. I cannot open Unity, so this is the verification pass.

    python Tools/Level/render_layout.py

Writes Tools/Level/preview/*.png plus audit.txt. The three-quarter renders use the shipped
camera angle (yaw 45, pitch 38) and draw the terrain grid and the objects in ONE depth-sorted
pass, so hills really do occlude what is behind them -- which is the only way to judge whether
the map has terrain rather than plates.

Drawn with PIL rather than matplotlib: the scratchpad venv ships a broken partial
python-dateutil, which makes `import matplotlib.pyplot` fail outright.
"""

from __future__ import annotations

import json
import math
import os
from collections import defaultdict

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
LAYOUT = os.path.join(REPO, "Assets", "Game", "Data", "Levels", "slice_layout.json")
BOUNDS = os.path.join(HERE, "asset_bounds.json")
OUT = os.path.join(HERE, "preview")

COLOURS = {
    "Tree": (47, 107, 52), "Bush": (76, 145, 71), "Fern": (63, 127, 74),
    "Grass": (127, 180, 90), "Flower": (217, 106, 160), "Reed": (111, 143, 58),
    "Lilypad": (63, 143, 90), "Moss": (95, 159, 85), "Vine": (61, 122, 68),
    "Rock": (129, 126, 118), "Cliff": (112, 100, 88), "Cave": (84, 76, 70),
    "Riverbank": (138, 122, 90), "Waterfall": (150, 188, 210), "Stepping": (150, 148, 140),
    "Bridge": (150, 110, 62), "House": (198, 104, 66), "Building": (86, 118, 176),
    "Fence": (176, 146, 100), "Lamp": (66, 66, 74), "Signpost": (150, 112, 62),
    "Bench": (162, 128, 78), "Crate": (176, 136, 78), "Barrel": (146, 110, 68),
    "Market": (198, 84, 84), "Well": (140, 142, 148), "Planter": (182, 110, 92),
    "Prop": (56, 190, 190),
}
ZONE_COLOUR = {"Zone_Town": (226, 132, 48), "Zone_Route": (52, 150, 52),
               "Zone_Lakeside": (48, 140, 200), "Zone_Cave": (132, 92, 186)}

MAT_COLOUR = {
    "grass": (126, 168, 92), "grass_dry": (152, 176, 104), "rock": (128, 120, 108),
    "road_dirt_route": (170, 132, 84), "trail_worn": (162, 138, 100),
    "road_cobble_town": (176, 168, 156), "road_flagstone": (188, 182, 170),
    "cave_gravel": (110, 100, 92), "shore_sand": (206, 190, 150),
    "water": (86, 148, 190), "water_deep": (56, 112, 158),
    "tall_grass": (86, 146, 68), "meadow": (140, 176, 100), "reeds": (108, 148, 78),
    "lilypads": (86, 148, 190), "moss": (104, 150, 92),
}


def font(size):
    for path in (r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\arial.ttf"):
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


# ---------------------------------------------------------------------------
class World:
    """Everything the renderer needs, resolved once."""

    def __init__(self, doc, bounds):
        self.doc = doc
        self.bounds = bounds
        hf = doc["terrain"]["heightField"]["grid"]
        self.gx0, self.gz0, self.step = hf["originX"], hf["originZ"], hf["step"]
        self.nx, self.nz = hf["countX"], hf["countZ"]
        self.grid = hf["rows"]
        self.paths = doc["paths"]
        self.fields = doc["foliageFields"]
        self.lake = None
        self.streams = []
        for w in doc["terrain"]["water"]:
            if w["kind"] == "still":
                self.lake = w
            else:
                self.streams.append(w)
        for o in doc["objects"]:
            key = os.path.splitext(os.path.basename(o["prefab"]))[0]
            info = bounds[key]
            s = o["scale"][0]
            o["_asset"] = key
            o["_sub"] = info["subfamily"]
            o["_w"] = info["size"][0] * s
            o["_d"] = info["size"][1] * s
            o["_h"] = info["size"][2] * s
        self._mat_cache = {}

    def h(self, ix, iz):
        return self.grid[max(0, min(self.nz - 1, iz))][max(0, min(self.nx - 1, ix))]

    def wx(self, ix):
        return self.gx0 + ix * self.step

    def wz(self, iz):
        return self.gz0 + iz * self.step

    # -- what material is this square metre? --------------------------------
    def material(self, x, z, y):
        best = None
        for p in self.paths:
            if p["name"] == "Path_CaveInterior":
                continue
            d = _dist_to_polyline(x, z, p["centreline"])
            if d < p["width"] * 0.5:
                if best is None or d < best[0]:
                    best = (d, p["material"])
        if best:
            return best[1]
        for w in self.streams:
            if _point_in_poly(x, z, w["polygon"]):
                return "water"
        if (self.lake and y <= self.lake["surfaceY"] + 0.05
                and _point_in_poly(x, z, self.lake["polygon"])):
            return "water_deep" if y < self.lake["surfaceY"] - 1.6 else "water"
        for sp in self.doc["terrain"]["surfacePatches"]:
            if _point_in_poly(x, z, sp["polygon"]):
                return sp["material"]
        for f in self.fields:
            if _point_in_poly(x, z, f["polygon"]):
                return f["kind"]
        return None

    def shaded(self, ix, iz):
        y = self.h(ix, iz)
        dx = (self.h(ix + 1, iz) - self.h(ix - 1, iz)) / (2 * self.step)
        dz = (self.h(ix, iz + 1) - self.h(ix, iz - 1)) / (2 * self.step)
        slope = math.degrees(math.atan(math.hypot(dx, dz)))
        mat = self.material(self.wx(ix), self.wz(iz), y)
        if mat is None:
            mat = "rock" if slope > 34 else ("grass_dry" if slope > 20 else "grass")
        base = MAT_COLOUR.get(mat, (150, 150, 150))
        # lambert from a north-west key, plus a height ramp so relief reads in plan too
        n = 1.0 / math.sqrt(dx * dx + dz * dz + 1.0)
        lam = 0.62 + 0.38 * max(0.0, (-0.45 * dx + 0.35 * dz + 0.82) * n)
        tint = 1.0 + 0.012 * max(-4.0, min(12.0, y))
        return tuple(int(max(0, min(255, c * lam * tint))) for c in base), slope, mat


def _dist_to_polyline(x, z, pts):
    best = 1e18
    for i in range(len(pts) - 1):
        ax, az = pts[i][0], pts[i][-1]
        bx, bz = pts[i + 1][0], pts[i + 1][-1]
        vx, vz = bx - ax, bz - az
        L2 = vx * vx + vz * vz
        t = 0.0 if L2 <= 1e-12 else max(0.0, min(1.0, ((x - ax) * vx + (z - az) * vz) / L2))
        best = min(best, math.hypot(x - (ax + vx * t), z - (az + vz * t)))
    return best


def _point_in_poly(x, z, poly):
    inside = False
    n = len(poly)
    for i in range(n):
        ax, az = poly[i][0], poly[i][1]
        bx, bz = poly[(i + 1) % n][0], poly[(i + 1) % n][1]
        if (az > z) != (bz > z):
            if x < (bx - ax) * (z - az) / ((bz - az) or 1e-12) + ax:
                inside = not inside
    return inside


class Canvas:
    def __init__(self, w, h, bg):
        self.img = Image.new("RGB", (w, h), bg)
        self.d = ImageDraw.Draw(self.img, "RGBA")

    def save(self, name):
        os.makedirs(OUT, exist_ok=True)
        self.img.save(os.path.join(OUT, name))
        print("  %s  %dx%d" % (name, self.img.width, self.img.height))


# ---------------------------------------------------------------------------
def render_plan(w):
    doc = w.doc
    e = doc["extents"]
    x0, x1, z0, z1 = e["minX"], e["maxX"], e["minZ"], e["maxZ"]
    ppm = 13.0
    pad = 56
    W = int((x1 - x0) * ppm) + pad * 2
    H = int((z1 - z0) * ppm) + pad * 2
    c = Canvas(W, H, (232, 228, 218))
    d = c.d

    def T(x, z):
        return (pad + (x - x0) * ppm, H - pad - (z - z0) * ppm)

    # terrain
    cell = w.step * ppm
    for iz in range(w.nz - 1):
        for ix in range(w.nx - 1):
            col, _slope, _mat = w.shaded(ix, iz)
            px, py = T(w.wx(ix), w.wz(iz))
            d.rectangle([px, py - cell, px + cell, py], fill=col)

    # contour lines every 2 m -- the cheapest proof the ground is not flat
    for iz in range(w.nz - 1):
        for ix in range(w.nx - 1):
            a, bq = w.h(ix, iz), w.h(ix + 1, iz)
            if math.floor(a / 2.0) != math.floor(bq / 2.0):
                px, py = T(w.wx(ix + 1), w.wz(iz))
                d.line([px, py, px, py - cell], fill=(60, 55, 48, 70), width=1)
            a, bq = w.h(ix, iz), w.h(ix, iz + 1)
            if math.floor(a / 2.0) != math.floor(bq / 2.0):
                px, py = T(w.wx(ix), w.wz(iz + 1))
                d.line([px, py, px + cell, py], fill=(60, 55, 48, 70), width=1)

    # cliffs
    for cl in doc["terrain"]["cliffs"]:
        pts = [T(p[0], p[2]) for p in cl["topPolyline"]]
        d.line(pts + [pts[0]], fill=(48, 40, 34, 230), width=3)

    # road centrelines
    for p in w.paths:
        pts = [T(q[0], q[2]) for q in p["centreline"]]
        d.line(pts, fill=(255, 255, 255, 110), width=max(2, int(p["width"] * ppm)),
               joint="curve")
        d.line(pts, fill=(190, 40, 40, 230), width=2, joint="curve")

    # foliage fields
    for f in w.fields:
        pts = [T(q[0], q[1]) for q in f["polygon"]]
        col = (40, 110, 30) if f["generatesEncounters"] else (90, 130, 60)
        d.polygon(pts, outline=col + (255,), width=3)
        cx = sum(q[0] for q in f["polygon"]) / len(f["polygon"])
        cz = sum(q[1] for q in f["polygon"]) / len(f["polygon"])
        if f["generatesEncounters"]:
            d.text(T(cx, cz), "TALL GRASS", fill=(16, 54, 10), font=font(13), anchor="mm",
                   stroke_width=3, stroke_fill=(230, 240, 210))

    # the cave, which is a hollow inside the massif and otherwise invisible in plan
    for cv in doc["terrain"]["caves"]:
        pts = [T(q[0], q[1]) for q in cv["floorPolygon"]]
        d.polygon(pts, fill=(60, 52, 48, 150), outline=(20, 16, 14, 255), width=3)
        mx, mz = cv["mouth"]["position"][0], cv["mouth"]["position"][2]
        d.text(T(mx - 8, mz + 6), "CAVE (inside the massif)", fill=(240, 236, 230),
               font=font(15), anchor="mm", stroke_width=3, stroke_fill=(30, 24, 20))
    for p in w.paths:
        if p["name"] != "Path_CaveInterior":
            continue
        pts = [T(q[0], q[2]) for q in p["centreline"]]
        d.line(pts, fill=(210, 196, 178, 200), width=max(2, int(p["width"] * ppm)),
               joint="curve")

    # objects
    by_sub = defaultdict(list)
    for o in doc["objects"]:
        by_sub[o["_sub"]].append(o)
    order = ["Moss", "Grass", "Flower", "Fern", "Bush", "Reed", "Stepping", "Rock", "Cave",
             "Vine", "Tree", "Fence", "Bench", "Crate", "Barrel", "Planter", "Lamp",
             "Signpost", "Market", "Well", "Bridge", "Waterfall", "Prop", "House", "Building"]
    for sub in order:
        col = COLOURS.get(sub, (136, 136, 136))
        for o in by_sub.get(sub, []):
            x, _, z = o["position"]
            if sub in ("House", "Building"):
                a = math.radians(-o["rotation"][1])
                hw, hd = o["_w"] / 2, o["_d"] / 2
                pts = [T(x + dx * math.cos(a) - dz * math.sin(a),
                         z + dx * math.sin(a) + dz * math.cos(a))
                       for dx, dz in ((-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd))]
                d.polygon(pts, fill=col + (255,), outline=(24, 24, 24), width=3)
                nx, nz = math.sin(math.radians(o["rotation"][1])), \
                    math.cos(math.radians(o["rotation"][1]))
                d.line([T(x, z), T(x + nx * (hd + 1.8), z + nz * (hd + 1.8))],
                       fill=(255, 255, 60, 255), width=3)
            elif sub == "Tree":
                r = o["_w"] / 2 * ppm
                cx, cy = T(x, z)
                d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=col + (150,))
                d.ellipse([cx - 3, cy - 3, cx + 3, cy + 3], fill=(58, 42, 26, 255))
            else:
                r = max(o["_w"], o["_d"]) / 2 * ppm
                cx, cy = T(x, z)
                d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=col + (235,))

    # zones
    for z in doc["zones"]:
        col = ZONE_COLOUR.get(z["name"], (85, 85, 85))
        for v in z["volumes"]:
            cx, _, cz = v["centre"]
            sx, _, sz = v["size"]
            d.rectangle([T(cx - sx / 2, cz + sz / 2), T(cx + sx / 2, cz - sz / 2)],
                        outline=col + (200,), width=3)
        cx, _, cz = z["volumes"][0]["centre"]
        sz = z["volumes"][0]["size"][2]
        d.text(T(cx, cz + sz / 2 - 2.0), z["displayName"].upper(), fill=col, font=font(26),
               anchor="mm", stroke_width=4, stroke_fill=(255, 255, 255))

    # gameplay
    for a in doc["ambientAnchors"]:
        x, _, zz = a["position"]
        kind = a["type"].split(".")[0]
        col = {"vfx": (255, 112, 208), "audio": (64, 208, 255),
               "light": (255, 208, 64)}.get(kind, (150, 150, 150))
        cx, cy = T(x, zz)
        d.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], fill=col + (255,),
                  outline=(30, 30, 30), width=2)
    for n in doc["gameplay"]["npcs"]:
        cx, cy = T(n["position"][0], n["position"][2])
        d.ellipse([cx - 8, cy - 8, cx + 8, cy + 8], fill=(248, 248, 248, 255),
                  outline=(20, 20, 20), width=3)
    for t in doc["gameplay"]["trainers"]:
        x, _, zz = t["position"]
        ang = math.radians(t["rotation"][1])
        half = math.radians(t["sightHalfAngle"])
        for s in (-half, 0.0, half):
            d.line([T(x, zz), T(x + math.sin(ang + s) * t["sightRange"],
                                zz + math.cos(ang + s) * t["sightRange"])],
                   fill=(224, 32, 32, 220), width=3)
        cx, cy = T(x, zz)
        d.ellipse([cx - 9, cy - 9, cx + 9, cy + 9], fill=(224, 32, 32, 255),
                  outline=(20, 20, 20), width=3)
    sp = doc["gameplay"]["playerSpawn"]["position"]
    cx, cy = T(sp[0], sp[2])
    d.ellipse([cx - 14, cy - 14, cx + 14, cy + 14], fill=(255, 232, 0, 255),
              outline=(0, 0, 0), width=4)

    d.text((pad, 14),
           "Poke Lab vertical slice -- PLAN.  %d objects, %d foliage fields (%d instances), "
           "%d roads / %.0f m.  Ground is the baked height field, shaded, 2 m contours.  "
           "Red = road centreline, white halo = road width.  Yellow spur off a building = "
           "the face it presents to the street."
           % (doc["counts"]["objects"], doc["counts"]["foliageFields"],
              doc["counts"]["foliageInstanceBudget"], doc["counts"]["paths"],
              doc["counts"]["pathMetres"]),
           fill=(28, 28, 28), font=font(23))
    c.save("plan.png")


# ---------------------------------------------------------------------------
def render_three_quarter(w, name, title, focus=None, span=None, ppm=30.0):
    """One depth-sorted pass over terrain quads AND objects at the shipped camera angle."""
    doc = w.doc
    yaw, pitch = math.radians(45.0), math.radians(38.0)
    cp, sp_ = math.cos(pitch), math.sin(pitch)
    cy, sy = math.cos(yaw), math.sin(yaw)

    def project(x, y, z):
        # camera at yaw 45 sits SOUTH-WEST of what it looks at and tilts down by `pitch`,
        # so screen-up = (0, cos p, sin p) and view-forward = (0, -sin p, cos p).
        # Getting the sign of the zr terms wrong mirrors the whole world and quietly
        # reverses which faces of the buildings you are judging.
        xr = x * cy - z * sy
        zr = x * sy + z * cy
        return xr, y * cp + zr * sp_, zr * cp - y * sp_

    items = []
    step = w.step
    for iz in range(w.nz - 1):
        for ix in range(w.nx - 1):
            x, z = w.wx(ix), w.wz(iz)
            if focus and (abs(x - focus[0]) > span or abs(z - focus[1]) > span):
                continue
            ys = [w.h(ix, iz), w.h(ix + 1, iz), w.h(ix + 1, iz + 1), w.h(ix, iz + 1)]
            quad = [(x, ys[0], z), (x + step, ys[1], z),
                    (x + step, ys[2], z + step), (x, ys[3], z + step)]
            proj = [project(*q) for q in quad]
            col, _slope, mat = w.shaded(ix, iz)
            depth = sum(p[2] for p in proj) / 4.0
            items.append((depth, "quad", proj, col))
            if mat in ("water", "water_deep") and w.lake:
                sy_ = w.lake["surfaceY"]
                surf = [project(q[0], sy_, q[2]) for q in quad]
                items.append((depth - 0.02, "quad", surf, (92, 160, 200)))

    # Cutaway floor for the cave: it is a hollow inside the massif, so without this its
    # contents hang in mid-air and there is no way to judge them.
    for cv in doc["terrain"]["caves"]:
        poly = cv["floorPolygon"]
        cx = sum(q[0] for q in poly) / len(poly)
        cz = sum(q[1] for q in poly) / len(poly)
        if focus and (abs(cx - focus[0]) > span + 14 or abs(cz - focus[1]) > span + 14):
            continue
        proj = [project(q[0], cv["floorY"], q[1]) for q in poly]
        items.append((min(p[2] for p in proj) - 60.0, "quad", proj, (74, 64, 58)))

    for o in doc["objects"]:
        x, y, z = o["position"]
        if focus and (abs(x - focus[0]) > span or abs(z - focus[1]) > span):
            continue
        sx, sy2, depth = project(x, y, z)
        if o["parent"].startswith("Cave/") and _point_in_poly(x, z,
                                                              doc["terrain"]["caves"][0]
                                                              ["floorPolygon"]):
            depth -= 60.0
        # bias objects a little toward the camera: an object and the terrain quad it stands
        # on have the same depth, and without this the quad paints over it
        items.append((depth - 0.8 - o["_h"] * 1.35, "obj", (sx, sy2), o))

    sel = [i for i in items]
    xs = [i[2][0] if i[1] == "obj" else min(p[0] for p in i[2]) for i in sel]
    xs2 = [i[2][0] if i[1] == "obj" else max(p[0] for p in i[2]) for i in sel]
    ys = [i[2][1] if i[1] == "obj" else min(p[1] for p in i[2]) for i in sel]
    ys2 = [(i[2][1] + 6) if i[1] == "obj" else max(p[1] for p in i[2]) for i in sel]
    X0, X1 = min(xs) - 2, max(xs2) + 2
    Y0, Y1 = min(ys) - 2, max(ys2) + 3
    pad = 46
    W = int((X1 - X0) * ppm) + pad * 2
    H = int((Y1 - Y0) * ppm) + pad * 2
    W, H = min(W, 5200), min(H, 5200)
    c = Canvas(W, H, (176, 200, 222))
    d = c.d

    def T(sx, sy_):
        return (pad + (sx - X0) * ppm, H - pad - (sy_ - Y0) * ppm)

    items.sort(key=lambda t: -t[0])
    for depth, kind, payload, extra in items:
        if kind == "quad":
            d.polygon([T(p[0], p[1]) for p in payload], fill=extra)
            continue
        sx, sy2 = payload
        o = extra
        col = COLOURS.get(o["_sub"], (136, 136, 136))
        wid = max(o["_w"], o["_d"])
        hgt = o["_h"] * cp
        sub = o["_sub"]
        if sub == "Tree":
            a = T(sx - wid * 0.055, sy2)
            bq = T(sx + wid * 0.055, sy2 + hgt * 0.45)
            d.rectangle([min(a[0], bq[0]), min(a[1], bq[1]),
                         max(a[0], bq[0]), max(a[1], bq[1])], fill=(72, 52, 32, 255))
            a = T(sx - wid / 2, sy2 + hgt * 0.36)
            bq = T(sx + wid / 2, sy2 + hgt)
            d.ellipse([min(a[0], bq[0]), min(a[1], bq[1]),
                       max(a[0], bq[0]), max(a[1], bq[1])],
                      fill=col + (255,), outline=(28, 56, 28), width=2)
        elif sub in ("House", "Building", "Market", "Well", "Bridge", "Waterfall", "Lamp",
                     "Signpost", "Cave", "Stepping"):
            a = T(sx - wid / 2, sy2)
            bq = T(sx + wid / 2, sy2 + max(hgt, 0.05))
            d.rectangle([min(a[0], bq[0]), min(a[1], bq[1]),
                         max(a[0], bq[0]), max(a[1], bq[1])],
                        fill=col + (255,), outline=(30, 30, 38), width=2)
        else:
            a = T(sx - wid / 2, sy2)
            bq = T(sx + wid / 2, sy2 + max(hgt, 0.06))
            d.ellipse([min(a[0], bq[0]), min(a[1], bq[1]),
                       max(a[0], bq[0]), max(a[1], bq[1])], fill=col + (245,))
    d.text((pad, 12), title, fill=(18, 18, 18), font=font(26),
           stroke_width=3, stroke_fill=(235, 240, 245))
    c.save(name)


# ---------------------------------------------------------------------------
def audit(w):
    doc = w.doc
    out = []
    objs = doc["objects"]

    out.append("AUDIT -- checked against the five failures of the rejected layout")
    out.append("=" * 78)

    # 1. paths as tiles
    tiles = [o for o in objs if o["_sub"] == "Path"]
    out.append("1. PATHS AS A RASH OF TILES")
    out.append("   path-tile objects placed: %d   (rejected layout: 226)" % len(tiles))
    out.append("   roads declared as splines: %d, %.0f m total"
               % (len(doc["paths"]), doc["counts"]["pathMetres"]))
    for p in doc["paths"]:
        out.append("     %-22s w=%.1f  blend=%.1f  %5.1f m  %d centreline points"
                   % (p["name"], p["width"], p["edgeBlend"], p["lengthMetres"],
                      len(p["centreline"])))

    # 2. flatness
    hs = [v for row in w.grid for v in row]
    walk = []
    for iz in range(0, w.nz, 2):
        for ix in range(0, w.nx, 2):
            # w.material returns None for "unassigned backdrop"; only cells that carry a
            # road, patch or foliage field are ground the player is meant to stand on
            mat = w.material(w.wx(ix), w.wz(iz), w.h(ix, iz))
            if mat is not None and "water" not in mat and "lilypad" not in mat:
                walk.append(w.shaded(ix, iz)[1])
    out.append("")
    out.append("2. EVERYTHING WAS FLAT")
    out.append("   height range %.2f .. %.2f m over %d x %d samples"
               % (min(hs), max(hs), w.nx, w.nz))
    band = defaultdict(int)
    for v in hs:
        band[int(math.floor(v / 2.0)) * 2] += 1
    for k in sorted(band):
        out.append("     %+5d..%+5d m  %6d cells  %s"
                   % (k, k + 2, band[k], "#" * int(band[k] / max(1, len(hs)) * 90)))
    slopes = []
    for iz in range(0, w.nz, 2):
        for ix in range(0, w.nx, 2):
            _c, s, _m = w.shaded(ix, iz)
            slopes.append(s)
    slopes.sort()
    out.append("   slope percentiles (deg): p50 %.1f  p75 %.1f  p90 %.1f  p99 %.1f"
               % (slopes[len(slopes) // 2], slopes[int(len(slopes) * .75)],
                  slopes[int(len(slopes) * .90)], slopes[int(len(slopes) * .99)]))
    walk.sort()
    out.append("   walkable-surface slope (deg), sampled where a material is assigned: "
               "p50 %.1f  p90 %.1f  p99 %.1f"
               % (walk[len(walk) // 2], walk[int(len(walk) * .90)], walk[int(len(walk) * .99)]))
    flat = sum(1 for s in slopes if s < 1.0) / len(slopes)
    out.append("   fraction of the map flatter than 1 deg: %.1f%%  "
               "(a deck-based layout would be near 100%%)" % (flat * 100))

    # 3. buildings composed
    out.append("")
    out.append("3. BUILDINGS SCATTERED RATHER THAN COMPOSED")
    town_paths = [p for p in doc["paths"] if p["name"].startswith("Path_Town")]
    plaza = next(sp for sp in doc["terrain"]["surfacePatches"]
                 if sp["name"] == "Patch_Plaza")
    px = sum(q[0] for q in plaza["polygon"]) / len(plaza["polygon"])
    pz = sum(q[1] for q in plaza["polygon"]) / len(plaza["polygon"])
    for o in objs:
        if o["_sub"] not in ("House", "Building"):
            continue
        x, _, z = o["position"]
        d_street = min(_dist_to_polyline(x, z, p["centreline"]) for p in town_paths)
        yaw = math.radians(o["rotation"][1])
        fx, fz = math.sin(yaw), math.cos(yaw)
        # A building may address the street it stands on OR the plaza; both count as
        # composed. What is not allowed is facing neither.
        targets = [_nearest_point(x, z, p["centreline"]) for p in town_paths]
        targets.append([px, 0.0, pz])
        best = -2.0
        for t in targets:
            vx, vz = t[0] - x, t[-1] - z
            L = math.hypot(vx, vz) or 1.0
            best = max(best, (fx * vx + fz * vz) / L)
        out.append("   %-22s setback %4.1f m, best front-to-frontage cos = %+.2f %s"
                   % (o["name"], d_street, best, "OK" if best > 0.45 else "<-- CHECK"))

    # 4. floating / sunken
    out.append("")
    out.append("4. OBJECTS FLOATING WHERE THERE IS NO GROUND")
    worst = []
    cave = doc["terrain"]["caves"][0]
    for o in objs:
        x, y, z = o["position"]
        if _point_in_poly(x, z, cave["floorPolygon"]) or o["parent"].startswith("Cave/"):
            continue
        if o["_asset"].startswith(("Env_Cave_Stalactite", "Env_Vine")):
            continue
        # these three exist precisely to span a hole in the ground or to cap a cliff,
        # so measuring them against the terrain underneath is meaningless
        if o["name"].startswith(("Lake_WaterfallLip", "Lake_SteppingStones", "Route_Bridge")):
            continue
        gy = _bilinear(w, x, z)
        worst.append((abs(y - gy), o["name"], round(y - gy, 3)))
    worst.sort(reverse=True)
    out.append("   objects checked against the baked height field: %d" % len(worst))
    out.append("   max |offset| %.3f m, mean %.4f m"
               % (worst[0][0], sum(t[0] for t in worst) / len(worst)))
    for t in worst[:6]:
        out.append("     %-28s %+.3f m" % (t[1], t[2]))

    # 5. density with intent
    out.append("")
    out.append("5. DENSITY WITHOUT INTENT")
    subs = defaultdict(int)
    for o in objs:
        subs[o["_sub"]] += 1
    out.append("   authored objects: %d   (rejected layout: 4682, of which 2201 grass)"
               % len(objs))
    out.append("   ground cover moved to %d declared foliage fields, %d instances, "
               "rendering=instanced"
               % (len(w.fields), doc["counts"]["foliageInstanceBudget"]))
    out.append("   by subfamily: " + ", ".join("%s %d" % kv for kv in
                                               sorted(subs.items(), key=lambda kv: -kv[1])))
    area = (doc["extents"]["maxX"] - doc["extents"]["minX"]) * \
           (doc["extents"]["maxZ"] - doc["extents"]["minZ"])
    out.append("   authored object density: %.3f / m2 over %.0f m2" % (len(objs) / area, area))

    # road corridor emptiness -- the "how much empty walkable space" check
    blocker_names = []
    for o in objs:
        if o["_sub"] in ("Grass", "Flower", "Moss", "Lilypad", "Bridge", "Stepping"):
            continue
        # hangs from the ceiling or spans the path on purpose
        if o["_asset"].startswith(("Env_Cave_Stalactite", "Env_Vine", "Env_Cave_Arch")):
            continue
        x, _, z = o["position"]
        for p in doc["paths"]:
            if _dist_to_polyline(x, z, p["centreline"]) < p["width"] * 0.5:
                blocker_names.append(o["name"])
                break
    out.append("   objects standing inside a road corridor: %d  %s"
               % (len(blocker_names), ", ".join(sorted(blocker_names)[:12])))

    # solid-vs-solid overlaps
    SOLID = {"House", "Building", "Market", "Well", "Bench", "Crate", "Barrel", "Planter",
             "Lamp", "Signpost", "Rock", "Bridge", "Prop", "Waterfall", "Fence"}
    solid = [o for o in objs if o["_sub"] in SOLID]
    clashes = []
    for i, a in enumerate(solid):
        for bo in solid[i + 1:]:
            if abs(a["position"][1] - bo["position"][1]) > 2.5:
                continue
            need = min(a["_w"], a["_d"]) * 0.40 + min(bo["_w"], bo["_d"]) * 0.40
            dd = math.dist((a["position"][0], a["position"][2]),
                           (bo["position"][0], bo["position"][2]))
            if dd < need - 0.05:
                clashes.append((a["name"], bo["name"], round(dd, 2), round(need, 2)))
    out.append("   solid-vs-solid overlaps: %d" % len(clashes))
    for x in clashes[:8]:
        out.append("     %s <-> %s  d=%.2f need %.2f" % x)

    # connectivity: is every gameplay anchor standable?
    out.append("")
    out.append("6. STANDABILITY OF ANCHORS")
    anchors = [("playerSpawn", doc["gameplay"]["playerSpawn"]["position"])]
    for s in doc["gameplay"]["spawnPoints"]:
        anchors.append((s["name"], s["position"]))
    for t in doc["gameplay"]["trainers"]:
        anchors.append((t["name"], t["position"]))
    for nm, pos in anchors:
        if nm.startswith(("Spawn_Cave", "Trainer_Cave")):
            out.append("   %-26s (cave floor, not height-field)" % nm)
            continue
        s = _slope_at(w, pos[0], pos[2])
        out.append("   %-26s slope %4.1f deg  %s" % (nm, s, "OK" if s < 30 else "<-- STEEP"))

    txt = "\n".join(out)
    os.makedirs(OUT, exist_ok=True)
    open(os.path.join(OUT, "audit.txt"), "w", encoding="utf-8").write(txt)
    print(txt)


def _bilinear(w, x, z):
    fx = max(0.0, min((x - w.gx0) / w.step, w.nx - 1.001))
    fz = max(0.0, min((z - w.gz0) / w.step, w.nz - 1.001))
    ix, iz = int(fx), int(fz)
    tx, tz = fx - ix, fz - iz
    a = w.grid[iz][ix] * (1 - tx) + w.grid[iz][ix + 1] * tx
    b = w.grid[iz + 1][ix] * (1 - tx) + w.grid[iz + 1][ix + 1] * tx
    return a * (1 - tz) + b * tz


def _slope_at(w, x, z):
    h = w.step
    dx = (_bilinear(w, x + h, z) - _bilinear(w, x - h, z)) / (2 * h)
    dz = (_bilinear(w, x, z + h) - _bilinear(w, x, z - h)) / (2 * h)
    return math.degrees(math.atan(math.hypot(dx, dz)))


def _nearest_point(x, z, pts):
    best = (1e18, pts[0])
    for p in pts:
        d = math.hypot(x - p[0], z - p[-1])
        if d < best[0]:
            best = (d, p)
    return best[1]


def main():
    doc = json.load(open(LAYOUT, encoding="utf-8"))
    bounds = json.load(open(BOUNDS, encoding="utf-8"))
    w = World(doc, bounds)
    render_plan(w)
    render_three_quarter(w, "threequarter_overview.png",
                         "OVERVIEW at the shipped camera (yaw 45, pitch 38). "
                         "Town lower-left, route through the middle, gorge and cave upper-"
                         "left, lake right.", ppm=14.0)
    render_three_quarter(w, "threequarter_town.png",
                         "ASTER TOWN. One street, a plaza, the lab staged at its head on "
                         "the 45 deg camera axis; the terrace cliff should read as a step.",
                         focus=(-14, -12), span=24, ppm=34.0)
    render_three_quarter(w, "threequarter_route.png",
                         "ROUTE 1 + CROSSROADS. Road ribbon, tall-grass fields at its edge, "
                         "massif wall north, ledge shelf south.",
                         focus=(8, 16), span=22, ppm=34.0)
    render_three_quarter(w, "threequarter_lake.png",
                         "LAKESIDE. Bridge, stream, beach, west-shore trail and the bowl.",
                         focus=(40, 38), span=24, ppm=30.0)
    render_three_quarter(w, "threequarter_gorge.png",
                         "GORGE + CAVE MOUTH. The pinch between the two massifs; the "
                         "cave beyond is drawn as a cutaway inside the hill.",
                         focus=(-2, 46), span=26, ppm=28.0)
    audit(w)


if __name__ == "__main__":
    main()
