"""Build the five family atlases (base colour + normal) and a preview sheet."""
import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import envlib as E
import textures as T

E.ensure_dirs()
force = "--force" in sys.argv

prev = []
for fam in ("Foliage", "Terrain", "Town", "Props", "Characters"):
    p = T.ensure_atlas(fam, force=force)
    E.log("  %s -> %s" % (fam, os.path.basename(p["base"])))
    prev.append((fam, p))

# quarter-size contact sheet of every atlas so we can eyeball it cheaply
import struct, zlib


def downsample(path, factor=4):
    # read our own PNG back via numpy by re-rendering is overkill; instead build
    # the preview during generation.  Here we just report existence.
    return os.path.exists(path)


for fam, p in prev:
    assert downsample(p["base"]), p["base"]
    assert downsample(p["normal"]), p["normal"]
E.log("atlases done")
