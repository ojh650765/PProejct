"""
Sound effects.

Structure mirrors the way the sounds are used rather than the way they are built:
moves, battle, capture, overworld, scanner, UI. Shared construction primitives live
at the top -- a whoosh is a swept band-pass over noise, an impact is a transient plus
a tuned body plus a decaying tail, and both appear in a dozen cues with different
tunings. That is deliberate: it is what makes the set sound like one library.

Every generator is deterministic (seeded), so re-running the build reproduces the
byte-identical WAVs and a diff on the audio folder stays meaningful.
"""

from __future__ import annotations

import math

import numpy as np

import dsp
import instruments as I
from dsp import (SR, adsr, apply_env, bandpass, biquad, expo, fit, highpass, lowpass,
                 mix, mono_to_stereo, noise_white, n_samples, note_hz, osc_fm,
                 osc_pulse, osc_saw, osc_sine, osc_tri, perc_env, place, ramp,
                 saturate, silence, sweep_filter, transient_shape, zero_edges)

# --------------------------------------------------------------------------------------
# shared primitives
# --------------------------------------------------------------------------------------


def whoosh(dur: float, f0: float, f1: float, q: float = 1.6, seed: int = 0,
           curve: float = 1.0, colour: str = "white", peak_at: float = 0.55) -> np.ndarray:
    """
    Air movement: noise through a band-pass whose centre sweeps.

    The amplitude envelope peaks part-way through rather than at the start, which is
    what makes it read as something passing by instead of something being hit.
    """
    length = n_samples(dur)
    src = {"white": noise_white, "pink": dsp.noise_pink, "brown": dsp.noise_brown}[colour](
        length=length, seed=seed)
    centre = expo(dur, f0, f1) if curve == 1.0 else dsp.fit_curve(
        f0 + (f1 - f0) * (np.linspace(0, 1, length) ** curve), length)
    swept = sweep_filter(src, centre, q=q, mode="bp")
    x = np.linspace(0.0, 1.0, length)
    env = np.exp(-((x - peak_at) ** 2) / (2 * 0.22**2))
    env *= np.clip(x / 0.04, 0, 1) * np.clip((1 - x) / 0.25, 0, 1)
    return swept * env


def impact(dur: float, body_hz: float = 150.0, bright: float = 3000.0, weight: float = 1.0,
           seed: int = 0, decay: float = 3.0, drive: float = 2.0) -> np.ndarray:
    """Transient + pitched body + broadband tail. The chassis under every hit in the set."""
    length = n_samples(dur)
    # click: a very short high burst gives the ear its arrival cue
    click = bandpass(noise_white(length=length, seed=seed), bright * 0.6, bright * 2.4, 2)
    click *= perc_env(dur, 0.0004, 9.0)
    # body: a pitch-dropping sine is the 'mass' of the hit
    body = osc_sine(expo(dur, body_hz * 2.2, body_hz * 0.8), length=length)
    body *= perc_env(dur, 0.001, decay) * weight
    # tail: low-mid noise, longer
    tail = lowpass(noise_white(length=length, seed=seed + 1), bright * 0.45, 2)
    tail *= perc_env(dur, 0.002, decay * 0.7) * 0.5
    out = saturate(body * 1.2 + click * 0.8 + tail * 0.6, drive, asym=0.08)
    return highpass(out, 32.0, 1)


def crackle(dur: float, density: float = 220.0, low: float = 900.0, high: float = 9000.0,
            seed: int = 0, decay: float = 2.0) -> np.ndarray:
    """Sparse impulsive grains -- fire, electricity, breaking rock."""

    def grain(d, rng):
        m = max(8, n_samples(min(d, 0.02)))
        g = rng.standard_normal(m)
        return bandpass(g, rng.uniform(low, high * 0.5), rng.uniform(high * 0.5, high), 2)

    out = dsp.granular(grain, dur, grain_ms=8.0, density=density, pitch_jitter=0.4,
                       seed=seed, stereo=False)
    return out * perc_env(dur, 0.002, decay)


def riser(dur: float, f0: float, f1: float, kind: str = "noise", seed: int = 0) -> np.ndarray:
    """Rising tension element; used ahead of crits and the encounter transition."""
    length = n_samples(dur)
    if kind == "noise":
        src = noise_white(length=length, seed=seed)
        out = sweep_filter(src, expo(dur, f0, f1), q=6.0, mode="bp")
    else:
        out = osc_saw(expo(dur, f0, f1), length=length)
        out = sweep_filter(out, expo(dur, f0 * 3, f1 * 3), q=2.0, mode="lp")
    return out * ramp(dur, 0.0, 1.0, curve=2.0)


def resonant_body(dur: float, freqs, qs=None, seed: int = 0, decay: float = 3.0,
                  amps=None) -> np.ndarray:
    """Excite a set of narrow resonators with a noise impulse -- struck metal, stone, wood."""
    length = n_samples(dur)
    exc = noise_white(length=length, seed=seed) * perc_env(dur, 0.0003, 40.0)
    qs = qs or [30.0] * len(freqs)
    amps = amps or [1.0] * len(freqs)
    out = np.zeros(length)
    for f, q, a in zip(freqs, qs, amps):
        out += biquad(exc, "bp", f, q) * a
    return out * perc_env(dur, 0.0005, decay)


def bubble(dur: float, f0: float, f1: float, seed: int = 0) -> np.ndarray:
    """A single bubble: a short sine with a rising pitch and a fast bell envelope."""
    length = n_samples(dur)
    tone = osc_sine(expo(dur, f0, f1), length=length)
    return tone * perc_env(dur, 0.002, 5.0)


def ring_mod(sig: np.ndarray, freq: float) -> np.ndarray:
    return sig * osc_sine(freq, length=sig.shape[0])


def finish_sfx(sig: np.ndarray, ir=None, rev: float = 0.12, peak: float = 0.9,
               hp: float = 30.0) -> np.ndarray:
    """One-shot mixdown: DC block, optional space, edge-safe fades, normalise."""
    out = highpass(sig, hp, 2)
    if ir is not None:
        out = dsp.reverb(out, ir if ir.ndim == 1 else ir[:, 0], rev)
    out = dsp.soft_clip(out, 0.97)
    out = zero_edges(out, 48)
    return dsp.normalize(out, peak)


_IR_SMALL = None
_IR_ARENA = None
_IR_CAVE = None


def irs():
    global _IR_SMALL, _IR_ARENA, _IR_CAVE
    if _IR_SMALL is None:
        _IR_SMALL = dsp.make_ir(0.55, decay=7.0, predelay_ms=6, damp_hz=6000, early=6,
                                seed=201, stereo=False)
        _IR_ARENA = dsp.make_ir(1.6, decay=4.0, predelay_ms=16, damp_hz=5200, early=10,
                                seed=202, stereo=False)
        _IR_CAVE = dsp.make_ir(3.2, decay=2.2, predelay_ms=34, damp_hz=2600, early=14,
                               seed=203, stereo=False)
    return _IR_SMALL, _IR_ARENA, _IR_CAVE


# ======================================================================================
# MOVES -- one cast and one impact per elemental type in the slice
# ======================================================================================


def move_normal_cast():
    a = whoosh(0.34, 500, 2600, q=1.3, seed=301)
    b = whoosh(0.28, 1800, 700, q=2.2, seed=302) * 0.5
    return finish_sfx(mix(a, b) * 1.2)


def move_normal_impact():
    hit = impact(0.36, body_hz=170, bright=3200, weight=1.0, seed=303, decay=3.4)
    slap = bandpass(noise_white(seconds=0.09, seed=304), 900, 4200, 2) * perc_env(0.09, 0.0005, 6.0)
    out = mix(hit, fit(slap, hit.shape[0]) * 0.7)
    return finish_sfx(transient_shape(out, 1.2), irs()[0], 0.1)


def move_fire_cast():
    """Granular roar over a rising filtered-noise column, with a low pressure swell."""
    dur = 0.75

    def flame(d, rng):
        m = max(16, n_samples(min(d, 0.09)))
        g = rng.standard_normal(m)
        return lowpass(bandpass(g, rng.uniform(220, 900), rng.uniform(1600, 5200), 2), 6500)

    roar = dsp.granular(flame, dur, grain_ms=55.0, density=130.0, pitch_jitter=0.35,
                        seed=305, stereo=False)
    roar *= ramp(dur, 0.15, 1.0, curve=1.4)
    column = sweep_filter(dsp.noise_pink(seconds=dur, seed=306), expo(dur, 380, 4200), q=2.4, mode="bp")
    column *= ramp(dur, 0.0, 1.0, curve=2.2)
    swell = osc_sine(expo(dur, 55, 92), seconds=dur) * ramp(dur, 0.0, 0.8, 2.0)
    spit = crackle(dur, density=150, low=1400, high=9000, seed=307, decay=0.8) * 0.5
    out = saturate(mix(roar * 1.0, column * 0.7, swell * 0.6, spit), 1.6)
    return finish_sfx(out, irs()[1], 0.14)


def move_fire_impact():
    dur = 0.85
    boom = osc_sine(expo(dur, 130, 42), seconds=dur) * perc_env(dur, 0.002, 2.2) * 1.3
    burst = lowpass(noise_white(seconds=dur, seed=308), 5000, 2) * perc_env(dur, 0.001, 3.4)
    fizz = crackle(dur, density=320, low=1200, high=11000, seed=309, decay=1.4)
    flare = sweep_filter(noise_white(seconds=0.25, seed=310), expo(0.25, 6000, 900), q=2.0, mode="bp")
    flare = fit(flare * perc_env(0.25, 0.001, 4.0), n_samples(dur))
    out = saturate(mix(boom, burst * 0.9, fizz * 0.7, flare * 0.6), 2.4, asym=0.1)
    return finish_sfx(transient_shape(out, 1.0), irs()[1], 0.18)


def move_water_cast():
    dur = 0.7

    def swirl(d, rng):
        m = max(16, n_samples(min(d, 0.08)))
        g = rng.standard_normal(m)
        return bandpass(g, rng.uniform(400, 1500), rng.uniform(2500, 7000), 2)

    body = dsp.granular(swirl, dur, grain_ms=45.0, density=110.0, pitch_jitter=0.3,
                        seed=311, stereo=False)
    body *= ramp(dur, 0.2, 1.0, 1.3)
    surge = sweep_filter(dsp.noise_pink(seconds=dur, seed=312), expo(dur, 300, 3000), q=1.4, mode="bp")
    surge *= ramp(dur, 0.0, 1.0, 1.8)
    bubbles = np.zeros(n_samples(dur))
    rng = np.random.default_rng(313)
    for _ in range(16):
        b = bubble(0.05, rng.uniform(400, 900), rng.uniform(900, 2200), seed=int(rng.integers(1e6)))
        place(bubbles, b * 0.35, rng.uniform(0.05, dur - 0.08))
    return finish_sfx(mix(body, surge * 0.8, bubbles), irs()[0], 0.14)


def move_water_impact():
    dur = 0.7
    splash = highpass(noise_white(seconds=dur, seed=314), 500, 2) * perc_env(dur, 0.0008, 4.2)
    splash = sweep_filter(splash, expo(dur, 7000, 1200), q=0.8, mode="lp")
    slam = osc_sine(expo(dur, 190, 70), seconds=dur) * perc_env(dur, 0.001, 4.0) * 0.9
    drops = np.zeros(n_samples(dur))
    rng = np.random.default_rng(315)
    for _ in range(22):
        d = bubble(0.045, rng.uniform(700, 1600), rng.uniform(1600, 3600), seed=int(rng.integers(1e6)))
        place(drops, d * rng.uniform(0.1, 0.4), rng.uniform(0.03, dur - 0.06))
    out = mix(splash * 1.1, slam, drops)
    return finish_sfx(transient_shape(out, 1.1), irs()[0], 0.16)


def move_electric_cast():
    dur = 0.55
    length = n_samples(dur)
    buzz = osc_pulse(expo(dur, 90, 220), length=length, width=0.3)
    buzz = ring_mod(buzz, 137.0) + ring_mod(buzz, 311.0) * 0.6
    buzz = sweep_filter(buzz, expo(dur, 900, 7000), q=3.0, mode="bp")
    spark = crackle(dur, density=420, low=2500, high=15000, seed=316, decay=0.9)
    charge = osc_sine(expo(dur, 220, 1400), length=length) * ramp(dur, 0.0, 0.4, 3.0)
    out = saturate(mix(buzz * 0.8, spark * 0.9, charge * 0.4), 2.2)
    return finish_sfx(out * ramp(dur, 0.3, 1.0, 1.2), irs()[0], 0.08)


def move_electric_impact():
    dur = 0.55
    length = n_samples(dur)
    zap = osc_saw(expo(dur, 2600, 180), length=length)
    zap = ring_mod(zap, 640.0)
    zap = sweep_filter(zap, expo(dur, 9000, 1200), q=2.2, mode="bp")
    zap *= perc_env(dur, 0.0004, 4.0)
    snap = bandpass(noise_white(seconds=0.05, seed=317), 4000, 16000, 2) * perc_env(0.05, 0.0002, 9.0)
    arc = crackle(dur, density=500, low=3000, high=16000, seed=318, decay=2.6)
    thud = osc_sine(expo(dur, 150, 55), length=length) * perc_env(dur, 0.001, 4.5) * 0.7
    out = saturate(mix(zap, fit(snap, length) * 1.2, arc * 0.8, thud), 2.8)
    return finish_sfx(transient_shape(out, 1.6), irs()[0], 0.1)


def move_grass_cast():
    dur = 0.6

    def leaf(d, rng):
        m = max(12, n_samples(min(d, 0.03)))
        return bandpass(rng.standard_normal(m), rng.uniform(1800, 4000), rng.uniform(4000, 12000), 2)

    rustle = dsp.granular(leaf, dur, grain_ms=22.0, density=300.0, pitch_jitter=0.5,
                          seed=319, stereo=False)
    rustle *= ramp(dur, 0.2, 1.0, 1.5)
    sweep = whoosh(dur, 900, 4500, q=1.8, seed=320) * 0.8
    growth = osc_saw(expo(dur, 120, 300), seconds=dur) * ramp(dur, 0.0, 0.35, 2.5)
    growth = lowpass(growth, 1400, 2)
    return finish_sfx(mix(rustle, sweep, growth), irs()[0], 0.12)


def move_grass_impact():
    dur = 0.45
    whip = whoosh(0.14, 5000, 1400, q=3.5, seed=321, peak_at=0.3) * 1.4
    slice_ = bandpass(noise_white(seconds=0.1, seed=322), 2500, 11000, 2) * perc_env(0.1, 0.0004, 7.0)
    thud = impact(dur, body_hz=140, bright=2200, weight=0.7, seed=323, decay=4.0) * 0.8

    def leaf(d, rng):
        m = max(12, n_samples(min(d, 0.025)))
        return bandpass(rng.standard_normal(m), 2200, 9000, 2)

    debris = dsp.granular(leaf, dur, grain_ms=18.0, density=160.0, seed=324, stereo=False)
    debris *= perc_env(dur, 0.005, 4.0) * 0.5
    out = mix(fit(whip, n_samples(dur)), fit(slice_, n_samples(dur)) * 0.9, thud, debris)
    return finish_sfx(transient_shape(out, 1.2), irs()[0], 0.1)


def move_poison_cast():
    dur = 0.7
    length = n_samples(dur)
    hiss = bandpass(noise_white(length=length, seed=325), 1200, 6000, 2) * ramp(dur, 0.1, 0.9, 1.4)
    gurgle = np.zeros(length)
    rng = np.random.default_rng(326)
    for _ in range(26):
        b = bubble(rng.uniform(0.04, 0.09), rng.uniform(120, 340), rng.uniform(340, 780),
                   seed=int(rng.integers(1e6)))
        place(gurgle, b * rng.uniform(0.3, 0.8), rng.uniform(0.0, dur - 0.1))
    sludge = lowpass(dsp.noise_brown(length=length, seed=327), 700, 2) * ramp(dur, 0.2, 1.0)
    out = mix(hiss * 0.55, gurgle, sludge * 0.8)
    return finish_sfx(saturate(out, 1.5), irs()[0], 0.14)


def move_poison_impact():
    dur = 0.6
    length = n_samples(dur)
    squelch = sweep_filter(noise_white(length=length, seed=328), expo(dur, 2600, 260), q=1.6, mode="lp")
    squelch *= perc_env(dur, 0.002, 3.2)
    drop = osc_sine(expo(dur, 320, 70), length=length) * perc_env(dur, 0.002, 3.6)
    pops = np.zeros(length)
    rng = np.random.default_rng(329)
    for _ in range(14):
        b = bubble(0.06, rng.uniform(200, 500), rng.uniform(90, 220), seed=int(rng.integers(1e6)))
        place(pops, b * rng.uniform(0.2, 0.6), rng.uniform(0.02, dur - 0.08))
    out = saturate(mix(squelch, drop * 1.1, pops * 0.8), 1.8, asym=0.15)
    return finish_sfx(out, irs()[0], 0.12)


def move_ground_cast():
    dur = 0.8
    length = n_samples(dur)
    rumble = lowpass(dsp.noise_brown(length=length, seed=330), 220, 2) * ramp(dur, 0.05, 1.0, 1.6)
    grind = sweep_filter(dsp.noise_pink(length=length, seed=331), expo(dur, 180, 900), q=1.2, mode="bp")
    grind *= ramp(dur, 0.1, 0.8, 1.3)
    shift = osc_sine(expo(dur, 34, 58), length=length) * ramp(dur, 0.0, 1.0, 2.0)
    stones = crackle(dur, density=60, low=300, high=2600, seed=332, decay=0.7) * 0.6
    return finish_sfx(saturate(mix(rumble * 1.2, grind * 0.7, shift, stones), 1.7), irs()[2], 0.12)


def move_ground_impact():
    dur = 1.0
    length = n_samples(dur)
    quake = osc_sine(expo(dur, 78, 30), length=length) * perc_env(dur, 0.003, 1.8) * 1.4
    slam = lowpass(noise_white(length=length, seed=333), 900, 2) * perc_env(dur, 0.001, 2.6)
    debris = crackle(dur, density=180, low=400, high=5200, seed=334, decay=1.2)
    body = resonant_body(dur, [86.0, 143.0, 231.0], [14, 18, 22], seed=335, decay=2.0)
    out = saturate(mix(quake, slam * 0.9, debris * 0.6, body * 0.7), 2.6, asym=0.14)
    return finish_sfx(out, irs()[2], 0.2)


def move_flying_cast():
    dur = 0.65
    a = whoosh(dur, 320, 3400, q=1.1, seed=336, peak_at=0.62)
    b = whoosh(dur * 0.7, 2600, 800, q=2.0, seed=337) * 0.55
    # wing beats: three amplitude pulses in the noise bed
    length = n_samples(dur)
    t = np.linspace(0, 1, length)
    flap = 1.0 + 0.6 * np.sin(2 * np.pi * 4.5 * t) ** 2
    return finish_sfx(mix(a * flap, fit(b, length)) * 1.2, irs()[1], 0.14)


def move_flying_impact():
    dur = 0.4
    gust = whoosh(0.22, 3000, 700, q=1.4, seed=338, peak_at=0.2)
    hit = impact(dur, body_hz=200, bright=3800, weight=0.75, seed=339, decay=4.2)
    feather = bandpass(noise_white(seconds=0.18, seed=340), 3000, 12000, 2)
    feather *= perc_env(0.18, 0.001, 5.0) * 0.45
    length = n_samples(dur)
    out = mix(fit(gust, length), hit, fit(feather, length))
    return finish_sfx(transient_shape(out, 1.3), irs()[1], 0.14)


def move_psychic_cast():
    """Inharmonic FM shimmer with a reverse swell -- deliberately unplaceable in space."""
    dur = 0.9
    length = n_samples(dur)
    idx = ramp(dur, 0.5, 9.0, curve=1.8)
    shimmer = osc_fm(expo(dur, 320, 780), 1.73, idx, length=length, feedback=0.2)
    shimmer *= ramp(dur, 0.0, 1.0, 2.4)
    warp = osc_sine(expo(dur, 90, 420), length=length) * ramp(dur, 0.0, 0.5, 2.0)
    air = sweep_filter(noise_white(length=length, seed=341), expo(dur, 1200, 9000), q=5.0, mode="bp")
    air *= ramp(dur, 0.0, 0.55, 2.6)
    out = mix(shimmer * 0.7, warp, air * 0.5)
    out = dsp.chorus(out, rate_hz=1.4, depth_ms=9.0, mix_amt=0.6)
    return finish_sfx(np.mean(out, axis=1), irs()[1], 0.28)


def move_psychic_impact():
    dur = 0.9
    length = n_samples(dur)
    idx = expo(dur, 11.0, 0.6)
    warp = osc_fm(expo(dur, 900, 180), 2.41, idx, length=length, feedback=0.3)
    warp *= perc_env(dur, 0.001, 2.4)
    sub = osc_sine(expo(dur, 220, 44), length=length) * perc_env(dur, 0.002, 2.8)
    glass = resonant_body(dur, [1870.0, 2940.0, 4610.0], [45, 50, 55], seed=342, decay=1.4)
    out = mix(warp * 1.0, sub * 1.1, glass * 0.6)
    return finish_sfx(out, irs()[1], 0.3)


def move_rock_cast():
    dur = 0.6
    length = n_samples(dur)
    grind = sweep_filter(dsp.noise_brown(length=length, seed=343), expo(dur, 140, 700), q=1.0, mode="bp")
    grind *= ramp(dur, 0.15, 1.0, 1.4)
    scrape = crackle(dur, density=90, low=500, high=4000, seed=344, decay=0.6)
    lift = osc_sine(expo(dur, 48, 110), length=length) * ramp(dur, 0.0, 0.7, 2.0)
    stones = resonant_body(dur, [320.0, 540.0, 880.0], [18, 20, 24], seed=345, decay=1.6) * 0.5
    return finish_sfx(saturate(mix(grind, scrape * 0.7, lift, stones), 1.8), irs()[2], 0.14)


def move_rock_impact():
    dur = 0.7
    crack = bandpass(noise_white(seconds=0.03, seed=346), 1800, 9000, 2) * perc_env(0.03, 0.0002, 12.0)
    stone = resonant_body(dur, [178.0, 296.0, 471.0, 812.0], [16, 20, 26, 30], seed=347, decay=2.4)
    mass = osc_sine(expo(dur, 120, 46), seconds=dur) * perc_env(dur, 0.001, 3.0) * 1.2
    rubble = crackle(dur, density=260, low=600, high=6500, seed=348, decay=1.6)
    length = n_samples(dur)
    out = saturate(mix(fit(crack, length) * 1.4, stone * 1.1, mass, rubble * 0.7), 2.4, asym=0.1)
    return finish_sfx(transient_shape(out, 1.5), irs()[2], 0.18)


def move_ghost_cast():
    dur = 1.0
    length = n_samples(dur)
    # detuned pair a semitone apart -- the beating is the unease
    voice = (osc_saw(expo(dur, 260, 150), length=length)
             + osc_saw(expo(dur, 275, 159), length=length) * 0.9)
    voice = sweep_filter(voice, expo(dur, 2200, 500), q=3.0, mode="lp")
    voice *= ramp(dur, 0.0, 1.0, 1.6) * np.clip(np.linspace(1.6, 0.0, length), 0, 1)
    breath = sweep_filter(noise_white(length=length, seed=349), expo(dur, 900, 260), q=1.4, mode="bp")
    breath *= ramp(dur, 0.1, 0.8, 1.2)
    moan = osc_sine(expo(dur, 74, 52), length=length) * ramp(dur, 0.2, 0.9)
    out = mix(voice * 0.55, breath * 0.7, moan * 0.8)
    return finish_sfx(out, irs()[2], 0.36)


def move_ghost_impact():
    dur = 1.1
    length = n_samples(dur)
    hollow = osc_fm(expo(dur, 420, 90), 1.19, expo(dur, 6.0, 0.5), length=length)
    hollow *= perc_env(dur, 0.004, 2.0)
    chill = resonant_body(dur, [640.0, 905.0, 1279.0], [40, 44, 48], seed=350, decay=1.2) * 0.7
    drop = osc_sine(expo(dur, 180, 38), length=length) * perc_env(dur, 0.003, 2.2) * 1.1
    air = sweep_filter(noise_white(length=length, seed=351), expo(dur, 5000, 400), q=1.2, mode="bp")
    air *= perc_env(dur, 0.001, 2.6) * 0.5
    out = mix(hollow, chill, drop, air)
    return finish_sfx(out, irs()[2], 0.42)


def move_fighting_cast():
    dur = 0.28
    a = whoosh(dur, 700, 3200, q=2.4, seed=352, peak_at=0.6) * 1.3
    b = whoosh(dur * 0.8, 260, 1100, q=1.4, seed=353) * 0.6
    return finish_sfx(mix(a, fit(b, n_samples(dur))))


def move_fighting_impact():
    dur = 0.42
    hit = impact(dur, body_hz=110, bright=2600, weight=1.4, seed=354, decay=3.0, drive=2.8)
    slap = bandpass(noise_white(seconds=0.06, seed=355), 700, 3600, 2) * perc_env(0.06, 0.0003, 8.0)
    chest = resonant_body(dur, [92.0, 168.0], [10, 12], seed=356, decay=3.4) * 0.6
    length = n_samples(dur)
    out = saturate(mix(hit * 1.1, fit(slap, length) * 1.1, chest), 2.6, asym=0.16)
    return finish_sfx(transient_shape(out, 1.6), irs()[0], 0.1)


# --- effectiveness / crit modifier layers -------------------------------------------


def move_critical():
    """
    Plays *over* the type impact, not instead of it. A tight upward riser into a
    metallic double-strike, so a crit reads as the same hit with more violence.
    """
    dur = 0.55
    length = n_samples(dur)
    pre = fit(riser(0.09, 900, 5200, seed=357), length) * 0.8
    strike = resonant_body(dur, [1450.0, 2310.0, 3720.0, 5900.0], [50, 55, 60, 65],
                           seed=358, decay=2.2)
    second = np.zeros(length)
    place(second, resonant_body(0.3, [1750.0, 2760.0], [50, 55], seed=359, decay=3.0) * 0.7, 0.055)
    snap = bandpass(noise_white(seconds=0.02, seed=360), 3000, 15000, 2) * perc_env(0.02, 0.0002, 14.0)
    sub = osc_sine(expo(dur, 160, 50), length=length) * perc_env(dur, 0.001, 3.2) * 0.8
    out = saturate(mix(pre, strike * 1.2, second, fit(snap, length) * 1.3, sub), 2.6)
    return finish_sfx(transient_shape(out, 1.8), irs()[1], 0.16)


def move_super_effective():
    """Bright, rewarding: a sharp attack and a rising shimmer that resolves upward."""
    dur = 0.7
    length = n_samples(dur)
    stab = resonant_body(dur, [2200.0, 3300.0, 4400.0], [40, 45, 50], seed=361, decay=2.6)
    lift = osc_fm(expo(dur, 700, 1900), 2.0, expo(dur, 5.0, 0.4), length=length)
    lift *= perc_env(dur, 0.002, 1.8)
    air = sweep_filter(noise_white(length=length, seed=362), expo(dur, 2000, 12000), q=3.0, mode="bp")
    air *= ramp(dur, 0.2, 1.0, 1.6) * perc_env(dur, 0.001, 1.2)
    punch = osc_sine(expo(dur, 200, 70), length=length) * perc_env(dur, 0.001, 3.6) * 0.9
    out = mix(stab * 1.1, lift * 0.8, air * 0.5, punch)
    return finish_sfx(transient_shape(out, 1.4), irs()[1], 0.2)


def move_not_very_effective():
    """Dull, absorbed, short. All the energy is below 1.2 kHz and the tail dies fast."""
    dur = 0.34
    length = n_samples(dur)
    thud = lowpass(impact(dur, body_hz=105, bright=900, weight=0.7, seed=363, decay=5.0), 1100, 2)
    pad_ = lowpass(noise_white(length=length, seed=364), 700, 2) * perc_env(dur, 0.004, 6.0) * 0.6
    out = mix(thud, pad_)
    return finish_sfx(out * 0.75, irs()[0], 0.06)


# ======================================================================================
# BATTLE
# ======================================================================================


def battle_send_out():
    """The ball opens: a mechanical snap, an energy bloom, then a settle."""
    dur = 1.0
    length = n_samples(dur)
    snap = resonant_body(0.12, [1400.0, 2600.0], [40, 45], seed=401, decay=6.0)
    bloom = sweep_filter(noise_white(length=length, seed=402), expo(dur, 700, 5200), q=2.0, mode="bp")
    bloom *= ramp(dur, 0.0, 1.0, 0.5) * perc_env(dur, 0.01, 1.6)
    tone = osc_fm(expo(dur, 320, 620), 1.5, expo(dur, 6.0, 0.5), length=length)
    tone *= perc_env(dur, 0.006, 1.5)
    sub = osc_sine(expo(dur, 90, 150), length=length) * perc_env(dur, 0.004, 2.2) * 0.7
    sparkle = crackle(dur, density=90, low=4000, high=14000, seed=403, decay=1.2) * 0.4
    out = mix(fit(snap, length) * 1.1, bloom * 0.8, tone * 0.9, sub, sparkle)
    return finish_sfx(out, irs()[1], 0.2)


def battle_recall():
    """The reverse gesture: energy contracts inward and the case clicks shut."""
    dur = 0.75
    length = n_samples(dur)
    suck = sweep_filter(noise_white(length=length, seed=404), expo(dur, 5200, 500), q=2.2, mode="bp")
    suck *= ramp(dur, 1.0, 0.15, 1.2) * perc_env(dur, 0.02, 1.0)
    tone = osc_fm(expo(dur, 700, 190), 1.5, expo(dur, 5.0, 0.6), length=length)
    tone *= perc_env(dur, 0.01, 1.6)
    click = np.zeros(length)
    place(click, resonant_body(0.1, [1250.0, 2400.0], [45, 50], seed=405, decay=7.0), dur * 0.78)
    out = mix(suck * 0.9, tone * 0.8, click * 1.1)
    return finish_sfx(out, irs()[0], 0.14)


def battle_faint():
    """A descending, deflating cry-shaped fall. Deliberately not comedic -- just tired."""
    dur = 1.1
    length = n_samples(dur)
    fall = osc_fm(expo(dur, 520, 78), 1.02, expo(dur, 4.0, 0.4), length=length)
    fall *= adsr(dur, 0.02, 0.3, 0.5, 0.55, curve=1.6)
    breath = sweep_filter(noise_white(length=length, seed=406), expo(dur, 2200, 300), q=1.2, mode="bp")
    breath *= adsr(dur, 0.03, 0.4, 0.35, 0.5) * 0.4
    thud = np.zeros(length)
    place(thud, impact(0.4, body_hz=95, bright=1400, weight=1.0, seed=407, decay=3.4) * 0.8, dur * 0.62)
    out = mix(fall * 0.9, breath, thud)
    return finish_sfx(out, irs()[1], 0.22)


def battle_hp_tick():
    """
    One tick of the health bar drain. Kept short, dry and neutral in pitch so the
    presenter can drive AudioSource.pitch from the HP fraction and get a continuous
    rising line as the bar empties -- see BattleAudioPresenter.HpTickPitch.
    """
    dur = 0.075
    length = n_samples(dur)
    tone = osc_pulse(1180.0, length=length, width=0.42) * perc_env(dur, 0.0008, 6.0)
    click = bandpass(noise_white(length=length, seed=408), 2500, 9000, 2) * perc_env(dur, 0.0003, 12.0)
    out = mix(tone * 0.8, click * 0.5)
    return finish_sfx(out, peak=0.72)


def battle_low_hp_warning():
    """The classic urgent two-beep, built as a seamless loop so it can run under battle."""
    period = 0.5
    length = n_samples(period)
    out = np.zeros(length)
    for offset in (0.0, 0.16):
        beep = osc_pulse(1568.0, seconds=0.11, width=0.5) * perc_env(0.11, 0.002, 4.0)
        beep += osc_sine(3136.0, seconds=0.11) * perc_env(0.11, 0.001, 6.0) * 0.3
        place(out, beep * 0.8, offset)
    out = highpass(out, 300.0, 2)
    return dsp.normalize(out, 0.7), period


def battle_level_up():
    """Rising major arpeggio on the bell voice with a bright wash. Unambiguously good news."""
    dur = 1.5
    out = np.zeros(n_samples(dur))
    for i, midi in enumerate([dsp.n("C5"), dsp.n("E5"), dsp.n("G5"), dsp.n("C6"), dsp.n("E6")]):
        place(out, I.bell(midi, 1.1, 0.55 + i * 0.09), i * 0.085)
    place(out, I.marimba(dsp.n("C6"), 0.6, 0.5), 0.34)
    shine = sweep_filter(noise_white(seconds=dur, seed=409), expo(dur, 3000, 13000), q=3.0, mode="bp")
    shine *= ramp(dur, 0.0, 0.5, 2.0) * perc_env(dur, 0.05, 1.4)
    return finish_sfx(mix(out, shine * 0.4), irs()[1], 0.26)


def battle_exp_gain():
    """
    Loops while the experience bar fills, so it must be seamless: a soft granular
    shimmer with a repeating 16th-note tick at 0.25 s.
    """
    period = 0.5
    length = n_samples(period)
    out = np.zeros(length)
    for offset in (0.0, 0.25):
        tick = osc_sine(2093.0, seconds=0.06) * perc_env(0.06, 0.0008, 7.0)
        tick += bandpass(noise_white(seconds=0.06, seed=410), 4000, 11000, 2) * perc_env(0.06, 0.0004, 10.0) * 0.4
        place(out, tick * 0.55, offset)
    bed = bandpass(dsp.noise_pink(length=length, seed=411), 2500, 8000, 2) * 0.16
    out = out + bed
    out = dsp.crossfade_loop(np.concatenate([out, out[: n_samples(0.06)]]), 0.06)
    return dsp.normalize(highpass(out, 400.0, 2), 0.66), out.shape[0] / SR


def battle_stat_up():
    """Upward glide plus a widening shimmer."""
    dur = 0.6
    length = n_samples(dur)
    glide = osc_fm(expo(dur, 330, 990), 2.0, expo(dur, 3.0, 0.5), length=length)
    glide *= adsr(dur, 0.01, 0.1, 0.7, 0.25, curve=1.5)
    air = sweep_filter(noise_white(length=length, seed=412), expo(dur, 1500, 11000), q=3.5, mode="bp")
    air *= ramp(dur, 0.1, 0.8, 1.6) * perc_env(dur, 0.01, 1.6)
    return finish_sfx(mix(glide * 0.9, air * 0.45), irs()[0], 0.18)


def battle_stat_down():
    dur = 0.6
    length = n_samples(dur)
    glide = osc_fm(expo(dur, 740, 210), 2.0, expo(dur, 3.0, 0.6), length=length)
    glide *= adsr(dur, 0.01, 0.12, 0.6, 0.3, curve=1.6)
    air = sweep_filter(noise_white(length=length, seed=413), expo(dur, 7000, 900), q=3.0, mode="bp")
    air *= ramp(dur, 0.7, 0.15, 1.2) * perc_env(dur, 0.01, 1.8)
    return finish_sfx(mix(glide * 0.85, air * 0.4), irs()[0], 0.16)


def status_burn():
    dur = 0.8
    length = n_samples(dur)

    def flame(d, rng):
        m = max(16, n_samples(min(d, 0.07)))
        return lowpass(bandpass(rng.standard_normal(m), rng.uniform(300, 1100),
                                rng.uniform(2000, 6000), 2), 7000)

    fire = dsp.granular(flame, dur, grain_ms=45.0, density=110.0, seed=414, stereo=False)
    fire *= adsr(dur, 0.03, 0.25, 0.5, 0.4)
    sear = crackle(dur, density=180, low=1500, high=9000, seed=415, decay=1.6) * 0.6
    low = osc_sine(expo(dur, 90, 62), length=length) * perc_env(dur, 0.01, 2.0) * 0.6
    return finish_sfx(saturate(mix(fire, sear, low), 1.6), irs()[0], 0.14)


def status_freeze():
    """Crystalline: a rising set of glass resonances then a hard crystallising snap."""
    dur = 1.0
    length = n_samples(dur)
    shards = np.zeros(length)
    rng = np.random.default_rng(416)
    for i in range(14):
        f = rng.uniform(2600, 9000)
        s = resonant_body(0.35, [f, f * 1.51], [70, 75], seed=int(rng.integers(1e6)), decay=3.0)
        place(shards, s * rng.uniform(0.25, 0.6), rng.uniform(0.0, 0.45))
    freeze_t = sweep_filter(noise_white(length=length, seed=417), expo(dur, 900, 7000), q=4.0, mode="bp")
    freeze_t *= ramp(dur, 0.1, 0.7, 1.8) * perc_env(dur, 0.02, 1.4)
    lock = np.zeros(length)
    place(lock, resonant_body(0.5, [1720.0, 2580.0, 3870.0], [60, 65, 70], seed=418, decay=1.8), 0.42)
    sub = osc_sine(expo(dur, 140, 60), length=length) * perc_env(dur, 0.004, 2.6) * 0.5
    return finish_sfx(mix(shards, freeze_t * 0.5, lock * 1.1, sub), irs()[1], 0.3)


def status_paralysis():
    dur = 0.7
    length = n_samples(dur)
    buzz = osc_pulse(72.0, length=length, width=0.25)
    buzz = ring_mod(buzz, 210.0)
    buzz = sweep_filter(buzz, expo(dur, 1400, 3600), q=2.6, mode="bp")
    buzz *= adsr(dur, 0.004, 0.2, 0.45, 0.35, curve=1.4)
    arc = crackle(dur, density=340, low=2500, high=14000, seed=419, decay=1.8)
    stutter = 0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 17.0 * np.linspace(0, dur, length)))
    out = mix(buzz * 0.9, arc * 0.7) * (0.55 + 0.45 * stutter)
    return finish_sfx(saturate(out, 2.0), irs()[0], 0.1)


def status_poison():
    dur = 0.8
    length = n_samples(dur)
    gurgle = np.zeros(length)
    rng = np.random.default_rng(420)
    for _ in range(20):
        b = bubble(rng.uniform(0.05, 0.1), rng.uniform(110, 300), rng.uniform(300, 700),
                   seed=int(rng.integers(1e6)))
        place(gurgle, b * rng.uniform(0.3, 0.75), rng.uniform(0.0, dur - 0.12))
    sludge = lowpass(dsp.noise_brown(length=length, seed=421), 600, 2) * adsr(dur, 0.02, 0.2, 0.6, 0.4)
    hiss = bandpass(noise_white(length=length, seed=422), 1400, 5000, 2) * adsr(dur, 0.02, 0.3, 0.3, 0.4) * 0.35
    return finish_sfx(saturate(mix(gurgle, sludge * 0.9, hiss), 1.5), irs()[0], 0.14)


def status_sleep():
    """Soft descending pair of bells with a drifting low pad. Reads as 'switching off'."""
    dur = 1.4
    out = np.zeros(n_samples(dur))
    for i, midi in enumerate([dsp.n("G5"), dsp.n("D5"), dsp.n("G4")]):
        place(out, I.bell(midi, 1.0, 0.5 - i * 0.08), i * 0.22)
    pad_ = I.pad(dsp.n("G2"), dur, 0.5, cutoff=800.0)
    breath = sweep_filter(noise_white(seconds=dur, seed=423), expo(dur, 800, 220), q=1.0, mode="bp")
    breath *= adsr(dur, 0.15, 0.4, 0.3, 0.6) * 0.3
    return finish_sfx(mix(out, fit(pad_, out.shape[0]) * 0.8, breath), irs()[1], 0.3)


# ======================================================================================
# CAPTURE -- the set piece
# ======================================================================================


def capture_throw():
    """Underhand throw: a short grunt-free air whoosh with a doppler-ish pitch arc."""
    dur = 0.45
    a = whoosh(dur, 380, 2400, q=1.5, seed=501, peak_at=0.5)
    b = whoosh(dur, 2000, 900, q=2.6, seed=502) * 0.5
    spin = osc_sine(expo(dur, 180, 320), seconds=dur) * ramp(dur, 0.0, 0.25, 2.0)
    length = n_samples(dur)
    return finish_sfx(mix(a * 1.2, fit(b, length), spin), irs()[1], 0.12)


def capture_absorb_beam():
    """
    The red beam. A sustained, slightly unstable tone with a rising component and a
    granular fizz, so it feels like energy being drawn rather than a synth pad.
    """
    dur = 1.3
    length = n_samples(dur)
    core = osc_fm(expo(dur, 240, 520), 1.5, expo(dur, 2.0, 6.0), length=length, feedback=0.12)
    core *= adsr(dur, 0.05, 0.2, 0.85, 0.35, curve=1.3)
    wobble = 1.0 + 0.05 * np.sin(2 * np.pi * 7.5 * np.linspace(0, dur, length))
    core *= wobble
    fizz = sweep_filter(noise_white(length=length, seed=503), expo(dur, 1400, 6500), q=3.0, mode="bp")
    fizz *= adsr(dur, 0.06, 0.25, 0.6, 0.4) * 0.45
    pull = osc_sine(expo(dur, 70, 130), length=length) * adsr(dur, 0.08, 0.3, 0.7, 0.35) * 0.5
    sparkle = crackle(dur, density=120, low=4500, high=15000, seed=504, decay=0.6) * 0.3
    return finish_sfx(mix(core, fizz, pull, sparkle), irs()[1], 0.2)


def capture_ball_land():
    """Ball hits the ground: a hard shell tap, a small bounce, and grass contact."""
    dur = 0.6
    length = n_samples(dur)
    tap = resonant_body(0.22, [880.0, 1640.0, 2710.0], [30, 35, 40], seed=505, decay=4.0)
    thud = impact(0.25, body_hz=140, bright=1800, weight=0.6, seed=506, decay=5.0) * 0.7
    out = np.zeros(length)
    place(out, fit(tap, n_samples(0.22)) * 1.0, 0.0)
    place(out, fit(thud, n_samples(0.25)), 0.0)
    # a lighter second contact 130 ms later reads as a bounce
    place(out, fit(tap, n_samples(0.18)) * 0.4, 0.13)
    place(out, fit(thud, n_samples(0.18)) * 0.35, 0.13)
    grass = bandpass(noise_white(seconds=0.12, seed=507), 2200, 8000, 2) * perc_env(0.12, 0.002, 5.0)
    place(out, grass * 0.3, 0.005)
    return finish_sfx(transient_shape(out, 1.3), irs()[0], 0.12)


def capture_shake_tick(variant: int = 0):
    """
    The shake tick -- the emotional core of the capture.

    Three elements, in this order and no other: a 55 ms mechanical whir as the ball
    rocks (band-passed noise with a falling centre), then a tight click transient, then
    two metal resonances plus a small case thump that ring for about 90 ms. The whir
    before the click is what makes it feel like a physical object moving rather than a
    UI beep, and the case thump is what gives it a size. Three variants detune the
    resonators by a few percent and shift the whir slightly so a run of four shakes
    never machine-guns.
    """
    tunings = [
        (1148.0, 2872.0, 176.0, 0.052, 508),
        (1096.0, 2744.0, 168.0, 0.058, 509),
        (1203.0, 3010.0, 184.0, 0.047, 510),
    ]
    f1, f2, case, whir_len, seed = tunings[variant % 3]
    dur = 0.26
    length = n_samples(dur)
    out = np.zeros(length)

    # The whir is supporting texture and must stay roughly 10 dB under the click --
    # if the two approach each other the attack blurs and the tick stops reading as
    # a mechanism latching.
    whir = sweep_filter(noise_white(seconds=whir_len, seed=seed), expo(whir_len, 2600, 700),
                        q=3.0, mode="bp")
    whir *= np.hanning(whir.shape[0]) * 0.22
    place(out, whir, 0.0)

    click_at = whir_len * 0.92
    click = bandpass(noise_white(seconds=0.012, seed=seed + 1), 2200, 11000, 2)
    click *= perc_env(0.012, 0.0002, 14.0)
    place(out, click * 1.1, click_at)

    ring = resonant_body(0.13, [f1, f2, f1 * 2.03], [55, 62, 48], seed=seed + 2, decay=4.4,
                         amps=[1.0, 0.55, 0.3])
    place(out, ring * 1.2, click_at)

    thump = osc_sine(expo(0.09, case * 1.4, case * 0.85), seconds=0.09) * perc_env(0.09, 0.0008, 4.0)
    place(out, thump * 0.7, click_at)

    # Light saturation only: heavier drive levels the click back down into the whir.
    out = saturate(out, 1.15, asym=0.08)
    return finish_sfx(transient_shape(out, 1.4), irs()[0], 0.11, peak=0.86)


def capture_success_click():
    """
    The click.

    Everything above the shake tick, and then the release: the mechanical lock is a
    tighter, higher version of the shake click, and immediately behind it a bright
    three-partial chime rings for 700 ms over a soft low confirm. The chime is tuned
    to a G major triad so it agrees harmonically with the capture-success sting that
    the music director fires straight afterwards -- the two are meant to be heard as
    one gesture.
    """
    dur = 1.0
    length = n_samples(dur)
    out = np.zeros(length)

    # tiny pre-lift so the click has somewhere to arrive from
    pre = sweep_filter(noise_white(seconds=0.035, seed=511), expo(0.035, 1200, 4200), q=4.0, mode="bp")
    pre *= ramp(0.035, 0.0, 0.26, 2.0)
    place(out, pre, 0.0)

    lock = bandpass(noise_white(seconds=0.01, seed=512), 3000, 14000, 2) * perc_env(0.01, 0.0001, 16.0)
    place(out, lock * 1.25, 0.035)
    mech = resonant_body(0.1, [1560.0, 3120.0], [60, 66], seed=513, decay=6.0)
    place(out, mech * 0.85, 0.035)
    case = osc_sine(expo(0.08, 210, 150), seconds=0.08) * perc_env(0.08, 0.0006, 4.5)
    place(out, case * 0.6, 0.035)

    # G major chime: G5, B5, D6 as FM bells, staggered by 18 ms
    for i, (midi, amp) in enumerate([(dsp.n("G5"), 1.0), (dsp.n("B5"), 0.7), (dsp.n("D6"), 0.55)]):
        place(out, I.bell(midi, 0.75, 0.55 * amp), 0.045 + i * 0.018)

    confirm = osc_sine(dsp.note_hz(dsp.n("G3")), seconds=0.5) * perc_env(0.5, 0.004, 2.4)
    place(out, confirm * 0.35, 0.045)

    shine = sweep_filter(noise_white(seconds=0.5, seed=514), expo(0.5, 5000, 13000), q=4.0, mode="bp")
    shine *= perc_env(0.5, 0.006, 2.2) * 0.3
    place(out, shine, 0.045)

    return finish_sfx(transient_shape(out, 1.2), irs()[1], 0.26)


def capture_break_out():
    """The failure: the shell cracks, energy escapes upward, and the creature is back."""
    dur = 0.9
    length = n_samples(dur)
    out = np.zeros(length)
    crack = bandpass(noise_white(seconds=0.03, seed=515), 1400, 10000, 2) * perc_env(0.03, 0.0002, 13.0)
    place(out, crack * 1.3, 0.0)
    shards = resonant_body(0.4, [980.0, 1720.0, 2640.0, 4100.0], [40, 45, 50, 55], seed=516, decay=3.0)
    place(out, shards * 1.0, 0.0)
    burst = lowpass(noise_white(seconds=0.4, seed=517), 4200, 2) * perc_env(0.4, 0.001, 3.2)
    place(out, burst * 0.8, 0.0)
    escape = osc_fm(expo(0.55, 180, 760), 1.5, expo(0.55, 5.0, 0.8), seconds=0.55)
    escape *= adsr(0.55, 0.01, 0.15, 0.5, 0.3)
    place(out, escape * 0.7, 0.02)
    sub = osc_sine(expo(dur, 150, 48), length=length) * perc_env(dur, 0.001, 3.0) * 0.9
    out = out + sub
    return finish_sfx(saturate(transient_shape(out, 1.5), 2.2), irs()[1], 0.2)


# ======================================================================================
# OVERWORLD
# ======================================================================================


_FOOTSTEP_SEEDS = {"grass": 6100, "dirt": 6200, "stone": 6300, "wood": 6400, "water": 6500}


def _footstep(surface: str, variant: int) -> np.ndarray:
    """
    Footsteps are the sound the player hears most, so each surface gets its own
    physical model rather than a filtered copy: grass is granular blade contact,
    stone is a hard tap with room, wood is a resonant panel, water is a splash with
    droplets, dirt is a soft broadband compression with a little grit.
    """
    # Explicit seed table, NOT hash((surface, variant)): Python randomises string hashing
    # per process, so that would have made every rebuild produce different footsteps and
    # quietly broken the determinism this whole toolchain claims.
    seed = _FOOTSTEP_SEEDS[surface] + variant * 37
    rng = np.random.default_rng(seed)
    # Wide enough that four variants are audibly different objects underfoot rather than
    # four renders of the same one -- the ear picks up a repeated footstep immediately.
    jitter = 1.0 + rng.uniform(-0.14, 0.14)

    if surface == "grass":
        dur = 0.19

        def blade(d, r):
            m = max(8, n_samples(min(d, 0.012)))
            return bandpass(r.standard_normal(m), r.uniform(1800, 4500), r.uniform(5000, 13000), 2)

        out = dsp.granular(blade, dur, grain_ms=9.0, density=420.0, pitch_jitter=0.5,
                           seed=seed, stereo=False)
        out *= perc_env(dur, 0.002, 5.0)
        soft = lowpass(noise_white(seconds=dur, seed=seed + 1), 700 * jitter, 2)
        out = mix(out * 1.0, soft * perc_env(dur, 0.001, 7.0) * 0.5)

    elif surface == "dirt":
        dur = 0.16
        thud = lowpass(noise_white(seconds=dur, seed=seed), 1100 * jitter, 2) * perc_env(dur, 0.001, 7.0)
        grit = bandpass(noise_white(seconds=dur, seed=seed + 1), 1800, 6500, 2) * perc_env(dur, 0.0008, 11.0)
        body = osc_sine(expo(dur, 150 * jitter, 70), seconds=dur) * perc_env(dur, 0.001, 8.0) * 0.35
        out = mix(thud, grit * 0.45, body)

    elif surface == "stone":
        dur = 0.22
        tap = resonant_body(dur, [720 * jitter, 1480 * jitter, 2960 * jitter], [26, 30, 34],
                            seed=seed, decay=6.0)
        click = bandpass(noise_white(seconds=0.014, seed=seed + 1), 2500, 12000, 2) * perc_env(0.014, 0.0002, 12.0)
        low = osc_sine(expo(dur, 190, 95), seconds=dur) * perc_env(dur, 0.0008, 8.0) * 0.3
        out = mix(tap * 1.0, fit(click, n_samples(dur)) * 0.8, low)
        out = dsp.reverb(out, irs()[0], 0.16)

    elif surface == "wood":
        dur = 0.24
        # Standing on a different part of a plank excites a different set of its modes,
        # so each variant re-weights the partials rather than only detuning them.
        amp_profiles = [
            [1.0, 0.70, 0.45, 0.25],
            [0.85, 1.0, 0.30, 0.40],
            [1.0, 0.45, 0.70, 0.20],
            [0.70, 0.85, 0.55, 0.50],
        ]
        amps = amp_profiles[variant % len(amp_profiles)]
        panel = resonant_body(dur, [186 * jitter, 342 * jitter, 611 * jitter, 1080 * jitter],
                              [18, 22, 26, 30], seed=seed, decay=4.0 + rng.uniform(0, 2.5),
                              amps=amps)
        knock = bandpass(noise_white(seconds=0.012, seed=seed + 1),
                         700 + 400 * rng.random(), 5000, 2) * perc_env(0.012, 0.0002, 12.0)
        out = mix(panel * 1.1, fit(knock, n_samples(dur)) * 0.7)

    elif surface == "water":
        dur = 0.3
        splash = highpass(noise_white(seconds=dur, seed=seed), 400, 2) * perc_env(dur, 0.0012, 5.0)
        splash = sweep_filter(splash, expo(dur, 6500, 1200), q=0.9, mode="lp")
        drops = np.zeros(n_samples(dur))
        for _ in range(int(rng.integers(5, 10))):
            b = bubble(0.04, rng.uniform(800, 1800), rng.uniform(1800, 4000), seed=int(rng.integers(1e6)))
            place(drops, b * rng.uniform(0.15, 0.4), rng.uniform(0.01, dur - 0.06))
        low = osc_sine(expo(dur, 160, 70), seconds=dur) * perc_env(dur, 0.002, 6.0) * 0.35
        out = mix(splash * 1.1, drops, low)
    else:
        raise ValueError(surface)

    # Footsteps are loudness-matched, not peak-matched: a stone tap is nearly all
    # transient and a wood panel is nearly all resonance, so peak normalising the two
    # leaves wood sounding four times quieter under the player's feet.
    out = highpass(out, 40.0, 2)
    out = zero_edges(out, 48)
    return dsp.normalize_rms(out * rng.uniform(0.88, 1.0), target_rms=0.075, ceiling=0.82)


def overworld_grass_rustle(variant: int = 0):
    """Brushing through tall grass -- the encounter-trigger cue."""
    seed = 700 + variant * 37
    dur = 0.42

    def blade(d, r):
        m = max(10, n_samples(min(d, 0.02)))
        return bandpass(r.standard_normal(m), r.uniform(1500, 4000), r.uniform(4500, 12000), 2)

    out = dsp.granular(blade, dur, grain_ms=16.0, density=380.0, pitch_jitter=0.55,
                       seed=seed, stereo=False)
    out *= np.exp(-np.linspace(0, 1, n_samples(dur)) * 2.2) * (0.4 + 0.6 * np.hanning(n_samples(dur)))
    body = lowpass(noise_white(seconds=dur, seed=seed + 1), 900, 2) * perc_env(dur, 0.01, 3.0) * 0.4
    return finish_sfx(mix(out, body), peak=0.8)


def overworld_ledge_hop():
    """Push off, a moment of air, then a two-foot landing."""
    dur = 0.65
    length = n_samples(dur)
    out = np.zeros(length)
    push = bandpass(noise_white(seconds=0.09, seed=701), 600, 4000, 2) * perc_env(0.09, 0.001, 7.0)
    place(out, push * 0.7, 0.0)
    air = whoosh(0.3, 900, 2200, q=1.2, seed=702, peak_at=0.5) * 0.35
    place(out, air, 0.05)
    land = _footstep("dirt", 0) * 1.0
    place(out, fit(land, n_samples(0.2)) * 1.0, 0.38)
    place(out, fit(_footstep("dirt", 1), n_samples(0.2)) * 0.7, 0.43)
    body = osc_sine(expo(0.2, 110, 55), seconds=0.2) * perc_env(0.2, 0.002, 4.0) * 0.5
    place(out, body, 0.38)
    return finish_sfx(out, irs()[0], 0.1)


def overworld_door_open():
    dur = 0.9
    length = n_samples(dur)
    out = np.zeros(length)
    latch = resonant_body(0.09, [1420.0, 2760.0], [45, 50], seed=703, decay=7.0)
    place(out, latch * 1.0, 0.0)
    creak = sweep_filter(noise_white(seconds=0.4, seed=704), expo(0.4, 420, 900), q=9.0, mode="bp")
    creak *= adsr(0.4, 0.03, 0.15, 0.5, 0.2) * 0.5
    place(out, creak, 0.06)
    swing = whoosh(0.45, 300, 900, q=1.0, seed=705, peak_at=0.5) * 0.35
    place(out, swing, 0.1)
    stop = resonant_body(0.25, [180.0, 340.0, 620.0], [16, 20, 24], seed=706, decay=4.0)
    place(out, stop * 0.6, 0.55)
    return finish_sfx(out, irs()[1], 0.16)


def overworld_door_close():
    dur = 0.7
    length = n_samples(dur)
    out = np.zeros(length)
    swing = whoosh(0.3, 800, 260, q=1.0, seed=707, peak_at=0.6) * 0.4
    place(out, swing, 0.0)
    slam = resonant_body(0.35, [148.0, 286.0, 512.0, 940.0], [14, 18, 22, 26], seed=708, decay=3.4,
                         amps=[1.0, 0.7, 0.4, 0.2])
    thud = impact(0.3, body_hz=95, bright=1600, weight=1.0, seed=709, decay=4.0)
    place(out, slam * 1.1, 0.3)
    place(out, thud * 0.8, 0.3)
    latch = resonant_body(0.08, [1380.0, 2700.0], [45, 50], seed=710, decay=8.0)
    place(out, latch * 0.7, 0.33)
    return finish_sfx(saturate(out, 1.6), irs()[1], 0.18)


def overworld_item_pickup():
    """Two-note rising chime with a small sparkle. Short enough to fire repeatedly."""
    dur = 0.55
    out = np.zeros(n_samples(dur))
    place(out, I.marimba(dsp.n("E6"), 0.28, 0.7), 0.0)
    place(out, I.marimba(dsp.n("B6"), 0.4, 0.6), 0.075)
    place(out, I.bell(dsp.n("E7"), 0.35, 0.3), 0.075)
    sparkle = crackle(0.3, density=70, low=6000, high=15000, seed=711, decay=2.4) * 0.25
    place(out, sparkle, 0.05)
    return finish_sfx(out, irs()[0], 0.16)


def overworld_heal():
    """The centre's restore: a warm rising major triad with a soft wash. Reassuring."""
    dur = 1.8
    out = np.zeros(n_samples(dur))
    for i, midi in enumerate([dsp.n("F4"), dsp.n("A4"), dsp.n("C5"), dsp.n("F5")]):
        place(out, I.bell(midi, 1.3, 0.5), i * 0.16)
    place(out, fit(I.pad(dsp.n("F3"), 1.6, 0.55), n_samples(1.6)), 0.05)
    shine = sweep_filter(noise_white(seconds=dur, seed=712), expo(dur, 2000, 9000), q=3.0, mode="bp")
    shine *= ramp(dur, 0.0, 0.4, 1.6) * perc_env(dur, 0.1, 1.2)
    return finish_sfx(mix(out, shine * 0.3), irs()[1], 0.3)


# ======================================================================================
# SCANNER -- its own sonic identity
# ======================================================================================
#
# Design rules, applied to every cue in this section so the device sounds like one
# piece of equipment:
#   * pitch material is restricted to an F# pentatonic (F#, G#, A#, C#, D#) -- the
#     scanner never plays a pitch outside it, which is what makes unrelated cues feel
#     related;
#   * tones are FM sines with a low, falling modulation index: clean, glassy, no saw
#     or noise in the tone itself;
#   * every cue sits over the same faint servo bed (a narrow band of filtered noise
#     around 3.2 kHz) so even the alert shares the device's floor;
#   * envelopes are 4 ms attack / 120-400 ms decay -- fast enough to read as digital,
#     slow enough not to click.

SCANNER_SCALE = ["F#5", "G#5", "A#5", "C#6", "D#6", "F#6", "G#6", "A#6", "C#7"]


def scanner_tone(midi: float, dur: float, vel: float = 1.0, index: float = 1.6,
                 ratio: float = 2.0, decay: float = 3.0) -> np.ndarray:
    length = n_samples(dur)
    idx = expo(dur, index, index * 0.12)
    tone = osc_fm(note_hz(midi), ratio, idx, length=length)
    tone += osc_sine(note_hz(midi) * 2.002, length=length) * 0.15
    env = perc_env(dur, 0.004, decay)
    return tone * env * vel


def _servo_bed(dur: float, seed: int, level: float = 0.05) -> np.ndarray:
    bed = bandpass(noise_white(seconds=dur, seed=seed), 2600, 4200, 2)
    wob = 1.0 + 0.35 * np.sin(2 * np.pi * 9.0 * np.linspace(0, dur, n_samples(dur)))
    return bed * wob * level


def scanner_boot():
    """
    Power-on: a low capacitor swell, three ascending pentatonic confirmation tones,
    a data burst, and a settle onto the device's idle pitch. About 1.6 s -- long
    enough to feel like a real instrument initialising, short enough not to gate play.
    """
    dur = 1.7
    length = n_samples(dur)
    out = np.zeros(length)

    swell = osc_sine(expo(0.5, 40, 110), seconds=0.5) * ramp(0.5, 0.0, 0.5, 2.0)
    swell += sweep_filter(noise_white(seconds=0.5, seed=801), expo(0.5, 200, 2000), q=2.0, mode="bp") * 0.3
    place(out, swell * 0.7, 0.0)

    for i, note in enumerate(["F#5", "A#5", "C#6"]):
        place(out, scanner_tone(dsp.n(note), 0.3, 0.7, index=2.0, decay=4.0), 0.34 + i * 0.115)

    # data burst: fast quantised blips, the device reading itself
    rng = np.random.default_rng(802)
    for i in range(11):
        note = SCANNER_SCALE[int(rng.integers(3, 8))]
        place(out, scanner_tone(dsp.n(note), 0.06, 0.28, index=1.2, decay=6.0),
              0.72 + i * 0.028)

    place(out, scanner_tone(dsp.n("F#6"), 0.55, 0.8, index=2.4, decay=2.4), 1.06)
    place(out, scanner_tone(dsp.n("C#6"), 0.6, 0.5, index=1.8, decay=2.2), 1.06)

    out += _servo_bed(dur, 803, 0.05) * np.clip(np.linspace(0, 2.2, length), 0, 1)
    return finish_sfx(out, irs()[0], 0.16)


def scanner_scan_loop():
    """
    The idle scanning bed. Seamless: a 2.0 s cycle containing one slow filter sweep,
    a pulsing servo bed and two quiet pentatonic blips placed off the cycle boundary.
    Quiet by design -- it sits under dialogue at about -24 dBFS RMS.
    """
    period = 2.0
    length = n_samples(period)
    # a full sine cycle of cutoff movement returns exactly to its start value
    t = np.linspace(0, 2 * math.pi, length, endpoint=False)
    cutoff = 2400.0 * (2.0 ** (0.9 * np.sin(t)))
    bed = sweep_filter(noise_white(length=length, seed=804), cutoff, q=4.0, mode="bp")
    bed *= 0.18 * (0.7 + 0.3 * np.sin(t * 2.0))

    hum = osc_sine(110.0, length=length) * 0.05
    hum += osc_sine(220.0, length=length) * 0.02

    blips = np.zeros(length)
    place(blips, scanner_tone(dsp.n("F#6"), 0.18, 0.16, index=1.0, decay=6.0), 0.45)
    place(blips, scanner_tone(dsp.n("C#6"), 0.18, 0.12, index=1.0, decay=6.0), 1.32)

    out = bed + hum + blips
    out = highpass(out, 60.0, 2)
    # cyclic content already wraps; a short circular crossfade removes any residue
    out = dsp.crossfade_loop(np.concatenate([out, out[: n_samples(0.08)]]), 0.08)
    return dsp.normalize(out, 0.5), out.shape[0] / SR


def scanner_data_blip(variant: int = 0):
    """A single field resolving. Three variants so a readout populating does not machine-gun."""
    notes = ["A#6", "C#7", "F#6"]
    dur = 0.16
    length = n_samples(dur)
    out = scanner_tone(dsp.n(notes[variant % 3]), dur, 0.8, index=1.1, ratio=2.0, decay=7.0)
    tick = bandpass(noise_white(length=length, seed=805 + variant), 5000, 12000, 2)
    tick *= perc_env(dur, 0.0004, 14.0) * 0.3
    return finish_sfx(out + tick, peak=0.72)


def scanner_threat_alert():
    """
    Threat detected. Two urgent descending pairs on the scanner's own scale plus a
    low pulse -- alarming without leaving the device's voice, so it never sounds like
    a generic error buzzer.
    """
    dur = 0.9
    length = n_samples(dur)
    out = np.zeros(length)
    for i, (hi, lo) in enumerate([("D#6", "A#5"), ("D#6", "A#5")]):
        base = i * 0.34
        place(out, scanner_tone(dsp.n(hi), 0.14, 0.85, index=3.2, decay=6.0), base)
        place(out, scanner_tone(dsp.n(lo), 0.2, 0.9, index=3.4, decay=5.0), base + 0.13)
    pulse = osc_sine(expo(dur, 130, 78), length=length) * adsr(dur, 0.01, 0.3, 0.35, 0.4) * 0.5
    edge = bandpass(noise_white(length=length, seed=806), 1800, 6000, 2)
    edge *= (0.5 + 0.5 * np.sign(np.sin(2 * np.pi * 11.0 * np.linspace(0, dur, length)))) * 0.12
    out = out + pulse + edge + _servo_bed(dur, 807, 0.06)
    return finish_sfx(saturate(out, 1.4), irs()[0], 0.14)


def scanner_recommendation():
    """Advice ready: a rising pentatonic triad, confident and quiet. The scanner's 'yes'."""
    dur = 0.85
    out = np.zeros(n_samples(dur))
    for i, note in enumerate(["F#5", "C#6", "F#6"]):
        place(out, scanner_tone(dsp.n(note), 0.55, 0.7 - i * 0.05, index=1.8, decay=2.6),
              i * 0.075)
    place(out, scanner_tone(dsp.n("A#6"), 0.5, 0.25, index=1.2, decay=3.0), 0.23)
    out += _servo_bed(dur, 808, 0.04)
    return finish_sfx(out, irs()[0], 0.2)


def scanner_probability_shift(direction: int = 1):
    """
    Probability moved.

    A short glide of a perfect fourth, up when the delta is positive and down when it
    is negative, over a second voice a fifth away that moves in the same direction.
    The presenter scales AudioSource.pitch and volume by |delta| so a two-point drift
    and a twenty-point swing are the same sound at different intensities -- see
    ScannerAudio.PlayProbabilityShift.
    """
    dur = 0.5
    length = n_samples(dur)
    lo, hi = (note_hz(dsp.n("C#6")), note_hz(dsp.n("F#6")))
    if direction >= 0:
        f_a, f_b = lo, hi
    else:
        f_a, f_b = hi, lo
    idx = expo(dur, 1.8, 0.3)
    main = osc_fm(expo(dur, f_a, f_b), 2.0, idx, length=length)
    main *= adsr(dur, 0.006, 0.12, 0.55, 0.25, curve=1.5)
    second = osc_fm(expo(dur, f_a * 1.5, f_b * 1.5), 2.0, idx * 0.6, length=length)
    second *= adsr(dur, 0.01, 0.14, 0.4, 0.28, curve=1.5) * 0.35
    air = sweep_filter(noise_white(length=length, seed=809),
                       expo(dur, f_a * 3, f_b * 3), q=5.0, mode="bp")
    air *= adsr(dur, 0.01, 0.15, 0.4, 0.3) * 0.18
    out = main + second + air + _servo_bed(dur, 810, 0.035)
    return finish_sfx(out, irs()[0], 0.12)


# ======================================================================================
# UI
# ======================================================================================


def ui_navigate():
    dur = 0.09
    length = n_samples(dur)
    out = osc_fm(1320.0, 3.0, expo(dur, 1.1, 0.15), length=length) * perc_env(dur, 0.0012, 8.0)
    out += bandpass(noise_white(length=length, seed=901), 3000, 9000, 2) * perc_env(dur, 0.0004, 14.0) * 0.25
    return finish_sfx(out, peak=0.68)


def ui_confirm():
    dur = 0.3
    out = np.zeros(n_samples(dur))
    place(out, I.marimba(dsp.n("D6"), 0.16, 0.7), 0.0)
    place(out, I.marimba(dsp.n("A6"), 0.24, 0.6), 0.045)
    return finish_sfx(out, irs()[0], 0.1, peak=0.8)


def ui_cancel():
    dur = 0.28
    out = np.zeros(n_samples(dur))
    place(out, I.marimba(dsp.n("A5"), 0.16, 0.6), 0.0)
    place(out, I.marimba(dsp.n("D5"), 0.22, 0.55), 0.045)
    return finish_sfx(out, irs()[0], 0.08, peak=0.76)


def ui_error():
    """Low, short, two-tone and slightly detuned. Discouraging without being harsh."""
    dur = 0.35
    length = n_samples(dur)
    a = osc_pulse(196.0, length=length, width=0.4) * adsr(dur, 0.004, 0.08, 0.5, 0.2)
    b = osc_pulse(207.0, length=length, width=0.4) * adsr(dur, 0.004, 0.08, 0.5, 0.2) * 0.8
    gate = 0.35 + 0.65 * (np.sin(2 * np.pi * 22.0 * np.linspace(0, dur, length)) > 0)
    out = lowpass((a + b) * gate, 2600, 2)
    return finish_sfx(saturate(out, 1.6), peak=0.7)


def ui_menu_open():
    dur = 0.35
    length = n_samples(dur)
    swish = sweep_filter(noise_white(length=length, seed=902), expo(dur, 800, 6000), q=2.2, mode="bp")
    swish *= perc_env(dur, 0.004, 3.4) * 0.6
    tone = osc_fm(expo(dur, 440, 880), 2.0, expo(dur, 1.4, 0.2), length=length)
    tone *= perc_env(dur, 0.004, 3.0)
    return finish_sfx(mix(swish, tone * 0.7), irs()[0], 0.1, peak=0.74)


def ui_menu_close():
    dur = 0.32
    length = n_samples(dur)
    swish = sweep_filter(noise_white(length=length, seed=903), expo(dur, 6000, 700), q=2.2, mode="bp")
    swish *= perc_env(dur, 0.003, 3.6) * 0.6
    tone = osc_fm(expo(dur, 780, 390), 2.0, expo(dur, 1.4, 0.2), length=length)
    tone *= perc_env(dur, 0.004, 3.4)
    return finish_sfx(mix(swish, tone * 0.7), irs()[0], 0.08, peak=0.72)


def ui_typewriter():
    """Per-character dialogue blip. Must be tiny and dull -- it fires 40 times a line."""
    dur = 0.045
    length = n_samples(dur)
    out = osc_pulse(880.0, length=length, width=0.5) * perc_env(dur, 0.0008, 9.0)
    out = lowpass(out, 3200, 2)
    return finish_sfx(out, peak=0.5)


# ======================================================================================
# registry -- name -> (callable, loop flag, category, trigger description)
# ======================================================================================


def _reg():
    r = {}

    types = [
        ("Normal", move_normal_cast, move_normal_impact),
        ("Fire", move_fire_cast, move_fire_impact),
        ("Water", move_water_cast, move_water_impact),
        ("Electric", move_electric_cast, move_electric_impact),
        ("Grass", move_grass_cast, move_grass_impact),
        ("Poison", move_poison_cast, move_poison_impact),
        ("Ground", move_ground_cast, move_ground_impact),
        ("Flying", move_flying_cast, move_flying_impact),
        ("Psychic", move_psychic_cast, move_psychic_impact),
        ("Rock", move_rock_cast, move_rock_impact),
        ("Ghost", move_ghost_cast, move_ghost_impact),
        ("Fighting", move_fighting_cast, move_fighting_impact),
    ]
    for name, cast, imp in types:
        r[f"SFX_Move_{name}_Cast"] = (
            cast, False, "Move",
            f"MoveExecutedEvent where MoveType == ElementType.{name} (the wind-up / release beat).")
        r[f"SFX_Move_{name}_Impact"] = (
            imp, False, "Move",
            f"DamageDealtEvent following a MoveExecutedEvent of ElementType.{name}.")

    r["SFX_Move_Critical"] = (move_critical, False, "Move",
                              "Layered over the type impact when DamageDealtEvent.Critical is true.")
    r["SFX_Move_SuperEffective"] = (move_super_effective, False, "Move",
                                    "Layered over the type impact when Effectiveness == SuperEffective.")
    r["SFX_Move_NotVeryEffective"] = (move_not_very_effective, False, "Move",
                                      "Replaces the impact tail when Effectiveness == NotVeryEffective.")

    battle = [
        ("SFX_Battle_SendOut", battle_send_out, "CreatureSentOutEvent."),
        ("SFX_Battle_Recall", battle_recall, "CreatureWithdrawnEvent."),
        ("SFX_Battle_Faint", battle_faint, "CreatureFaintedEvent."),
        ("SFX_Battle_HpTick", battle_hp_tick,
         "One tick per health-bar step during DamageDealtEvent; pitch is driven from HP fraction."),
        ("SFX_Battle_LevelUp", battle_level_up, "ExperienceGainedEvent with LeveledUp == true."),
        ("SFX_Battle_StatUp", battle_stat_up, "StatStageChangedEvent with Delta > 0."),
        ("SFX_Battle_StatDown", battle_stat_down, "StatStageChangedEvent with Delta < 0."),
        ("SFX_Status_Burn", status_burn, "StatusChangedEvent to StatusCondition.Burn."),
        ("SFX_Status_Freeze", status_freeze, "StatusChangedEvent to StatusCondition.Freeze."),
        ("SFX_Status_Paralysis", status_paralysis, "StatusChangedEvent to StatusCondition.Paralysis."),
        ("SFX_Status_Poison", status_poison,
         "StatusChangedEvent to StatusCondition.Poison or BadPoison."),
        ("SFX_Status_Sleep", status_sleep, "StatusChangedEvent to StatusCondition.Sleep."),
    ]
    for name, fn, trig in battle:
        r[name] = (fn, False, "Battle", trig)

    r["SFX_Battle_LowHpWarning"] = (
        battle_low_hp_warning, True, "Battle",
        "Loops while the active player creature is below 20% HP; stops on heal or faint.")
    r["SFX_Battle_ExpGain"] = (
        battle_exp_gain, True, "Battle",
        "Loops while the experience bar fills during ExperienceGainedEvent.")

    capture = [
        ("SFX_Capture_Throw", capture_throw, "Start of CaptureAttemptEvent -- the ball leaves the hand."),
        ("SFX_Capture_AbsorbBeam", capture_absorb_beam, "The creature is drawn into the ball."),
        ("SFX_Capture_BallLand", capture_ball_land, "Ball touches the ground before the shakes."),
        ("SFX_Capture_SuccessClick", capture_success_click,
         "CaptureAttemptEvent.Succeeded == true, after the final shake."),
        ("SFX_Capture_BreakOut", capture_break_out,
         "CaptureAttemptEvent.Succeeded == false, after the final shake."),
    ]
    for name, fn, trig in capture:
        r[name] = (fn, False, "Capture", trig)
    for v in range(3):
        r[f"SFX_Capture_ShakeTick_{v + 1:02d}"] = (
            (lambda vv: (lambda: capture_shake_tick(vv)))(v), False, "Capture",
            "Exactly one per shake in CaptureAttemptEvent.Shakes; variants cycle so repeats do not machine-gun.")

    for surface in ("Grass", "Dirt", "Stone", "Wood", "Water"):
        for v in range(4):
            r[f"SFX_Foot_{surface}_{v + 1:02d}"] = (
                (lambda s, vv: (lambda: _footstep(s.lower(), vv)))(surface, v), False, "Overworld",
                f"Player footstep on {surface.lower()}; pick a random variant per step.")
    for v in range(3):
        r[f"SFX_Overworld_GrassRustle_{v + 1:02d}"] = (
            (lambda vv: (lambda: overworld_grass_rustle(vv)))(v), False, "Overworld",
            "Player brushes tall grass; also the encounter-trigger tell.")

    overworld = [
        ("SFX_Overworld_LedgeHop", overworld_ledge_hop, "Player hops a ledge."),
        ("SFX_Overworld_DoorOpen", overworld_door_open, "Door or building entrance opens."),
        ("SFX_Overworld_DoorClose", overworld_door_close, "Door closes behind the player."),
        ("SFX_Overworld_ItemPickup", overworld_item_pickup, "Item collected in the overworld."),
        ("SFX_Overworld_Heal", overworld_heal, "Party healed at the centre."),
    ]
    for name, fn, trig in overworld:
        r[name] = (fn, False, "Overworld", trig)

    scanner = [
        ("SFX_Scanner_Boot", scanner_boot, "Scanner raised / Poke Lab panel opens."),
        ("SFX_Scanner_ThreatAlert", scanner_threat_alert,
         "TacticalReadout.Threats gains a high-severity entry."),
        ("SFX_Scanner_Recommendation", scanner_recommendation,
         "TacticalReadout.RecommendedLine resolves and IsConfident becomes true."),
    ]
    for name, fn, trig in scanner:
        r[name] = (fn, False, "Scanner", trig)
    for v in range(3):
        r[f"SFX_Scanner_DataBlip_{v + 1:02d}"] = (
            (lambda vv: (lambda: scanner_data_blip(vv)))(v), False, "Scanner",
            "One readout field resolves; cycle variants as the panel populates.")
    r["SFX_Scanner_ScanLoop"] = (
        scanner_scan_loop, True, "Scanner",
        "Loops while the scanner is open and analysing.")
    r["SFX_Scanner_ProbabilityUp"] = (
        lambda: scanner_probability_shift(1), False, "Scanner",
        "TacticalReadout.DeltaPoints > 0; pitch and volume scale with |delta|.")
    r["SFX_Scanner_ProbabilityDown"] = (
        lambda: scanner_probability_shift(-1), False, "Scanner",
        "TacticalReadout.DeltaPoints < 0; pitch and volume scale with |delta|.")

    ui = [
        ("SFX_UI_Navigate", ui_navigate, "Selection moves between menu entries."),
        ("SFX_UI_Confirm", ui_confirm, "Menu entry accepted."),
        ("SFX_UI_Cancel", ui_cancel, "Menu backed out of."),
        ("SFX_UI_Error", ui_error, "Illegal action, e.g. a move with no PP."),
        ("SFX_UI_MenuOpen", ui_menu_open, "Menu or panel opens."),
        ("SFX_UI_MenuClose", ui_menu_close, "Menu or panel closes."),
        ("SFX_UI_Typewriter", ui_typewriter, "One character revealed in a dialogue box."),
    ]
    for name, fn, trig in ui:
        r[name] = (fn, False, "UI", trig)

    return r


REGISTRY = _reg()
