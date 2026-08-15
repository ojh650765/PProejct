"""
Build every clip and write Assets/Game/Audio + audio_manifest.json.

Usage (from Tools/Audio):
    python build_all.py                 # everything
    python build_all.py music sfx       # only those groups
    python build_all.py --list          # names only, no rendering

Output is deterministic: every generator is seeded, so re-running produces
byte-identical WAVs and a diff over Assets/Game/Audio stays meaningful.
"""

from __future__ import annotations

import json
import math
import os
import sys
import time

import numpy as np

import ambience
import dsp
import music
import sfx

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
AUDIO_ROOT = os.path.join(REPO, "Assets", "Game", "Audio")
MANIFEST = os.path.join(AUDIO_ROOT, "audio_manifest.json")

# Peak ceiling. -1 dBFS leaves headroom for the mixer's group gain and for the
# summing of several simultaneous layers before Unity's output limiter is reached.
PEAK = 0.891  # 20*log10(0.891) = -1.00 dBFS


def _dbfs(x: float) -> float:
    return 20.0 * math.log10(max(x, 1e-9))


def _write(entries, name, sig, category, subdir, loop, trigger, peak=PEAK,
           normalise=True):
    path = os.path.join(AUDIO_ROOT, subdir, name + ".wav")
    info = dsp.write_wav(path, sig, peak=peak, normalise=normalise)
    data, sr, _, ch = dsp.read_wav(path)
    seam = dsp.loop_seam_report(data) if loop else None
    rms = float(np.sqrt(np.mean(data**2)))
    entries.append({
        "name": name,
        "path": f"Assets/Game/Audio/{subdir}/{name}.wav",
        "category": category,
        "loop": bool(loop),
        "duration": round(info["duration"], 4),
        "channels": info["channels"],
        "sample_rate": sr,
        "bit_depth": 16,
        "peak_dbfs": round(_dbfs(info["peak"]), 2),
        "rms_dbfs": round(_dbfs(rms), 2),
        "bytes": info["bytes"],
        "loop_step_ratio": round(seam["step_ratio"], 4) if seam else None,
        "trigger": trigger,
    })
    return info


def build_music(entries):
    for name, (fn, loop, trigger) in music.TRACKS.items():
        t0 = time.time()
        sig, _ = fn()
        _write(entries, name, sig, "Music", "Music", loop, trigger)
        print(f"  {name:26s} {time.time() - t0:5.2f}s")


def build_sfx(entries):
    for name, (fn, loop, category, trigger) in sfx.REGISTRY.items():
        res = fn()
        sig = res[0] if isinstance(res, tuple) else res
        # UI, footsteps and the typewriter are intentionally quieter than combat cues;
        # their generators already set their own ceiling, so do not re-normalise them.
        preserve = name.startswith(("SFX_Foot_", "SFX_UI_")) or name in (
            "SFX_Battle_HpTick", "SFX_Battle_LowHpWarning", "SFX_Battle_ExpGain",
            "SFX_Scanner_ScanLoop", "SFX_Scanner_DataBlip_01", "SFX_Scanner_DataBlip_02",
            "SFX_Scanner_DataBlip_03", "SFX_Overworld_GrassRustle_01",
            "SFX_Overworld_GrassRustle_02", "SFX_Overworld_GrassRustle_03")
        _write(entries, name, sig, f"SFX/{category}", "SFX", loop, trigger,
               normalise=not preserve)
    print(f"  {len(sfx.REGISTRY)} sfx")


def build_ambience(entries):
    for name, (sig, loop, desc) in ambience.build_all().items():
        _write(entries, name, sig, "Ambience", "Ambience", loop, desc, normalise=False)
        print(f"  {name}")


GROUPS = {"music": build_music, "sfx": build_sfx, "ambience": build_ambience}


def main(argv):
    if "--list" in argv:
        for k in music.TRACKS:
            print("Music   ", k)
        for k in sfx.REGISTRY:
            print("SFX     ", k)
        for k in list(ambience.LAYERS) + list(ambience.ONESHOTS):
            print("Ambience", k)
        return 0

    wanted = [a for a in argv if a in GROUPS] or list(GROUPS)
    os.makedirs(AUDIO_ROOT, exist_ok=True)

    existing = []
    if os.path.exists(MANIFEST):
        with open(MANIFEST, "r", encoding="utf-8") as f:
            existing = json.load(f).get("clips", [])

    entries = []
    t0 = time.time()
    for g in wanted:
        print(f"[{g}]")
        GROUPS[g](entries)

    # keep entries for groups that were not rebuilt this run
    if len(wanted) < len(GROUPS):
        rebuilt = {e["name"] for e in entries}
        entries += [e for e in existing if e["name"] not in rebuilt]

    order = {"Music": 0, "Ambience": 2}
    entries.sort(key=lambda e: (order.get(e["category"].split("/")[0], 1), e["name"]))

    total_bytes = sum(e["bytes"] for e in entries)
    manifest = {
        "schema": 1,
        "generator": "Tools/Audio/build_all.py",
        "note": ("Every clip here is synthesised procedurally by the scripts in Tools/Audio. "
                 "No sampled or licensed material is used. Regenerate with "
                 "'python build_all.py' from that directory."),
        "sample_rate": dsp.SR,
        "bit_depth": 16,
        "peak_ceiling_dbfs": round(_dbfs(PEAK), 2),
        "clip_count": len(entries),
        "total_bytes": total_bytes,
        "total_mb": round(total_bytes / (1024 * 1024), 2),
        "categories": sorted({e["category"] for e in entries}),
        "clips": entries,
    }
    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"\n{len(entries)} clips, {manifest['total_mb']} MB, "
          f"{time.time() - t0:.1f}s -> {MANIFEST}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
