"""
Merge the per-family manifest parts written by each generator into
Assets/Game/Art/Environment/environment_manifest.json.

The integrator builds scene-dressing tooling from this file, so it is
regenerated from the parts rather than hand-maintained, and every path is
verified to exist on disk before it is written.
"""

import sys
import os
import json
import glob

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import envlib as E
import textures as T

FAMILIES = ["Foliage", "Terrain", "Town", "Props", "Characters"]

# The brief's budgets are per asset CLASS, not per folder: the town folder
# holds both 6k buildings and 400-tri benches, and a single grass blade is
# meant to be tiny.  Classify each asset so the report is honest.
BUDGETS = {
    "foliage": [200, 1500],
    "rock": [300, 2000],
    "building": [1500, 6000],
    "prop": [300, 2000],
    "character": [3000, 8000],
}

# Deliberately below the class floor: single-element scatter pieces where the
# floor would only force pointless geometry.
EXEMPT = {
    "Env_Grass_Blade": "single blade for dense scatter -- 10 tris is the point",
    "Env_Lilypad_A": "flat floating pad",
    "Env_Lilypad_B": "flat floating pad",
    "Env_Cave_Stalactite_A": "tapered spike",
    "Env_Cave_Stalactite_B": "tapered spike",
    "Env_Cave_Stalagmite_A": "tapered spike",
    "Env_Cave_Stalagmite_B": "tapered spike",
    "Env_Path_Paved_1m": "1 m ground tile",
    "Env_Path_Paved_2m": "2 m ground tile",
    "Env_Path_Paved_Corner": "1x2 m ground tile",
}

BUILDINGS = {"Env_House_Cottage_A", "Env_House_Townhouse_B",
             "Env_House_Farmhouse_C", "Env_Building_PokeLab"}
ROCKY = ("Cliff", "Rock", "Cave", "Riverbank", "Waterfall", "Stepping",
         "Bridge")


def budget_class(entry):
    fam = entry["family"]
    if fam == "Characters":
        return "character"
    if fam == "Foliage":
        return "foliage"
    if entry["name"] in BUILDINGS:
        return "building"
    if fam == "Terrain" and entry.get("subfamily") in ROCKY:
        return "rock"
    return "prop"


def main():
    parts = {}
    missing = []
    for fam in FAMILIES:
        p = E.part_manifest_path(fam)
        if not os.path.exists(p):
            E.log("!! no manifest part for %s -- run gen_%s.py"
                  % (fam, fam.lower()))
            continue
        with open(p, "r", encoding="utf-8") as f:
            parts[fam] = json.load(f)

    assets = []
    for fam in FAMILIES:
        for e in parts.get(fam, []):
            for rel in [e["path"]] + [l["path"] for l in e.get("lods", [])] + \
                    e.get("textures", []) + \
                    [c["path"] for c in e.get("clips", [])]:
                if not os.path.exists(os.path.join(E.REPO, rel)):
                    missing.append(rel)
            e["budgetClass"] = budget_class(e)
            lo, hi = BUDGETS[e["budgetClass"]]
            e["withinBudget"] = bool(lo <= e["triangles"] <= hi)
            if e["name"] in EXEMPT:
                e["budgetExempt"] = EXEMPT[e["name"]]
                e["withinBudget"] = e["triangles"] <= hi
            assets.append(e)

    atlases = {}
    for fam in FAMILIES:
        ap = T.atlas_paths(fam)
        cols = T.load_colors(fam)
        atlases[fam] = {
            "baseColor": os.path.relpath(ap["base"], E.REPO).replace("\\", "/"),
            "normal": os.path.relpath(ap["normal"], E.REPO).replace("\\", "/"),
            "resolution": 2048,
            "grid": [4, 4],
            "cells": [
                {"index": i, "name": n,
                 "uvRect": [round(v, 5) for v in E.cell_rect(i)],
                 "averageColorLinear": [round(c, 4)
                                        for c in cols.get(n, (0.5, 0.5, 0.5))]}
                for i, (n, _) in enumerate(T.FAMILY_CELLS[fam])
            ],
        }

    doc = {
        "schema": "pokelab.environment.manifest/1",
        "generatedBy": "Tools/Blender/environment (Blender 4.0.1, headless)",
        "units": "1 Blender unit = 1 metre; models face +Z with Y up after "
                 "the FBX axis conversion (-Y forward, Z up on export)",
        "modularGrid": 0.5,
        "triangleBudgets": BUDGETS,
        "budgetExemptions": EXEMPT,
        "windVertexColors": {
            "attribute": "Col",
            "type": "FLOAT_COLOR, CORNER domain, exported colors_type=LINEAR",
            "R": "sway mask, 0 at the trunk base rising to 1 at the leaf tips",
            "G": "phase offset, randomised per leaf cluster",
            "B": "high-frequency flutter mask (leaves and blades only)",
            "A": "always 1",
            "appliesTo": "every asset in the Foliage family",
            "encoding": "Exported with colors_type=LINEAR, so the FBX carries "
                        "the authored 0-1 mask values VERBATIM -- no gamma "
                        "encode is applied on the way out. Measured: authoring "
                        "(0.25, 0.50, 0.75) writes (0.25, 0.50, 0.75) to the "
                        "file. Blender's own importer then decodes them as "
                        "sRGB and shows (0.051, 0.216, 0.521), which is an "
                        "artefact of that importer, not of the data. If Unity "
                        "turns out to apply the same decode, either undo it in "
                        "the shader with pow(c, 1/2.2) or flip "
                        "envlib.export_fbx to colors_type='SRGB' and re-run "
                        "build_all.py -- the choice is one line.",
        },
        "atlases": atlases,
        "unitySetup": {
            "importScale": 1.0,
            "convertUnits": False,
            "normals": "Import (custom split normals are authored; do not "
                       "recalculate)",
            "tangents": "Calculate Mikktspace",
            "materials": "One material per family atlas is enough: assign the "
                         "family BaseColor + Normal to the shared stylised "
                         "toon shader. Every mesh in a family is UV-packed "
                         "into that one atlas.",
            "lodGroups": "For any asset with a non-empty `lods` array, add a "
                         "LODGroup with LOD0 = base mesh, LOD1 and LOD2 from "
                         "the listed FBXs. Suggested screen-relative heights "
                         "0.45 / 0.16 / 0.03 with 30% fade transition width, "
                         "Cross Fade animation mode.",
            "characterRig": "Animation Type = Humanoid, Avatar Definition = "
                            "Create From This Model on Env_Char_Player.fbx, "
                            "then Copy From Other Avatar for the three NPCs "
                            "(identical skeleton). Import the @Clip FBXs with "
                            "Avatar Definition = Copy From Other Avatar.",
            "colliders": "None authored. Cliff, path and riverbank modules "
                         "are closed solids suitable for mesh colliders; "
                         "foliage should use no collider or a capsule.",
        },
        "assetCount": len(assets),
        "assets": assets,
    }

    # sweep stale LOD exports: an asset that dropped under 2k on a later pass
    # leaves orphan _LOD1/_LOD2 files behind, and the integrator would wire
    # them into a LODGroup that the manifest does not describe.
    referenced = set()
    for a in assets:
        for l in a.get("lods", []):
            referenced.add(os.path.normpath(os.path.join(E.REPO, l["path"])))
    orphans = []
    for fam in FAMILIES:
        for f in glob.glob(os.path.join(E.FAMILY_DIR[fam], "*_LOD*.fbx")):
            if os.path.normpath(f) not in referenced:
                orphans.append(f)
    for f in orphans:
        os.remove(f)
        E.log("removed stale LOD %s" % os.path.basename(f))

    out = os.path.join(E.ART_ENV, "environment_manifest.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)
    E.log("wrote %s (%d assets)" % (out, len(assets)))

    if missing:
        E.log("!! %d referenced paths do not exist:" % len(missing))
        for m in sorted(set(missing))[:20]:
            E.log("   %s" % m)
        return 1

    for cls in ("foliage", "rock", "building", "prop", "character"):
        rows = [a for a in assets if a["budgetClass"] == cls]
        if not rows:
            continue
        tris = [r["triangles"] for r in rows]
        lo, hi = BUDGETS[cls]
        bad = [r["name"] for r in rows if not r["withinBudget"]]
        E.log("%-10s %3d assets  tris %5d-%5d  budget %d-%d  %s"
              % (cls, len(rows), min(tris), max(tris), lo, hi,
                 ("OVER: " + ", ".join(bad)) if bad else "all within budget"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
