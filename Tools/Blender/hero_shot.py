"""Render one large 1:1 view of a finished creature FBX-equivalent build.

    blender --background --python Tools/Blender/hero_shot.py -- <module> [yaw] [pitch]

Contact-sheet tiles get downsampled when reviewed, which can hide whether a
marking or a surface detail actually came out. This renders a single frame big
enough to judge at full resolution.
"""

import importlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
for p in (HERE, os.path.join(HERE, "creatures")):
    if p not in sys.path:
        sys.path.insert(0, p)

import pl_core as C
import pl_build
import pl_render as PR

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
module_name = args[0] if args else "c001_bulbasaur"
yaw = float(args[1]) if len(args) > 1 else -34.0
pitch = float(args[2]) if len(args) > 2 else 10.0

mod = importlib.import_module(module_name)
entry = pl_build.build_creature(mod, render=False, strict=False)
mesh = bpy_mesh = None
import bpy
name = "Creature_%d_%s" % (mod.ID, mod.NAME)
mesh = bpy.data.objects[name]
lo, hi = PR.subject_bounds([mesh])
cam = PR.setup_studio(hi.z - lo.z, engine='BLENDER_EEVEE', samples=96, res=900)
PR.frame_camera(cam, [mesh], yaw, pitch, margin=1.05)
out = os.path.join(C.PREVIEW_DIR, "%s_hero.png" % name)
PR.render_to(out)
print("hero -> %s  (%d tris)" % (out, entry['triangles']))
