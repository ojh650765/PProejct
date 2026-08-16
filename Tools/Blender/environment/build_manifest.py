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

# Families whose atlas still exists and is still referenced. Characters was
# retired when the project moved to official pixel sprites (Docs/GOAL.md), so
# its FBXs are gone from disk; its atlas block is dropped with them rather than
# left in the manifest describing files nobody can open.
ATLAS_FAMILIES = ["Foliage", "Terrain", "Town", "Props"]

# The brief's budgets are per asset CLASS, not per folder: the town folder
# holds both 6k buildings and 400-tri benches, and a single grass blade is
# meant to be tiny.  Classify each asset so the report is honest.
BUDGETS = {
    "foliage": [200, 1500],
    "rock": [300, 2000],
    # 900, not 1500: the rebuilt buildings dropped the whole-mesh bevel
    # pass (it collapsed faces across the intersections of the closed
    # solids they are assembled from) and gained real wall thickness and
    # real reveals instead. A cottage that is 1,200 correct triangles is
    # not under-built; the old 3,000 were mostly chamfer.
    "building": [900, 6000],
    "prop": [300, 2000],
    # The capture balls, declared by gen_props. 2,000 is right for street
    # furniture -- a bench is read at 40 px from a camera 22 m up -- and wrong
    # for the one prop the game shows full screen: a ball fills the frame
    # during every capture, so its silhouette needs enough columns to stay
    # round, and the open variant is two HOLLOW half shells, which is
    # inherently about twice the closed ball. The Net and Dusk liveries carry
    # another ~700 for raised netting, which a 28-column shell cannot paint
    # thinner than 12.9 degrees. Everything past 2,000 gets LODs generated.
    "hero_prop": [300, 4000],
    "character": [3000, 8000],
    # Ground decks, water surfaces, ramps and ledges. One instance each, world
    # placed, and a route deck covering 68 x 38 m cannot be held to a prop's
    # 2,000 tris without either a staircase boundary or a height field the
    # layout's 4,682 placements no longer sit on.
    "ground": [30, 26000],
    # Scatter foliage drawn with GPU instancing: one mesh, one material, no
    # LODs, and a tight ceiling because the triangles multiply by the instance
    # count directly.
    "scatter": [40, 260],
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
             "Env_House_Farmhouse_C", "Env_Building_PokeLab",
             "Env_Building_PokeCentre"}
ROCKY = ("Cliff", "Rock", "Cave", "Riverbank", "Waterfall", "Stepping",
         "Bridge")


def budget_class(entry):
    # a generator may declare its own class; ground and scatter both do
    declared = entry.get("budgetClass")
    if declared in BUDGETS:
        return declared
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
        rows = []
        for p in E.part_manifest_paths(fam):
            with open(p, "r", encoding="utf-8") as f:
                rows.extend(json.load(f))
        if not rows:
            E.log("!! no manifest part for %s -- run gen_%s.py"
                  % (fam, fam.lower()))
            continue
        parts[fam] = rows

    assets = []
    dropped = []
    for fam in FAMILIES:
        for e in parts.get(fam, []):
            if not os.path.exists(os.path.join(E.REPO, e["path"])):
                # the asset itself is gone: drop the entry instead of shipping a
                # manifest that points the integrator at a file that is not there
                dropped.append(e["path"])
                continue
            for rel in [l["path"] for l in e.get("lods", [])] + \
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
    for d in sorted(set(dropped)):
        E.log("dropped missing asset from manifest: %s" % d)

    atlases = {}
    for fam in ATLAS_FAMILIES:
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
        "units": "1 metre. The FBX declares UnitScaleFactor 100 and carries "
                 "real metres, Y up, with the axis conversion baked into the "
                 "vertex data (export: axis_forward='-Z', axis_up='Y', "
                 "apply_scale_options='FBX_SCALE_UNITS', "
                 "bake_space_transform on anything without an armature). "
                 "Models face +Z with Y up in Unity, on stock import settings.",
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
        "terrainVertexColors": {
            "attribute": "Col",
            "appliesTo": "every asset whose `unityMaterial` is "
                         "PokeLab/TerrainBlend -- the Ground, Ramp and Ledge "
                         "subfamilies",
            "R": "grass layer weight",
            "G": "dirt layer weight",
            "B": "sand layer weight",
            "A": "rock layer weight",
            "note": "Normalised to sum to 1 per vertex, matching "
                    "PokeLabTerrainBlend's stated convention. Worn dirt is "
                    "baked along the layout's own walkable path polylines, and "
                    "rock rises automatically on the skirt walls, so the "
                    "shader's slope-driven rock is reinforcing an authored "
                    "weight rather than fighting a flat one.",
        },
        "gpuInstancing": {
            "families": ["Grass", "TallGrass", "Flower", "Fern", "Bush",
                         "Reed", "Lilypad", "Moss"],
            "contract": "One mesh, one material, no sub-objects, no LODs, "
                        "pivot at the base and centred so a per-instance Y "
                        "rotation spins the cluster in place. Variation comes "
                        "from a handful of genuinely different cluster meshes "
                        "plus per-instance rotation, scale and wind phase at "
                        "draw time -- not from more asset variants.",
            "windPhase": "The green channel is the WITHIN-cluster phase "
                         "offset only. Add the per-instance phase on top at "
                         "draw time.",
        },
        "atlases": atlases,
        "unitySetup": {
            "importScale": 1.0,
            "convertUnits": True,
            "bakeAxisConversion": False,
            "exportFix": "FIXED. The kit previously exported with "
                         "axis_forward='-Y', axis_up='Z' and "
                         "apply_scale_options='FBX_SCALE_NONE', which wrote "
                         "UnitScaleFactor=1.0 (centimetres) over metre-valued "
                         "vertices and declared UpAxis=+Z. Unity therefore "
                         "imported the whole kit at 1/100 scale, lying on its "
                         "back. It now exports axis_forward='-Z', axis_up='Y', "
                         "apply_scale_options='FBX_SCALE_UNITS', "
                         "bake_space_transform=True: UnitScaleFactor=100.0, "
                         "real metres, Y up baked into the vertex data, "
                         "identity node transforms. Verify any file with "
                         "`python Tools/Blender/environment/fbx_probe.py "
                         "<file.fbx>`. IMPORT WITH STOCK SETTINGS -- Scale "
                         "Factor 1, Convert Units ON, Bake Axis Conversion "
                         "OFF. Any globalScale=100 or bakeAxisConversion "
                         "compensation added while the bug was live must now "
                         "be removed, or the correction is applied twice.",
            "normals": "Import (custom split normals are authored; do not "
                       "recalculate)",
            "tangents": "Calculate Mikktspace",
            "materials": "One material per family atlas is enough: assign the "
                         "family BaseColor + Normal to the shared stylised "
                         "toon shader. Every mesh in a family is UV-packed "
                         "into that one atlas. EXCEPT the Ground/Water/Ramp/"
                         "Ledge/Waterfall subfamilies, which carry no atlas: "
                         "see each entry's `unityMaterial` field.",
            "lodGroups": "For any asset with a non-empty `lods` array, add a "
                         "LODGroup with LOD0 = base mesh, LOD1 and LOD2 from "
                         "the listed FBXs. Suggested screen-relative heights "
                         "0.45 / 0.16 / 0.03 with 30% fade transition width, "
                         "Cross Fade animation mode. Assets with an empty "
                         "`lods` array are deliberately single-mesh: the "
                         "scatter families are GPU instanced (per-instance LOD "
                         "selection costs more than it saves at 200 tris) and "
                         "the walkable ground decks would desync from the "
                         "layout's placement heights if they were decimated.",
            "worldSpaceAssets": "Every asset whose `pivot` reads 'world origin "
                                "(0,0,0)' is authored in level world space: "
                                "instantiate it at position (0,0,0) with "
                                "identity rotation and scale 1, and it lands "
                                "exactly where slice_layout.json's terrain "
                                "block says it should. Do not re-position it.",
            "colliders": "Ground decks, ramps and ledges are closed solids -- "
                         "boundary skirted to a floor and capped -- so a "
                         "MeshCollider straight on the imported mesh is "
                         "watertight; leave Convex OFF. Cliff, path and "
                         "riverbank modules are likewise closed solids. Water "
                         "surfaces want a trigger collider on the Water layer, "
                         "not a solid one. Foliage should use no collider, or "
                         "a capsule on the tall-grass patches if encounters "
                         "are driven by trigger volumes.",
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

    # every class in BUDGETS, not a hand-kept list: a class that is missing
    # here is silently unreported, which is how eight over-budget capture
    # balls could have gone out looking like a clean run
    for cls in BUDGETS:
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
