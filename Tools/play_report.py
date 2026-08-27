# -*- coding: utf-8 -*-
"""Turns a play-probe run into something an agent can actually read.

A probe run leaves a pile of PNGs and a trace.csv. Neither is reviewable as it stands:

  * A PNG carries no provenance. Opening one shows pixels and nothing else -- not when it was
    taken, not where the camera was, not what the player was doing. Judgements were being made
    from pictures whose own subject was a guess, and "I do not know what I am looking at" is
    not a thing more pictures fix.

  * A CSV of four hundred rows is not readable either, and the interesting parts of it are
    never single rows. Speed is a difference. Hit-stop is a run of rows. Screen shake is the
    high-frequency part of a path. All three are invisible when you look at the file.

So this does the two halves. It burns each frame's own telemetry into the frame as a caption,
which makes one contact sheet self-describing -- open it and every tile says when it is and
where the camera was. And it reduces the trace to the handful of numbers that answer the
questions actually being asked: how fast is that NPC really moving, did the frame rate hold,
how long was the hit-stop, how far did the camera kick and how quickly did it settle.

Then, because none of that answers "is this good", it can diff a run against a baseline you
have already accepted. That is the honest form of the question: nobody can tell you a number
is correct, but "this used to be 1.40 and is now 0.31" is a finding at any hour of the night.

    python Tools/play_report.py Captures/play
    python Tools/play_report.py Captures/play --baseline Captures/golden/overworld.json
    python Tools/play_report.py Captures/play --promote Captures/golden/overworld.json
"""
import argparse
import json
import math
import os
import re
import sys

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    Image = None

BURST = re.compile(r"^(?P<prefix>.+)_b(?P<index>\d{3})\.png$")

# Anything slower than this counts as standing still. A NavMeshAgent jitters by a few
# millimetres a frame while idle, and averaging that in drags every "walking speed" down.
MOVING = 0.05


# --------------------------------------------------------------------------- reading

def read_trace(path):
    """The CSV as a list of dicts, numbers already parsed."""
    with open(path, encoding="utf-8") as handle:
        lines = [line for line in handle.read().splitlines() if line.strip()]
    if not lines:
        return [], []

    header = lines[0].split(",")
    rows = []
    for line in lines[1:]:
        cells = line.split(",")
        if len(cells) != len(header):
            continue
        row = {}
        for key, cell in zip(header, cells):
            try:
                row[key] = float(cell)
            except ValueError:
                row[key] = cell
        rows.append(row)
    return header, rows


def actor_ids(header):
    """Everything tracked EXCEPT the player.

    The player's columns are named the same way as an NPC's, so the obvious version of this
    returns the player too -- which puts them in their own summary twice and lists them among
    the NPCs in every frame caption, as though the player were something walking past.
    """
    names = [name[:-len("_measured")] for name in header if name.endswith("_measured")]
    return [name for name in names if name != "player"]


# --------------------------------------------------------------------------- analysis

def spans(rows, predicate):
    """Contiguous stretches where predicate holds, as (start_t, seconds, rows)."""
    out, run = [], []
    for row in rows:
        if predicate(row):
            run.append(row)
        elif run:
            out.append(run)
            run = []
    if run:
        out.append(run)
    return [(r[0]["t"], r[-1]["t"] - r[0]["t"], r) for r in out]


def residual(rows, keys, window=5):
    """How far each frame sits off a moving average of its own path.

    This is what isolates a shake from a pan. A camera that swings to follow the player moves
    a long way, smoothly; a camera that is being kicked moves a short way, sharply, and comes
    back. Subtracting the local average leaves only the second kind -- so the number below is
    shake amplitude, and it does not go up merely because the player turned a corner.

    Frames within one window of either end are returned as None rather than as a number.
    Their window is truncated and therefore lopsided, which on a camera that is simply
    tracking forward leaves a residual that is pure arithmetic -- large enough, on a run
    ending mid-pan, to be mistaken for a shake that never settled.
    """
    out = []
    for i in range(len(rows)):
        if i < window or i >= len(rows) - window:
            out.append(None)
            continue
        window_rows = rows[i - window:i + window + 1]
        offset = 0.0
        for key in keys:
            mean = sum(r[key] for r in window_rows) / len(window_rows)
            offset += (rows[i][key] - mean) ** 2
        out.append(math.sqrt(offset))
    return out


def stat(values):
    if not values:
        return {"mean": 0.0, "max": 0.0, "min": 0.0}
    ordered = sorted(values)
    return {
        "mean": round(sum(values) / len(values), 4),
        "max": round(ordered[-1], 4),
        "min": round(ordered[0], 4),
    }


def analyse(rows, header):
    if not rows:
        return {"frames": 0}

    dts = [r["dt"] for r in rows if r["dt"] > 0]
    fps = sorted(1.0 / dt for dt in dts) if dts else [0.0]
    summary = {
        "frames": len(rows),
        "seconds": round(rows[-1]["t"] - rows[0]["t"], 3),
        "fps": {
            "mean": round(len(fps) / sum(1.0 / f for f in fps), 1) if fps and all(fps) else 0.0,
            # The fifth percentile rather than the minimum: one 200 ms frame is a shader
            # compiling, and reporting it as "the frame rate" describes the wrong thing.
            "p05": round(fps[max(0, int(len(fps) * 0.05))], 1),
            "worst": round(fps[0], 1),
        },
    }

    # Hit-stop, and anything else that leans on the clock.
    stops = spans(rows, lambda r: r["timeScale"] < 0.99)
    summary["hitstop"] = [
        {
            "at": round(start, 3),
            # Plus the final frame's own dt. A span of N frames lasts N frame-times, not the
            # N-1 gaps between their timestamps -- the last frame is still being displayed.
            # Twelve frames of a 60 Hz hit-stop is 200 ms, and the naive difference calls it
            # 183, which is the kind of quiet 8% that makes a tuning number impossible to
            # trust.
            "seconds": round(length + run[-1]["dt"], 3),
            "floor": round(min(r["timeScale"] for r in run), 3),
        }
        for start, length, run in stops if length > 0.001
    ]

    # The same thing again as plain numbers, because the list above is invisible to the
    # baseline diff -- it only walks scalars. Hit-stop length is precisely the sort of value
    # somebody retunes from 120 ms to 40 and does not mention, and a regression report that
    # cannot see it is checking everything except the part that gets edited.
    summary["hitstops"] = {
        "count": len(summary["hitstop"]),
        "total": round(sum(s["seconds"] for s in summary["hitstop"]), 3),
        "longest": round(max([s["seconds"] for s in summary["hitstop"]] or [0.0]), 3),
        "floor": round(min([s["floor"] for s in summary["hitstop"]] or [1.0]), 3),
    }

    # Camera kick, and how long it took to settle.
    shake = residual(rows, ["cam_x", "cam_y", "cam_z"])
    valid = [s for s in shake if s is not None]
    peak = max(valid) if valid else 0.0
    settle = None
    if peak > 0.0005:
        at = shake.index(peak)
        floor = peak * 0.1

        # The LAST frame still above a tenth of the peak, not the first frame below it.
        #
        # A shake oscillates, so it passes through zero on the way to every extreme. Taking
        # the first small value catches one of those crossings and calls a 200 ms kick
        # settled in 17 ms -- which would make a perfectly good screen shake look as though
        # it were being cut off.
        #
        # Bounded by a quiet run, so that a SECOND, unrelated kick later in the same leg is
        # not folded into the first one's decay.
        quiet_needed = int(0.25 / (rows[at]["dt"] or 0.016)) + 1
        settle, quiet = 0.0, 0
        for i in range(at, len(rows)):
            if shake[i] is None:
                break
            if shake[i] >= floor:
                settle = round(rows[i]["t"] - rows[at]["t"], 3)
                quiet = 0
            else:
                quiet += 1
                if quiet >= quiet_needed:
                    break
    summary["camera"] = {
        "shake_peak": round(peak, 4),
        "shake_settle": settle,
        "boom": stat([r["boom"] for r in rows]),
        "fov": stat([r["cam_fov"] for r in rows]),
    }

    # Every mover, declared against measured.
    actors = {}
    for actor in ["player"] + actor_ids(header):
        declared = [r[actor + "_declared"] for r in rows]

        # Measured speed is put back onto the game's clock before it is compared with the
        # declared one.
        #
        # The trace divides distance by UNSCALED time, which is what makes a stutter visible.
        # But a declared speed is in game units per game second, so during a hit-stop the two
        # legitimately differ by exactly the time scale -- and comparing them raw reported
        # every actor on screen as "disagreeing" at the one moment the game was working
        # perfectly. Dividing it back out means a disagreement is a real one.
        measured = [
            r[actor + "_measured"] / r["timeScale"] if r["timeScale"] > 0.01 else 0.0
            for r in rows
        ]
        moving = [m for m in measured if m > MOVING]

        # Compared only while moving, because a stopped actor agrees with itself trivially
        # and averaging those frames in hides a disagreement that only happens under way.
        gap = [abs(d - m) for d, m in zip(declared, measured) if m > MOVING or d > MOVING]

        actors[actor] = {
            "declared": stat(declared),
            "measured": stat(measured),
            "moving_speed": round(sum(moving) / len(moving), 3) if moving else 0.0,
            "moving_fraction": round(len(moving) / len(measured), 3) if measured else 0.0,
            "disagreement": round(max(gap), 3) if gap else 0.0,
        }
    summary["actors"] = actors
    return summary


# --------------------------------------------------------------------------- drawing

def font(size):
    for name in ("consola.ttf", "arial.ttf", "DejaVuSansMono.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except (OSError, IOError):
            continue
    return ImageFont.load_default()


def caption_for(row, header):
    """The two lines burned under a frame: when and who, then where the camera was.

    Two rather than one because one did not fit. A single line ran past the edge of its own
    tile and drew across the neighbouring one, so the frame at 4.00s was captioned with an
    NPC belonging to the frame beside it -- a provenance tool getting the provenance wrong,
    which is worse than not captioning at all.
    """
    first = [
        "t=%.2fs" % row["t"],
        "%s" % (row["beat"] or "-"),
        "spd %.2f" % row["player_measured"],
    ]
    if row["timeScale"] < 0.99:
        first.append("TIME x%.2f" % row["timeScale"])

    second = ["cam yaw %.0f  pit %.0f  boom %.1fm"
              % (row["cam_yaw"], row["cam_pitch"], row["boom"])]
    movers = [
        "%s %.2f" % (actor.split(".")[-1][:10], row[actor + "_measured"])
        for actor in actor_ids(header)
        if row[actor + "_measured"] > MOVING
    ]
    if movers:
        second.append("| " + "  ".join(movers[:3]))

    return ["   ".join(first), "   ".join(second)]


def fit(draw, text, glyphs, width):
    """Trims a caption to the tile it belongs to, with an ellipsis so the trim is visible."""
    if draw.textlength(text, font=glyphs) <= width:
        return text
    while text and draw.textlength(text + "…", font=glyphs) > width:
        text = text[:-1]
    return text + "…"


def sheet(frames, out_path, title, columns, tile_width=420):
    """A grid of frames, each carrying its own caption.

    The caption is the whole point. A contact sheet of bare frames is the same problem as a
    folder of bare frames, only faster to scroll -- what makes it evidence is that every tile
    states its own time and pose, so a claim about one of them can be checked against the
    picture it is a claim about.
    """
    if not frames:
        return None

    with Image.open(frames[0][0]) as probe:
        scale = tile_width / probe.width
        tile_height = int(probe.height * scale)
    bar = 46
    pad = 8

    rows = (len(frames) + columns - 1) // columns
    width = columns * tile_width + (columns + 1) * pad
    height = rows * (tile_height + bar) + (rows + 1) * pad + 34

    canvas = Image.new("RGB", (width, height), (16, 17, 22))
    draw = ImageDraw.Draw(canvas)
    draw.text((pad, 10), title, font=font(17), fill=(235, 235, 240))

    small = font(13)
    for i, (path, caption) in enumerate(frames):
        column, row = i % columns, i // columns
        x = pad + column * (tile_width + pad)
        y = 34 + pad + row * (tile_height + bar + pad)

        with Image.open(path) as image:
            canvas.paste(image.convert("RGB").resize((tile_width, tile_height)), (x, y))

        draw.rectangle([x, y + tile_height, x + tile_width, y + tile_height + bar],
                       fill=(28, 30, 38))
        for line, text in enumerate(caption):
            draw.text((x + 7, y + tile_height + 6 + line * 17),
                      fit(draw, text, small, tile_width - 14), font=small,
                      fill=(210, 216, 228) if line == 0 else (150, 158, 176))

    canvas.save(out_path)
    return out_path


def build_sheets(run_dir, rows, header, out_dir):
    """One sheet for the stills, one per burst."""
    made = []
    by_shot = {r["shot"]: r for r in rows if isinstance(r.get("shot"), str) and r["shot"]}

    stills, bursts = [], {}
    for name, row in by_shot.items():
        path = os.path.join(run_dir, name)
        if not os.path.exists(path):
            continue
        match = BURST.match(name)
        if match:
            bursts.setdefault(match.group("prefix"), []).append((int(match.group("index")), path, row))
        else:
            stills.append((name, path, row))

    if stills:
        stills.sort(key=lambda item: item[2]["t"])
        made.append(sheet(
            [(path, caption_for(row, header)) for _, path, row in stills],
            os.path.join(out_dir, "contact.png"),
            "stills  ·  %d frames  ·  %s" % (len(stills), os.path.basename(run_dir)),
            columns=3))

    for prefix, items in sorted(bursts.items()):
        items.sort(key=lambda item: item[0])
        # 10 across keeps a two-second burst at 30 fps on six readable rows. Wider and the
        # tiles are too small to see the thing the burst was taken to see.
        made.append(sheet(
            [(path, caption_for(row, header)) for _, path, row in items],
            os.path.join(out_dir, "burst_%s.png" % prefix),
            "burst %s  ·  %d frames  ·  %.2fs" % (
                prefix, len(items), items[-1][2]["t"] - items[0][2]["t"]),
            columns=5, tile_width=330))

    return [m for m in made if m]


# --------------------------------------------------------------------------- baseline

def flatten(summary, prefix=""):
    """Every scalar in the summary, as dotted keys, so two runs can be compared key by key."""
    out = {}
    for key, value in summary.items():
        name = prefix + key
        if isinstance(value, dict):
            out.update(flatten(value, name + "."))
        elif isinstance(value, (int, float)) and not isinstance(value, bool):
            out[name] = float(value)
    return out


def diff(current, baseline, tolerance):
    """What changed, and by enough to say so.

    Relative, with an absolute floor: a boom of 6.1 against 6.0 is noise, and a shake peak of
    0.002 against 0.001 is a doubling that means nothing. Both need to be ignorable by the
    same rule, or the report cries wolf and stops being read.
    """
    now, before = flatten(current), flatten(baseline)
    findings = []
    for key in sorted(set(now) | set(before)):
        if key not in before:
            findings.append((key, None, now[key], "new"))
            continue
        if key not in now:
            findings.append((key, before[key], None, "gone"))
            continue

        a, b = before[key], now[key]
        floor = max(abs(a), abs(b))
        if floor < 0.01:
            continue
        if abs(b - a) / floor > tolerance:
            findings.append((key, a, b, "%+.0f%%" % ((b - a) / floor * 100)))
    return findings


# --------------------------------------------------------------------------- entry

def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run", help="a probe output directory, e.g. Captures/play")
    parser.add_argument("--baseline", help="summary.json of an accepted run to diff against")
    parser.add_argument("--promote", help="write this run's summary there as the new baseline")
    parser.add_argument("--tolerance", type=float, default=0.10,
                        help="relative change worth reporting (default 0.10)")
    args = parser.parse_args()

    trace_path = os.path.join(args.run, "trace.csv")
    if not os.path.exists(trace_path):
        sys.exit("no trace.csv in %s -- run the probe with \"trace\": true" % args.run)

    header, rows = read_trace(trace_path)
    summary = analyse(rows, header)
    summary["run"] = os.path.basename(os.path.abspath(args.run))

    with open(os.path.join(args.run, "summary.json"), "w", encoding="utf-8") as handle:
        json.dump(summary, handle, indent=2)

    print("== %s ==" % summary["run"])
    print("%d frames over %.2fs   fps mean %.0f  p05 %.0f  worst %.0f"
          % (summary["frames"], summary["seconds"], summary["fps"]["mean"],
             summary["fps"]["p05"], summary["fps"]["worst"]))

    camera = summary["camera"]
    print("camera  boom %.2f-%.2fm  fov %.0f  shake peak %.3f%s"
          % (camera["boom"]["min"], camera["boom"]["max"], camera["fov"]["mean"],
             camera["shake_peak"],
             "  settled in %.0fms" % (camera["shake_settle"] * 1000)
             if camera["shake_settle"] else ""))

    for stop in summary["hitstop"]:
        print("hit-stop at %.2fs for %.0fms (timeScale floor %.2f)"
              % (stop["at"], stop["seconds"] * 1000, stop["floor"]))

    print("")
    print("%-26s %8s %8s %8s %7s" % ("actor", "declared", "measured", "moving%", "gap"))
    for name, actor in summary["actors"].items():
        flag = "  <-- disagrees" if actor["disagreement"] > 0.25 else ""
        print("%-26s %8.2f %8.2f %7.0f%% %7.2f%s"
              % (name[:26], actor["declared"]["max"], actor["measured"]["max"],
                 actor["moving_fraction"] * 100, actor["disagreement"], flag))

    if Image is None:
        print("\n(Pillow is not installed, so no contact sheet was drawn)")
    else:
        for made in build_sheets(args.run, rows, header, args.run):
            print("\nsheet: %s" % made)

    if args.baseline:
        print("")
        if not os.path.exists(args.baseline):
            print("baseline %s does not exist yet -- run with --promote to create it"
                  % args.baseline)
        else:
            with open(args.baseline, encoding="utf-8") as handle:
                findings = diff(summary, json.load(handle), args.tolerance)
            if not findings:
                print("== matches baseline (within %.0f%%) ==" % (args.tolerance * 100))
            else:
                print("== %d changes against baseline ==" % len(findings))
                for key, before, now, how in findings:
                    print("  %-40s %10s -> %-10s %s"
                          % (key,
                             "-" if before is None else "%.3f" % before,
                             "-" if now is None else "%.3f" % now,
                             how))

    if args.promote:
        os.makedirs(os.path.dirname(os.path.abspath(args.promote)), exist_ok=True)
        with open(args.promote, "w", encoding="utf-8") as handle:
            json.dump(summary, handle, indent=2)
        print("\nbaseline written: %s" % args.promote)


if __name__ == "__main__":
    main()
