"""Plan a review walk through the level and hand it to the Unity capture tool.

Reviewing a level from a far overhead orbit is what let a town full of z-fighting
walls, doorless doorways and floating props get signed off — at that range none of
those defects is more than a pixel wide. So this walks the roads the player actually
walks, at the player's own eye height, through the exploration camera's framing.

Every route is walked in **both directions**. That is not thoroughness for its own
sake: the exploration camera's yaw is locked at 45 degrees, so walking back down a
path shows the far side of everything already passed. That far side is the one nobody
has ever looked at, and it is exactly where a missing back wall or a single-sided leaf
shows up.

    python Tools/Level/plan_walkthrough.py [--spacing 5] [--out Captures/sweep]

Writes Temp/pokelab_capture.json, which the editor picks up within a tick or two.
Unity throttles its update loop when unfocused, so click the Unity window if nothing
happens.
"""

import argparse
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)

from emit_unity_layout import Grid
from worldgen import chaikin, resample

SOURCE = os.path.join(ROOT, "Assets", "Game", "Data", "Levels", "slice_layout.json")
REQUEST = os.path.join(ROOT, "Temp", "pokelab_capture.json")
SCENE = "Assets/Game/Scenes/Overworld.unity"


def station(name, x, z, grid, yaw=None, distance=None):
    s = {"name": name, "position": [round(x, 3), round(grid.at(x, z), 3), round(z, 3)]}
    if yaw is not None:
        s["overrideYaw"] = True
        s["yaw"] = float(yaw)
    if distance is not None:
        s["overrideDistance"] = True
        s["distance"] = float(distance)
    return s


def walk_path(path, grid, spacing):
    """Stations along one road, out and back."""
    pts = resample(chaikin([tuple(float(v) for v in c)
                            for c in path["controlPoints"]], 3), spacing)
    name = path["name"].replace("Path_", "")
    out = [station("%s_fwd_%02d" % (name, i), p[0], p[-1], grid)
           for i, p in enumerate(pts)]
    back = [station("%s_rev_%02d" % (name, i), p[0], p[-1], grid)
            for i, p in enumerate(reversed(pts))]
    return out + back


def cross(field, grid, samples=6):
    """A traverse straight through a foliage field, so the grass is reviewed from
    inside it rather than from its edge. Standing in the cover is the only way to see
    whether it is dense enough to be cover."""
    poly = [(float(p[0]), float(p[1])) for p in field["polygon"]]
    cx = sum(p[0] for p in poly) / len(poly)
    cz = sum(p[1] for p in poly) / len(poly)
    minx = min(p[0] for p in poly)
    maxx = max(p[0] for p in poly)
    name = field["name"].replace("Field_", "")
    out = []
    for i in range(samples):
        t = i / max(1, samples - 1)
        x = minx + (maxx - minx) * t
        out.append(station("%s_cross_%02d" % (name, i), x, cz, grid))
    # One low, close shot so the blades are judged at the height they are actually
    # seen from, and one from the opposite yaw to catch single-sided geometry.
    out.append(station("%s_close" % name, cx, cz, grid, distance=2.6))
    out.append(station("%s_behind" % name, cx, cz, grid, yaw=225.0))
    return out


def orbit(name, x, z, grid, distance=6.0):
    """Four yaws around one spot. Used on buildings, where the locked camera means a
    face is either always seen or never seen, and 'never seen' is where the holes are."""
    return [station("%s_yaw%03d" % (name, yaw), x, z, grid, yaw=yaw, distance=distance)
            for yaw in (45, 135, 225, 315)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--spacing", type=float, default=5.0,
                    help="metres between stations along a road")
    ap.add_argument("--out", default="Captures/sweep")
    ap.add_argument("--width", type=int, default=1600)
    ap.add_argument("--height", type=int, default=900)
    ap.add_argument("--only", default="", help="substring filter on station names")
    args = ap.parse_args()

    with open(SOURCE, encoding="utf-8") as fh:
        layout = json.load(fh)
    grid = Grid(layout["terrain"]["heightField"]["grid"])

    stations = []

    spawn = layout["gameplay"]["playerSpawn"]["position"]
    stations.append(station("Spawn", float(spawn[0]), float(spawn[2]), grid))

    for path in layout["paths"]:
        # The cave interior is a different scene; there is no ground under it here.
        if not path.get("conformsTerrain", True):
            continue
        stations += walk_path(path, grid, args.spacing)

    for field in layout["foliageFields"]:
        if field.get("kind") == "tall_grass":
            stations += cross(field, grid)

    # Buildings, four yaws each, because a locked camera hides whole elevations.
    for obj in layout["objects"]:
        prefab = obj.get("prefab", "")
        if "/Env_House_" in prefab or "/Env_Building_" in prefab:
            pos = obj["position"]
            stations += orbit(obj["name"], float(pos[0]), float(pos[2]), grid)

    if args.only:
        stations = [s for s in stations if args.only.lower() in s["name"].lower()]

    request = {
        "scene": SCENE,
        "build": True,
        "outputDirectory": args.out,
        "width": args.width,
        "height": args.height,
        "yaw": 45.0,
        "pitch": 38.0,
        "distance": 5.5,
        "fov": 40.0,
        "eyeHeight": 1.15,
        "stations": stations,
    }

    os.makedirs(os.path.dirname(REQUEST), exist_ok=True)
    with open(REQUEST, "w", encoding="utf-8") as fh:
        json.dump(request, fh)

    print("wrote %s" % REQUEST)
    print("  %d stations -> %s" % (len(stations), args.out))


if __name__ == "__main__":
    main()
