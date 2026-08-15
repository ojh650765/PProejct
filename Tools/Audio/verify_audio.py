"""
Numeric verification of everything under Assets/Game/Audio, plus preview plots.

Nobody on this task can listen to the output, so every claim about it has to be
measurable. This script asserts, for every clip:

  * the file is a readable RIFF/WAVE, 44.1 kHz, 16-bit;
  * it is not silent and not near-silent;
  * it does not clip -- peak is at or below the -1 dBFS ceiling, and there are no
    runs of consecutive full-scale samples (which is what actual clipping looks like
    even when the peak measurement passes);
  * channel count matches its role (mono for SFX, stereo for music and ambience beds,
    mono for the waterfall because it is a 3D point source);
  * DC offset is negligible;
  * the manifest entry agrees with the file on disk;
  * and for every clip marked loop, that the wrap is not a discontinuity.

It then writes contact sheets of waveforms and spectrograms to previews/ so the
spectral behaviour can actually be inspected rather than assumed.
"""

from __future__ import annotations

import json
import math
import os
import sys
import wave

import numpy as np

import dsp

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
AUDIO_ROOT = os.path.join(REPO, "Assets", "Game", "Audio")
MANIFEST = os.path.join(AUDIO_ROOT, "audio_manifest.json")
PREVIEWS = os.path.join(HERE, "previews")

CEILING_DBFS = -0.9        # allow a hair of rounding slack on the -1.0 dBFS target
MIN_RMS_DBFS = -46.0       # anything quieter than this is effectively silent
MAX_DC = 0.02
MAX_LOOP_STEP_RATIO = 1.0  # wrap step must not exceed the sharpest transient in the clip

MONO_EXPECTED_PREFIX = ("SFX_",)
MONO_EXCEPTIONS = {"Amb_Waterfall"}   # 3D point source, deliberately mono


def dbfs(x: float) -> float:
    return 20.0 * math.log10(max(x, 1e-12))


def check_clip(entry) -> tuple[list, dict]:
    problems = []
    path = os.path.join(REPO, entry["path"].replace("/", os.sep))
    if not os.path.exists(path):
        return [f"missing file {entry['path']}"], {}

    with wave.open(path, "rb") as w:
        ch, sw, sr, frames = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(frames)

    if sr != 44100:
        problems.append(f"sample rate {sr} != 44100")
    if sw != 2:
        problems.append(f"sample width {sw * 8} bit != 16 bit")

    ints = np.frombuffer(raw, dtype=np.int16)
    data = ints.astype(np.float64) / 32768.0
    if ch == 2:
        data = data.reshape(-1, 2)
        ints = ints.reshape(-1, 2)

    peak = float(np.max(np.abs(data))) if data.size else 0.0
    rms = float(np.sqrt(np.mean(data**2))) if data.size else 0.0
    mono = np.mean(data, axis=1) if data.ndim == 2 else data

    if rms <= 0.0 or dbfs(rms) < MIN_RMS_DBFS:
        problems.append(f"silent or near-silent (rms {dbfs(rms):.1f} dBFS)")
    if dbfs(peak) > CEILING_DBFS:
        problems.append(f"peak {dbfs(peak):+.2f} dBFS exceeds ceiling {CEILING_DBFS}")

    # true clipping detection: three or more consecutive samples pinned to full scale
    flat = np.abs(ints.reshape(-1)) >= 32760
    if flat.any():
        runs, run = 0, 0
        for v in flat:
            run = run + 1 if v else 0
            runs = max(runs, run)
        if runs >= 3:
            problems.append(f"clipped: {runs} consecutive full-scale samples")

    dc = float(np.mean(mono))
    if abs(dc) > MAX_DC:
        problems.append(f"DC offset {dc:+.4f}")

    if not np.all(np.isfinite(data)):
        problems.append("non-finite samples")

    name = entry["name"]
    expect_mono = name.startswith(MONO_EXPECTED_PREFIX) or name in MONO_EXCEPTIONS
    if expect_mono and ch != 1:
        problems.append(f"expected mono, got {ch} channels")
    if not expect_mono and ch != 2:
        problems.append(f"expected stereo, got {ch} channels")

    duration = frames / float(sr)
    if abs(duration - entry["duration"]) > 0.002:
        problems.append(f"duration {duration:.4f} != manifest {entry['duration']:.4f}")
    if ch != entry["channels"]:
        problems.append(f"channels {ch} != manifest {entry['channels']}")
    if os.path.getsize(path) != entry["bytes"]:
        problems.append("byte size differs from manifest")

    seam = None
    if entry["loop"]:
        seam = dsp.loop_seam_report(data)
        if seam["step_ratio"] > MAX_LOOP_STEP_RATIO:
            problems.append(f"loop seam discontinuity, step ratio {seam['step_ratio']:.2f}")
        # An edge fade leaves the first/last few ms far quieter than the 30 ms beside
        # them; a loop that simply rests at the wrap is quiet on both counts and passes.
        if seam["edge_fade_ratio"] < 0.12:
            problems.append(
                f"loop edges appear faded (edge/near RMS {seam['edge_fade_ratio']:.3f}) "
                "-- a hole at the wrap, not a seam")

    stats = {
        "peak_dbfs": dbfs(peak), "rms_dbfs": dbfs(rms), "dc": dc,
        "duration": duration, "channels": ch,
        "step_ratio": seam["step_ratio"] if seam else None,
        "crest_db": dbfs(peak) - dbfs(rms),
    }
    return problems, stats


# --------------------------------------------------------------------------------------
# plots
# --------------------------------------------------------------------------------------


def spectrogram(mono: np.ndarray, nfft: int = 1024, hop: int = 256):
    win = np.hanning(nfft)
    frames = max(1, (len(mono) - nfft) // hop + 1)
    out = np.empty((nfft // 2 + 1, frames))
    for i in range(frames):
        seg = mono[i * hop: i * hop + nfft]
        if len(seg) < nfft:
            seg = np.pad(seg, (0, nfft - len(seg)))
        out[:, i] = np.abs(np.fft.rfft(seg * win))
    return 20.0 * np.log10(out + 1e-7)


def sheet(names, title, filename, cols=4, cell=(3.4, 2.3)):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    rows = int(math.ceil(len(names) / cols))
    fig, axes = plt.subplots(rows, cols, figsize=(cell[0] * cols, cell[1] * rows))
    axes = np.atleast_1d(axes).reshape(-1)
    for ax in axes[len(names):]:
        ax.axis("off")

    for ax, (name, path) in zip(axes, names):
        data, sr, _, ch = dsp.read_wav(path)
        mono = np.mean(data, axis=1) if data.ndim == 2 else data
        S = spectrogram(mono)
        dur = len(mono) / sr
        vmax = S.max()
        ax.imshow(S, origin="lower", aspect="auto", cmap="magma",
                  extent=[0, dur, 0, sr / 2000.0], vmin=vmax - 78, vmax=vmax)
        ax.set_yscale("symlog", linthresh=1.0)
        ax.set_ylim(0.05, sr / 2000.0)
        # waveform envelope drawn over the spectrogram, scaled into the top decade
        step = max(1, len(mono) // 700)
        env = np.abs(mono[: len(mono) // step * step].reshape(-1, step)).max(axis=1)
        t = np.linspace(0, dur, len(env))
        ax.plot(t, 1.2 + env * 14.0, color="#7fffd4", lw=0.7, alpha=0.85)
        ax.set_title(f"{name}\n{dur:.2f}s  {'st' if ch == 2 else 'mo'}  "
                     f"pk {dbfs(np.max(np.abs(mono))):.1f}dB", fontsize=6.5)
        ax.tick_params(labelsize=5)
        ax.set_ylabel("kHz", fontsize=5)
    fig.suptitle(title, fontsize=11)
    fig.tight_layout(rect=[0, 0, 1, 0.985])
    os.makedirs(PREVIEWS, exist_ok=True)
    out = os.path.join(PREVIEWS, filename)
    fig.savefig(out, dpi=100)
    plt.close(fig)
    return out


SHEETS = [
    ("Moves - cast", "moves_cast.png", lambda e: e["name"].startswith("SFX_Move_") and e["name"].endswith("_Cast")),
    ("Moves - impact and effectiveness", "moves_impact.png",
     lambda e: e["name"].startswith("SFX_Move_") and not e["name"].endswith("_Cast")),
    ("Battle and status", "battle.png",
     lambda e: e["name"].startswith(("SFX_Battle_", "SFX_Status_"))),
    ("Capture set piece", "capture.png", lambda e: e["name"].startswith("SFX_Capture_")),
    ("Overworld and footsteps", "overworld.png", lambda e: e["name"].startswith(("SFX_Foot_", "SFX_Overworld_"))),
    ("Scanner and UI", "scanner_ui.png", lambda e: e["name"].startswith(("SFX_Scanner_", "SFX_UI_"))),
    ("Music", "music.png", lambda e: e["category"] == "Music"),
    ("Ambience", "ambience.png", lambda e: e["category"] == "Ambience"),
]


def main(argv):
    with open(MANIFEST, "r", encoding="utf-8") as f:
        manifest = json.load(f)
    clips = manifest["clips"]

    print(f"verifying {len(clips)} clips, {manifest['total_mb']} MB\n")
    failures = []
    all_stats = {}
    for entry in clips:
        problems, stats = check_clip(entry)
        all_stats[entry["name"]] = stats
        if problems:
            failures.append((entry["name"], problems))

    if failures:
        print("FAILURES")
        for name, problems in failures:
            for p in problems:
                print(f"  {name}: {p}")
    else:
        print("all clips pass: 44.1 kHz / 16-bit, non-silent, unclipped, "
              "channel-correct, manifest-consistent")

    loops = [(n, s) for n, s in all_stats.items() if s.get("step_ratio") is not None]
    if loops:
        worst = max(loops, key=lambda kv: kv[1]["step_ratio"])
        print(f"\nloops: {len(loops)} checked, worst wrap step ratio "
              f"{worst[1]['step_ratio']:.3f} ({worst[0]}); threshold {MAX_LOOP_STEP_RATIO}")

    peaks = [s["peak_dbfs"] for s in all_stats.values() if s]
    rmss = [s["rms_dbfs"] for s in all_stats.values() if s]
    print(f"peak  min {min(peaks):+.2f} max {max(peaks):+.2f} dBFS")
    print(f"rms   min {min(rmss):+.2f} max {max(rmss):+.2f} dBFS")

    if "--no-plots" not in argv:
        print("\nplots:")
        for title, filename, pred in SHEETS:
            sel = [(e["name"], os.path.join(REPO, e["path"].replace("/", os.sep)))
                   for e in clips if pred(e)]
            if sel:
                print("  " + sheet(sel, title, filename))

    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
