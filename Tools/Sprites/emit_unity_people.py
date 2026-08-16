"""Bridge the human sprite manifest into the shape a Unity runtime can read.

    python Tools/Sprites/emit_unity_people.py

The people-side counterpart to emit_unity_manifest.py, and it exists for the
same two reasons: `people_manifest.json` beside the art is the pipeline's
record -- nested, descriptive, keyed the way the extraction code thinks --
while Unity's JsonUtility cannot deserialise a dictionary and `Resources.Load`
can only see textures that sit under a `Resources` folder.

So every nested map in the pipeline manifest is flattened into named fields
here, and every sheet is copied into `Resources/PokeLabSprites/` with a
`person_` prefix.  Re-run whenever the people sheets are rebuilt.

The shape below is chosen to need as little new C# as possible.  In particular
each clip is emitted as a complete `SpriteSheetInfo` -- the class that already
exists in CreatureSpriteLibrary.cs, with `columns`, `rows`, `frames`,
`sequence` and `durationsMs` meaning exactly what they already mean.  All the
clips of one character point at the same texture and differ only in
`sequence`, which is what `StepCount`, `CellAt` and `StepSeconds` already
handle without modification.

What still needs a C# change, precisely
---------------------------------------
Nothing in this file edits Assets/Game/Scripts, and three things there do not
yet exist.  They are listed in `runtime_requirements` in the emitted JSON so
the ask travels with the data rather than living only in a report:

  1. A reader.  `CreatureSpriteManifest` is hardcoded to `creatures[]` keyed by
     `speciesId`; people have no species id.  A `PersonSpriteManifest` mirroring
     it, over `people[]` keyed by `key`, is the whole of it.

  2. A bound side view.  `CreatureBillboard` already declares `_sideSheet` and
     `_sideTexture` and already routes `ActiveSheet()`/`ActiveTexture()`
     through them -- but nothing ever assigns them, so `SideSheet()` returns
     null forever and both side sectors fall back to a mirrored front or back.
     People have real side artwork; assigning those two fields in `Bind` is
     all that is needed to use it.

  3. A clip switch.  `Bind` takes one sheet per view. Walking needs the walk
     clip and standing needs the idle clip over the same texture, so something
     has to swap `_frontSheet`/`_backSheet`/`_sideSheet` between the two
     SpriteSheetInfos when the character starts and stops moving.

  And one thing that is already there and is worth a second look before the
  side view is wired -- see `facing_sign_note` in the emitted JSON.
"""

from __future__ import annotations

import json
import os
import shutil
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import people as P
from unity_meta import ensure_meta

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))

SOURCE_MANIFEST = os.path.join(
    ROOT, "Assets", "Game", "Art", "Sprites", "People", "people_manifest.json")

RESOURCES_DIR = os.path.join(ROOT, "Assets", "Game", "Art", "Sprites", "Resources")
SHEET_SUBDIR = "PokeLabSprites"
OUT_MANIFEST = os.path.join(RESOURCES_DIR, "people_manifest.json")

# Staged under a prefix so a human sheet can never be confused with a creature
# sheet in a flat Resources folder -- `hiker` and `lass` are perfectly
# plausible file stems for something else later.
PREFIX = "person_"


# Import settings are authored rather than inherited from Unity's defaults --
# see unity_meta.py for the four defaults that are wrong for this art and why.
#
# NOTE, and it is not this pipeline's to fix: the creature sheets already in
# Resources/PokeLabSprites carry Unity's defaults, enableMipMap: 1 and
# textureCompression: 1 on Standalone, which is the exact opposite of what
# extract.py's own IMPORT_SETTINGS says is mandatory. Left alone.

# --------------------------------------------------------------------------
# manifest
# --------------------------------------------------------------------------


def sheet_info(entry, view: str, clip: str, texture: str):
    """One clip, described the way SpriteSheetInfo expects."""
    clips = entry["views"][view]
    if clip not in clips:
        return None
    c = clips[clip]
    cell = entry["cell"]
    rows = max(1, entry["sheet_size"][1] // cell["height"])
    seq = c["sequence"]
    durations = c["durations_ms"] if len(seq) > 1 else []
    return {
        "texture": texture,
        "columns": cell["columns"],
        "rows": rows,
        "frames": entry["unique_frames"],
        # Nominal only; durationsMs overrides it per step and is what plays.
        "fps": round(1000.0 / P.WALK_STEP_MS, 3),
        "sequence": seq,
        "durationsMs": durations,
    }


def main():
    with open(SOURCE_MANIFEST, encoding="utf-8") as fh:
        src = json.load(fh)

    sheets_dir = os.path.join(RESOURCES_DIR, SHEET_SUBDIR)
    os.makedirs(sheets_dir, exist_ok=True)

    people = []
    staged = metas = 0

    for c in src["characters"]:
        stem = PREFIX + c["key"]
        src_png = os.path.join(ROOT, c["sheet"].replace("/", os.sep))
        dst_png = os.path.join(sheets_dir, stem + ".png")
        if os.path.exists(src_png):
            shutil.copy2(src_png, dst_png)
            staged += 1
            if ensure_meta(dst_png, stem, c["pixels_per_unit"]) != "unchanged":
                metas += 1

        texture = f"{SHEET_SUBDIR}/{stem}"
        cell = c["cell"]
        disp = c["display"]

        person = {
            "key": c["key"],
            "nameEn": c["name"],
            "role": c["role"],
            # The height actually rendered, in metres. A spawner passes this
            # straight to CreatureBillboard.Bind as displayHeight.
            "displayHeightMetres": disp["world_height_m"],
            # Both are fractions of the cell, and they mean exactly what they
            # mean on the creature side: where the feet sit inside the padded
            # frame, and how much of the frame the drawn figure fills.
            "groundOrigin": round(c["pivot_px"]["y_from_bottom"] / cell["height"], 5),
            "contentHeight": round(disp["sprite_height_px"] / cell["height"], 5),
            # Static fallback: cell 0 of the sheet is a valid standing pose, so
            # a separate static texture would only be a second copy.
            "front": texture,
            "back": texture,
            "side": texture,
            # The authored side view walks toward screen-left. The opposite
            # side is this sheet with uv.x = 1 - uv.x.
            "sideWalksScreenLeft": True,
        }
        for view in ("front", "back", "side"):
            for clip in ("idle", "walk", "run"):
                info = sheet_info(c, view, clip, texture)
                if info is not None:
                    person[f"{view}{clip.capitalize()}"] = info
        people.append(person)

    # Props: field objects, one texture each, no directions.
    props = []
    for pr in src.get("props", []):
        stem = "prop_" + pr["key"]
        sp = os.path.join(ROOT, pr["sheet"].replace("/", os.sep))
        dp = os.path.join(sheets_dir, stem + ".png")
        if os.path.exists(sp):
            shutil.copy2(sp, dp)
            staged += 1
            if ensure_meta(dp, stem, pr["pixels_per_unit"]) != "unchanged":
                metas += 1
        texture = f"{SHEET_SUBDIR}/{stem}"
        cell = pr["cell"]
        rows = max(1, pr["sheet_size"][1] // cell["height"])
        entry = {
            "key": pr["key"],
            "nameEn": pr["name"],
            "displayHeightMetres": pr["display"]["world_height_m"],
            "groundOrigin": round(pr["pivot_px"]["y_from_bottom"] / cell["height"], 5),
            "contentHeight": round(pr["display"]["sprite_height_px"] / cell["height"], 5),
            "texture": texture,
        }
        for name, clip in pr["clips"].items():
            entry[name.capitalize()] = {
                "texture": texture, "columns": cell["columns"], "rows": rows,
                "frames": pr["unique_frames"],
                "fps": round(1000.0 / P.WALK_STEP_MS, 3),
                "sequence": clip["sequence"],
                "durationsMs": clip["durations_ms"] if len(clip["sequence"]) > 1 else [],
            }
        props.append(entry)

    # JsonUtility cannot deserialise a dictionary, so the placement map and the
    # procedural-state recipes both become arrays of records.
    by_key = {p["key"]: p for p in people}
    placement = [
        {"objectId": oid, "characterKey": key,
         "kind": "trainer" if oid.startswith("trainer_") else "npc",
         "displayHeightMetres": by_key[key]["displayHeightMetres"]}
        for oid, key in sorted(src["placement"].items())
        if key in by_key
    ]

    out = {
        "schema": "pokelab-people-manifest-unity/1",
        "frameSize": src["cell_size"],
        "pixelsPerUnit": src["pixels_per_unit"],
        "resourceRoot": "",
        "provenance": src["provenance"],
        "scaleNote": src["scale_note"],
        "runtimeRequirements": [
            "PersonSpriteManifest: a reader over people[] keyed by `key`. "
            "CreatureSpriteManifest is hardcoded to creatures[] keyed by "
            "speciesId and people have no species id.",
            "CreatureBillboard._sideSheet and ._sideTexture are declared and "
            "read but never assigned, so SideSheet() is null forever and both "
            "side sectors mirror the front or back instead. People ship real "
            "side artwork; assigning those two in Bind is the whole change.",
            "A clip switch: idle and walk are two SpriteSheetInfos over one "
            "texture, so something must swap the bound sheet when the "
            "character starts and stops moving. Nothing else differs.",
        ],
        "facingSignNote": (
            "Flagged, not fixed, and it only bites once a real side sheet is "
            "bound. SpriteFacing.Left is documented as 'the subject's left "
            "side is toward the lens', but SelectFacing assigns Right when "
            "SignedAngle(toCamera, forward, up) is positive, and a positive "
            "angle there is the case where the subject's LEFT flank faces the "
            "lens. Worked example: camera on -Z, subject forward -X. The "
            "subject's right vector is +Z, pointing away from the lens, so the "
            "lens sees the left flank; SignedAngle returns +90 and the code "
            "picks Right. Today both side sectors borrow a mirrored front or "
            "back so the swap is invisible. With a side sheet bound it shows "
            "the character walking backwards."),
        "people": people,
        "props": props,
        "proceduralStates": [
            {"state": k, "base": v["base"], "recipe": v["recipe"],
             "note": v.get("note", "")}
            for k, v in src.get("procedural_states", {}).items()
        ],
        "proceduralStatesNote": src.get("states_note", ""),
        "placement": placement,
    }

    with open(OUT_MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(out, fh, indent=2)

    print(f"staged {staged} sheets into Resources/{SHEET_SUBDIR} "
          f"(prefix '{PREFIX}'), {metas} .meta written or corrected")
    print(f"wrote {OUT_MANIFEST} with {len(people)} people\n")
    print(f"{'key':12}{'role':10}{'height':>8}{'ground':>8}{'content':>9}  clips")
    for p in people:
        clips = [k for k in p if k.endswith(("Idle", "Walk", "Run"))]
        kinds = sorted({k for kind in ("Idle", "Walk", "Run")
                        for k in (kind,) if any(c.endswith(kind) for c in clips)})
        print(f"{p['key']:12}{p['role']:10}{p['displayHeightMetres']:>7.2f}m"
              f"{p['groundOrigin']:>8.3f}{p['contentHeight']:>9.3f}  "
              f"{len(clips)} sheets = 3 views x {kinds}")
    print(f"\nplacement: {len(placement)} level objects bound")
    for pl in placement:
        print(f"  {pl['objectId']:20} {pl['kind']:8} -> {pl['characterKey']}")


if __name__ == "__main__":
    main()
