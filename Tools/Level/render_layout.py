"""Render and audit slice_layout.json. I cannot open Unity, so this is the verification pass.

    python Tools/Level/render_layout.py

Writes Tools/Level/preview/*.png plus audit.txt, covering overlapping objects, floating or
sunken pivots, grid-like regularity, empty regions and blocked paths.

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
MANIFEST = os.path.join(REPO, "Assets", "Game", "Art", "Environment", "environment_manifest.json")
OUT = os.path.join(HERE, "preview")

COLOURS = {
    "Tree": (47, 107, 52), "Bush": (76, 145, 71), "Fern": (63, 127, 74),
    "Grass": (127, 180, 90), "Flower": (217, 106, 160), "Reed": (111, 143, 58),
    "Lilypad": (63, 143, 90), "Moss": (95, 159, 85), "Vine": (61, 122, 68),
    "Rock": (125, 127, 134), "Cliff": (93, 95, 104), "Cave": (74, 77, 87),
    "Riverbank": (138, 122, 90), "Waterfall": (106, 144, 168), "Stepping": (138, 141, 146),
    "Bridge": (138, 106, 58), "House": (196, 100, 63), "Building": (74, 127, 176),
    "Fence": (154, 122, 74), "Lamp": (58, 61, 70), "Signpost": (138, 106, 58),
    "Bench": (154, 122, 74), "Crate": (168, 131, 74), "Barrel": (138, 106, 66),
    "Market": (192, 80, 80), "Well": (138, 141, 146), "Planter": (176, 106, 90),
    "Path": (184, 178, 164), "Prop": (48, 176, 176),
}
ZONE_COLOUR = {"Zone_Town": (224, 138, 60), "Zone_Route": (60, 150, 60),
               "Zone_Lakeside": (63, 143, 192), "Zone_Cave": (122, 90, 176)}

DRAW_ORDER = ["Path", "Moss", "Lilypad", "Grass", "Flower", "Fern", "Bush", "Reed", "Stepping",
              "Riverbank", "Rock", "Cave", "Cliff", "Vine", "Tree", "Fence", "Bench", "Crate",
              "Barrel", "Planter", "Lamp", "Signpost", "Market", "Well", "Bridge", "Waterfall",
              "Prop", "House", "Building"]


def font(size):
    for path in (r"C:\Windows\Fonts\segoeui.ttf", r"C:\Windows\Fonts\arial.ttf"):
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def load():
    doc = json.load(open(LAYOUT, encoding="utf-8"))
    bounds = json.load(open(BOUNDS, encoding="utf-8"))
    for o in doc["objects"]:
        key = os.path.splitext(os.path.basename(o["prefab"]))[0]
        info = bounds[key]
        o["_asset"] = key
        o["_sub"] = info["subfamily"]
        s = o["scale"][0]
        o["_w"] = info["size"][0] * s
        o["_d"] = info["size"][1] * s
        o["_h"] = info["size"][2] * s
        o["_hang"] = "top anchor" in info["pivot"]
    return doc, bounds


class Canvas:
    def __init__(self, w, h, bg):
        self.img = Image.new("RGB", (w, h), bg)
        self.d = ImageDraw.Draw(self.img, "RGBA")

    def save(self, name):
        self.img.save(os.path.join(OUT, name))


# ---------------------------------------------------------------------------
def render_plan(doc):
    x0, x1, z0, z1 = -70.0, 96.0, -32.0, 62.0
    ppm = 15.0
    pad = 60
    W = int((x1 - x0) * ppm) + pad * 2
    H = int((z1 - z0) * ppm) + pad * 2
    c = Canvas(W, H, (234, 230, 220))
    d = c.d

    def T(x, z):
        return (pad + (x - x0) * ppm, H - pad - (z - z0) * ppm)

    def poly(points, fill=None, outline=None, width=1):
        d.polygon([T(px, pz) for px, pz in points], fill=fill, outline=outline, width=width)

    def disc(x, z, r, fill, outline=None, width=1):
        cx, cy = T(x, z)
        rp = max(r * ppm, 1.0)
        d.ellipse([cx - rp, cy - rp, cx + rp, cy + rp], fill=fill, outline=outline, width=width)

    # grid
    for gx in range(int(x0 // 10) * 10, int(x1) + 1, 10):
        d.line([T(gx, z0), T(gx, z1)], fill=(255, 255, 255, 90), width=1)
    for gz in range(int(z0 // 10) * 10, int(z1) + 1, 10):
        d.line([T(x0, gz), T(x1, gz)], fill=(255, 255, 255, 90), width=1)

    for deck in doc["terrain"]["decks"]:
        if deck.get("facesDown"):
            continue
        poly(deck["polygon"], fill=(216, 210, 194, 220), outline=(154, 148, 132), width=2)
    for w in doc["terrain"]["water"]:
        poly(w["polygon"], fill=(127, 182, 216, 235), outline=(74, 134, 168), width=3)

    by_sub = defaultdict(list)
    for o in doc["objects"]:
        by_sub[o["_sub"]].append(o)

    for sub in DRAW_ORDER:
        col = COLOURS.get(sub, (136, 136, 136))
        for o in by_sub.get(sub, []):
            x, _, z = o["position"]
            if sub in ("House", "Building"):
                a = math.radians(-o["rotation"][1])
                hw, hd = o["_w"] / 2, o["_d"] / 2
                pts = [(x + dx * math.cos(a) - dz * math.sin(a),
                        z + dx * math.sin(a) + dz * math.cos(a))
                       for dx, dz in ((-hw, -hd), (hw, -hd), (hw, hd), (-hw, hd))]
                poly(pts, fill=col + (255,), outline=(24, 24, 24), width=3)
            elif sub == "Tree":
                disc(x, z, o["_w"] / 2, col + (130,))
                disc(x, z, 0.32, (58, 42, 26, 255))
            elif sub in ("Cliff", "Riverbank"):
                a = math.radians(-o["rotation"][1])
                # module pivot is its corner: +X length, -Z depth in Unity
                corners = [(0, 0), (o["_w"], 0), (o["_w"], -o["_d"]), (0, -o["_d"])]
                pts = [(x + dx * math.cos(a) - dz * math.sin(a),
                        z + dx * math.sin(a) + dz * math.cos(a)) for dx, dz in corners]
                poly(pts, fill=col + (255,), outline=(48, 50, 60), width=1)
            else:
                alpha = 190 if sub in ("Grass", "Flower", "Fern", "Bush", "Reed", "Moss") else 235
                disc(x, z, max(o["_w"], o["_d"]) / 2, col + (alpha,))

    # walkable spine
    for p in doc["paths"]:
        pts = [T(pt[0], pt[2]) for pt in p["points"]]
        d.line(pts, fill=(255, 255, 255, 120), width=int(p["halfWidth"] * 2 * ppm), joint="curve")
    for p in doc["paths"]:
        pts = [T(pt[0], pt[2]) for pt in p["points"]]
        d.line(pts, fill=(200, 32, 32, 255), width=3, joint="curve")

    # tall grass triggers
    for g in doc["gameplay"]["tallGrassPatches"]:
        cx, _, cz = g["centre"]
        sx, _, sz = g["triggerSize"]
        a, bpt = T(cx - sx / 2, cz + sz / 2), T(cx + sx / 2, cz - sz / 2)
        d.ellipse([a, bpt], fill=(168, 216, 74, 90), outline=(74, 122, 18), width=4)
        d.text(T(cx, cz), "TALL GRASS", fill=(30, 60, 8), font=font(15), anchor="mm")

    # zone volumes
    for z in doc["zones"]:
        col = ZONE_COLOUR.get(z["name"], (85, 85, 85))
        for v in z["volumes"]:
            cx, _, cz = v["centre"]
            sx, _, sz = v["size"]
            a, bpt = T(cx - sx / 2, cz + sz / 2), T(cx + sx / 2, cz - sz / 2)
            d.rectangle([a, bpt], outline=col + (255,), width=4)
        cx, _, cz = z["volumes"][0]["centre"]
        d.text(T(cx, cz + z["volumes"][0]["size"][2] / 2 - 2.5), z["displayName"].upper(),
               fill=col, font=font(30), anchor="mm",
               stroke_width=4, stroke_fill=(255, 255, 255))

    # anchors
    for a in doc["ambientAnchors"]:
        x, _, zz = a["position"]
        kind = a["type"].split(".")[0]
        col = {"vfx": (255, 112, 208), "audio": (64, 208, 255),
               "light": (255, 208, 64)}.get(kind, (150, 150, 150))
        disc(x, zz, 0.55, col + (255,), outline=(40, 40, 40), width=1)
    for n in doc["gameplay"]["npcs"]:
        disc(n["position"][0], n["position"][2], 0.8, (245, 245, 245, 255), (24, 24, 24), 3)
    for t in doc["gameplay"]["trainers"]:
        x, _, zz = t["position"]
        ang = math.radians(t["rotation"][1])
        half = math.radians(t["sightHalfAngle"])
        for s in (-half, 0.0, half):
            d.line([T(x, zz), T(x + math.sin(ang + s) * t["sightRange"],
                                zz + math.cos(ang + s) * t["sightRange"])],
                   fill=(224, 32, 32, 220), width=3)
        disc(x, zz, 1.0, (224, 32, 32, 255), (24, 24, 24), 3)
    sp = doc["gameplay"]["playerSpawn"]["position"]
    disc(sp[0], sp[2], 1.5, (255, 235, 0, 255), (0, 0, 0), 4)

    d.text((pad, 16), "Poke Lab vertical slice -- PLAN.  %d objects, %d ambient anchors.  "
                      "Red dashed = walkable spine (white halo = corridor width).  "
                      "Yellow star = player spawn, red = trainers + sight cones, white = NPCs.  "
                      "Pink = VFX anchor, cyan = audio, amber = light."
           % (doc["counts"]["objects"], doc["counts"]["ambientAnchors"]),
           fill=(30, 30, 30), font=font(26))
    c.save("plan.png")


# ---------------------------------------------------------------------------
def render_three_quarter(doc, yaw_deg, pitch_deg, name, title, bounds_screen=None, ppm=26.0,
                         zone=None):
    """Painter-sorted projection at the shipped camera pitch. This is what judges massing,
    layering and silhouette -- the plan view cannot show verticality at all."""
    p = math.radians(pitch_deg)
    yr = math.radians(yaw_deg)
    cp, sp_ = math.cos(p), math.sin(p)

    def project(x, y, z):
        xr = x * math.cos(yr) - z * math.sin(yr)
        zr = x * math.sin(yr) + z * math.cos(yr)
        return xr, y * cp - zr * sp_, zr * cp + y * sp_

    items = []
    for o in doc["objects"]:
        x, y, z = o["position"]
        sx, sy, depth = project(x, y, z)
        items.append((depth, sx, sy, o))
    if not bounds_screen:
        # Auto-fit, optionally to one zone. Hand-picked screen-space crops are guesswork once
        # the yaw changes, so let the projected objects decide the frame.
        sel = [i for i in items if zone is None or i[3]["parent"].startswith(zone)]
        xs = [i[1] for i in sel]
        ys = [i[2] for i in sel]
        bounds_screen = (min(xs) - 5, max(xs) + 5, min(ys) - 4, max(ys) + 10)
    X0, X1, Y0, Y1 = bounds_screen
    pad = 50
    W = int((X1 - X0) * ppm) + pad * 2
    H = int((Y1 - Y0) * ppm) + pad * 2
    c = Canvas(W, H, (185, 203, 220))
    d = c.d

    def T(sx, sy):
        return (pad + (sx - X0) * ppm, H - pad - (sy - Y0) * ppm)

    for deck in doc["terrain"]["decks"]:
        if deck.get("facesDown"):
            continue
        yv = deck["y"] if isinstance(deck["y"], (int, float)) else 0.0
        pts = [T(*project(px, yv, pz)[:2]) for px, pz in deck["polygon"]]
        d.polygon(pts, fill=(207, 216, 184, 255), outline=(143, 152, 120), width=2)
    for w in doc["terrain"]["water"]:
        pts = [T(*project(px, w["surfaceY"], pz)[:2]) for px, pz in w["polygon"]]
        d.polygon(pts, fill=(111, 176, 216, 255), outline=(63, 127, 168), width=2)

    items.sort(key=lambda t: -t[0])
    for depth, sx, sy, o in items:
        if not (X0 - 8 < sx < X1 + 8 and Y0 - 8 < sy < Y1 + 8):
            continue
        col = COLOURS.get(o["_sub"], (136, 136, 136))
        w = max(o["_w"], o["_d"])
        h = o["_h"] * cp
        if o["_hang"]:
            sy -= h
        sub = o["_sub"]
        if sub == "Tree":
            x0p, y0p = T(sx - w * 0.06, sy)
            x1p, y1p = T(sx + w * 0.06, sy + h * 0.5)
            d.rectangle([min(x0p, x1p), min(y0p, y1p), max(x0p, x1p), max(y0p, y1p)],
                        fill=(74, 53, 32, 255))
            a = T(sx - w / 2, sy + h * 0.42)
            bq = T(sx + w / 2, sy + h)
            d.ellipse([min(a[0], bq[0]), min(a[1], bq[1]), max(a[0], bq[0]), max(a[1], bq[1])],
                      fill=col + (255,), outline=(29, 58, 29), width=2)
        elif sub in ("House", "Building", "Market", "Well", "Cliff", "Cave", "Riverbank",
                     "Bridge", "Waterfall", "Lamp", "Signpost"):
            a = T(sx - w / 2, sy)
            bq = T(sx + w / 2, sy + max(h, 0.05))
            d.rectangle([min(a[0], bq[0]), min(a[1], bq[1]), max(a[0], bq[0]), max(a[1], bq[1])],
                        fill=col + (255,), outline=(32, 32, 42), width=2)
        else:
            a = T(sx - w / 2, sy)
            bq = T(sx + w / 2, sy + max(h, 0.06))
            d.ellipse([min(a[0], bq[0]), min(a[1], bq[1]), max(a[0], bq[0]), max(a[1], bq[1])],
                      fill=col + (240,))
    d.text((pad, 16), title, fill=(20, 20, 20), font=font(28))
    c.save(name)


# ---------------------------------------------------------------------------
def _in_poly(x, z, poly):
    inside = False
    n = len(poly)
    for i in range(n):
        ax, az = poly[i]
        bx, bz = poly[(i + 1) % n]
        if (az > z) != (bz > z) and x < (bx - ax) * (z - az) / ((bz - az) or 1e-9) + ax:
            inside = not inside
    return inside


def audit(doc):
    out = []
    objs = doc["objects"]
    SOLID = {"House", "Building", "Market", "Well", "Bench", "Crate", "Barrel", "Planter",
             "Lamp", "Signpost", "Rock", "Bridge", "Prop", "Waterfall"}

    # Props authored to sit ON another prop are stacked on purpose, not clashing.
    STACKED = ("Town_Scanner", "Town_LabBallDisplay", "Town_MarketBallDisplay")
    solid = [o for o in objs if o["_sub"] in SOLID and not o["name"].startswith(STACKED)]
    clashes = []
    for i, a in enumerate(solid):
        for bo in solid[i + 1:]:
            if abs(a["position"][1] - bo["position"][1]) > 2.5:
                continue
            need = min(a["_w"], a["_d"]) * 0.42 + min(bo["_w"], bo["_d"]) * 0.42
            dd = math.dist((a["position"][0], a["position"][2]),
                           (bo["position"][0], bo["position"][2]))
            if dd < need - 0.05:
                clashes.append((a["name"], bo["name"], round(dd, 2), round(need, 2)))
    out.append("1. solid-vs-solid overlaps: %d" % len(clashes))
    for x in clashes[:10]:
        out.append("     %s <-> %s  d=%.2f need %.2f" % x)

    buildings = [o for o in objs if o["_sub"] in ("House", "Building")]
    inside = []
    for o in objs:
        if o["_sub"] in ("House", "Building", "Path", "Cliff"):
            continue
        for bl in buildings:
            if abs(o["position"][1] - bl["position"][1]) > 2.5:
                continue
            a = math.radians(bl["rotation"][1])
            dx = o["position"][0] - bl["position"][0]
            dz = o["position"][2] - bl["position"][2]
            lx = dx * math.cos(a) - dz * math.sin(a)
            lz = dx * math.sin(a) + dz * math.cos(a)
            if abs(lx) < bl["_w"] / 2 - 0.4 and abs(lz) < bl["_d"] / 2 - 0.4:
                inside.append((o["name"], bl["name"]))
                break
    out.append("2. objects buried inside a building footprint: %d" % len(inside))
    for x in inside[:10]:
        out.append("     %s inside %s" % x)

    planes = [-3.0, -2.0, -1.5, 0.0, 1.5, 3.0, 4.5, 6.5, 11.5, 16.5]
    bad_y = [(o["name"], round(o["position"][1], 2)) for o in objs
             if not o["_hang"] and min(abs(o["position"][1] - q) for q in planes) > 2.2]
    out.append("3. pivots >2.2 m off every declared height plane (floating/sunken): %d" % len(bad_y))
    for x in bad_y[:10]:
        out.append("     %s y=%.2f" % x)

    blockers = []
    DESIGNED = ("Cave_Arch", "Route_Ledge", "Lake_Bridge", "Lake_SteppingStones",
                "Town_LabBallDisplay", "Town_MarketBallDisplay", "Town_Scanner")
    for o in objs:
        if o["_sub"] in ("Path", "Grass", "Flower", "Moss", "Lilypad", "Stepping", "Fern"):
            continue
        if o["name"].startswith(DESIGNED):
            continue                      # doorway, ledge or bridge: on the walk on purpose
        x, oy, z = o["position"]
        hit = None
        for pth in doc["paths"]:
            pts = pth["points"]
            for k in range(len(pts) - 1):
                ax, az = pts[k][0], pts[k][2]
                bx, bz = pts[k + 1][0], pts[k + 1][2]
                dx, dz = bx - ax, bz - az
                L = dx * dx + dz * dz
                t = 0.0 if L == 0 else max(0.0, min(1.0, ((x - ax) * dx + (z - az) * dz) / L))
                dd = math.hypot(x - (ax + t * dx), z - (az + t * dz))
                py = pts[k][1] + t * (pts[k + 1][1] - pts[k][1])
                if dd < pth["halfWidth"] * 0.65 and abs(oy - py) < 2.5:
                    hit = (o["name"], pth["name"], round(dd, 2))
                    break
            if hit:
                break
        if hit:
            blockers.append(hit)
    out.append("4. solid objects inside the inner 65%% of a walkable corridor: %d" % len(blockers))
    for x in blockers[:12]:
        out.append("     %s on %s d=%.2f" % x)

    rot_hist = defaultdict(int)
    for o in objs:
        if o["_sub"] in ("Path", "Cliff", "Riverbank", "Fence"):
            continue
        rot_hist[(o["_asset"], round(o["rotation"][1] / 5) * 5)] += 1
    out.append("5. most repeated (asset, yaw rounded to 5 deg) -- a big number means a grid:")
    for (asset, rot), n in sorted(rot_hist.items(), key=lambda kv: -kv[1])[:5]:
        out.append("     %-26s yaw~%-6s x%d" % (asset, rot, n))
    scales = defaultdict(int)
    for o in objs:
        scales[round(o["scale"][0], 2)] += 1
    out.append("     distinct scale values: %d, range %.2f-%.2f"
               % (len(scales), min(scales), max(scales)))

    cells = defaultdict(int)
    for o in objs:
        cells[(int(math.floor(o["position"][0] / 8)), int(math.floor(o["position"][2] / 8)))] += 1
    empties, seen = [], set()
    for z in doc["zones"]:
        for v in z["volumes"]:
            cx, _, cz = v["centre"]
            sx, _, sz = v["size"]
            for gx in range(int(math.floor((cx - sx / 2) / 8)), int(math.floor((cx + sx / 2) / 8)) + 1):
                for gz in range(int(math.floor((cz - sz / 2) / 8)), int(math.floor((cz + sz / 2) / 8)) + 1):
                    if (gx, gz) in seen:
                        continue
                    seen.add((gx, gz))
                    ccx, ccz = gx * 8 + 4, gz * 8 + 4
                    if any(_in_poly(ccx, ccz, w["polygon"]) for w in doc["terrain"]["water"]):
                        continue          # open water is meant to be empty
                    if not any(_in_poly(ccx, ccz, dk["polygon"]) for dk in doc["terrain"]["decks"]
                               if not dk.get("facesDown")):
                        continue          # cell centre is off the built ground entirely
                    if cells.get((gx, gz), 0) < 4:
                        empties.append((z["displayName"], gx * 8, gz * 8, cells.get((gx, gz), 0)))
    out.append("6. 8x8 m cells inside a zone volume holding <4 objects: %d of %d cells"
               % (len(empties), len(seen)))
    for e in empties[:16]:
        out.append("     %-12s cell x=%4d z=%4d  n=%d" % e)

    tris = {a["name"]: a["triangles"]
            for a in json.load(open(MANIFEST, encoding="utf-8"))["assets"]}
    tri = sum(tris.get(o["_asset"], 0) for o in objs)
    out.append("7. triangles placed: %s over %d objects (mean %d)"
               % ("{:,}".format(tri), len(objs), tri // max(len(objs), 1)))
    per_zone = defaultdict(int)
    for o in objs:
        per_zone[o["parent"].split("/")[0]] += 1
    out.append("   objects per zone: " + ", ".join("%s=%d" % kv for kv in sorted(per_zone.items())))
    dist = defaultdict(int)
    for o in objs:
        dist[o["_asset"]] += 1
    out.append("   distinct assets used: %d of 81 usable (4 character FBX are retired)" % len(dist))
    unused = sorted(set(json.load(open(BOUNDS, encoding="utf-8"))) - set(dist))
    out.append("   unused: " + (", ".join(unused) if unused else "none"))
    return "\n".join(out)


def main():
    os.makedirs(OUT, exist_ok=True)
    doc, _ = load()
    render_plan(doc)
    render_three_quarter(
        doc, 35.0, 42.0, "threequarter_overview.png",
        "THREE-QUARTER OVERVIEW -- yaw 35 deg, pitch 42 deg (the shipped camera angle). "
        "Judge verticality, layering and silhouette here.", ppm=13.0)
    render_three_quarter(
        doc, 30.0, 42.0, "threequarter_town.png",
        "TOWN + TERRACE at the shipped camera angle. The lab dome should be the tallest "
        "silhouette; the terrace cliff should read as a step, not a slope.",
        zone="Town", ppm=22.0)
    render_three_quarter(
        doc, 125.0, 42.0, "threequarter_lake.png",
        "LAKESIDE + CAVE MASSIF from the south-east. Check the waterfall drop, the shoreline "
        "band and that the massif reads as a mountain behind the arch.",
        zone="Lakeside", ppm=22.0)
    report = audit(doc)
    print(report)
    open(os.path.join(OUT, "audit.txt"), "w", encoding="utf-8").write(report)


if __name__ == "__main__":
    main()
