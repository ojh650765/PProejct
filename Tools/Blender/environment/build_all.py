"""
Rebuild the whole environment kit from scratch.

    python build_all.py                # everything
    python build_all.py foliage town   # only those stages

Runs each stage in its own headless Blender so a crash in one family cannot
corrupt another, then merges the manifest parts.
"""

import os
import sys
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
BLENDER = os.environ.get(
    "BLENDER",
    r"D:\Program Files\Blender Foundation\Blender 4.0\blender.exe")

STAGES = [
    ("atlases", "build_atlases.py", ["--", "--force"]),
    # The four tiling terrain layer maps for M_Ground_TerrainBlend. Not atlas
    # cells and not built by build_atlases: those layers sample a wrapping UV,
    # so an atlas cell's neighbours would bleed in on the first repeat.
    ("terrain_layers", "terrain_layers.py", ["--", "--verify"]),
    ("foliage", "gen_foliage.py", []),
    ("terrain", "gen_terrain.py", []),
    # ground must follow terrain: both write into the Terrain family folder and
    # into their own manifest parts, and ground reads the level layout
    ("ground", "gen_ground.py", []),
    ("town", "gen_town.py", []),
    ("props", "gen_props.py", []),
    # ("characters", "gen_characters.py", []) -- retired with the pixel-sprite
    # pivot (Docs/GOAL.md); the generator and its FBXs are gone from the repo.
    ("manifest", "build_manifest.py", []),
    ("contact_foliage_trees", "render_contact.py",
     ["--", "Foliage", "Env_Tree_*.fbx", "previews/contact_foliage_trees.png",
      "9.0", "4"]),
    ("contact_foliage_plants", "render_contact.py",
     ["--", "Foliage", "Env_[!T]*.fbx", "previews/contact_foliage_plants.png",
      "2.2", "6"]),
    ("contact_terrain", "render_contact.py",
     ["--", "Terrain", "Env_[!GW]*.fbx", "previews/contact_terrain.png",
      "6.0", "5"]),
    ("contact_ground", "render_contact.py",
     ["--", "Terrain", "Env_Ground_*.fbx", "previews/contact_ground.png",
      "70.0", "3"]),
    ("contact_water", "render_contact.py",
     ["--", "Terrain", "Env_Water*.fbx", "previews/contact_water.png",
      "46.0", "3"]),
    ("contact_town", "render_contact.py",
     ["--", "Town", "*.fbx", "previews/contact_town.png", "7.0", "5"]),
    ("contact_props", "render_contact.py",
     ["--", "Props", "*.fbx", "previews/contact_props.png", "2.4", "3", "norm"]),
    ("hero_ball", "render_contact.py",
     ["--", "Props", "Env_Prop_CaptureBall.fbx", "previews/hero_captureball.png",
      "1.6", "1", "norm"]),
    ("contact_characters", "render_contact.py",
     ["--", "Characters", "Env_Char_[PN]*.fbx",
      "previews/contact_characters.png", "2.6", "4"]),
    ("dressed", "render_dressed.py", []),
]


def main():
    want = [a.lower() for a in sys.argv[1:]]
    fails = []
    for (name, script, extra) in STAGES:
        if want and name not in want:
            continue
        cmd = [BLENDER, "--background", "--python",
               os.path.join(HERE, script)] + extra
        print("=== %s (%s)" % (name, script))
        sys.stdout.flush()
        r = subprocess.run(cmd, cwd=HERE, capture_output=True, text=True)
        for line in r.stdout.splitlines():
            if line.startswith("[env]") or "Error" in line or "Traceback" in line:
                print("   " + line)
        if r.returncode != 0:
            fails.append(name)
            print("   !! %s exited %d" % (name, r.returncode))
            print(r.stderr[-2000:])
    print("done. failures: %s" % (fails or "none"))
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
