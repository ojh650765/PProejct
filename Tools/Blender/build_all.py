"""
Poke Lab creature build driver.

    blender --background --python Tools/Blender/build_all.py -- [--no-render]
                                                               [--engine CYCLES]
                                                               [--lenient] [ids...]

With no ids it builds every creature module found in Tools/Blender/creatures/.
The manifest is merged, not overwritten, so partial rebuilds are safe.
"""

import importlib
import json
import os
import re
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)
CREATURE_DIR = os.path.join(HERE, "creatures")
if CREATURE_DIR not in sys.path:
    sys.path.insert(0, CREATURE_DIR)

import pl_core as C
import pl_build

MANIFEST = os.path.join(C.ART_DIR, "creature_manifest.json")

# the slice needs these first
PRIORITY = [1, 5, 10, 21, 47, 81]


def discover():
    out = {}
    for fn in sorted(os.listdir(CREATURE_DIR)):
        m = re.match(r"^c(\d+)_.*\.py$", fn)
        if m:
            out[int(m.group(1))] = fn[:-3]
    return out


def argv():
    if "--" in sys.argv:
        return sys.argv[sys.argv.index("--") + 1:]
    return []


def main():
    args = argv()
    render = "--no-render" not in args
    strict = "--lenient" not in args
    engine = 'BLENDER_EEVEE'
    if "--engine" in args:
        engine = args[args.index("--engine") + 1]
    ids = [int(a) for a in args if a.isdigit()]

    mods = discover()
    if not ids:
        ids = [i for i in PRIORITY if i in mods] + \
              [i for i in sorted(mods) if i not in PRIORITY]

    existing = {}
    if os.path.exists(MANIFEST):
        try:
            with open(MANIFEST, 'r', encoding='utf-8') as fh:
                data = json.load(fh)
            existing = {int(k): v for k, v in data.get("creatures", {}).items()}
        except Exception:
            existing = {}

    failures = []
    for cid in ids:
        if cid not in mods:
            print("!! no module for species %d" % cid)
            failures.append((cid, "no module"))
            continue
        try:
            mod = importlib.import_module(mods[cid])
            importlib.reload(mod)
            entry = pl_build.build_creature(mod, render=render, engine=engine,
                                            strict=strict)
            existing[cid] = entry
        except Exception:
            print("!! FAILED %d:\n%s" % (cid, traceback.format_exc()))
            failures.append((cid, traceback.format_exc().strip().splitlines()[-1]))

    manifest = {
        "$schema": "poke-lab creature art manifest v1",
        "generator": "Tools/Blender/build_all.py (Blender 4.0.1)",
        "conventions": {
            "units": "1 blender unit = 1 metre; creatures authored at true size",
            "orientation": ("modelled facing -Y with +Z up, exported with "
                            "axis_forward='-Y', axis_up='Z' so the FBX declares "
                            "UpAxis=+Z / FrontAxis=+Y / CoordAxis=-X and Unity's "
                            "axis conversion lands the creature facing +Z, Y up, "
                            "feet at the origin (see Tools/Blender/previews/"
                            "axis_probe.txt)"),
            "fps": 30,
            "anchors": ["Anchor_Head", "Anchor_Body", "Anchor_Muzzle"],
            "clipNaming": "one action per CreatureAnimation enum value",
            "rootMotion": "none - locomotion clips are in place",
        },
        "creatures": {str(k): existing[k] for k in sorted(existing)},
    }
    C.write_json(MANIFEST, manifest)
    print("\nmanifest -> %s (%d creatures)" % (MANIFEST, len(existing)))
    if failures:
        print("FAILURES: %s" % failures)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
