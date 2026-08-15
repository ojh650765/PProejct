"""
Ambience.

These are shipped as *layers*, not as finished per-biome beds. A route at dusk in
light rain is birdsong fading out, wind holding, night insects fading in and rain
fading up -- four gains moving independently. Pre-baking one clip per
biome-x-time-x-weather combination would be a dozen near-identical files that can only
ever switch, and the brief is explicit that ambience crossfades rather than switches.
``AmbienceDirector`` holds the per-biome weight sets and drives these gains.

Seam handling differs by layer type and this matters:
  * continuous beds (wind, rain, rumble, waterfall) are rendered one crossfade longer
    than the loop and then circularly cross-faded, which keeps RMS constant through
    the splice;
  * event layers (birds, drips, insects, murmur) are rendered one tail longer with all
    events inside the loop window, then the overhang is folded back onto the head so
    that a bird call starting at t = loop-0.3 s finishes over the top of the loop.
Mixing the two would either duplicate energy or truncate tails.
"""

from __future__ import annotations

import math

import numpy as np

import dsp
from dsp import (SR, bandpass, expo, fit, highpass, lowpass, mix, noise_white, n_samples,
                 note_hz, osc_fm, osc_sine, perc_env, place, ramp, saturate, silence,
                 sweep_filter)

LOOP = 10.0          # seconds per ambience loop
BED_FADE = 1.2       # circular crossfade length for continuous beds
EVENT_TAIL = 3.0     # overhang rendered for event layers so their tails fold back


def bed_layer(build_fn) -> np.ndarray:
    """Render a continuous texture and close it into an exact LOOP-length cycle."""
    raw = build_fn(LOOP + BED_FADE)
    return dsp.crossfade_loop(raw, BED_FADE)


def event_layer(build_fn) -> np.ndarray:
    """Render sparse events plus their tails and fold the overhang onto the head."""
    raw = build_fn(LOOP + EVENT_TAIL)
    return dsp.make_seamless(raw, LOOP)


def stereo_place(dest: np.ndarray, mono: np.ndarray, at_s: float, position: float,
                 gain_amt: float = 1.0):
    """Place a mono event into a stereo buffer at a pan position."""
    st = dsp.pan(mono, position) * gain_amt
    return place(dest, st, at_s)


_IR_OPEN = None
_IR_CAVE = None
_IR_TOWN = None


def irs():
    global _IR_OPEN, _IR_CAVE, _IR_TOWN
    if _IR_OPEN is None:
        _IR_OPEN = dsp.make_ir(1.2, decay=6.0, predelay_ms=18, damp_hz=5000, early=7,
                               seed=901, stereo=False)
        _IR_CAVE = dsp.make_ir(3.6, decay=2.0, predelay_ms=40, damp_hz=2400, early=16,
                               diffusion=1.0, seed=902, stereo=False)
        _IR_TOWN = dsp.make_ir(2.0, decay=4.0, predelay_ms=24, damp_hz=3600, early=12,
                               seed=903, stereo=False)
    return _IR_OPEN, _IR_CAVE, _IR_TOWN


# --------------------------------------------------------------------------------------
# element generators
# --------------------------------------------------------------------------------------


def bird_call(species: int, rng) -> np.ndarray:
    """
    Four call shapes rather than one randomised chirp, because a dawn chorus is
    recognisably several different birds repeating their own motif.

    0: a rising two-note whistle       2: a descending warble
    1: a fast trill of 5-9 pips        3: a single long pure whistle
    """
    if species == 0:
        dur = rng.uniform(0.16, 0.26)
        f0 = rng.uniform(2100, 3200)
        pitch = np.concatenate([
            expo(dur * 0.45, f0, f0 * 1.32),
            expo(dur * 0.55, f0 * 1.32, f0 * 1.5),
        ])
        tone = osc_sine(dsp.fit_curve(pitch, n_samples(dur)), seconds=dur)
        env = dsp.adsr(dur, 0.012, 0.05, 0.7, 0.09, curve=1.6)
        out = tone * env
    elif species == 1:
        pips = int(rng.integers(5, 10))
        gap = rng.uniform(0.032, 0.05)
        dur = pips * gap + 0.06
        out = np.zeros(n_samples(dur))
        f0 = rng.uniform(3000, 4600)
        for i in range(pips):
            p = f0 * (1.0 + 0.05 * math.sin(i * 1.7))
            pip = osc_sine(expo(0.028, p, p * 1.12), seconds=0.028)
            pip *= perc_env(0.028, 0.004, 4.0)
            place(out, pip * rng.uniform(0.7, 1.0), i * gap)
    elif species == 2:
        dur = rng.uniform(0.3, 0.45)
        f0 = rng.uniform(2600, 3600)
        length = n_samples(dur)
        t = np.linspace(0, 1, length)
        warble = f0 * (1.0 - 0.35 * t) * (1.0 + 0.06 * np.sin(2 * np.pi * 14.0 * t))
        out = osc_sine(warble, length=length) * dsp.adsr(dur, 0.015, 0.1, 0.6, 0.18, 1.5)
    else:
        dur = rng.uniform(0.35, 0.55)
        f0 = rng.uniform(1900, 2700)
        out = osc_sine(expo(dur, f0, f0 * 1.06), seconds=dur)
        out *= dsp.adsr(dur, 0.05, 0.12, 0.8, 0.2, curve=1.3)

    # a little second harmonic and breath keeps it from being a pure test tone
    out = out + osc_sine(np.full(out.shape[0], 1.0), length=out.shape[0]) * 0.0
    out += bandpass(noise_white(length=out.shape[0], seed=int(rng.integers(1e6))),
                    3000, 9000, 2) * 0.06 * np.abs(out)
    return out * 0.5


def cricket(rng, seconds: float) -> np.ndarray:
    """
    A cricket is an amplitude-gated tone near 4.5 kHz, chirped in bursts of 3-5.
    Gating the tone rather than enveloping it is what produces the dry rasp.
    """
    f = rng.uniform(3900, 5200)
    pulses = int(rng.integers(3, 6))
    pulse_len = rng.uniform(0.016, 0.024)
    gap = pulse_len * rng.uniform(1.6, 2.2)
    dur = pulses * gap + 0.05
    out = np.zeros(n_samples(dur))
    for i in range(pulses):
        length = n_samples(pulse_len)
        tone = osc_sine(f, length=length) + osc_sine(f * 2.0, length=length) * 0.3
        buzz = 0.55 + 0.45 * np.sign(np.sin(2 * np.pi * 260.0 * np.arange(length) / SR))
        tone *= buzz * np.hanning(length)
        place(out, tone * rng.uniform(0.7, 1.0), i * gap)
    return out * 0.35


def drip(rng) -> np.ndarray:
    """A cave drip: an impact click into a fast upward pitch bend, which is the
    resonance of the cavity shrinking as the droplet merges."""
    dur = rng.uniform(0.1, 0.18)
    f0 = rng.uniform(700, 1900)
    length = n_samples(dur)
    tone = osc_sine(expo(dur, f0, f0 * rng.uniform(1.6, 2.4)), length=length)
    tone *= perc_env(dur, 0.0012, 5.0)
    tick = bandpass(noise_white(length=length, seed=int(rng.integers(1e6))), 2000, 9000, 2)
    tick *= perc_env(dur, 0.0004, 16.0) * 0.35
    return (tone + tick) * rng.uniform(0.4, 1.0) * 0.6


def voice_babble(rng, seconds: float) -> np.ndarray:
    """
    Distant crowd: formant-filtered noise gated at syllable rate. Never intelligible
    -- the formants move but there are no consonants, which is exactly how a crowd
    two streets away sounds.
    """
    length = n_samples(seconds)
    src = dsp.noise_pink(length=length, seed=int(rng.integers(1e6)))
    f1 = rng.uniform(420, 780)
    f2 = rng.uniform(1050, 1650)
    v = dsp.biquad(src, "peak", f1, 4.0, 14.0)
    v = dsp.biquad(v, "peak", f2, 5.0, 11.0)
    v = lowpass(v, 2400, 2)
    syll = np.abs(np.sin(2 * np.pi * rng.uniform(2.5, 4.5) * np.arange(length) / SR
                         + rng.random() * 6.28)) ** 2.0
    env = dsp.adsr(seconds, 0.25, 0.3, 0.8, 0.35)
    return v * syll * env * 0.5


# --------------------------------------------------------------------------------------
# layers
# --------------------------------------------------------------------------------------


def amb_birdsong():
    """Dawn/day chorus: four species, 26 calls, panned and set back in the open IR."""

    def build(total):
        out = np.zeros((n_samples(total), 2))
        rng = np.random.default_rng(1001)
        for _ in range(26):
            call = bird_call(int(rng.integers(0, 4)), rng)
            wet = dsp.reverb(call, irs()[0], 0.34)
            wet = wet if wet.ndim == 1 else np.mean(wet, axis=1)
            # distance: further birds are quieter and duller
            dist = rng.random()
            wet = lowpass(wet, 12000 - 8000 * dist, 2) * (1.0 - 0.75 * dist)
            stereo_place(out, wet, rng.uniform(0.0, LOOP - 0.1), rng.uniform(-0.85, 0.85),
                         rng.uniform(0.5, 1.0))
        return out

    return event_layer(build), "Daytime birdsong. Route and lakeside, day only."


def amb_wind_grass():
    """Wind through grass: a gusting band of noise plus granular blade movement."""

    def build(total):
        length = n_samples(total)
        # Gusts run at about a third of a hertz: two or three swells across the loop.
        gust = dsp.drift_unipolar(length, rate_hz=0.32, seed=1002, floor=0.42)

        base = dsp.noise_pink(length=length, seed=1003)
        cutoff = 380.0 * (2.0 ** (1.6 * gust))
        body = sweep_filter(base, cutoff, q=0.9, mode="bp", block=256) * gust

        def blade(d, r):
            m = max(8, n_samples(min(d, 0.02)))
            return bandpass(r.standard_normal(m), r.uniform(1600, 3800), r.uniform(4000, 11000), 2)

        rustle = dsp.granular(blade, total, grain_ms=26.0, density=260.0, pitch_jitter=0.5,
                              seed=1004, pan_spread=0.9)
        rustle *= (0.3 + 0.7 * gust)[:, None]
        out = dsp.mono_to_stereo(body) * 0.5 + rustle * 0.55
        return highpass(out, 45.0, 2)

    return bed_layer(build), "Wind moving through grass. Route and lakeside beds."


def amb_wind_high():
    """A thinner, colder wind for night and exposed ground."""

    def build(total):
        length = n_samples(total)
        # faster and more restless than the grass wind, and it opens further
        gust = dsp.drift_unipolar(length, rate_hz=0.55, seed=1005, floor=0.32, partials=6)
        base = dsp.noise_pink(length=length, seed=1006)
        cutoff = 700.0 * (2.0 ** (2.2 * gust))
        body = sweep_filter(base, cutoff, q=1.6, mode="bp", block=256) * gust
        whistle = sweep_filter(base, cutoff * 3.2, q=7.0, mode="bp", block=256) * gust * 0.25
        wide = dsp.widen(dsp.mono_to_stereo(body * 0.6 + whistle), 1.6)
        return highpass(wide, 60.0, 2)

    return bed_layer(build), "Thin high wind. Night route, cliff edges, exposed ground."


def amb_water_lapping():
    """Shore water: slow surges with granular ripple detail on top."""

    def build(total):
        length = n_samples(total)
        # slow shore surges -- roughly one every four seconds
        surge = dsp.drift_unipolar(length, rate_hz=0.26, seed=1007, floor=0.3, partials=4)
        base = dsp.noise_pink(length=length, seed=1008)
        body = sweep_filter(base, 260.0 + 1500.0 * surge, q=0.8, mode="bp", block=256)
        body *= surge

        def ripple(d, r):
            m = max(10, n_samples(min(d, 0.05)))
            g = bandpass(r.standard_normal(m), r.uniform(600, 2200), r.uniform(2500, 8000), 2)
            return g * np.linspace(1.0, 0.2, m)

        detail = dsp.granular(ripple, total, grain_ms=48.0, density=70.0, pitch_jitter=0.4,
                              seed=1009, pan_spread=0.85)
        out = dsp.mono_to_stereo(body) * 0.6 + detail * 0.5
        return highpass(out, 50.0, 2)

    return bed_layer(build), "Lake water lapping at the shore. Lakeside bed."


def amb_waterfall():
    """
    Mono on purpose: this is a point source and AmbienceDirector attaches it to a
    3D-positioned emitter, where a stereo clip would be collapsed by Unity anyway.
    Broadband, with a heavy low column and a slowly moving spray band on top.
    """

    def build(total):
        length = n_samples(total)
        column = lowpass(dsp.noise_brown(length=length, seed=1010), 340, 2) * 1.2
        mid = bandpass(dsp.noise_pink(length=length, seed=1011), 300, 2600, 2)
        spray_c = 3600.0 * (2.0 ** (0.45 * dsp.drift(length, 0.5, seed=1012, partials=4)))
        spray = sweep_filter(noise_white(length=length, seed=1012), spray_c, q=1.1,
                             mode="bp", block=256)
        churn = dsp.drift_unipolar(length, rate_hz=0.35, seed=1030, floor=0.78, partials=3)
        out = (column * 0.7 + mid * 0.8 + spray * 0.35) * churn
        return highpass(saturate(out, 1.2), 35.0, 2)

    return bed_layer(build), "Waterfall. Mono point source for a 3D emitter at the falls."


def amb_cave_drips():
    """Sparse drips in a long cave reverb. The spacing is the whole effect."""

    def build(total):
        out = np.zeros((n_samples(total), 2))
        rng = np.random.default_rng(1013)
        for _ in range(17):
            d = drip(rng)
            wet = dsp.reverb(fit(d, n_samples(1.8)), irs()[1], 0.55)
            wet = wet if wet.ndim == 1 else np.mean(wet, axis=1)
            stereo_place(out, wet, rng.uniform(0.0, LOOP - 0.05), rng.uniform(-0.9, 0.9),
                         rng.uniform(0.35, 1.0))
        return out

    return event_layer(build), "Cave water drips with cave reverb. Cave bed, event layer."


def amb_cave_rumble():
    """Sub-bass room tone: what a large enclosed volume sounds like with nothing in it."""

    def build(total):
        length = n_samples(total)
        low = lowpass(dsp.noise_brown(length=length, seed=1014), 110, 2)
        t = np.linspace(0, 2 * math.pi * 2.0, length)
        breathe = 0.6 + 0.4 * np.sin(t)
        air = bandpass(dsp.noise_pink(length=length, seed=1015), 180, 900, 2) * 0.18
        # two faint room modes give the space a size
        modes = (dsp.biquad(low, "peak", 47.0, 12.0, 8.0)
                 + dsp.biquad(low, "peak", 73.0, 14.0, 6.0) * 0.6)
        out = dsp.mono_to_stereo((modes * 0.9 + air) * breathe)
        out = dsp.widen(out, 1.3)
        return highpass(out, 22.0, 2)

    return bed_layer(build), "Low cave rumble and room tone. Cave bed, always on."


def amb_rain():
    """Dense droplet impacts over a hiss bed, with a few close heavy drops."""

    def build(total):
        length = n_samples(total)
        hiss = bandpass(dsp.noise_pink(length=length, seed=1016), 900, 9000, 2) * 0.35

        def droplet(d, r):
            m = max(6, n_samples(min(d, 0.01)))
            g = r.standard_normal(m)
            return bandpass(g, r.uniform(1500, 4000), r.uniform(5000, 14000), 2) * np.linspace(1, 0, m)

        patter = dsp.granular(droplet, total, grain_ms=7.0, density=900.0, pitch_jitter=0.5,
                              seed=1017, pan_spread=0.95)
        heavy = np.zeros((length, 2))
        rng = np.random.default_rng(1018)
        for _ in range(40):
            d = drip(rng) * 0.35
            stereo_place(heavy, lowpass(d, 6000, 2), rng.uniform(0, total - 0.2),
                         rng.uniform(-0.9, 0.9), rng.uniform(0.2, 0.6))
        # rain is not constant: intensity breathes over about four seconds
        intensity = dsp.drift_unipolar(length, rate_hz=0.24, seed=1031, floor=0.62, partials=3)
        out = dsp.mono_to_stereo(hiss) + patter * 0.5 + heavy * 0.5
        out *= intensity[:, None]
        return highpass(out, 90.0, 2)

    return bed_layer(build), "Rain. Weather layer, faded in on GameEvents.WeatherChanged to Rain."


def amb_night_insects():
    """Crickets in the near field plus a continuous cicada shimmer further back."""

    def build(total):
        length = n_samples(total)
        out = np.zeros((length, 2))
        rng = np.random.default_rng(1019)
        for _ in range(34):
            c = cricket(rng, total)
            dist = rng.random()
            c = lowpass(c, 14000 - 9000 * dist, 2) * (1.0 - 0.7 * dist)
            stereo_place(out, c, rng.uniform(0.0, LOOP - 0.1), rng.uniform(-0.95, 0.95),
                         rng.uniform(0.4, 1.0))
        # cicada bed: a narrow high band amplitude-modulated at ~40 Hz
        t = np.arange(length) / SR
        shimmer = bandpass(noise_white(length=length, seed=1020), 5200, 7600, 2)
        shimmer *= 0.6 + 0.4 * np.sin(2 * np.pi * 41.0 * t)
        out += dsp.mono_to_stereo(shimmer) * 0.09
        return out

    return event_layer(build), "Night insects: crickets and a cicada bed. Night, outdoors."


def amb_town_murmur():
    """Distant crowd, an occasional door and a far-off dog. Warm, never intelligible."""

    def build(total):
        length = n_samples(total)
        out = np.zeros((length, 2))
        rng = np.random.default_rng(1021)
        for _ in range(9):
            v = voice_babble(rng, rng.uniform(1.2, 2.6))
            wet = dsp.reverb(v, irs()[2], 0.42)
            wet = wet if wet.ndim == 1 else np.mean(wet, axis=1)
            stereo_place(out, lowpass(wet, 2600, 2), rng.uniform(0, LOOP - 1.0),
                         rng.uniform(-0.8, 0.8), rng.uniform(0.35, 0.7))
        # a low continuous hubbub so the gaps between phrases are not empty
        hub = lowpass(dsp.noise_pink(length=length, seed=1022), 900, 2)
        hub = dsp.biquad(hub, "peak", 520.0, 2.0, 7.0) * 0.12
        out += dsp.mono_to_stereo(hub)
        # two distant events per loop: a door and a bird over the rooftops
        door = dsp.reverb(fit(_far_door(rng), n_samples(1.2)), irs()[2], 0.5)
        door = door if door.ndim == 1 else np.mean(door, axis=1)
        stereo_place(out, lowpass(door, 2000, 2), 3.4, -0.55, 0.4)
        return out

    return event_layer(build), "Town crowd murmur and distant activity. Town bed."


def _far_door(rng) -> np.ndarray:
    body = np.zeros(n_samples(0.5))
    thump = osc_sine(expo(0.25, 160, 80), seconds=0.25) * perc_env(0.25, 0.002, 4.0)
    place(body, thump * 0.8, 0.0)
    return body


def amb_thunder(variant: int = 0):
    """
    Thunder as a one-shot, not a loop.

    A sharp crack whose spectral centre falls fast, followed by a rumble tail built
    from low-passed noise pushed through the open-air IR twice, which is what gives
    the roll its distance and its irregular restatements.
    """
    seeds = [(1023, 0.22, 1.0), (1024, 0.55, 0.75)]
    seed, crack_amt, bright = seeds[variant % 2]
    dur = 4.2 if variant == 0 else 5.0
    length = n_samples(dur)
    out = np.zeros(length)
    rng = np.random.default_rng(seed)

    crack = sweep_filter(noise_white(seconds=0.5, seed=seed), expo(0.5, 6000 * bright, 300),
                         q=0.8, mode="bp")
    crack *= perc_env(0.5, 0.0015, 3.0) * crack_amt
    place(out, crack, 0.0)

    rumble = lowpass(dsp.noise_brown(length=length, seed=seed + 1), 320 * bright, 2)
    swell = np.zeros(length)
    # three overlapping restatements make the roll uneven rather than a single decay
    for at, amp, sp in [(0.05, 1.0, 1.4), (0.9, 0.75, 2.2), (2.0, 0.5, 2.6)]:
        seg = n_samples(sp)
        env = np.exp(-np.linspace(0, 3.0, seg)) * np.clip(np.linspace(0, 6, seg), 0, 1)
        place(swell, fit(env, seg) * amp, at)
    out = out + rumble * swell * 1.4
    out = dsp.reverb(out, irs()[0], 0.35)
    out = out if out.ndim == 1 else np.mean(out, axis=1)
    out = saturate(out, 1.4)
    out = dsp.widen(dsp.mono_to_stereo(out), 1.5)
    out = highpass(out, 25.0, 2)
    out = dsp.fade_out(dsp.zero_edges(out), 0.5)
    return dsp.normalize(out, 0.88)


# --------------------------------------------------------------------------------------
# registry
# --------------------------------------------------------------------------------------


def _finalise(sig, peak=0.72):
    """Ambience is mixed low: these are beds, and the director sums several at once."""
    return dsp.normalize(dsp.soft_clip(sig, 0.95), peak)


def _wrap(fn, peak=0.72):
    def build():
        sig, desc = fn()
        return _finalise(sig, peak), desc

    return build


LAYERS = {
    "Amb_Birdsong": (amb_birdsong, 0.62),
    "Amb_WindGrass": (amb_wind_grass, 0.70),
    "Amb_WindHigh": (amb_wind_high, 0.62),
    "Amb_WaterLapping": (amb_water_lapping, 0.70),
    "Amb_Waterfall": (amb_waterfall, 0.80),
    "Amb_CaveDrips": (amb_cave_drips, 0.66),
    "Amb_CaveRumble": (amb_cave_rumble, 0.72),
    "Amb_Rain": (amb_rain, 0.74),
    "Amb_NightInsects": (amb_night_insects, 0.60),
    "Amb_TownMurmur": (amb_town_murmur, 0.60),
}

ONESHOTS = {
    "Amb_Thunder_01": (lambda: (amb_thunder(0), "Thunder crack and roll, close. Rain weather."), 0.88),
    "Amb_Thunder_02": (lambda: (amb_thunder(1), "Thunder, distant roll. Rain weather."), 0.86),
}


def build_all():
    """name -> (signal, loop_flag, description)"""
    out = {}
    for name, (fn, peak) in LAYERS.items():
        sig, desc = fn()
        out[name] = (_finalise(sig, peak), True, desc)
    for name, (fn, peak) in ONESHOTS.items():
        sig, desc = fn()
        out[name] = (_finalise(sig, peak), False, desc)
    return out
