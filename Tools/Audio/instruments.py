"""
Instrument voices and the little sequencer the compositions are written against.

Each voice is a function ``(midi, seconds, velocity, **kw) -> mono float array``.
They are deliberately built from different synthesis families -- physical model,
FM, subtractive stack, formant -- because an arrangement where every layer is the
same subtractive patch reads as one thick synth rather than as an ensemble.
"""

from __future__ import annotations

import math

import numpy as np

import dsp
from dsp import (SR, adsr, apply_env, biquad, chorus, expo, fit, highpass, karplus,
                 lowpass, mix, noise_white, n_samples, note_hz, osc_fm, osc_pulse,
                 osc_saw, osc_sine, osc_tri, perc_env, ramp, saturate, supersaw,
                 sweep_filter)

# --------------------------------------------------------------------------------------
# pitched voices
# --------------------------------------------------------------------------------------


def pluck(midi: float, seconds: float, vel: float = 1.0, brightness: float = 0.55,
          damping: float = 0.5, seed: int = 0) -> np.ndarray:
    """Nylon-ish plucked string. The route and town themes lean on this."""
    f = note_hz(midi)
    body = karplus(f, seconds, damping=damping, brightness=brightness * vel, seed=seed)
    # a touch of the fundamental restores low end the KS loop filter eats
    sub = osc_sine(f, seconds) * perc_env(seconds, 0.004, 3.2) * 0.16
    out = body + sub
    out = biquad(out, "peak", f * 2.0, 1.2, 3.0)
    return out * vel * 0.75


def harp(midi: float, seconds: float, vel: float = 1.0, seed: int = 0) -> np.ndarray:
    """Brighter, longer-ringing pluck for the lakeside theme."""
    out = karplus(note_hz(midi), seconds, damping=0.22, brightness=0.8, seed=seed, pick_pos=0.12)
    out = biquad(out, "highshelf", 3000.0, 0.7, 4.0)
    return out * vel * 0.8


def marimba(midi: float, seconds: float, vel: float = 1.0) -> np.ndarray:
    """FM mallet. Index falls with the envelope, so the strike is bright and the tail is pure."""
    f = note_hz(midi)
    dur = min(seconds, 1.1)
    idx = expo(dur, 5.0 * vel + 1.5, 0.25)
    tone = osc_fm(f, 3.0, idx, dur)
    env = perc_env(dur, 0.0016, 3.4)
    body = tone * env
    # the wooden bar's second partial, tuned a 4th above two octaves up
    partial = osc_sine(f * 3.98, dur) * perc_env(dur, 0.001, 6.0) * 0.22
    return fit((body + partial) * vel * 0.7, n_samples(seconds))


def bell(midi: float, seconds: float, vel: float = 1.0) -> np.ndarray:
    """Inharmonic FM bell. Used for the capture sting and the town clock colour."""
    f = note_hz(midi)
    idx = expo(seconds, 7.0 * vel, 0.4)
    tone = osc_fm(f, 1.41, idx, seconds, feedback=0.15)
    env = perc_env(seconds, 0.003, 1.6)
    partials = sum(
        osc_sine(f * r, seconds) * perc_env(seconds, 0.002, 1.4 + i * 0.7) * a
        for i, (r, a) in enumerate([(2.76, 0.18), (5.4, 0.09), (8.9, 0.04)])
    )
    return (tone * env + partials) * vel * 0.55


def pad(midi: float, seconds: float, vel: float = 1.0, cutoff: float = 1900.0,
        seed: int = 0) -> np.ndarray:
    """Wide detuned pad. Slow filter opening keeps sustained chords from sitting still."""
    f = note_hz(midi)
    raw = supersaw(f, seconds, voices=5, detune_cents=11.0, seed=seed)
    raw += osc_saw(f * 0.5, seconds) * 0.3
    cur = expo(seconds, cutoff * 0.55, cutoff)
    filt = sweep_filter(raw, cur, q=0.8, mode="lp")
    env = adsr(seconds, attack=min(0.35, seconds * 0.3), decay=0.3, sustain=0.75,
               release=min(0.6, seconds * 0.4), curve=1.6)
    return filt * env * vel * 0.34


def strings(midi: float, seconds: float, vel: float = 1.0, seed: int = 0) -> np.ndarray:
    """Saw ensemble with per-player pitch drift -- the drift is what stops it sounding like one saw."""
    f = note_hz(midi)
    rng = np.random.default_rng(seed + int(midi))
    length = n_samples(seconds)
    out = np.zeros(length)
    for i in range(4):
        drift = 1.0 + rng.uniform(-0.004, 0.004)
        vib_rate = rng.uniform(4.2, 5.6)
        vib = 1.0 + 0.0035 * np.sin(2 * np.pi * vib_rate * np.arange(length) / SR + rng.random() * 6.28)
        out += osc_saw(f * drift * vib, length=length, phase0=rng.random())
    out /= 4.0
    out = lowpass(out, 3200.0, 2)
    env = adsr(seconds, attack=min(0.16, seconds * 0.25), decay=0.2, sustain=0.8,
               release=min(0.45, seconds * 0.35), curve=1.4)
    return out * env * vel * 0.3


def brass(midi: float, seconds: float, vel: float = 1.0) -> np.ndarray:
    """
    Filter-envelope brass. The cutoff overshoot at the top of the attack is the
    'blat' that makes the trainer battle and the victory fanfare feel heroic.
    """
    f = note_hz(midi)
    raw = supersaw(f, seconds, voices=3, detune_cents=6.0, seed=int(midi))
    raw = raw + osc_pulse(f, seconds, width=0.42) * 0.35
    atk = min(0.06, seconds * 0.25)
    cur = np.concatenate([
        expo(atk, f * 2.0, f * 9.0),
        expo(max(0.001, seconds - atk), f * 9.0, f * 3.2),
    ])
    filt = sweep_filter(raw, dsp.fit_curve(cur, n_samples(seconds)), q=1.5, mode="lp")
    env = adsr(seconds, attack=atk * 0.7, decay=0.12, sustain=0.72,
               release=min(0.25, seconds * 0.35), curve=1.5)
    out = saturate(filt * env, 1.7)
    return out * vel * 0.3


def lead_square(midi: float, seconds: float, vel: float = 1.0, vibrato: float = 0.5) -> np.ndarray:
    """Pulse lead with delayed vibrato and slow PWM. Carries the route and battle melodies."""
    f = note_hz(midi)
    length = n_samples(seconds)
    t = np.arange(length) / SR
    vib_depth = 0.006 * vibrato
    onset = np.clip((t - 0.12) / 0.25, 0.0, 1.0)
    vib = 1.0 + vib_depth * onset * np.sin(2 * np.pi * 5.4 * t)
    width = 0.5 + 0.18 * np.sin(2 * np.pi * 0.35 * t + 1.1)
    raw = osc_pulse(f * vib, length=length, width=width)
    raw += osc_pulse(f * vib * 1.005, length=length, width=0.28) * 0.4
    raw = lowpass(raw, 5200.0, 2)
    env = adsr(seconds, attack=0.012, decay=0.09, sustain=0.72,
               release=min(0.14, seconds * 0.4), curve=1.8)
    return raw * env * vel * 0.26


def flute(midi: float, seconds: float, vel: float = 1.0, seed: int = 0) -> np.ndarray:
    """Sine plus breath noise; the breath transient is most of the realism."""
    f = note_hz(midi)
    length = n_samples(seconds)
    t = np.arange(length) / SR
    vib = 1.0 + 0.008 * np.clip((t - 0.18) / 0.3, 0, 1) * np.sin(2 * np.pi * 4.8 * t)
    tone = osc_sine(f * vib, length=length)
    tone += osc_sine(f * 2 * vib, length=length) * 0.13
    tone += osc_sine(f * 3 * vib, length=length) * 0.05
    breath = dsp.bandpass(noise_white(length=length, seed=seed), f * 1.4, f * 5.0, 2)
    breath *= np.exp(-t * 9.0) * 0.5 + 0.06
    env = adsr(seconds, attack=min(0.07, seconds * 0.3), decay=0.12, sustain=0.85,
               release=min(0.2, seconds * 0.35), curve=1.3)
    return (tone + breath * 0.35) * env * vel * 0.3


def choir(midi: float, seconds: float, vel: float = 1.0, seed: int = 0) -> np.ndarray:
    """Formant-filtered saws. Two vowel bands is enough to read as voices in a bed."""
    f = note_hz(midi)
    raw = supersaw(f, seconds, voices=4, detune_cents=16.0, seed=seed)
    v1 = biquad(raw, "peak", 620.0, 3.5, 11.0)
    v2 = biquad(v1, "peak", 1180.0, 4.0, 9.0)
    out = lowpass(v2, 2600.0, 2)
    env = adsr(seconds, attack=min(0.5, seconds * 0.35), decay=0.3, sustain=0.8,
               release=min(0.7, seconds * 0.4), curve=1.4)
    return out * env * vel * 0.22


def bass(midi: float, seconds: float, vel: float = 1.0, drive: float = 1.8) -> np.ndarray:
    """Saw + sub with a snappy filter envelope. Every theme's floor."""
    f = note_hz(midi)
    raw = osc_saw(f, seconds) + osc_pulse(f, seconds, width=0.35) * 0.4
    sub = osc_sine(f * 0.5, seconds) * 0.55
    cur = expo(seconds, min(4200.0, f * 14.0), max(140.0, f * 2.6))
    filt = sweep_filter(raw, cur, q=1.4, mode="lp")
    env = adsr(seconds, attack=0.006, decay=0.1, sustain=0.65,
               release=min(0.1, seconds * 0.4), curve=2.0)
    out = saturate((filt + sub) * env, drive)
    return out * vel * 0.38


def sub_bass(midi: float, seconds: float, vel: float = 1.0) -> np.ndarray:
    f = note_hz(midi)
    env = adsr(seconds, attack=0.01, decay=0.08, sustain=0.8, release=0.08, curve=1.6)
    return osc_sine(f, seconds) * env * vel * 0.5


def organ(midi: float, seconds: float, vel: float = 1.0) -> np.ndarray:
    """Drawbar-style additive organ for the town theme's warmth."""
    f = note_hz(midi)
    out = np.zeros(n_samples(seconds))
    for ratio, amp in [(0.5, 0.32), (1.0, 1.0), (2.0, 0.42), (3.0, 0.2), (4.0, 0.14), (6.0, 0.08)]:
        out += osc_sine(f * ratio, seconds) * amp
    env = adsr(seconds, attack=0.02, decay=0.05, sustain=0.9, release=min(0.12, seconds * 0.3))
    return out * env * vel * 0.16


# --------------------------------------------------------------------------------------
# drums
# --------------------------------------------------------------------------------------


def kick(seconds: float = 0.42, vel: float = 1.0, tune: float = 52.0) -> np.ndarray:
    """Pitch-swept sine with a noise click and asymmetric saturation for weight."""
    pitch = expo(seconds, tune * 3.4, tune * 0.92)
    body = osc_sine(pitch, seconds) * perc_env(seconds, 0.0012, 2.4)
    click = dsp.bandpass(noise_white(seconds=0.02, seed=5), 1800, 7000, 2)
    click = fit(click * perc_env(0.02, 0.0004, 7.0), n_samples(seconds)) * 0.42
    out = saturate(body * 1.1 + click, 2.2, asym=0.12)
    return dsp.highpass(out, 26.0, 1) * vel * 0.95


def snare(seconds: float = 0.24, vel: float = 1.0, tone_hz: float = 190.0) -> np.ndarray:
    """Two tuned shells plus a band-passed noise 'wire' layer with its own faster decay."""
    shell = (osc_sine(tone_hz, seconds) + osc_sine(tone_hz * 1.48, seconds) * 0.6)
    shell *= perc_env(seconds, 0.001, 5.0) * 0.55
    wires = dsp.bandpass(noise_white(seconds=seconds, seed=9), 1500, 9500, 2)
    wires *= perc_env(seconds, 0.0008, 3.4)
    out = saturate(shell + wires * 0.75, 1.5)
    return dsp.highpass(out, 140.0, 2) * vel * 0.7


def rimshot(seconds: float = 0.09, vel: float = 1.0) -> np.ndarray:
    body = dsp.resonator(noise_white(seconds=seconds, seed=13), 1750.0, 26.0)
    return body * perc_env(seconds, 0.0004, 9.0) * vel * 2.4


def hat(seconds: float = 0.055, vel: float = 1.0, open_amt: float = 0.0,
        seed: int = 4) -> np.ndarray:
    """Metallic hat: a stack of inharmonic squares through a high band-pass, as in a 606/808."""
    length = n_samples(seconds)
    src = np.zeros(length)
    for r in (1.0, 1.4471, 1.6170, 1.9265, 2.5028, 2.6637):
        src += osc_pulse(320.0 * r, length=length, width=0.5)
    src = dsp.highpass(src, 7200.0, 2)
    src += noise_white(length=length, seed=seed) * 0.5
    src = dsp.bandpass(src, 6500, 15000, 2)
    env = perc_env(seconds, 0.0004, 3.0 - open_amt * 2.4)
    return src * env * vel * 0.22


def tom(seconds: float = 0.35, vel: float = 1.0, tune: float = 130.0) -> np.ndarray:
    pitch = expo(seconds, tune * 1.6, tune * 0.85)
    body = osc_sine(pitch, seconds) * perc_env(seconds, 0.0015, 3.0)
    skin = dsp.bandpass(noise_white(seconds=seconds, seed=17), 250, 2200, 2)
    skin *= perc_env(seconds, 0.001, 8.0) * 0.3
    return saturate(body + skin, 1.4) * vel * 0.7


def shaker(seconds: float = 0.09, vel: float = 1.0, seed: int = 21) -> np.ndarray:
    src = dsp.bandpass(noise_white(seconds=seconds, seed=seed), 4200, 12000, 2)
    env = perc_env(seconds, 0.008, 4.0)
    return src * env * vel * 0.3


def tambourine(seconds: float = 0.28, vel: float = 1.0, seed: int = 23) -> np.ndarray:
    jingles = np.zeros(n_samples(seconds))
    rng = np.random.default_rng(seed)
    for _ in range(9):
        f = rng.uniform(5200, 11500)
        jingles += dsp.resonator(noise_white(seconds=seconds, seed=int(rng.integers(1e6))), f, 40.0)
    env = perc_env(seconds, 0.001, 2.2)
    return jingles * env * vel * 0.16


def crash(seconds: float = 1.6, vel: float = 1.0, seed: int = 27) -> np.ndarray:
    src = noise_white(seconds=seconds, seed=seed)
    metal = np.zeros_like(src)
    rng = np.random.default_rng(seed)
    for _ in range(14):
        metal += dsp.resonator(src, rng.uniform(2600, 13000), 22.0)
    metal = dsp.highpass(metal, 2200.0, 2)
    env = perc_env(seconds, 0.002, 1.1)
    return metal * env * vel * 0.1


def timpani(seconds: float = 1.1, vel: float = 1.0, midi: float = 40.0) -> np.ndarray:
    f = note_hz(midi)
    pitch = expo(seconds, f * 1.25, f)
    body = osc_sine(pitch, seconds) * perc_env(seconds, 0.004, 1.3)
    body += osc_sine(pitch * 1.51, seconds) * perc_env(seconds, 0.003, 2.2) * 0.3
    strike = dsp.bandpass(noise_white(seconds=0.05, seed=31), 200, 3000, 2)
    strike = fit(strike * perc_env(0.05, 0.001, 6.0), n_samples(seconds)) * 0.3
    return saturate(body + strike, 1.3) * vel * 0.75


# --------------------------------------------------------------------------------------
# sequencer
# --------------------------------------------------------------------------------------


class Track:
    """
    A monophonic or chordal part.

    Notes are written as ``(beat, pitch, duration_beats, velocity)`` where pitch is a
    note name, a MIDI number, a list of either (a chord), or None for a rest. That
    keeps the compositions in music.py readable as music rather than as buffer maths.
    """

    def __init__(self, voice, bpm: float, gain: float = 1.0, pan: float = 0.0,
                 release_beats: float = 0.0, humanise: float = 0.0, seed: int = 0):
        self.voice = voice
        self.bpm = bpm
        self.gain = gain
        self.pan = pan
        self.release_beats = release_beats
        self.humanise = humanise
        self.rng = np.random.default_rng(seed)
        self.notes = []

    def add(self, beat, pitch, dur_beats, vel=1.0, **kw):
        self.notes.append((beat, pitch, dur_beats, vel, kw))
        return self

    def extend(self, notes, offset=0.0, vel_scale=1.0):
        for entry in notes:
            beat, pitch, dur, vel = entry[0], entry[1], entry[2], (entry[3] if len(entry) > 3 else 1.0)
            self.add(beat + offset, pitch, dur, vel * vel_scale)
        return self

    def render(self, total_beats: float, tail_beats: float = 4.0) -> np.ndarray:
        spb = 60.0 / self.bpm
        length = n_samples((total_beats + tail_beats) * spb)
        out = np.zeros(length)
        for beat, pitch, dur_beats, vel, kw in self.notes:
            if pitch is None:
                continue
            pitches = pitch if isinstance(pitch, (list, tuple)) else [pitch]
            jitter = self.rng.uniform(-self.humanise, self.humanise) if self.humanise else 0.0
            start = n_samples(max(0.0, (beat + jitter) * spb))
            seconds = (dur_beats + self.release_beats) * spb
            v = vel * (1.0 + (self.rng.uniform(-0.06, 0.06) if self.humanise else 0.0))
            for p in pitches:
                midi = dsp.n(p) if isinstance(p, str) else float(p)
                sig = self.voice(midi, seconds, v, **kw)
                end = min(length, start + sig.shape[0])
                if end > start:
                    out[start:end] += sig[: end - start]
        return out * self.gain


def stereo_mix(parts, pans=None) -> np.ndarray:
    """Pan a list of mono parts into a stereo bed."""
    length = max(p.shape[0] for p in parts)
    out = np.zeros((length, 2))
    pans = pans or [0.0] * len(parts)
    for p, position in zip(parts, pans):
        st = dsp.pan(fit(p, length), position)
        out += st
    return out
