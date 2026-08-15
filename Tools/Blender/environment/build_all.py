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
    ("foliage", "gen_foliage.py", []),
    ("terrain", "gen_terrain.py", []),
    ("town", "gen_town.py", []),
    ("props", "gen_props.py", []),
    ("characters", "gen_characters.py", ["--", "--poses"]),
    ("manifest", "build_manifest.py", []),
    ("contact_foliage_trees", "render_contact.py",
     ["--", "Foliage", "Env_Tree_*.fbx", "previews/contact_foliage_trees.png",
      "9.0", "4"]),
    ("contact_foliage_plants", "render_contact.py",
     ["--", "Foliage", "Env_[!T]*.fbx", "previews/contact_foliage_plants.png",
      "2.2", "6"]),
    ("contact_terrain", "render_contact.py",
     ["--", "Terrain", "*.fbx", "previews/contact_terrain.png", "6.0", "5"]),
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
