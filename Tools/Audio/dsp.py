"""
Poke Lab procedural audio -- synthesis core.

Everything shipped under Assets/Game/Audio is produced by this library. No sampled
material is used anywhere; the goal is that any sound can be re-tuned by editing a
number here rather than by re-recording an opaque binary.

Conventions
-----------
* Sample rate is 44100 everywhere (Unity import expects it, and the manifest asserts it).
* Signals are float64 numpy arrays. Mono is shape (N,), stereo is shape (N, 2).
* Nothing is normalised until write time; ``write_wav`` applies the headroom policy.

The techniques used are deliberately not "a sine with an envelope":
oscillators are band-limited (polyBLEP) and detuned in stacks, filters are
time-varying state-variable/biquad sweeps run block-wise so cutoff envelopes
actually move, impacts are filtered noise bursts with transient shaping,
reverb is true convolution against a procedurally grown impulse response, and
weight comes from asymmetric saturation rather than raw gain.
"""

from __future__ import annotations

import math
import os
import struct
import wave

import numpy as np
from scipy.signal import butter, fftconvolve, lfilter, lfilter_zi, sosfilt

SR = 44100

# --------------------------------------------------------------------------------------
# basics
# --------------------------------------------------------------------------------------


def n_samples(seconds: float) -> int:
    return int(round(seconds * SR))


def t_axis(seconds: float) -> np.ndarray:
    return np.arange(n_samples(seconds), dtype=np.float64) / SR


def silence(seconds: float, stereo: bool = False) -> np.ndarray:
    n = n_samples(seconds)
    return np.zeros((n, 2) if stereo else n, dtype=np.float64)


def note_hz(midi: float) -> float:
    """MIDI note number to Hz. 69 = A4 = 440."""
    return 440.0 * (2.0 ** ((midi - 69.0) / 12.0))


# Note-name helper so compositions can be written as readable pitch strings.
_PITCH_CLASS = {"C": 0, "D": 2, "E": 4, "F": 5, "G": 7, "A": 9, "B": 11}


def n(name: str) -> float:
    """'A4' -> 69, 'F#3' -> 54, 'Bb5' -> 82. Returns a MIDI number."""
    name = name.strip()
    pc = _PITCH_CLASS[name[0].upper()]
    i = 1
    while i < len(name) and name[i] in "#b":
        pc += 1 if name[i] == "#" else -1
        i += 1
    octave = int(name[i:])
    return float((octave + 1) * 12 + pc)


def db(x: float) -> float:
    """Decibels to linear gain."""
    return 10.0 ** (x / 20.0)


def to_db(x: float) -> float:
    return 20.0 * math.log10(max(x, 1e-12))


def fit(sig: np.ndarray, length: int) -> np.ndarray:
    """Pad with zeros or truncate to exactly ``length`` samples."""
    if sig.ndim == 1:
        out = np.zeros(length, dtype=np.float64)
    else:
        out = np.zeros((length, sig.shape[1]), dtype=np.float64)
    m = min(length, sig.shape[0])
    out[:m] = sig[:m]
    return out


def mix(*signals: np.ndarray) -> np.ndarray:
    """Sum signals of differing lengths, extending to the longest."""
    sigs = [s for s in signals if s is not None and s.shape[0] > 0]
    if not sigs:
        return np.zeros(0)
    stereo = any(s.ndim == 2 for s in sigs)
    length = max(s.shape[0] for s in sigs)
    out = np.zeros((length, 2) if stereo else length, dtype=np.float64)
    for s in sigs:
        if stereo and s.ndim == 1:
            s = mono_to_stereo(s)
        out[: s.shape[0]] += s
    return out


def place(dest: np.ndarray, src: np.ndarray, at_seconds: float) -> np.ndarray:
    """Add ``src`` into ``dest`` at a time offset, clipping at the buffer end."""
    start = n_samples(at_seconds)
    if start >= dest.shape[0] or src.shape[0] == 0:
        return dest
    if dest.ndim == 2 and src.ndim == 1:
        src = mono_to_stereo(src)
    end = min(dest.shape[0], start + src.shape[0])
    dest[start:end] += src[: end - start]
    return dest


def mono_to_stereo(sig: np.ndarray) -> np.ndarray:
    return np.stack([sig, sig], axis=1)


def gain(sig: np.ndarray, amount: float) -> np.ndarray:
    return sig * amount


def concat(*signals: np.ndarray) -> np.ndarray:
    return np.concatenate([s for s in signals if s.shape[0] > 0])


# --------------------------------------------------------------------------------------
# envelopes
# --------------------------------------------------------------------------------------


def adsr(
    seconds: float,
    attack: float = 0.005,
    decay: float = 0.08,
    sustain: float = 0.6,
    release: float = 0.2,
    curve: float = 2.0,
) -> np.ndarray:
    """
    ADSR with exponential-ish decay/release shaping.

    ``curve`` > 1 makes decay and release fall fast then tail off, which is what
    makes plucked and percussive material read as physical rather than synthetic.
    The attack is always at least a couple of samples so nothing clicks.
    """
    total = n_samples(seconds)
    a = max(1, n_samples(attack))
    d = max(1, n_samples(decay))
    r = max(1, n_samples(release))
    s = max(0, total - a - d - r)
    if a + d + r > total:  # very short hit: squeeze proportionally
        scale = total / float(a + d + r)
        a, d, r = max(1, int(a * scale)), max(1, int(d * scale)), max(1, int(r * scale))
        s = max(0, total - a - d - r)

    env = np.zeros(total, dtype=np.float64)
    # smooth (raised-cosine) attack avoids the DC step a linear ramp leaves at t=0
    env[:a] = 0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, a))
    dec = np.linspace(0.0, 1.0, d) ** curve
    env[a : a + d] = 1.0 + (sustain - 1.0) * dec
    env[a + d : a + d + s] = sustain
    rel = np.linspace(0.0, 1.0, r) ** curve
    env[a + d + s : a + d + s + r] = sustain * (1.0 - rel)
    return env


def perc_env(seconds: float, attack: float = 0.001, curve: float = 3.0) -> np.ndarray:
    """One-shot percussive envelope: fast attack, single exponential fall to zero."""
    total = n_samples(seconds)
    a = max(2, n_samples(attack))
    a = min(a, max(2, total // 2))
    env = np.zeros(total, dtype=np.float64)
    env[:a] = 0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, a))
    tail = total - a
    if tail > 0:
        env[a:] = np.exp(-curve * np.linspace(0.0, 1.0, tail) * 4.0)
        env[a:] *= np.linspace(1.0, 0.0, tail) ** 0.5  # guarantee it reaches exact zero
    return env


def ramp(seconds: float, start: float, end: float, curve: float = 1.0) -> np.ndarray:
    x = np.linspace(0.0, 1.0, n_samples(seconds))
    return start + (end - start) * (x**curve)


def expo(seconds: float, start: float, end: float) -> np.ndarray:
    """Geometric interpolation -- the right shape for pitch and cutoff sweeps."""
    x = np.linspace(0.0, 1.0, n_samples(seconds))
    start = max(start, 1e-6)
    end = max(end, 1e-6)
    return start * (end / start) ** x


def fade_in(sig: np.ndarray, seconds: float) -> np.ndarray:
    k = min(n_samples(seconds), sig.shape[0])
    if k <= 1:
        return sig
    w = 0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, k))
    out = sig.copy()
    if sig.ndim == 2:
        out[:k] *= w[:, None]
    else:
        out[:k] *= w
    return out


def fade_out(sig: np.ndarray, seconds: float) -> np.ndarray:
    k = min(n_samples(seconds), sig.shape[0])
    if k <= 1:
        return sig
    w = 0.5 + 0.5 * np.cos(np.linspace(0.0, math.pi, k))
    out = sig.copy()
    if sig.ndim == 2:
        out[-k:] *= w[:, None]
    else:
        out[-k:] *= w
    return out


def apply_env(sig: np.ndarray, env: np.ndarray) -> np.ndarray:
    env = fit(env, sig.shape[0]) if env.shape[0] != sig.shape[0] else env
    if sig.ndim == 2:
        return sig * env[:, None]
    return sig * env


# --------------------------------------------------------------------------------------
# oscillators -- band-limited via polyBLEP, fully vectorised so pitch may sweep
# --------------------------------------------------------------------------------------


def phase_of(freq, seconds: float = None, length: int = None) -> np.ndarray:
    """
    Integrate a (possibly time-varying) frequency into a wrapped 0..1 phase array.
    Passing an array for ``freq`` is how every pitch envelope in this project works.
    """
    if length is None:
        length = n_samples(seconds)
    if np.isscalar(freq):
        f = np.full(length, float(freq))
    else:
        f = fit_curve(np.asarray(freq, dtype=np.float64), length)
    return np.cumsum(f) / SR


def fit_curve(curve: np.ndarray, length: int) -> np.ndarray:
    """Resample a control curve to ``length`` points by linear interpolation."""
    if curve.shape[0] == length:
        return curve
    if curve.shape[0] == 1:
        return np.full(length, curve[0])
    src = np.linspace(0.0, 1.0, curve.shape[0])
    dst = np.linspace(0.0, 1.0, length)
    return np.interp(dst, src, curve)


def _polyblep(t: np.ndarray, dt: np.ndarray) -> np.ndarray:
    """Polynomial band-limited step correction; removes most aliasing from saw/pulse."""
    out = np.zeros_like(t)
    dt = np.maximum(dt, 1e-9)
    m1 = t < dt
    x = t[m1] / dt[m1]
    out[m1] = x + x - x * x - 1.0
    m2 = t > 1.0 - dt
    x = (t[m2] - 1.0) / dt[m2]
    out[m2] = x * x + x + x + 1.0
    return out


def _freq_array(freq, length: int) -> np.ndarray:
    if np.isscalar(freq):
        return np.full(length, float(freq))
    return fit_curve(np.asarray(freq, dtype=np.float64), length)


def osc_sine(freq, seconds: float = None, length: int = None, phase0: float = 0.0) -> np.ndarray:
    if length is None:
        length = n_samples(seconds)
    ph = phase_of(freq, length=length)
    return np.sin(2.0 * np.pi * (ph + phase0))


def osc_tri(freq, seconds: float = None, length: int = None) -> np.ndarray:
    if length is None:
        length = n_samples(seconds)
    ph = phase_of(freq, length=length) % 1.0
    return 4.0 * np.abs(ph - 0.5) - 1.0


def osc_saw(freq, seconds: float = None, length: int = None, phase0: float = 0.0) -> np.ndarray:
    if length is None:
        length = n_samples(seconds)
    f = _freq_array(freq, length)
    ph = (np.cumsum(f) / SR + phase0) % 1.0
    dt = f / SR
    return (2.0 * ph - 1.0) - _polyblep(ph, dt)


def osc_pulse(freq, seconds: float = None, length: int = None, width=0.5) -> np.ndarray:
    if length is None:
        length = n_samples(seconds)
    f = _freq_array(freq, length)
    w = _freq_array(width, length) if not np.isscalar(width) else np.full(length, float(width))
    ph = (np.cumsum(f) / SR) % 1.0
    dt = f / SR
    ph2 = (ph + (1.0 - w)) % 1.0
    sq = np.where(ph < w, 1.0, -1.0)
    return sq - _polyblep(ph, dt) + _polyblep(ph2, dt)


def supersaw(
    freq,
    seconds: float = None,
    length: int = None,
    voices: int = 7,
    detune_cents: float = 14.0,
    spread: float = 1.0,
    seed: int = 0,
) -> np.ndarray:
    """
    Detuned saw stack. The random start phases matter as much as the detune: aligned
    phases give a thin comb-filtered attack instead of a wide chorused one.
    """
    if length is None:
        length = n_samples(seconds)
    rng = np.random.default_rng(seed)
    f = _freq_array(freq, length)
    out = np.zeros(length)
    for i in range(voices):
        offset = (i - (voices - 1) / 2.0) / max(1.0, (voices - 1) / 2.0)
        ratio = 2.0 ** (offset * detune_cents * spread / 1200.0)
        out += osc_saw(f * ratio, length=length, phase0=rng.random())
    return out / voices


def osc_fm(
    carrier,
    ratio: float,
    index,
    seconds: float = None,
    length: int = None,
    feedback: float = 0.0,
) -> np.ndarray:
    """
    Two-operator FM. ``index`` may be an array, which is how the bell and mallet
    voices lose brightness as they decay -- the single most important cue that a
    struck-metal sound is not just a filtered sine.
    """
    if length is None:
        length = n_samples(seconds)
    c = _freq_array(carrier, length)
    idx = _freq_array(index, length)
    mod_ph = np.cumsum(c * ratio) / SR
    mod = np.sin(2.0 * np.pi * mod_ph)
    if feedback > 0.0:
        mod = np.sin(2.0 * np.pi * mod_ph + feedback * mod)
    car_ph = np.cumsum(c) / SR
    return np.sin(2.0 * np.pi * car_ph + idx * mod)


# --------------------------------------------------------------------------------------
# noise
# --------------------------------------------------------------------------------------


def noise_white(seconds: float = None, length: int = None, seed: int = 0) -> np.ndarray:
    if length is None:
        length = n_samples(seconds)
    rng = np.random.default_rng(seed)
    return rng.standard_normal(length)


def noise_pink(seconds: float = None, length: int = None, seed: int = 0) -> np.ndarray:
    """Paul Kellet's economy pink filter -- 1/f within a fraction of a dB across the band."""
    w = noise_white(length=length if length is not None else n_samples(seconds), seed=seed)
    b = [0.049922035, -0.095993537, 0.050612699, -0.004408786]
    a = [1.0, -2.494956002, 2.017265875, -0.522189400]
    out = lfilter(b, a, w)
    return out / (np.max(np.abs(out)) + 1e-12)


def noise_brown(seconds: float = None, length: int = None, seed: int = 0) -> np.ndarray:
    w = noise_white(length=length if length is not None else n_samples(seconds), seed=seed)
    out = np.cumsum(w)
    out = highpass(out, 12.0)
    return out / (np.max(np.abs(out)) + 1e-12)


# --------------------------------------------------------------------------------------
# filters
# --------------------------------------------------------------------------------------


def _nyq_clip(freq: float) -> float:
    """
    Clamp a cutoff into the range a Butterworth design is well conditioned over.

    Note the 12 Hz floor: these filters cannot be used to shape sub-Hz control
    signals -- asking for 0.4 Hz silently gives you 12 Hz. Use :func:`drift` for slow
    envelopes instead.
    """
    return float(np.clip(freq, 12.0, SR * 0.49))


def drift(length: int, rate_hz: float = 0.35, seed: int = 0, partials: int = 5,
          cyclic: bool = True) -> np.ndarray:
    """
    A slow, smooth, quasi-random control curve in [-1, 1] -- gusts, swells, churn.

    Built from a handful of low-order sinusoids with random phases rather than from
    filtered noise, because the Butterworth designs above cannot reach sub-Hz cutoffs.
    When ``cyclic`` is set each partial is snapped to a whole number of cycles across
    the buffer, so the curve wraps exactly and does not fight the loop crossfade.
    """
    rng = np.random.default_rng(seed)
    idx = np.arange(length, dtype=np.float64)
    out = np.zeros(length)
    duration = length / SR
    for k in range(1, partials + 1):
        target = k * rate_hz * duration            # cycles across the buffer
        cycles = max(1.0, round(target)) if cyclic else max(0.05, target)
        out += np.sin(2 * np.pi * cycles * idx / length + rng.random() * 2 * np.pi) / k
    m = np.max(np.abs(out))
    return out / m if m > 1e-9 else out


def drift_unipolar(length: int, rate_hz: float = 0.35, seed: int = 0, floor: float = 0.35,
                   partials: int = 5, cyclic: bool = True) -> np.ndarray:
    """``drift`` mapped into [floor, 1] -- the usual shape for a gust or swell gain."""
    d = drift(length, rate_hz, seed, partials, cyclic)
    return floor + (1.0 - floor) * (0.5 + 0.5 * d)


def lowpass(sig: np.ndarray, cutoff: float, order: int = 2) -> np.ndarray:
    sos = butter(order, _nyq_clip(cutoff) / (SR / 2), btype="low", output="sos")
    return _sosfilt_any(sos, sig)


def highpass(sig: np.ndarray, cutoff: float, order: int = 2) -> np.ndarray:
    sos = butter(order, _nyq_clip(cutoff) / (SR / 2), btype="high", output="sos")
    return _sosfilt_any(sos, sig)


def bandpass(sig: np.ndarray, low: float, high: float, order: int = 2) -> np.ndarray:
    lo = _nyq_clip(low) / (SR / 2)
    hi = _nyq_clip(high) / (SR / 2)
    if hi <= lo:
        hi = min(0.99, lo * 1.2 + 1e-4)
    sos = butter(order, [lo, hi], btype="band", output="sos")
    return _sosfilt_any(sos, sig)


def _sosfilt_any(sos, sig: np.ndarray) -> np.ndarray:
    if sig.ndim == 2:
        return np.stack([sosfilt(sos, sig[:, 0]), sosfilt(sos, sig[:, 1])], axis=1)
    return sosfilt(sos, sig)


def _biquad_coeffs(mode: str, freq: float, q: float, gain_db: float = 0.0):
    """RBJ audio EQ cookbook coefficients."""
    freq = _nyq_clip(freq)
    w0 = 2.0 * math.pi * freq / SR
    cw, sw = math.cos(w0), math.sin(w0)
    alpha = sw / (2.0 * max(q, 0.05))
    A = 10.0 ** (gain_db / 40.0)
    if mode == "lp":
        b = [(1 - cw) / 2, 1 - cw, (1 - cw) / 2]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif mode == "hp":
        b = [(1 + cw) / 2, -(1 + cw), (1 + cw) / 2]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif mode == "bp":
        b = [alpha, 0.0, -alpha]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif mode == "notch":
        b = [1.0, -2 * cw, 1.0]
        a = [1 + alpha, -2 * cw, 1 - alpha]
    elif mode == "peak":
        b = [1 + alpha * A, -2 * cw, 1 - alpha * A]
        a = [1 + alpha / A, -2 * cw, 1 - alpha / A]
    elif mode == "lowshelf":
        sa = 2.0 * math.sqrt(A) * alpha
        b = [A * ((A + 1) - (A - 1) * cw + sa), 2 * A * ((A - 1) - (A + 1) * cw),
             A * ((A + 1) - (A - 1) * cw - sa)]
        a = [(A + 1) + (A - 1) * cw + sa, -2 * ((A - 1) + (A + 1) * cw),
             (A + 1) + (A - 1) * cw - sa]
    elif mode == "highshelf":
        sa = 2.0 * math.sqrt(A) * alpha
        b = [A * ((A + 1) + (A - 1) * cw + sa), -2 * A * ((A - 1) + (A + 1) * cw),
             A * ((A + 1) + (A - 1) * cw - sa)]
        a = [(A + 1) - (A - 1) * cw + sa, 2 * ((A - 1) - (A + 1) * cw),
             (A + 1) - (A - 1) * cw - sa]
    else:
        raise ValueError(mode)
    a0 = a[0]
    return [c / a0 for c in b], [1.0] + [c / a0 for c in a[1:]]


def biquad(sig: np.ndarray, mode: str, freq: float, q: float = 0.707, gain_db: float = 0.0):
    b, a = _biquad_coeffs(mode, freq, q, gain_db)
    if sig.ndim == 2:
        return np.stack([lfilter(b, a, sig[:, 0]), lfilter(b, a, sig[:, 1])], axis=1)
    return lfilter(b, a, sig)


def sweep_filter(
    sig: np.ndarray,
    cutoff_curve,
    q: float = 1.0,
    mode: str = "lp",
    block: int = 48,
) -> np.ndarray:
    """
    Time-varying biquad. Coefficients are recomputed per short block and state is
    carried across block boundaries, which gives an audibly smooth sweep without a
    per-sample Python loop. This is what puts real spectral movement into the
    whooshes, the fire cast and the scanner sweep -- a static filter reads as a buzz.
    """
    length = sig.shape[0]
    cutoff = fit_curve(np.atleast_1d(np.asarray(cutoff_curve, dtype=np.float64)), length)
    if sig.ndim == 2:
        return np.stack(
            [sweep_filter(sig[:, 0], cutoff, q, mode, block),
             sweep_filter(sig[:, 1], cutoff, q, mode, block)],
            axis=1,
        )
    out = np.zeros(length)
    zi = np.zeros(2)
    for start in range(0, length, block):
        end = min(length, start + block)
        f = float(np.mean(cutoff[start:end]))
        b, a = _biquad_coeffs(mode, f, q)
        chunk, zi = lfilter(b, a, sig[start:end], zi=zi)
        out[start:end] = chunk
    return out


def resonator(sig: np.ndarray, freq: float, q: float = 30.0, gain_amt: float = 1.0):
    """Narrow band-pass used to give impacts a pitched 'body' resonance."""
    return biquad(sig, "bp", freq, q) * gain_amt


# --------------------------------------------------------------------------------------
# saturation and dynamics
# --------------------------------------------------------------------------------------


def saturate(sig: np.ndarray, drive: float = 2.0, asym: float = 0.0) -> np.ndarray:
    """
    Soft clipper. A little asymmetry adds even harmonics, which is what reads as
    'weight' on kicks and impacts rather than as distortion.
    """
    x = sig * drive
    if asym != 0.0:
        x = x + asym * x * x
    return np.tanh(x) / math.tanh(max(drive, 1e-6)) * min(1.0, drive)


def soft_clip(sig: np.ndarray, ceiling: float = 0.98) -> np.ndarray:
    return ceiling * np.tanh(sig / max(ceiling, 1e-6))


def transient_shape(sig: np.ndarray, amount: float = 1.5, attack_ms: float = 4.0) -> np.ndarray:
    """Emphasise the leading edge by boosting the difference between a fast and slow envelope."""
    env_fast = _follow(np.abs(sig), attack_ms)
    env_slow = _follow(np.abs(sig), attack_ms * 12.0)
    boost = 1.0 + amount * np.clip(env_fast - env_slow, 0.0, None) / (env_slow + 1e-4)
    return sig * np.clip(boost, 0.2, 6.0)


def _follow(x: np.ndarray, ms: float) -> np.ndarray:
    if x.ndim == 2:
        x = np.mean(np.abs(x), axis=1)
    a = math.exp(-1.0 / (SR * ms / 1000.0))
    return lfilter([1 - a], [1, -a], x)


def compress(sig: np.ndarray, threshold_db: float = -18.0, ratio: float = 3.0,
             attack_ms: float = 5.0, release_ms: float = 120.0, makeup_db: float = 0.0):
    """Simple feed-forward compressor; glues layered ambience and music beds together."""
    mono = np.mean(np.abs(sig), axis=1) if sig.ndim == 2 else np.abs(sig)
    env = _follow(mono, attack_ms)
    env_r = _follow(env, release_ms)
    env = np.maximum(env, env_r)
    level_db = 20.0 * np.log10(env + 1e-9)
    over = np.clip(level_db - threshold_db, 0.0, None)
    gain_db = -over * (1.0 - 1.0 / ratio) + makeup_db
    g = 10.0 ** (gain_db / 20.0)
    return sig * (g[:, None] if sig.ndim == 2 else g)


def normalize(sig: np.ndarray, peak: float = 0.89) -> np.ndarray:
    m = np.max(np.abs(sig))
    if m < 1e-9:
        return sig
    return sig * (peak / m)


def normalize_rms(sig: np.ndarray, target_rms: float = 0.08, ceiling: float = 0.88) -> np.ndarray:
    """
    Match perceived loudness rather than peak.

    Peak normalisation makes a wooden footstep four times quieter than a stone one
    because the stone tap is all transient and the wood panel is all resonance. For
    families of sounds that must sit at the same level in a sequence -- footsteps,
    variant sets -- match RMS and only fall back to a peak limit if that would clip.
    """
    r = float(np.sqrt(np.mean(sig**2)))
    if r < 1e-9:
        return sig
    g = target_rms / r
    pk = float(np.max(np.abs(sig))) * g
    if pk > ceiling:
        g *= ceiling / pk
    return sig * g


def dc_block(sig: np.ndarray) -> np.ndarray:
    return highpass(sig, 18.0, order=1)


# --------------------------------------------------------------------------------------
# space: delay, chorus, convolution reverb
# --------------------------------------------------------------------------------------


def delay(sig: np.ndarray, time_s: float, feedback: float = 0.35, mix_amt: float = 0.3,
          damp_hz: float = 6000.0, taps: int = 12) -> np.ndarray:
    """Feedback delay rendered as a finite tap sum -- cheap, and stable by construction."""
    out = sig.copy()
    d = n_samples(time_s)
    if d <= 0:
        return out
    echo = np.zeros_like(sig)
    src = sig
    g = feedback
    for i in range(1, taps + 1):
        if g < 1e-4:
            break
        shifted = np.zeros_like(sig)
        off = d * i
        if off >= sig.shape[0]:
            break
        shifted[off:] = src[: sig.shape[0] - off]
        shifted = lowpass(shifted, damp_hz * (0.85**i))
        echo += shifted * g
        g *= feedback
    return out + echo * mix_amt


def chorus(sig: np.ndarray, rate_hz: float = 0.6, depth_ms: float = 6.0,
           voices: int = 3, mix_amt: float = 0.5, seed: int = 1) -> np.ndarray:
    """Modulated fractional-delay chorus; widens pads and supersaws."""
    mono = sig if sig.ndim == 1 else np.mean(sig, axis=1)
    length = mono.shape[0]
    rng = np.random.default_rng(seed)
    left = np.zeros(length)
    right = np.zeros(length)
    idx = np.arange(length, dtype=np.float64)
    for v in range(voices):
        phase = rng.random() * 2 * math.pi
        rate = rate_hz * (1.0 + 0.23 * v)
        base = 12.0 + 5.0 * v
        lfo = np.sin(2 * math.pi * rate * idx / SR + phase)
        d_samp = (base + depth_ms * lfo) * SR / 1000.0
        read = np.clip(idx - d_samp, 0, length - 1)
        i0 = read.astype(np.int64)
        frac = read - i0
        i1 = np.minimum(i0 + 1, length - 1)
        wet = mono[i0] * (1 - frac) + mono[i1] * frac
        pan = 0.5 + 0.5 * math.cos(v * 2.1)
        left += wet * pan
        right += wet * (1.0 - pan)
    left /= voices
    right /= voices
    dry = sig if sig.ndim == 2 else mono_to_stereo(mono)
    wet = np.stack([left, right], axis=1)
    return dry * (1.0 - mix_amt) + wet * mix_amt


_IR_CACHE: dict = {}


def make_ir(
    seconds: float = 2.0,
    decay: float = 4.5,
    predelay_ms: float = 12.0,
    damp_hz: float = 5200.0,
    early: int = 9,
    diffusion: float = 0.9,
    seed: int = 7,
    stereo: bool = True,
) -> np.ndarray:
    """
    Grow an impulse response: sparse early reflections, then an exponentially
    decaying noise tail that loses high frequency as it decays (air absorption).
    Convolving with this is what gives the cave its size and the fanfare its hall.
    """
    key = (seconds, decay, predelay_ms, damp_hz, early, diffusion, seed, stereo)
    if key in _IR_CACHE:
        return _IR_CACHE[key]
    length = n_samples(seconds)
    rng = np.random.default_rng(seed)
    chans = 2 if stereo else 1
    ir = np.zeros((length, chans))
    pre = n_samples(predelay_ms / 1000.0)

    # early reflections: discrete, slightly decorrelated per ear
    for c in range(chans):
        t = pre
        amp = 0.85
        for i in range(early):
            t += int(rng.integers(int(0.004 * SR), int(0.021 * SR)))
            if t >= length:
                break
            ir[t, c] += amp * rng.choice([-1.0, 1.0]) * (0.6 + 0.4 * rng.random())
            amp *= 0.76

    # diffuse tail
    tail_start = pre + n_samples(0.02)
    if tail_start < length:
        m = length - tail_start
        env = np.exp(-decay * np.linspace(0.0, 1.0, m))
        env *= np.linspace(1.0, 0.0, m) ** 0.6  # force the tail to true zero
        for c in range(chans):
            nz = rng.standard_normal(m) * diffusion
            # progressive damping: split into bands and decay highs faster
            low = lowpass(nz, damp_hz * 0.35, 2)
            mid = bandpass(nz, damp_hz * 0.35, damp_hz, 2)
            high = highpass(nz, damp_hz, 2)
            tail = low * env + mid * (env**1.7) + high * (env**3.2)
            ir[tail_start:, c] += tail
    ir = ir / (np.max(np.abs(ir)) + 1e-12)
    if not stereo:
        ir = ir[:, 0]
    _IR_CACHE[key] = ir
    return ir


def reverb(sig: np.ndarray, ir: np.ndarray, mix_amt: float = 0.3, wet_only: bool = False):
    """FFT convolution reverb. Output is stereo whenever the IR is."""
    stereo_out = ir.ndim == 2 or sig.ndim == 2
    dry = sig if sig.ndim == 2 else (mono_to_stereo(sig) if stereo_out else sig)
    if ir.ndim == 1:
        ir = mono_to_stereo(ir) if stereo_out else ir
    if stereo_out:
        wet = np.stack(
            [fftconvolve(dry[:, 0], ir[:, 0])[: dry.shape[0]],
             fftconvolve(dry[:, 1], ir[:, 1])[: dry.shape[0]]],
            axis=1,
        )
    else:
        wet = fftconvolve(dry, ir)[: dry.shape[0]]
    wet /= math.sqrt(max(1.0, ir.shape[0] / SR)) * 6.0
    if wet_only:
        return wet
    return dry * (1.0 - mix_amt * 0.5) + wet * mix_amt


def reverb_tail(sig: np.ndarray, ir: np.ndarray, mix_amt: float = 0.3, tail_s: float = 1.5):
    """Like ``reverb`` but extends the buffer so the tail is not truncated."""
    extended = fit(sig, sig.shape[0] + n_samples(tail_s))
    return reverb(extended, ir, mix_amt)


# --------------------------------------------------------------------------------------
# stereo
# --------------------------------------------------------------------------------------


def pan(sig: np.ndarray, position: float = 0.0) -> np.ndarray:
    """Equal-power pan. -1 hard left, +1 hard right."""
    position = float(np.clip(position, -1.0, 1.0))
    angle = (position + 1.0) * math.pi / 4.0
    l, r = math.cos(angle), math.sin(angle)
    mono = sig if sig.ndim == 1 else np.mean(sig, axis=1)
    return np.stack([mono * l, mono * r], axis=1)


def widen(sig: np.ndarray, amount: float = 1.4) -> np.ndarray:
    """Mid/side widening, with the low end kept mono so it stays solid."""
    if sig.ndim == 1:
        sig = mono_to_stereo(sig)
    mid = (sig[:, 0] + sig[:, 1]) * 0.5
    side = (sig[:, 0] - sig[:, 1]) * 0.5 * amount
    side = highpass(side, 180.0)  # bass stays centred
    return np.stack([mid + side, mid - side], axis=1)


def haas(sig: np.ndarray, ms: float = 12.0, side: int = 1) -> np.ndarray:
    """Tiny inter-channel delay for width on single-source material."""
    mono = sig if sig.ndim == 1 else np.mean(sig, axis=1)
    d = n_samples(ms / 1000.0)
    delayed = np.zeros_like(mono)
    if d < mono.shape[0]:
        delayed[d:] = mono[: mono.shape[0] - d]
    return np.stack([mono, delayed], axis=1) if side > 0 else np.stack([delayed, mono], axis=1)


# --------------------------------------------------------------------------------------
# physical-model and texture voices
# --------------------------------------------------------------------------------------


def karplus(freq: float, seconds: float, damping: float = 0.5, brightness: float = 0.5,
            seed: int = 0, pick_pos: float = 0.25) -> np.ndarray:
    """
    Karplus-Strong plucked string, processed a delay-line-length at a time so it is
    vectorised rather than a per-sample loop. Used for the route/town guitar-ish
    plucks and the lakeside harp.
    """
    length = n_samples(seconds)
    L = max(2, int(round(SR / max(freq, 20.0))))
    rng = np.random.default_rng(seed)
    exc = rng.standard_normal(L)
    exc = lowpass(exc, 200.0 + 14000.0 * brightness, 2)
    # comb the excitation to emulate pick position
    shift = max(1, int(L * pick_pos))
    exc = exc - np.roll(exc, shift) * 0.7
    exc /= np.max(np.abs(exc)) + 1e-12

    out = np.zeros(length + L * 2)
    out[:L] = exc
    fb = 0.5 * (1.0 - damping * 0.06)
    blocks = (length + L) // L + 1
    for bidx in range(1, blocks):
        s = bidx * L
        e = s + L
        if s >= out.shape[0]:
            break
        prev = out[s - L : e - L]
        prev_shift = np.empty_like(prev)
        prev_shift[0] = out[s - L - 1] if s - L - 1 >= 0 else prev[0]
        prev_shift[1:] = prev[:-1]
        chunk = (prev + prev_shift) * fb
        e = min(e, out.shape[0])
        out[s:e] = chunk[: e - s]
    out = out[:length]
    out *= np.exp(-np.linspace(0.0, 1.0, length) * (1.5 + damping * 5.0))
    return dc_block(out)


def granular(
    source_fn,
    seconds: float,
    grain_ms: float = 60.0,
    density: float = 40.0,
    pitch_jitter: float = 0.25,
    pan_spread: float = 0.8,
    seed: int = 3,
    stereo: bool = True,
) -> np.ndarray:
    """
    Scatter windowed grains produced by ``source_fn(duration, rng)``. This is how the
    water, fire and crowd textures get their irregular, non-looping-sounding surface;
    a single filtered noise bed always betrays itself as static.
    """
    length = n_samples(seconds)
    out = np.zeros((length, 2) if stereo else length)
    rng = np.random.default_rng(seed)
    count = max(1, int(seconds * density))
    for _ in range(count):
        dur = grain_ms / 1000.0 * (1.0 + rng.uniform(-0.5, 0.9))
        g = source_fn(dur, rng)
        if g.shape[0] < 4:
            continue
        # Hann window so grains never click
        g = g * np.hanning(g.shape[0])
        if pitch_jitter > 0:
            factor = 1.0 + rng.uniform(-pitch_jitter, pitch_jitter)
            m = max(4, int(g.shape[0] / factor))
            g = np.interp(np.linspace(0, g.shape[0] - 1, m), np.arange(g.shape[0]), g)
        start = int(rng.uniform(0, max(1, length - g.shape[0])))
        end = min(length, start + g.shape[0])
        seg = g[: end - start]
        if stereo:
            p = rng.uniform(-pan_spread, pan_spread)
            angle = (p + 1.0) * math.pi / 4.0
            out[start:end, 0] += seg * math.cos(angle)
            out[start:end, 1] += seg * math.sin(angle)
        else:
            out[start:end] += seg
    return out


# --------------------------------------------------------------------------------------
# seamless looping
# --------------------------------------------------------------------------------------


def make_seamless(sig: np.ndarray, loop_seconds: float, overlap_s: float = None) -> np.ndarray:
    """
    Fold the overhang back over the head.

    The correct way to build a musical loop: render ``loop_seconds`` of material *plus*
    the reverb/decay overhang that spills past the end, then add that overhang onto the
    beginning. The result is a buffer of exactly ``loop_seconds`` where the last sample
    flows into the first with no discontinuity and no truncated tail.
    """
    loop_n = n_samples(loop_seconds)
    if sig.shape[0] <= loop_n:
        return fit(sig, loop_n)
    head = sig[:loop_n].copy()
    over = sig[loop_n:]
    m = min(over.shape[0], loop_n)
    head[:m] += over[:m]
    return head


def crossfade_loop(sig: np.ndarray, fade_s: float = 0.5) -> np.ndarray:
    """
    Circular crossfade for textures with no bar structure (ambience beds).
    The tail is cross-faded into the head with equal-power curves so that RMS stays
    constant through the splice and the seam is inaudible.
    """
    length = sig.shape[0]
    k = min(n_samples(fade_s), length // 3)
    if k < 8:
        return sig
    body = sig[: length - k].copy()
    tail = sig[length - k :]
    x = np.linspace(0.0, 1.0, k)
    fade_out_w = np.cos(x * math.pi / 2.0)
    fade_in_w = np.sin(x * math.pi / 2.0)
    if sig.ndim == 2:
        body[:k] = body[:k] * fade_in_w[:, None] + tail * fade_out_w[:, None]
    else:
        body[:k] = body[:k] * fade_in_w + tail * fade_out_w
    return body


def pin_loop_wrap(sig: np.ndarray, fade_s: float = 0.05) -> np.ndarray:
    """
    Land the loop's last sample exactly on its first.

    ``crossfade_loop`` / ``make_seamless`` make the interior continuous, but the wrap
    itself still steps by whatever |x[0] - x[-1]| happens to be, and on a broadband
    bed that residual is an audible per-loop tick. The residual is spread across the
    final ``fade_s`` as a half-cosine ramp: at 50 ms the correction sits below 10 Hz,
    under the highpass every bed and theme already carries, so it cannot be heard as
    program material. Per-sample maps applied afterwards (soft_clip, normalize, gain)
    preserve the endpoint equality; filters do not -- this must be the last stateful
    step before write. Loops only; a one-shot has no wrap to pin.
    """
    k = min(n_samples(fade_s), sig.shape[0] // 4)
    if k < 4:
        return sig
    out = sig.copy()
    w = 0.5 - 0.5 * np.cos(np.linspace(0.0, math.pi, k))
    delta = out[0] - out[-1]
    if out.ndim == 2:
        out[-k:] += w[:, None] * delta[None, :]
    else:
        out[-k:] += w * delta
    return out


def zero_edges(sig: np.ndarray, samples: int = 32) -> np.ndarray:
    """
    Micro fade on the very first and last samples of a one-shot.

    Only for one-shots -- loops must NOT be edge-faded or the seam becomes a hole.
    """
    out = sig.copy()
    k = min(samples, out.shape[0] // 2)
    if k < 2:
        return out
    w = np.linspace(0.0, 1.0, k)
    if out.ndim == 2:
        out[:k] *= w[:, None]
        out[-k:] *= w[::-1][:, None]
    else:
        out[:k] *= w
        out[-k:] *= w[::-1]
    return out


def loop_seam_report(sig: np.ndarray, window_ms: float = 25.0) -> dict:
    """
    Numeric seam check used by the verifier.

    A click at a loop point is a sample-level discontinuity: the single step from the
    last sample back to the first is larger than any step the material itself contains.
    So the headline number is that wrap step measured against the largest first
    difference anywhere in the file. ``step_ratio <= 1`` means the join is no sharper
    than the sharpest transient already in the piece, i.e. inaudible as a seam.

    ``level_jump_db`` compares the RMS either side of the wrap. A large positive value
    is usually legitimate (the loop restarts on a downbeat) but a large negative value
    means the tail was truncated, which is the other way a loop betrays itself.
    """
    mono = np.mean(sig, axis=1) if sig.ndim == 2 else sig
    if mono.shape[0] < 64:
        return {"step_ratio": 0.0, "wrap_step": 0.0, "level_jump_db": 0.0, "edge_dc": 0.0}
    d = np.abs(np.diff(mono))
    gmax = float(np.max(d)) + 1e-12
    wrap_step = float(abs(mono[0] - mono[-1]))
    w = min(n_samples(window_ms / 1000.0), mono.shape[0] // 4)
    rms_pre = float(np.sqrt(np.mean(mono[-w:] ** 2))) + 1e-9
    rms_post = float(np.sqrt(np.mean(mono[:w] ** 2))) + 1e-9
    # Detect an accidental edge fade: a loop that was treated like a one-shot has its
    # first and last few milliseconds ramped to zero, which puts a hole at the wrap.
    # Silence at the wrap is only a defect if the material *around* it is loud -- a
    # rhythmic loop that rests on the last beat is legitimately silent there.
    e = max(4, n_samples(0.003))
    inner = min(mono.shape[0] // 4, n_samples(0.03))
    edge_rms = float(np.sqrt(np.mean(np.concatenate([mono[:e], mono[-e:]]) ** 2)))
    near_rms = float(np.sqrt(np.mean(np.concatenate([mono[e:e + inner],
                                                     mono[-e - inner:-e]]) ** 2))) + 1e-9
    return {
        "step_ratio": wrap_step / gmax,
        "wrap_step": wrap_step,
        "level_jump_db": 20.0 * math.log10(rms_post / rms_pre),
        "edge_dc": float(max(abs(mono[0]), abs(mono[-1]))),
        "edge_fade_ratio": edge_rms / near_rms,
    }


def loop_seam_error(sig: np.ndarray, window_ms: float = 25.0) -> float:
    return loop_seam_report(sig, window_ms)["step_ratio"]


# --------------------------------------------------------------------------------------
# output
# --------------------------------------------------------------------------------------


def write_wav(path: str, sig: np.ndarray, peak: float = 0.89, dither: bool = True,
              normalise: bool = True) -> dict:
    """
    Write 16-bit PCM at 44.1 kHz.

    ``peak`` defaults to about -1 dBFS of headroom so nothing clips after Unity's
    mixer applies group gain. TPDF dither is added before quantisation.
    """
    os.makedirs(os.path.dirname(path), exist_ok=True)
    x = np.asarray(sig, dtype=np.float64)
    x = np.nan_to_num(x, nan=0.0, posinf=0.0, neginf=0.0)
    if normalise:
        x = normalize(x, peak)
    else:
        m = np.max(np.abs(x))
        if m > peak:
            x = x * (peak / m)
    channels = 2 if x.ndim == 2 else 1
    if dither:
        rng = np.random.default_rng(11)
        lsb = 1.0 / 32768.0
        x = x + (rng.random(x.shape) - rng.random(x.shape)) * lsb * 0.5
    x = np.clip(x, -1.0, 32767.0 / 32768.0)
    pcm = np.round(x * 32767.0).astype(np.int16)
    frames = pcm.shape[0]
    with wave.open(path, "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    return {
        "path": path,
        "channels": channels,
        "frames": frames,
        "duration": frames / float(SR),
        "peak": float(np.max(np.abs(x))) if frames else 0.0,
        "bytes": os.path.getsize(path),
    }


def read_wav(path: str):
    with wave.open(path, "rb") as w:
        ch = w.getnchannels()
        sw = w.getsampwidth()
        sr = w.getframerate()
        frames = w.getnframes()
        raw = w.readframes(frames)
    data = np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
    if ch == 2:
        data = data.reshape(-1, 2)
    return data, sr, sw, ch
