"""Diagnostic: dump the vertex-colour histogram of a creature's body part.

    blender --background --python Tools/Blender/debug_paint.py -- <module_name> <part_prefix>

Used to prove whether a marking pattern actually reached the mesh, rather than
inferring it from a render where lighting and baking can hide the answer.
"""

import collections
import importlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
for p in (HERE, os.path.join(HERE, "creatures")):
    if p not in sys.path:
        sys.path.insert(0, p)

import pl_core as C

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
module_name = args[0] if args else "c001_bulbasaur"
prefix = args[1] if len(args) > 1 else "Body"

C.reset_scene()
mod = importlib.import_module(module_name)
res = mod.build()

for part in res['parts']:
    if prefix.lower() not in part.name.lower():
        continue
    me = part.data
    if C.COLOR_LAYER not in me.color_attributes:
        print("%s: no colour layer" % part.name)
        continue
    layer = me.color_attributes[C.COLOR_LAYER]
    cnt = collections.Counter()
    for i in range(len(layer.data)):
        srgb = C.linear_to_srgb_rgba(layer.data[i].color)
        cnt["#%02x%02x%02x" % tuple(int(max(0, min(1, v)) * 255) for v in srgb[:3])] += 1
    mn, mx = C.bbox(part)
    print("\n%s  loops=%d distinct=%d" % (part.name, len(layer.data), len(cnt)))
    print("  bbox min=%s max=%s" % (tuple(round(v, 3) for v in mn),
                                    tuple(round(v, 3) for v in mx)))
    for k, v in cnt.most_common(10):
        print("   %s x%d" % (k, v))
