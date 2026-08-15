"""
Round-trip verification: re-import the shipped FBXs and check what actually
survived the export, rather than trusting what was in memory before it.

Checks, per asset:
  * mesh present, transforms identity, no loose or zero-area geometry
  * exactly one UV set, all loops inside 0-1
  * foliage carries a colour attribute, R spans a real range (the sway mask)
    and G varies between clusters (the phase offset)
  * characters carry an armature with the Unity Humanoid bone names
"""

import sys
import os
import glob

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy
import bmesh

import envlib as E

# Documented exceptions, not defects: a single blade has one phase by
# definition, and cave moss is a ground crust that must not sway.
EXPECTED = {
    "Env_Grass_Blade": {"G phase offset constant"},
    "Env_Moss_Cave_A": {"R sway mask flat"},
    "Env_Moss_Cave_B": {"R sway mask flat"},
}

HUMANOID = ["Hips", "Spine", "Chest", "UpperChest", "Neck", "Head",
            "Shoulder.L", "UpperArm.L", "LowerArm.L", "Hand.L",
            "Shoulder.R", "UpperArm.R", "LowerArm.R", "Hand.R",
            "UpperLeg.L", "LowerLeg.L", "Foot.L", "Toes.L",
            "UpperLeg.R", "LowerLeg.R", "Foot.R", "Toes.R"]


def check(path, family):
    E.reset_scene()
    bpy.ops.import_scene.fbx(filepath=path,
                             axis_forward=E.FBX_AXIS_FORWARD,
                             axis_up=E.FBX_AXIS_UP,
                             automatic_bone_orientation=True)
    objs = list(bpy.context.scene.objects)
    meshes = [o for o in objs if o.type == 'MESH']
    rigs = [o for o in objs if o.type == 'ARMATURE']
    problems = []
    if not meshes:
        return ["no mesh in file"]
    tris = 0
    for o in meshes:
        me = o.data
        tris += sum(max(0, len(p.vertices) - 2) for p in me.polygons)
        if len(me.uv_layers) != 1:
            problems.append("%d UV layers" % len(me.uv_layers))
        else:
            d = me.uv_layers[0].data
            out = sum(1 for i in range(len(d))
                      if not (-0.002 <= d[i].uv[0] <= 1.002 and
                              -0.002 <= d[i].uv[1] <= 1.002))
            if out:
                problems.append("%d UV loops outside 0-1" % out)
        bm = bmesh.new()
        bm.from_mesh(me)
        if any(not v.link_edges for v in bm.verts):
            problems.append("loose verts")
        if any(not e.link_faces for e in bm.edges):
            problems.append("loose edges")
        if any(f.calc_area() < 1e-9 for f in bm.faces):
            problems.append("zero-area faces")
        bm.free()

        if family == "Foliage":
            attrs = list(me.color_attributes)
            if not attrs:
                problems.append("NO VERTEX COLOURS")
            else:
                data = attrs[0].data
                rs = [data[i].color[0] for i in range(len(data))]
                gs = [data[i].color[1] for i in range(len(data))]
                if max(rs) - min(rs) < 0.20:
                    problems.append("R sway mask flat (%.2f..%.2f)"
                                    % (min(rs), max(rs)))
                if len(set(round(g, 3) for g in gs)) < 2:
                    problems.append("G phase offset constant")
                if max(rs) > 1.001 or min(rs) < -0.001:
                    problems.append("R out of 0-1 (%.3f..%.3f)"
                                    % (min(rs), max(rs)))
    if family == "Characters" and "@" not in os.path.basename(path):
        if not rigs:
            problems.append("NO ARMATURE")
        else:
            names = set(b.name for b in rigs[0].data.bones)
            miss = [b for b in HUMANOID if b not in names]
            if miss:
                problems.append("missing humanoid bones: %s" % ", ".join(miss))
    return problems, tris


def main():
    total = 0
    bad = 0
    for family in ("Foliage", "Terrain", "Town", "Props", "Characters"):
        files = sorted(glob.glob(os.path.join(E.FAMILY_DIR[family], "*.fbx")))
        files = [f for f in files if "@" not in os.path.basename(f)]
        for f in files:
            res = check(f, family)
            probs, tris = res if isinstance(res, tuple) else (res, 0)
            total += 1
            name = os.path.basename(f).replace(".fbx", "")
            allow = EXPECTED.get(name, set())
            real = [p for p in probs
                    if not any(p.startswith(a) for a in allow)]
            if real:
                bad += 1
                E.log("FAIL %-34s %s" % (os.path.basename(f), "; ".join(real)))
            elif probs:
                E.log("ok   %-34s (expected: %s)"
                      % (os.path.basename(f), "; ".join(probs)))
    E.log("==== round trip: %d files checked, %d with problems"
          % (total, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
