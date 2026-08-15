"""Bridge the sprite pipeline's manifest into the shape the Unity runtime reads.

Two shapes exist on purpose. `sprite_manifest.json` is the art pipeline's record: nested,
descriptive, and keyed the way the extraction code thinks. `CreatureSpriteLibrary` reads a
flat structure because Unity's JsonUtility cannot deserialise dictionaries, and it loads
textures through `Resources.Load`, which requires them to sit under a `Resources` folder.

Rather than bend either side, this converts one into the other and stages the sheets. Re-run
it whenever sprites are rebuilt.

    python emit_unity_manifest.py
"""

import json
import os
import shutil

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

SOURCE_MANIFEST = os.path.join(
    ROOT, "Assets", "Game", "Art", "Sprites", "Creatures", "sprite_manifest.json")

# Resources is a Unity magic folder: anything under it ships in the build and is reachable
# by name at runtime. Sheets outside it work in the editor and vanish in a player build,
# which is the sort of bug that only shows up on the day you make a build.
RESOURCES_DIR = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Resources")
SHEET_SUBDIR = "PokeLabSprites"
OUT_MANIFEST = os.path.join(RESOURCES_DIR, "sprite_manifest.json")


def sheet_info(view, cell):
    """One view's sheet, described the way SpriteSheetInfo expects."""
    if not view:
        return None

    sheet_w, sheet_h = view["sheet_size"]
    columns = cell["columns"]
    rows = max(1, sheet_h // cell["height"])

    clip = (view.get("clips") or {}).get("idle") or {}
    sequence = clip.get("sequence") or []
    durations = clip.get("durations_ms") or []

    # Resources.Load takes a path with no extension, relative to the Resources folder.
    stem = os.path.splitext(os.path.basename(view["sheet"]))[0]

    return {
        "texture": f"{SHEET_SUBDIR}/{stem}",
        "columns": columns,
        "rows": rows,
        "frames": view["unique_frames"],
        # A nominal rate only; durationsMs overrides it per step and is what actually plays.
        "fps": 10.0,
        "sequence": sequence,
        "durationsMs": durations,
    }


def main():
    with open(SOURCE_MANIFEST, encoding="utf-8") as fh:
        src = json.load(fh)

    os.makedirs(os.path.join(RESOURCES_DIR, SHEET_SUBDIR), exist_ok=True)

    creatures = []
    staged = 0

    for c in src["creatures"]:
        cell = c["cell"]
        views = c.get("views") or {}
        front = views.get("front")
        back = views.get("back")

        for view in (front, back):
            if not view:
                continue
            src_png = os.path.join(ROOT, view["sheet"].replace("/", os.sep))
            dst_png = os.path.join(RESOURCES_DIR, SHEET_SUBDIR, os.path.basename(view["sheet"]))
            if os.path.exists(src_png):
                shutil.copy2(src_png, dst_png)
                staged += 1

        display = c.get("display") or {}
        sprite_px = display.get("sprite_height_px") or cell["height"]

        creatures.append({
            "speciesId": c["species_id"],
            "nameEn": c["name"],
            # Static fallbacks point at the same sheets; frame 0 of the sequence is a valid
            # resting pose, so a separate static texture would only be a second copy.
            "front": sheet_info(front, cell)["texture"] if front else "",
            "back": sheet_info(back, cell)["texture"] if back else "",
            "frontAnim": sheet_info(front, cell),
            "backAnim": sheet_info(back, cell),
            # Both are fractions of the cell, which is what stops a padded Gen 5 frame
            # floating above the ground or burying its feet in it.
            "groundOrigin": round(c["pivot_px"]["y_from_bottom"] / cell["height"], 5),
            "contentHeight": round(sprite_px / cell["height"], 5),
        })

    creatures.sort(key=lambda c: c["speciesId"])

    out = {
        "schema": "pokelab-sprite-manifest-unity/1",
        "frameSize": src.get("cell_size", 96),
        "resourceRoot": "",
        "creatures": creatures,
    }

    with open(OUT_MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=2)

    print(f"staged {staged} sheets into Resources/{SHEET_SUBDIR}")
    print(f"wrote {OUT_MANIFEST} with {len(creatures)} creatures")
    for c in creatures:
        fa, ba = c["frontAnim"], c["backAnim"]
        print(f"  [{c['speciesId']:3d}] {c['nameEn']:<11} "
              f"front {fa['frames']:3d} cells / {len(fa['sequence']):3d} steps   "
              f"back {ba['frames']:3d} cells / {len(ba['sequence']):3d} steps   "
              f"ground {c['groundOrigin']:.3f}  content {c['contentHeight']:.3f}")


if __name__ == "__main__":
    main()
