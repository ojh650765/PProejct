"""
Compositions.

Every track below is written out as notes, chords and drum patterns -- there is no
randomised pitch material anywhere in this file. Each theme states a key, a tempo,
a chord progression and a melody, and the night variants are genuine re-harmonisations
and re-orchestrations of their daytime counterparts rather than pitch-shifted copies,
so the crossfade between them lands on shared harmony.

Loop construction: every theme renders ``total_beats`` of music plus an overhang long
enough to contain the final reverb tail and note releases, then ``dsp.make_seamless``
folds that overhang back onto the head. The result is exactly ``loop_seconds`` long and
wraps with no click and no truncated decay.
"""

from __future__ import annotations

import numpy as np

import dsp
import instruments as I
from dsp import make_seamless, n_samples

# --------------------------------------------------------------------------------------
# helpers
# --------------------------------------------------------------------------------------


def drum_voice(fn, **fixed):
    """Adapt a drum generator (seconds, vel) to the Track voice signature."""

    def voice(midi, seconds, vel, **kw):
        return fn(vel=vel, **fixed, **kw)

    return voice


_VEL = {"X": 1.0, "x": 0.72, "o": 0.45, "s": 0.28}


def drums(track: I.Track, pattern: str, bars: int, bar_beats: float = 4.0,
          start_bar: int = 0):
    """
    Add hits from a step-sequencer string. One character per step; 16 characters is
    one bar of sixteenths. 'X' accent, 'x' normal, 'o' ghost, 's' very soft, '.' rest.
    """
    step = bar_beats / len(pattern)
    for b in range(start_bar, start_bar + bars):
        for i, c in enumerate(pattern):
            if c == ".":
                continue
            track.add(b * bar_beats + i * step, 60, step, _VEL[c])
    return track


def arpeggiate(track: I.Track, chords, bar_beats: float = 4.0, step: float = 0.5,
               shape=(0, 1, 2, 1), vel: float = 0.8, octave_lift: int = 0):
    """Run a repeating arpeggio figure over a chord-per-bar list."""
    for bar, chord in enumerate(chords):
        steps = int(round(bar_beats / step))
        for s in range(steps):
            idx = shape[s % len(shape)]
            pitch = chord[idx % len(chord)] + 12 * (idx // len(chord)) + 12 * octave_lift
            track.add(bar * bar_beats + s * step, pitch, step * 1.6, vel)
    return track


def chord_pad(track: I.Track, chords, bar_beats: float = 4.0, vel: float = 0.8,
              hold: float = 1.0):
    for bar, chord in enumerate(chords):
        track.add(bar * bar_beats, list(chord), bar_beats * hold, vel)
    return track


def ch(root: str, kind: str = "maj", octave_shift: int = 0):
    """Build a chord as MIDI numbers from a root name and a quality."""
    intervals = {
        "maj": (0, 4, 7),
        "min": (0, 3, 7),
        "maj7": (0, 4, 7, 11),
        "min7": (0, 3, 7, 10),
        "dom7": (0, 4, 7, 10),
        "sus2": (0, 2, 7),
        "sus4": (0, 5, 7),
        "add9": (0, 4, 7, 14),
        "min9": (0, 3, 7, 14),
        "maj9": (0, 4, 7, 14),
        "dim": (0, 3, 6),
        "min6": (0, 3, 7, 9),
        "5": (0, 7),
    }[kind]
    base = dsp.n(root) + 12 * octave_shift
    return [base + i for i in intervals]


def finish(parts_and_pans, loop_seconds: float, ir=None, rev_mix: float = 0.18,
           width: float = 1.25, peak: float = 0.86, loop: bool = True):
    """Common mixdown: pan, sum, reverb, widen, glue-compress, fold the loop."""
    parts = [p for p, _ in parts_and_pans]
    pans = [q for _, q in parts_and_pans]
    bed = I.stereo_mix(parts, pans)
    if ir is not None:
        bed = dsp.reverb(bed, ir, rev_mix)
    bed = dsp.widen(bed, width)
    bed = dsp.compress(bed, threshold_db=-16.0, ratio=2.4, attack_ms=12.0, release_ms=180.0)
    bed = dsp.highpass(bed, 28.0, 2)
    bed = dsp.soft_clip(bed, 0.95)
    if loop:
        bed = make_seamless(bed, loop_seconds)
    return dsp.normalize(bed, peak)


# ======================================================================================
# ROUTE -- D major, walking tempo, bright
# ======================================================================================

ROUTE_BPM = 132.0
ROUTE_BARS = 8
ROUTE_BEATS = ROUTE_BARS * 4

# | D | A/C# | Bm | G | D/F# | G | A | D |
ROUTE_CHORDS = [
    ch("D3"), ch("A3"), ch("B3", "min"), ch("G3"),
    ch("D3"), ch("G3"), ch("A3"), ch("D3", "add9"),
]
ROUTE_BASS = [
    dsp.n("D2"), dsp.n("C#2"), dsp.n("B1"), dsp.n("G1"),
    dsp.n("F#1"), dsp.n("G1"), dsp.n("A1"), dsp.n("D2"),
]

# The tune: an eight-bar arc that climbs to A5 in bar 4 and settles back to D5.
ROUTE_MELODY = [
    (0.0, "A4", 1.0), (1.0, "D5", 1.0), (2.0, "F#5", 1.5), (3.5, "E5", 0.5),
    (4.0, "E5", 1.0), (5.0, "C#5", 1.0), (6.0, "A4", 2.0),
    (8.0, "B4", 1.0), (9.0, "D5", 1.0), (10.0, "F#5", 1.0), (11.0, "B5", 1.0),
    (12.0, "A5", 1.5), (13.5, "G5", 0.5), (14.0, "F#5", 2.0),
    (16.0, "F#5", 1.0), (17.0, "E5", 1.0), (18.0, "D5", 2.0),
    (20.0, "G5", 1.0), (21.0, "F#5", 1.0), (22.0, "E5", 1.0), (23.0, "D5", 1.0),
    (24.0, "C#5", 1.0), (25.0, "E5", 1.0), (26.0, "A5", 1.5), (27.5, "G5", 0.5),
    (28.0, "F#5", 1.5), (29.5, "E5", 0.5), (30.0, "D5", 2.0),
]


def route_theme():
    bpm = ROUTE_BPM
    ir = dsp.make_ir(1.4, decay=5.5, damp_hz=6200, seed=3)

    lead = I.Track(I.lead_square, bpm, gain=1.0, humanise=0.006, seed=1)
    lead.extend([(b, p, d, 0.95) for b, p, d in ROUTE_MELODY])

    doubler = I.Track(I.marimba, bpm, gain=0.55, humanise=0.008, seed=2)
    doubler.extend([(b, p, min(d, 1.0), 0.7) for b, p, d in ROUTE_MELODY])

    arp = I.Track(I.pluck, bpm, gain=0.62, humanise=0.01, seed=3)
    arpeggiate(arp, ROUTE_CHORDS, step=0.5, shape=(0, 1, 2, 1, 3, 2, 1, 0), vel=0.6)

    pads = I.Track(I.strings, bpm, gain=0.5, seed=4)
    chord_pad(pads, [[p - 12 for p in c] for c in ROUTE_CHORDS], vel=0.55, hold=0.95)

    low = I.Track(I.bass, bpm, gain=0.95, humanise=0.004, seed=5)
    for bar, root in enumerate(ROUTE_BASS):
        b = bar * 4
        low.add(b + 0.0, root, 0.5, 1.0)
        low.add(b + 0.75, root, 0.25, 0.6)
        low.add(b + 1.5, root + 12, 0.5, 0.72)
        low.add(b + 2.0, root + 7, 0.5, 0.85)
        low.add(b + 3.0, root + 12, 0.5, 0.7)
        low.add(b + 3.5, root + 7, 0.5, 0.6)

    kicks = I.Track(drum_voice(I.kick, seconds=0.36), bpm, gain=0.95, seed=6)
    drums(kicks, "X.......x..x....", ROUTE_BARS)
    snares = I.Track(drum_voice(I.snare, seconds=0.2), bpm, gain=0.72, seed=7)
    drums(snares, "....X.......X...", ROUTE_BARS)
    hats = I.Track(drum_voice(I.hat, seconds=0.05), bpm, gain=0.62, humanise=0.004, seed=8)
    drums(hats, "x.o.x.o.x.o.x.ox", ROUTE_BARS)
    shk = I.Track(drum_voice(I.shaker, seconds=0.08), bpm, gain=0.55, humanise=0.006, seed=9)
    drums(shk, "..x...x...x...x.", ROUTE_BARS)
    tamb = I.Track(drum_voice(I.tambourine, seconds=0.26), bpm, gain=0.5, seed=10)
    drums(tamb, "............X...", ROUTE_BARS)

    tail = 5.0
    loop_seconds = ROUTE_BEATS * 60.0 / bpm
    parts = [
        (lead.render(ROUTE_BEATS, tail), 0.12),
        (doubler.render(ROUTE_BEATS, tail), -0.34),
        (arp.render(ROUTE_BEATS, tail), 0.42),
        (pads.render(ROUTE_BEATS, tail), -0.15),
        (low.render(ROUTE_BEATS, tail), 0.0),
        (kicks.render(ROUTE_BEATS, tail), 0.0),
        (snares.render(ROUTE_BEATS, tail), -0.06),
        (hats.render(ROUTE_BEATS, tail), 0.3),
        (shk.render(ROUTE_BEATS, tail), -0.45),
        (tamb.render(ROUTE_BEATS, tail), 0.5),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.16), loop_seconds


def route_night():
    """
    Same eight-bar harmony as the route, taken down to 112 BPM and re-scored for
    flute, harp and pad. Drums drop out except a soft shaker so the night version
    can crossfade over the day version without a rhythmic collision.
    """
    bpm = 112.0
    ir = dsp.make_ir(2.4, decay=3.6, damp_hz=4200, seed=11)

    # add-9 and sus colours for the cooler night harmony
    chords = [
        ch("D3", "add9"), ch("A3", "sus2"), ch("B3", "min7"), ch("G3", "maj7"),
        ch("D3", "add9"), ch("G3", "maj7"), ch("A3", "sus4"), ch("D3", "maj9"),
    ]

    tune = I.Track(I.flute, bpm, gain=1.0, humanise=0.012, seed=21)
    # the route melody, thinned to its long notes so it reads as a memory of the day theme
    tune.extend([
        (0.0, "A4", 2.0, 0.8), (2.0, "F#5", 2.0, 0.85),
        (6.0, "A4", 2.0, 0.7),
        (8.0, "B4", 1.5, 0.75), (10.0, "F#5", 2.0, 0.8),
        (14.0, "F#5", 2.0, 0.7),
        (16.0, "F#5", 1.5, 0.72), (18.0, "D5", 2.0, 0.78),
        (22.0, "E5", 1.0, 0.6), (23.0, "D5", 1.0, 0.6),
        (26.0, "A5", 2.0, 0.8),
        (30.0, "D5", 2.5, 0.72),
    ])

    hp = I.Track(I.harp, bpm, gain=0.7, humanise=0.014, seed=22)
    arpeggiate(hp, chords, step=0.5, shape=(0, 2, 1, 3, 2, 1), vel=0.5, octave_lift=1)

    pads = I.Track(I.pad, bpm, gain=0.85, seed=23)
    chord_pad(pads, [[p - 12 for p in c] for c in chords], vel=0.7, hold=1.0)

    ch_track = I.Track(I.choir, bpm, gain=0.6, seed=24)
    chord_pad(ch_track, [[c[0], c[2]] for c in chords], vel=0.55, hold=1.0)

    low = I.Track(I.sub_bass, bpm, gain=0.9, seed=25)
    for bar, root in enumerate(ROUTE_BASS):
        low.add(bar * 4, root, 3.6, 0.85)

    shk = I.Track(drum_voice(I.shaker, seconds=0.1), bpm, gain=0.3, humanise=0.01, seed=26)
    drums(shk, "..s...s...s...s.", 8)

    beats = 32
    tail = 7.0
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (tune.render(beats, tail), 0.05),
        (hp.render(beats, tail), 0.4),
        (pads.render(beats, tail), -0.2),
        (ch_track.render(beats, tail), 0.25),
        (low.render(beats, tail), 0.0),
        (shk.render(beats, tail), -0.5),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.3, width=1.4), loop_seconds


# ======================================================================================
# TOWN -- F major, warm and relaxed
# ======================================================================================


TOWN_CHORDS = [
    ch("F3", "maj7"), ch("D3", "min7"), ch("Bb2", "maj7"), ch("C3", "dom7"),
    ch("F3", "maj7"), ch("A2", "min7"), ch("G2", "min7"), ch("C3", "dom7"),
]
TOWN_BASS = [dsp.n("F2"), dsp.n("D2"), dsp.n("Bb1"), dsp.n("C2"),
             dsp.n("F2"), dsp.n("A1"), dsp.n("G1"), dsp.n("C2")]

TOWN_MELODY = [
    (0.0, "C5", 1.5), (1.5, "D5", 0.5), (2.0, "F5", 2.0),
    (4.0, "E5", 1.0), (5.0, "D5", 1.0), (6.0, "C5", 2.0),
    (8.0, "D5", 1.5), (9.5, "F5", 0.5), (10.0, "G5", 2.0),
    (12.0, "E5", 1.0), (13.0, "G5", 1.0), (14.0, "A5", 2.0),
    (16.0, "F5", 1.5), (17.5, "E5", 0.5), (18.0, "C5", 2.0),
    (20.0, "E5", 1.0), (21.0, "C5", 1.0), (22.0, "A4", 2.0),
    (24.0, "Bb4", 1.0), (25.0, "D5", 1.0), (26.0, "C5", 2.0),
    (28.0, "G4", 1.0), (29.0, "Bb4", 1.0), (30.0, "A4", 2.0),
]


def town_theme():
    bpm = 100.0
    ir = dsp.make_ir(1.9, decay=4.2, damp_hz=5200, seed=31)

    tune = I.Track(I.flute, bpm, gain=0.95, humanise=0.01, seed=31)
    tune.extend([(b, p, d, 0.85) for b, p, d in TOWN_MELODY])

    org = I.Track(I.organ, bpm, gain=0.9, seed=32)
    chord_pad(org, [[p - 12 for p in c] for c in TOWN_CHORDS], vel=0.6, hold=0.92)

    plk = I.Track(I.pluck, bpm, gain=0.75, humanise=0.012, seed=33)
    arpeggiate(plk, TOWN_CHORDS, step=0.5, shape=(0, 2, 1, 3), vel=0.55)

    mar = I.Track(I.marimba, bpm, gain=0.5, humanise=0.01, seed=34)
    mar.extend([(b + 0.25, p, 0.5, 0.45) for b, p, d in TOWN_MELODY[::2]])

    low = I.Track(I.bass, bpm, gain=0.85, humanise=0.005, seed=35)
    for bar, root in enumerate(TOWN_BASS):
        b = bar * 4
        low.add(b + 0.0, root, 1.0, 0.9)
        low.add(b + 1.5, root + 12, 0.5, 0.55)
        low.add(b + 2.0, root + 7, 1.0, 0.75)
        low.add(b + 3.5, root + 10, 0.5, 0.5)

    # brushed, quiet kit: rim instead of snare, no hats
    rim = I.Track(drum_voice(I.rimshot, seconds=0.09), bpm, gain=0.55, humanise=0.006, seed=36)
    drums(rim, "....x.......x..o", 8)
    kicks = I.Track(drum_voice(I.kick, seconds=0.3), bpm, gain=0.55, seed=37)
    drums(kicks, "x.......o.......", 8)
    shk = I.Track(drum_voice(I.shaker, seconds=0.09), bpm, gain=0.45, humanise=0.008, seed=38)
    drums(shk, "..o...o...o...o.", 8)

    beats, tail = 32, 5.5
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (tune.render(beats, tail), 0.08),
        (org.render(beats, tail), -0.22),
        (plk.render(beats, tail), 0.38),
        (mar.render(beats, tail), -0.42),
        (low.render(beats, tail), 0.0),
        (rim.render(beats, tail), 0.2),
        (kicks.render(beats, tail), 0.0),
        (shk.render(beats, tail), -0.35),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.2), loop_seconds


def town_night():
    """Town at 84 BPM: organ becomes pad, melody moves to bell and harp, kit drops out."""
    bpm = 84.0
    ir = dsp.make_ir(2.8, decay=3.2, damp_hz=3800, seed=41)

    chords = [
        ch("F3", "maj9"), ch("D3", "min9"), ch("Bb2", "maj7"), ch("C3", "sus4"),
        ch("F3", "maj9"), ch("A2", "min7"), ch("G2", "min9"), ch("C3", "dom7"),
    ]

    bl = I.Track(I.bell, bpm, gain=0.62, humanise=0.015, seed=41)
    bl.extend([(b, p, min(d * 2.2, 3.0), 0.55) for b, p, d in TOWN_MELODY[::2]])

    hp = I.Track(I.harp, bpm, gain=0.6, humanise=0.016, seed=42)
    arpeggiate(hp, chords, step=0.75, shape=(0, 2, 3, 1), vel=0.45, octave_lift=1)

    pads = I.Track(I.pad, bpm, gain=0.95, seed=43)
    chord_pad(pads, [[p - 12 for p in c] for c in chords], vel=0.72, hold=1.0)

    ch_track = I.Track(I.choir, bpm, gain=0.55, seed=44)
    chord_pad(ch_track, [[c[0] + 12, c[2] + 12] for c in chords], vel=0.5, hold=1.0)

    low = I.Track(I.sub_bass, bpm, gain=0.85, seed=45)
    for bar, root in enumerate(TOWN_BASS):
        low.add(bar * 4, root, 3.7, 0.8)

    beats, tail = 32, 8.0
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (bl.render(beats, tail), 0.15),
        (hp.render(beats, tail), -0.35),
        (pads.render(beats, tail), 0.0),
        (ch_track.render(beats, tail), 0.3),
        (low.render(beats, tail), 0.0),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.34, width=1.45), loop_seconds


# ======================================================================================
# CAVE -- A minor, sparse, low, very reverberant
# ======================================================================================


def cave_theme():
    """
    Deliberately almost empty. Two-bar gaps, a drone that never resolves, and a
    long dark IR. The interest comes from the space between events, not from density.
    """
    bpm = 84.0
    ir = dsp.make_ir(4.0, decay=2.1, predelay_ms=38.0, damp_hz=2400, early=14,
                     diffusion=1.0, seed=51)

    chords = [
        ch("A2", "min"), ch("A2", "min"), ch("F2", "maj7"), ch("F2", "maj7"),
        ch("D2", "min"), ch("D2", "min"), ch("E2", "min7"), ch("E2", "min7"),
    ]

    drone = I.Track(I.pad, bpm, gain=1.0, seed=51)
    for bar, c in enumerate(chords):
        drone.add(bar * 4, [c[0] - 12, c[1] - 12, c[2] - 12], 4.2, 0.62, cutoff=900.0)

    # a five-note motif answered two bars later a fourth lower -- reads as an echo of itself
    motif = I.Track(I.marimba, bpm, gain=0.85, humanise=0.02, seed=52)
    motif.extend([
        (0.0, "A4", 1.0, 0.7), (1.5, "C5", 1.0, 0.55), (3.0, "E5", 1.5, 0.65),
        (8.0, "E4", 1.0, 0.6), (9.5, "G4", 1.0, 0.45), (11.0, "B4", 1.5, 0.55),
        (16.0, "D4", 1.0, 0.55), (18.0, "F4", 2.0, 0.5),
        (24.0, "E4", 1.0, 0.6), (25.5, "B4", 1.0, 0.5), (27.0, "E5", 2.0, 0.6),
    ])

    glass = I.Track(I.bell, bpm, gain=0.45, humanise=0.03, seed=53)
    glass.extend([
        (5.0, "A5", 3.0, 0.35), (13.0, "C6", 3.0, 0.3),
        (21.0, "D5", 3.0, 0.32), (29.0, "E5", 3.0, 0.35),
    ])

    voices = I.Track(I.choir, bpm, gain=0.5, seed=54)
    voices.add(0.0, [dsp.n("A3"), dsp.n("E4")], 16.0, 0.4)
    voices.add(16.0, [dsp.n("D3"), dsp.n("A3")], 16.0, 0.38)

    thud = I.Track(drum_voice(I.timpani, seconds=1.4, midi=33.0), bpm, gain=0.7, seed=55)
    for bar in (0, 4):
        thud.add(bar * 4, 60, 1.0, 0.55)
    thud.add(16 * 1.0, 60, 1.0, 0.45)
    thud.add(28.0, 60, 1.0, 0.5)

    low = I.Track(I.sub_bass, bpm, gain=0.8, seed=56)
    for bar, c in enumerate(chords):
        if bar % 2 == 0:
            low.add(bar * 4, c[0] - 24, 7.6, 0.7)

    beats, tail = 32, 10.0
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (drone.render(beats, tail), 0.0),
        (motif.render(beats, tail), -0.3),
        (glass.render(beats, tail), 0.45),
        (voices.render(beats, tail), 0.15),
        (thud.render(beats, tail), 0.0),
        (low.render(beats, tail), 0.0),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.52, width=1.5, peak=0.84), loop_seconds


# ======================================================================================
# LAKESIDE -- G lydian, flowing
# ======================================================================================


def lakeside_theme():
    bpm = 104.0
    ir = dsp.make_ir(2.6, decay=3.4, damp_hz=6000, seed=61)

    # lydian: the C# in bar 2 is the whole character of this cue
    chords = [
        ch("G3", "maj9"), ch("A3", "maj"), ch("E3", "min7"), ch("C3", "maj7"),
        ch("G3", "maj9"), ch("D3", "sus2"), ch("E3", "min9"), ch("C3", "maj7"),
    ]
    basses = [dsp.n("G1"), dsp.n("A1"), dsp.n("E1"), dsp.n("C2"),
              dsp.n("G1"), dsp.n("D2"), dsp.n("E1"), dsp.n("C2")]

    tune = I.Track(I.flute, bpm, gain=0.95, humanise=0.012, seed=61)
    tune.extend([
        (0.0, "D5", 1.5), (1.5, "B4", 0.5), (2.0, "G5", 2.0, 0.9),
        (4.0, "F#5", 1.0), (5.0, "E5", 1.0), (6.0, "C#5", 2.0, 0.85),
        (8.0, "B4", 1.5), (9.5, "D5", 0.5), (10.0, "E5", 2.0),
        (12.0, "G5", 1.0), (13.0, "E5", 1.0), (14.0, "D5", 2.0),
        (16.0, "B5", 1.5, 0.95), (17.5, "A5", 0.5), (18.0, "G5", 2.0),
        (20.0, "F#5", 1.0), (21.0, "E5", 1.0), (22.0, "D5", 2.0),
        (24.0, "E5", 1.0), (25.0, "G5", 1.0), (26.0, "B5", 2.0, 0.9),
        (28.0, "A5", 1.5), (29.5, "F#5", 0.5), (30.0, "G5", 2.0),
    ])

    hp = I.Track(I.harp, bpm, gain=0.85, humanise=0.012, seed=62)
    arpeggiate(hp, chords, step=0.25, shape=(0, 1, 2, 3, 2, 1), vel=0.4, octave_lift=0)

    pads = I.Track(I.pad, bpm, gain=0.75, seed=63)
    chord_pad(pads, [[p - 12 for p in c] for c in chords], vel=0.6, hold=1.0)

    strings_t = I.Track(I.strings, bpm, gain=0.5, seed=64)
    chord_pad(strings_t, [[c[1], c[2]] for c in chords], vel=0.45, hold=0.95)

    low = I.Track(I.bass, bpm, gain=0.72, seed=65)
    for bar, root in enumerate(basses):
        low.add(bar * 4, root, 1.5, 0.8)
        low.add(bar * 4 + 2.0, root + 7, 1.5, 0.6)

    shk = I.Track(drum_voice(I.shaker, seconds=0.1), bpm, gain=0.4, humanise=0.01, seed=66)
    drums(shk, "o..o..o...o.o...", 8)

    beats, tail = 32, 7.0
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (tune.render(beats, tail), 0.1),
        (hp.render(beats, tail), -0.4),
        (pads.render(beats, tail), 0.2),
        (strings_t.render(beats, tail), -0.15),
        (low.render(beats, tail), 0.0),
        (shk.render(beats, tail), 0.45),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.3, width=1.4), loop_seconds


# ======================================================================================
# WILD BATTLE -- E minor, 168 BPM, driving
# ======================================================================================


def wild_battle():
    bpm = 168.0
    ir = dsp.make_ir(1.1, decay=6.5, damp_hz=6800, seed=71)

    chords = [ch("E3", "min"), ch("C3"), ch("G3"), ch("D3"),
              ch("E3", "min"), ch("C3"), ch("B2", "dom7"), ch("B2", "dom7")]
    roots = [dsp.n("E1"), dsp.n("C1"), dsp.n("G1"), dsp.n("D1"),
             dsp.n("E1"), dsp.n("C1"), dsp.n("B0"), dsp.n("B0")]

    # urgent sixteenth-driven tune, mostly stepwise so it stays playable at tempo
    tune = I.Track(I.lead_square, bpm, gain=1.0, humanise=0.004, seed=71)
    tune.extend([
        (0.0, "E5", 0.5), (0.5, "G5", 0.5), (1.0, "B5", 1.0), (2.0, "A5", 0.5),
        (2.5, "G5", 0.5), (3.0, "F#5", 1.0),
        (4.0, "E5", 0.5), (4.5, "G5", 0.5), (5.0, "C6", 1.0), (6.0, "B5", 1.0),
        (7.0, "G5", 1.0),
        (8.0, "D5", 0.5), (8.5, "G5", 0.5), (9.0, "B5", 1.0), (10.0, "A5", 0.5),
        (10.5, "G5", 0.5), (11.0, "D5", 1.0),
        (12.0, "F#5", 0.5), (12.5, "A5", 0.5), (13.0, "D6", 1.5), (14.5, "C6", 0.5),
        (15.0, "B5", 1.0),
        (16.0, "E5", 0.5), (16.5, "G5", 0.5), (17.0, "B5", 1.0), (18.0, "E6", 2.0),
        (20.0, "C6", 0.5), (20.5, "B5", 0.5), (21.0, "G5", 1.0), (22.0, "E5", 2.0),
        (24.0, "F#5", 0.5), (24.5, "A5", 0.5), (25.0, "D#6", 1.0), (26.0, "F#6", 1.0),
        (27.0, "D#6", 1.0),
        (28.0, "B5", 0.5), (28.5, "A5", 0.5), (29.0, "F#5", 1.0), (30.0, "B5", 2.0),
    ])

    stabs = I.Track(I.brass, bpm, gain=0.7, seed=72)
    for bar, c in enumerate(chords):
        b = bar * 4
        stabs.add(b + 0.0, c, 0.4, 0.85)
        stabs.add(b + 1.5, c, 0.3, 0.6)
        stabs.add(b + 3.0, c, 0.4, 0.7)

    strings_t = I.Track(I.strings, bpm, gain=0.42, seed=73)
    chord_pad(strings_t, [[p + 12 for p in c] for c in chords], vel=0.45, hold=0.98)

    low = I.Track(I.bass, bpm, gain=1.05, humanise=0.003, seed=74)
    for bar, root in enumerate(roots):
        b = bar * 4
        for k in range(8):
            pitchd = root + (12 if k == 7 else 0)
            low.add(b + k * 0.5, pitchd, 0.45, 0.95 if k % 2 == 0 else 0.7)

    kicks = I.Track(drum_voice(I.kick, seconds=0.3), bpm, gain=1.0, seed=75)
    drums(kicks, "X..x..X...X.x...", 8)
    snares = I.Track(drum_voice(I.snare, seconds=0.18), bpm, gain=0.85, seed=76)
    drums(snares, "....X..o....X..x", 8)
    hats = I.Track(drum_voice(I.hat, seconds=0.045), bpm, gain=0.6, humanise=0.003, seed=77)
    drums(hats, "x.x.x.x.x.x.x.x.", 8)
    toms = I.Track(drum_voice(I.tom, seconds=0.26, tune=150.0), bpm, gain=0.6, seed=78)
    drums(toms, "..............xx", 8)
    cr = I.Track(drum_voice(I.crash, seconds=1.5), bpm, gain=0.8, seed=79)
    cr.add(0.0, 60, 1.0, 0.85)
    cr.add(16.0, 60, 1.0, 0.6)

    beats, tail = 32, 4.0
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (tune.render(beats, tail), 0.05),
        (stabs.render(beats, tail), -0.25),
        (strings_t.render(beats, tail), 0.3),
        (low.render(beats, tail), 0.0),
        (kicks.render(beats, tail), 0.0),
        (snares.render(beats, tail), -0.05),
        (hats.render(beats, tail), 0.35),
        (toms.render(beats, tail), -0.4),
        (cr.render(beats, tail), 0.15),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.13, width=1.2, peak=0.88), loop_seconds


# ======================================================================================
# TRAINER BATTLE -- A minor, 172 BPM, bigger and more heroic
# ======================================================================================


def trainer_battle():
    bpm = 172.0
    ir = dsp.make_ir(1.6, decay=4.8, damp_hz=6200, seed=81)

    chords = [ch("A3", "min"), ch("F3"), ch("C3"), ch("G3"),
              ch("A3", "min"), ch("F3"), ch("D3", "min"), ch("E3", "dom7")]
    roots = [dsp.n("A1"), dsp.n("F1"), dsp.n("C1"), dsp.n("G1"),
             dsp.n("A1"), dsp.n("F1"), dsp.n("D1"), dsp.n("E1")]

    # brass carries the tune here rather than the square lead: bigger, more formal
    fan = I.Track(I.brass, bpm, gain=1.0, humanise=0.004, seed=81)
    fan.extend([
        (0.0, "A4", 0.5), (0.5, "C5", 0.5), (1.0, "E5", 1.0), (2.0, "A5", 2.0),
        (4.0, "G5", 0.5), (4.5, "F5", 0.5), (5.0, "C5", 1.0), (6.0, "F5", 2.0),
        (8.0, "E5", 0.5), (8.5, "G5", 0.5), (9.0, "C6", 1.5), (10.5, "B5", 0.5),
        (11.0, "G5", 1.0),
        (12.0, "D5", 0.5), (12.5, "G5", 0.5), (13.0, "B5", 1.0), (14.0, "D6", 2.0),
        (16.0, "A5", 1.0), (17.0, "E5", 0.5), (17.5, "C5", 0.5), (18.0, "A4", 2.0),
        (20.0, "F5", 1.0), (21.0, "A5", 1.0), (22.0, "C6", 2.0),
        (24.0, "D6", 1.0), (25.0, "A5", 1.0), (26.0, "F5", 2.0),
        (28.0, "E5", 0.5), (28.5, "G#5", 0.5), (29.0, "B5", 1.0), (30.0, "E6", 2.0),
    ])

    counter = I.Track(I.lead_square, bpm, gain=0.55, seed=82)
    counter.extend([
        (2.0, "E5", 0.5, 0.5), (2.5, "C5", 0.5, 0.5), (3.0, "A4", 1.0, 0.5),
        (6.0, "A4", 0.5, 0.5), (6.5, "C5", 0.5, 0.5), (7.0, "F5", 1.0, 0.5),
        (14.0, "B4", 0.5, 0.5), (14.5, "D5", 0.5, 0.5), (15.0, "G5", 1.0, 0.5),
        (18.0, "C5", 0.5, 0.5), (18.5, "E5", 0.5, 0.5), (19.0, "A5", 1.0, 0.5),
        (26.0, "F5", 0.5, 0.5), (26.5, "A5", 0.5, 0.5), (27.0, "C6", 1.0, 0.5),
        (30.0, "B5", 1.0, 0.55), (31.0, "E6", 1.0, 0.6),
    ])

    strings_t = I.Track(I.strings, bpm, gain=0.62, seed=83)
    for bar, c in enumerate(chords):
        b = bar * 4
        for k in range(8):  # driving ostinato quavers, not held pads
            strings_t.add(b + k * 0.5, [c[0], c[1]], 0.45, 0.5 if k % 2 else 0.65)

    org = I.Track(I.choir, bpm, gain=0.45, seed=84)
    chord_pad(org, [[p + 12 for p in c] for c in chords], vel=0.4, hold=1.0)

    low = I.Track(I.bass, bpm, gain=1.05, humanise=0.003, seed=85)
    for bar, root in enumerate(roots):
        b = bar * 4
        pattern = [0.0, 0.5, 1.0, 1.75, 2.0, 2.5, 3.0, 3.5]
        for k, off in enumerate(pattern):
            low.add(b + off, root + (7 if k == 3 else 0), 0.42, 1.0 if k % 2 == 0 else 0.72)

    kicks = I.Track(drum_voice(I.kick, seconds=0.32), bpm, gain=1.0, seed=86)
    drums(kicks, "X..x..X.X..x..x.", 8)
    snares = I.Track(drum_voice(I.snare, seconds=0.2), bpm, gain=0.9, seed=87)
    drums(snares, "....X..x....X.ox", 8)
    hats = I.Track(drum_voice(I.hat, seconds=0.04), bpm, gain=0.5, humanise=0.003, seed=88)
    drums(hats, "xoxoxoxoxoxoxoxo", 8)
    tim = I.Track(drum_voice(I.timpani, seconds=0.9, midi=45.0), bpm, gain=0.75, seed=89)
    drums(tim, "X.......x.......", 8)
    cr = I.Track(drum_voice(I.crash, seconds=1.8), bpm, gain=0.9, seed=90)
    cr.add(0.0, 60, 1.0, 0.9)
    cr.add(16.0, 60, 1.0, 0.7)
    cr.add(30.0, 60, 1.0, 0.55)

    beats, tail = 32, 4.5
    loop_seconds = beats * 60.0 / bpm
    parts = [
        (fan.render(beats, tail), -0.1),
        (counter.render(beats, tail), 0.35),
        (strings_t.render(beats, tail), 0.25),
        (org.render(beats, tail), -0.3),
        (low.render(beats, tail), 0.0),
        (kicks.render(beats, tail), 0.0),
        (snares.render(beats, tail), -0.05),
        (hats.render(beats, tail), 0.4),
        (tim.render(beats, tail), -0.2),
        (cr.render(beats, tail), 0.1),
    ]
    return finish(parts, loop_seconds, ir, rev_mix=0.16, width=1.25, peak=0.88), loop_seconds


# ======================================================================================
# ONE-SHOT STINGS
# ======================================================================================


def victory_fanfare():
    """
    C major. Triplet pickup, a rising three-chord lift, then a held tonic with a
    6/9 colour so it feels celebratory rather than merely final.
    """
    bpm = 150.0
    ir = dsp.make_ir(2.6, decay=3.2, damp_hz=6500, seed=101)

    fan = I.Track(I.brass, bpm, gain=1.0, seed=101)
    pickup = [(0.0, "G4", 0.33), (0.33, "C5", 0.33), (0.66, "E5", 0.34)]
    fan.extend([(b, p, d, 0.9) for b, p, d in pickup])
    fan.extend([
        (1.0, "G5", 0.5, 1.0), (1.5, "E5", 0.5, 0.85), (2.0, "G5", 0.5, 0.95),
        (2.5, "C6", 1.5, 1.0),
        (4.0, "B5", 0.5, 0.85), (4.5, "C6", 0.5, 0.9), (5.0, "D6", 0.5, 0.9),
        (5.5, "E6", 2.5, 1.0),
    ])

    harmony = I.Track(I.brass, bpm, gain=0.6, seed=102)
    harmony.add(1.0, ch("C4"), 1.5, 0.7)
    harmony.add(2.5, ch("G3"), 1.5, 0.7)
    harmony.add(4.0, ch("F3", "maj7"), 1.0, 0.7)
    harmony.add(5.0, ch("G3", "dom7"), 0.5, 0.75)
    harmony.add(5.5, [dsp.n("C4"), dsp.n("E4"), dsp.n("G4"), dsp.n("A4"), dsp.n("D5")], 2.5, 0.8)

    strings_t = I.Track(I.strings, bpm, gain=0.6, seed=103)
    strings_t.add(1.0, ch("C4", "maj", 1), 4.5, 0.55)
    strings_t.add(5.5, [dsp.n("C5"), dsp.n("E5"), dsp.n("G5"), dsp.n("A5")], 2.5, 0.6)

    low = I.Track(I.bass, bpm, gain=0.95, seed=104)
    for b, p in [(1.0, "C2"), (2.5, "G1"), (4.0, "F1"), (5.0, "G1"), (5.5, "C2")]:
        low.add(b, p, 1.2, 0.9)

    tim = I.Track(drum_voice(I.timpani, seconds=1.0, midi=36.0), bpm, gain=0.9, seed=105)
    for b, v in [(0.0, 0.5), (0.66, 0.6), (1.0, 0.95), (2.5, 0.8), (4.0, 0.7),
                 (5.0, 0.7), (5.5, 1.0)]:
        tim.add(b, 60, 0.5, v)

    cr = I.Track(drum_voice(I.crash, seconds=2.6), bpm, gain=1.0, seed=106)
    cr.add(1.0, 60, 1.0, 0.9)
    cr.add(5.5, 60, 1.0, 1.0)

    beats, tail = 8.0, 3.5
    parts = [
        (fan.render(beats, tail), 0.0),
        (harmony.render(beats, tail), -0.25),
        (strings_t.render(beats, tail), 0.3),
        (low.render(beats, tail), 0.0),
        (tim.render(beats, tail), -0.15),
        (cr.render(beats, tail), 0.2),
    ]
    sig = finish(parts, 0, ir, rev_mix=0.3, width=1.3, loop=False)
    return dsp.fade_out(dsp.zero_edges(sig), 0.5), sig.shape[0] / dsp.SR


def capture_success_sting():
    """
    The reward for the whole capture set piece. Bell arpeggio rising through a
    IV-V-I in G, landing on a bright add9. Short, warm, unmistakably positive.
    """
    bpm = 132.0
    ir = dsp.make_ir(2.8, decay=2.8, damp_hz=7000, seed=111)

    bl = I.Track(I.bell, bpm, gain=1.0, seed=111)
    for i, p in enumerate(["G4", "B4", "D5", "G5"]):
        bl.add(i * 0.25, p, 1.5, 0.6 + i * 0.1)
    bl.add(1.25, "A5", 1.2, 0.85)
    bl.add(1.75, "B5", 2.4, 1.0)
    bl.add(2.5, "D6", 2.2, 0.8)

    hp = I.Track(I.harp, bpm, gain=0.85, seed=112)
    for i, p in enumerate(["D4", "G4", "B4", "D5", "G5", "B5", "D6", "G6"]):
        hp.add(1.75 + i * 0.09, p, 1.6, 0.5)

    pads = I.Track(I.pad, bpm, gain=0.85, seed=113)
    pads.add(0.0, ch("C3", "maj7"), 1.0, 0.6)
    pads.add(1.0, ch("D3", "dom7"), 0.75, 0.65)
    pads.add(1.75, [dsp.n("G2"), dsp.n("B2"), dsp.n("D3"), dsp.n("A3")], 3.0, 0.75)

    mar = I.Track(I.marimba, bpm, gain=0.6, seed=114)
    for i, p in enumerate(["G5", "B5", "D6"]):
        mar.add(1.75 + i * 0.16, p, 0.6, 0.55)

    low = I.Track(I.sub_bass, bpm, gain=0.8, seed=115)
    low.add(0.0, dsp.n("C2"), 1.0, 0.7)
    low.add(1.0, dsp.n("D2"), 0.75, 0.7)
    low.add(1.75, dsp.n("G1"), 3.0, 0.85)

    beats, tail = 5.0, 2.5
    parts = [
        (bl.render(beats, tail), 0.1),
        (hp.render(beats, tail), -0.3),
        (pads.render(beats, tail), 0.0),
        (mar.render(beats, tail), 0.35),
        (low.render(beats, tail), 0.0),
    ]
    sig = finish(parts, 0, ir, rev_mix=0.34, width=1.35, loop=False)
    return dsp.fade_out(dsp.zero_edges(sig), 0.4), sig.shape[0] / dsp.SR


def encounter_sting():
    """
    The bridge out of exploration and into battle.

    It opens by quoting the route theme's own D-major cadence on the same marimba
    the route uses, then a chromatic bass climb and an accelerating snare roll pull
    the harmony off its axis, and it lands on a B-flat/E tritone stab whose top note
    is the E that the wild battle theme opens on. That shared pitch is what lets the
    music director hard-hand-off into the battle loop without a seam.
    """
    bpm = 132.0
    ir = dsp.make_ir(1.8, decay=4.0, damp_hz=5200, seed=121)

    quote = I.Track(I.marimba, bpm, gain=0.9, seed=121)
    quote.extend([(0.0, "F#5", 0.5, 0.7), (0.5, "E5", 0.5, 0.65), (1.0, "D5", 0.5, 0.6)])

    climb = I.Track(I.bass, bpm, gain=1.0, seed=122)
    for i, p in enumerate(["D2", "D#2", "E2", "F2", "F#2", "G2"]):
        climb.add(1.5 + i * 0.25, p, 0.3, 0.65 + i * 0.06)
    climb.add(3.0, dsp.n("A1"), 0.5, 0.95)
    climb.add(3.5, dsp.n("A#1"), 1.5, 1.0)

    rise = I.Track(I.strings, bpm, gain=0.75, seed=123)
    rise.add(1.5, [dsp.n("A3"), dsp.n("D4")], 2.0, 0.5)
    rise.add(3.5, [dsp.n("A#3"), dsp.n("E4"), dsp.n("A#4")], 1.8, 0.85)

    stab = I.Track(I.brass, bpm, gain=1.0, seed=124)
    stab.add(3.5, [dsp.n("A#3"), dsp.n("E4"), dsp.n("A#4"), dsp.n("E5")], 1.4, 1.0)

    # accelerating snare roll: intervals shrink from a quaver to a semiquaver triplet
    roll = I.Track(drum_voice(I.snare, seconds=0.16), bpm, gain=0.8, seed=125)
    t = 1.5
    gap = 0.25
    v = 0.35
    while t < 3.5:
        roll.add(t, 60, 0.1, min(1.0, v))
        t += gap
        gap = max(0.0625, gap * 0.86)
        v += 0.05

    hit = I.Track(drum_voice(I.kick, seconds=0.5, tune=44.0), bpm, gain=1.0, seed=126)
    hit.add(3.5, 60, 0.5, 1.0)
    cr = I.Track(drum_voice(I.crash, seconds=2.2), bpm, gain=0.9, seed=127)
    cr.add(3.5, 60, 1.0, 0.95)

    beats, tail = 5.5, 2.0
    parts = [
        (quote.render(beats, tail), -0.2),
        (climb.render(beats, tail), 0.0),
        (rise.render(beats, tail), 0.25),
        (stab.render(beats, tail), -0.1),
        (roll.render(beats, tail), 0.15),
        (hit.render(beats, tail), 0.0),
        (cr.render(beats, tail), 0.0),
    ]
    sig = finish(parts, 0, ir, rev_mix=0.22, width=1.2, loop=False)
    return dsp.fade_out(dsp.zero_edges(sig), 0.35), sig.shape[0] / dsp.SR


# --------------------------------------------------------------------------------------


TRACKS = {
    "Music_Route_Day": (route_theme, True, "Overworld route, daytime. MusicDirector default for biome 'route'."),
    "Music_Route_Night": (route_night, True, "Overworld route at night; crossfade target on TimeOfDayChanged."),
    "Music_Town_Day": (town_theme, True, "Town biome, daytime."),
    "Music_Town_Night": (town_night, True, "Town biome at night."),
    "Music_Cave": (cave_theme, True, "Cave biome, any time of day."),
    "Music_Lakeside": (lakeside_theme, True, "Lakeside / water biome."),
    "Music_Battle_Wild": (wild_battle, True, "Wild encounter battle loop; BattleStartedEvent with Kind=Wild."),
    "Music_Battle_Trainer": (trainer_battle, True, "Trainer battle loop; BattleStartedEvent with Kind=Trainer."),
    "Music_Victory_Fanfare": (victory_fanfare, False, "BattleEndedEvent with Outcome=PlayerVictory. Ducks music."),
    "Music_Capture_Success": (capture_success_sting, False, "CaptureAttemptEvent with Succeeded=true, after the click."),
    "Music_Encounter_Sting": (encounter_sting, False, "GameMode -> EncounterIntro. Bridges exploration into battle."),
}
