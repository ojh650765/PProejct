"""
Tileable terrain layer maps for the PokeLab/TerrainBlend shader.

The four layers of M_Ground_TerrainBlend are *not* atlas cells. The shader
projects layers 0-2 planar from world XZ and layer 3 triplanar from world XYZ,
so every layer map is sampled with an unbounded, wrapping UV. An atlas cell
cannot be used here at all: its neighbours would bleed in on the very first
repeat. These are therefore separate, individually seamless textures, and this
module is deliberately independent of textures.py's 4x4 cell machinery.

Outputs, into Assets/Game/Art/Environment/Terrain/Textures/ :

    Env_Terrain_Grass_BaseColor.png   RGB albedo, A = height   -> _Layer0Map
    Env_Terrain_Grass_Normal.png      tangent space            -> _Layer0Normal
    ... and the same pair for Dirt, Sand and Rock.

Three rules the shader and the camera impose on the content:

  * A = height, and it is load bearing. PL_HeightBlend4 compares
    weight * (height + 1) across the four layers within a _HeightContrast of
    0.18, which is what makes gravel poke through grass instead of cross-fading
    with it. A flat alpha throws the whole mechanism away, so each layer is
    authored with a deliberate height range *and* a deliberate mean relative to
    the other three (see TARGET_HEIGHT below).

  * Near-neutral colour. The material multiplies each layer by its own tint, so
    any saturation baked in here would be applied twice. The maps carry value
    structure and only a few percent of hue drift.

  * No low-frequency landmark. The camera is a locked 3/4 view, 38 deg pitch,
    40 deg vertical FOV, 5.5 m boom, and it sees ground from about 3 m to 30 m.
    A screenful is roughly 9 m across near the player and 25 m at the back, so
    at the shipped scales each tile repeats between 3 and 10 times on screen.
    Anything with one big blob per tile would immediately read as a grid at that
    repeat count. All the contrast therefore lives in the mid and high
    frequencies; the shader's own _MacroVariation (fbm over 26 m) supplies the
    large-scale drift instead.

Everything is periodic by construction rather than by mirroring or by a
cross-fade: the value noise wraps through its lattice, the Worley points wrap
through a torus, and the brush splats wrap through their index arrays. Run with
--verify to get the wrap-seam numbers and 3x3 tiling sheets.

    python terrain_layers.py            # write the eight maps
    python terrain_layers.py --verify   # also write previews/terrain_layer_*.png

Pure numpy + stdlib, so it runs under the system Python as well as inside
Blender (build_all.py drives it the same way as every other stage).
"""

import os
import sys
import math
import struct
import zlib

import numpy as np

SIZE = 1024

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
OUT_DIR = os.path.join(REPO, "Assets", "Game", "Art", "Environment",
                       "Terrain", "Textures")
PREVIEW_DIR = os.path.join(HERE, "previews")

# Metres of world covered by one tile of each layer, i.e. 1 / _LayerNScale.
# Only used to keep the feature sizes below honest and to print them.
TILE_METRES = {"Grass": 1 / 0.35, "Dirt": 1 / 0.5, "Sand": 1 / 0.6,
               "Rock": 1 / 0.26}

# Mean of the height channel per layer. These are relative values and they are
# the whole point of the alpha channel: where two layers have equal weight the
# taller one wins, so grass sits above dirt and sand, and rock sits above
# everything. That is what puts tufts over the edge of a road and lets a block
# of stone break through the grass at the top of a cliff.
TARGET_HEIGHT = {"Grass": 0.58, "Dirt": 0.44, "Sand": 0.40, "Rock": 0.62}


def log(msg):
    print("[terrain] %s" % msg)
    sys.stdout.flush()


# ---------------------------------------------------------------------------
# PNG io -- RGBA, because the albedo's alpha carries the height map
# ---------------------------------------------------------------------------

def _png(path, arr, colour_type):
    h, w = arr.shape[0], arr.shape[1]
    stride = arr.shape[2]
    raw = bytearray()
    flat = arr.reshape(h, w * stride)
    for y in range(h):
        raw.append(0)
        raw.extend(flat[y].tobytes())
    comp = zlib.compress(bytes(raw), 9)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    hdr = struct.pack(">IIBBBBB", w, h, 8, colour_type, 0, 0, 0)
    blob = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", hdr) +
            chunk(b"IDAT", comp) + chunk(b"IEND", b""))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(blob)
    return path


def write_rgba(path, rgba):
    return _png(path, rgba, 6)


def write_rgb(path, rgb):
    return _png(path, rgb, 2)


def u8(x):
    return (np.clip(x, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)


# ---------------------------------------------------------------------------
# periodic noise primitives
#
# Every one of these has period exactly `size` in both axes. That is the only
# way to get a texture that tiles without a visible seam; fading a copy of the
# image over its own edges would work too but it doubles the pattern density
# along every border, which shows up as a soft cross under minification.
# ---------------------------------------------------------------------------

def _rng(seed):
    return np.random.RandomState(seed & 0x7FFFFFFF)


def pnoise(freq, seed, size=SIZE):
    """Value noise on a freq x freq lattice, wrapped at the lattice."""
    freq = max(2, int(freq))
    r = _rng(seed)
    g = r.rand(freq, freq)
    t = (np.arange(size) + 0.5) / size * freq
    i0 = np.floor(t).astype(int) % freq
    i1 = (i0 + 1) % freq
    f = t - np.floor(t)
    f = f * f * (3 - 2 * f)
    a = g[np.ix_(i0, i0)]
    b = g[np.ix_(i0, i1)]
    c = g[np.ix_(i1, i0)]
    d = g[np.ix_(i1, i1)]
    fx = f[None, :]
    fy = f[:, None]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def pfbm(freq, seed, octaves=5, gain=0.5, size=SIZE):
    total = np.zeros((size, size))
    amp, norm, f = 1.0, 0.0, int(max(2, freq))
    for i in range(octaves):
        total += pnoise(f, seed + i * 977, size) * amp
        norm += amp
        amp *= gain
        f *= 2
    return total / norm


def pridge(freq, seed, octaves=4, size=SIZE):
    """Ridged fbm -- creases rather than blobs. Reads as fracture, not cloud."""
    total = np.zeros((size, size))
    amp, norm, f = 1.0, 0.0, int(max(2, freq))
    for i in range(octaves):
        n = 1.0 - np.abs(pnoise(f, seed + i * 313, size) * 2.0 - 1.0)
        total += (n ** 2) * amp
        norm += amp
        amp *= 0.55
        f *= 2
    return total / norm


def pvoronoi(n, seed, jitter=1.0, size=SIZE):
    """Worley on a torus. `n` is either a count or an (nx, ny) pair.

    Returns (d1, d2, cell_id, ox, oy). Distances are in cell units, so a cell is
    about 1 across whatever its pixel aspect, and ox/oy is the wrapped offset
    from the pixel to its nearest site -- which is what lets a cell be shaded as
    a little tilted plane instead of a flat patch of tone.

    The separate nx/ny is what makes bedded rock possible: sites on a 4x7 grid
    give cells about twice as wide as they are tall, which is how stratified
    stone actually breaks. A square lattice gives equant blocks, and equant
    blocks with dark joints between them read as dried mud rather than as a
    cliff no matter how the tones are graded.

    Only the 3x3 neighbourhood of each pixel's own cell is searched, so this
    stays linear in pixels however fine the lattice gets.
    """
    if isinstance(n, (tuple, list)):
        nx, ny = int(n[0]), int(n[1])
    else:
        nx = ny = int(n)
    nx, ny = max(2, nx), max(2, ny)

    r = _rng(seed)
    px = (np.arange(nx)[None, :] + 0.5 + (r.rand(ny, nx) - 0.5) * jitter) / nx
    py = (np.arange(ny)[:, None] + 0.5 + (r.rand(ny, nx) - 0.5) * jitter) / ny

    t = (np.arange(size) + 0.5) / size
    ci = np.floor(t * nx).astype(int) % nx
    cj = np.floor(t * ny).astype(int) % ny
    X = t[None, :]
    Y = t[:, None]

    d1 = np.full((size, size), 1e9)
    d2 = np.full((size, size), 1e9)
    cid = np.zeros((size, size), dtype=np.int32)
    o1x = np.zeros((size, size))
    o1y = np.zeros((size, size))

    for dj in (-1, 0, 1):
        for di in (-1, 0, 1):
            jj = (cj[:, None] + dj) % ny
            ii = (ci[None, :] + di) % nx
            dx = (X - px[jj, ii])
            dy = (Y - py[jj, ii])
            dx -= np.round(dx)
            dy -= np.round(dy)
            dx = dx * nx
            dy = dy * ny
            d = np.sqrt(dx * dx + dy * dy)
            closer = d < d1
            d2 = np.where(closer, d1, np.minimum(d2, d))
            cid = np.where(closer, jj * nx + ii, cid)
            o1x = np.where(closer, dx, o1x)
            o1y = np.where(closer, dy, o1y)
            d1 = np.where(closer, d, d1)

    return d1, d2, cid, o1x, o1y


def cell_random(cid, seed, count=None):
    r = _rng(seed)
    lut = r.rand(int(count if count else cid.max() + 2))
    return lut[cid]


def splat(size, count, seed, length, width, elongation=2.2, signed=True,
          angle=None, spread=math.pi):
    """Splat `count` oriented gaussian strokes into a periodic accumulator.

    Each stroke is evaluated only inside its own bounding box and written back
    through wrapped indices, so the cost is set by the stroke size rather than
    by the texture size and the result wraps for free. `length`/`width` are in
    pixels; `angle` fixes the mean direction and `spread` the jitter around it.
    """
    acc = np.zeros((size, size))
    r = _rng(seed)
    for _ in range(count):
        ln = max(1.0, length * (0.5 + r.rand()))
        wd = max(0.8, width * (0.6 + 0.8 * r.rand()))
        a = (r.rand() * 2.0 * math.pi) if angle is None else \
            (angle + (r.rand() - 0.5) * 2.0 * spread)
        rad = int(math.ceil(max(ln, wd) * 1.7)) + 1
        rad = min(rad, size // 2 - 1)
        yy, xx = np.mgrid[-rad:rad + 1, -rad:rad + 1]
        ca, sa = math.cos(a), math.sin(a)
        u = xx * ca + yy * sa
        v = -xx * sa + yy * ca
        m = np.exp(-(u / ln) ** 2 * elongation - (v / wd) ** 2 * elongation)
        amp = (r.rand() * 2.0 - 1.0) if signed else (0.4 + 0.6 * r.rand())
        cy = int(r.rand() * size)
        cx = int(r.rand() * size)
        ys = (np.arange(cy - rad, cy + rad + 1)) % size
        xs = (np.arange(cx - rad, cx + rad + 1)) % size
        acc[np.ix_(ys, xs)] += m * amp
    # Normalise on a high percentile, not on the maximum. Dividing by the max
    # means the amplitude of a typical stroke is set by whichever handful of
    # strokes happened to pile up on the same pixel, so doubling the stroke
    # count halves the visibility of every stroke -- which is how a field of
    # 9000 grass blades came out looking like stucco.
    return np.clip(acc / max(np.percentile(np.abs(acc), 99.0), 1e-6), -1.6, 1.6)


def discs(size, count, seed, r_lo, r_hi, dome=0.75, soft=False, profile=1.15):
    """Scattered squashed domes -- pebbles, or tufts of grass.

    A free-scattered field like this has no lattice at all, which is the reason
    it is used for the things the eye is most likely to catch repeating. Set
    `soft` for tufts, where the tone should fade out at the edge; leave it off
    for gravel, where a stone has a definite outline.
    """
    tone = np.zeros((size, size))
    hgt = np.zeros((size, size))
    r = _rng(seed)
    for _ in range(count):
        rr = r_lo + r.rand() * (r_hi - r_lo)
        rad = int(math.ceil(rr)) + 2
        rad = min(rad, size // 2 - 1)
        yy, xx = np.mgrid[-rad:rad + 1, -rad:rad + 1]
        # slightly squashed and rotated, so pebbles are not a field of circles
        a = r.rand() * math.pi
        ca, sa = math.cos(a), math.sin(a)
        u = (xx * ca + yy * sa) / rr
        v = (-xx * sa + yy * ca) / (rr * (0.62 + 0.5 * r.rand()))
        d = np.sqrt(u * u + v * v)
        mask = np.clip(1.0 - d, 0.0, 1.0)
        # `profile` must stay above 1. A hemisphere (exponent 0.5) has a
        # vertical tangent at its rim, and a vertical tangent in a height map
        # becomes a hard bright outline in the normal -- every tuft of grass
        # traced in pen, which is exactly what it looked like.
        shape = np.clip(1.0 - d * d, 0.0, 1.0) ** profile * dome
        cy = int(r.rand() * size)
        cx = int(r.rand() * size)
        ys = (np.arange(cy - rad, cy + rad + 1)) % size
        xs = (np.arange(cx - rad, cx + rad + 1)) % size
        sl = np.ix_(ys, xs)
        keep = shape > hgt[sl]
        hgt[sl] = np.where(keep, shape, hgt[sl])
        body = (mask ** 0.45) if soft else (mask > 0).astype(np.float64)
        tone[sl] = np.where(keep, body * (r.rand() * 2 - 1), tone[sl])
    return tone, hgt


def warp(field, dx, dy):
    """Bilinear resample of `field` displaced by (dx, dy) pixels, wrapping."""
    h, w = field.shape
    ys = np.arange(h)[:, None] + dy
    xs = np.arange(w)[None, :] + dx
    y0 = np.floor(ys).astype(int)
    x0 = np.floor(xs).astype(int)
    fy = ys - y0
    fx = xs - x0
    y0 %= h
    x0 %= w
    y1 = (y0 + 1) % h
    x1 = (x0 + 1) % w
    a = field[y0, x0]
    b = field[y0, x1]
    c = field[y1, x0]
    d = field[y1, x1]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def damp_lowfreq(x, knee=2.5, floor=0.35):
    """Attenuate everything below `knee` cycles per tile.

    This is the one structural rule the camera imposes. A tile repeats three to
    ten times across a screenful of ground, so any feature that is unique within
    a tile and large enough to notice becomes a landmark the eye can count -- one
    dark block per 3.85 m is not read as a dark block, it is read as a grid. The
    fix is not to remove the variation but to move it: the shader's
    _MacroVariation already drifts the colour over 26 m, which is far longer than
    the tile and so cannot repeat, and it is the right place for anything at that
    scale.

    Done in the Fourier domain, which assumes the signal is periodic -- exactly
    what these maps are -- so unlike a spatial blur it cannot introduce a seam.
    """
    ny, nx = x.shape
    mean = x.mean()
    f = np.fft.rfft2(x - mean)
    fy = np.fft.fftfreq(ny) * ny
    fx = np.fft.rfftfreq(nx) * nx
    rad = np.sqrt(fy[:, None] ** 2 + fx[None, :] ** 2)
    gain = floor + (1.0 - floor) * np.clip(rad / max(knee, 1e-3), 0.0, 1.0)
    return np.fft.irfft2(f * gain, s=x.shape) + mean


def level(x, mean, spread, lo=0.0, hi=1.0, pct=97.0):
    """Re-centre and rescale a signal to a target mean and visible range.

    Authoring by "add 0.1 of this and 0.06 of that" never lands on a usable
    exposure, and the exposure is what decides whether a layer reads at all once
    the material multiplies its tint through. So the painters below compose in
    arbitrary units and every channel is levelled here at the end.
    """
    x = x - x.mean()
    p = np.percentile(np.abs(x), pct)
    x = x / max(p, 1e-6) * (spread * 0.5)
    return np.clip(x + mean, lo, hi)


def height_to_normal(hgt, strength):
    """Periodic tangent-space normal.

    Green is +gy, not -gy. Image row increases downward while Unity's v axis
    increases upward, so dh/dv = -dh/drow and N.y = -dh/dv = +dh/drow. Getting
    this backwards is invisible in a thumbnail and reads in engine as relief
    that is lit from the wrong side -- bumps that look like dents whenever the
    sun has any north-south component, which here it always does.
    """
    gx = (np.roll(hgt, -1, axis=1) - np.roll(hgt, 1, axis=1)) * 0.5
    gy = (np.roll(hgt, -1, axis=0) - np.roll(hgt, 1, axis=0)) * 0.5
    nx = -gx * strength
    ny = gy * strength
    nz = np.ones_like(hgt)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    return np.stack([nx / ln * 0.5 + 0.5,
                     ny / ln * 0.5 + 0.5,
                     nz / ln * 0.5 + 0.5], axis=-1)


def tint(grey, warm_cool, amount=0.022):
    """Grey plus a whisper of warm/cool drift.

    Two things keep this tiny. The material multiplies the real colour on top,
    so anything substantial here is applied twice and the ground goes muddy.
    And `warm_cool` must be a *low frequency* field: driving the hue from a
    per-cell value instead paints every Worley cell its own colour, which turns
    a ground texture into crazy paving at any viewing distance.
    """
    t = np.clip((warm_cool - warm_cool.mean()) / (np.std(warm_cool) + 1e-6),
                -2.0, 2.0) * 0.5
    return np.stack([grey * (1.0 + t * amount),
                     grey * (1.0 + t * amount * 0.2),
                     grey * (1.0 - t * amount)], axis=-1)


# ---------------------------------------------------------------------------
# layer 0 -- grass, planar XZ, 2.86 m per tile
# ---------------------------------------------------------------------------

def build_grass(seed=901, size=SIZE):
    """Grass seen from above at 3-30 m.

    Deliberately not blades. One blade is about 8 mm, which at this scale is
    under two screen pixels near the player and less than one further out: it
    cannot be resolved, so drawing it only buys shimmer. What does resolve is
    the tuft -- 20 cm or so, about 35 screen pixels close up -- and its shadow.
    So the tuft carries the read, a stroke pass gives it a fibrous edge, and the
    finer stroke pass exists only to break up the tuft into something that does
    not look moulded.

    Almost all of the contrast is pushed into the height channel rather than the
    albedo. Grass that varies strongly in colour reads as patchy and dying; grass
    that varies strongly in *relief* reads as thick, and relief is what the
    player was actually missing.
    """
    # Free-scattered tufts, not a Worley lattice. Worley was the obvious tool
    # and it was wrong: its cells tile a plane exactly, so every tuft shares a
    # wall with its neighbours and the field reads as leather or as crazy
    # paving. Grass has gaps and overlaps. Scattered domes that combine by
    # taking the taller of the two give both, and carry no lattice for the eye
    # to lock onto over the three to ten repeats that fit on screen.
    tuft_tone, tuft_h = discs(size, 900, seed + 61,
                              size * 0.024, size * 0.058, soft=True, profile=1.5)
    clump_tone, clump_h = discs(size, 260, seed + 63,
                                size * 0.055, size * 0.120, soft=True, profile=1.6)
    # Where nothing is growing. Grass reads as green with dark between it, and
    # the dark is not symmetric noise -- it is the ground showing through, so it
    # gets its own one-sided term rather than being left to the wash.
    gap = np.clip(1.0 - (clump_h * 0.75 + tuft_h * 0.75), 0, 1) ** 1.5

    # coarse patches of longer / shorter growth, well above the tuft scale
    patch = pfbm(4, seed + 13, 4, size=size) - 0.5
    drift = pfbm(9, seed + 17, 3, size=size) - 0.5

    # Few and legible rather than many and fine. A dense mat of tiny strokes
    # averages out to stucco: it has texture everywhere and reads as nothing.
    # 11 cm strokes survive to about 20 screen pixels near the player, which is
    # the size at which the eye calls something a blade of grass.
    blade = splat(size, 4200, seed + 31, size * 0.040, size * 0.0060)
    fibre = splat(size, 6000, seed + 53, size * 0.016, size * 0.0028)
    grain = pfbm(150, seed + 11, 2, size=size) - 0.5

    value = (patch * 0.40
             + drift * 0.20
             + clump_tone * 0.26
             + tuft_tone * 0.20
             + clump_h * 0.16
             + tuft_h * 0.12
             - gap * 0.34
             + blade * 0.44
             + fibre * 0.18
             + grain * 0.07)

    height = (clump_h * 0.75
              + tuft_h * 0.55
              + clump_tone * 0.16
              + blade * 0.50
              + fibre * 0.16
              - gap * 0.30
              + patch * 0.20)

    grey = level(damp_lowfreq(value, 2.5, 0.45), 0.86, 0.44)
    rgb = tint(grey, patch, 0.030)
    hgt = level(height, TARGET_HEIGHT["Grass"], 0.82)
    return rgb, hgt, 2.4


# ---------------------------------------------------------------------------
# layer 1 -- dirt, planar XZ, 2.0 m per tile. This is what the roads wear.
# ---------------------------------------------------------------------------

def build_dirt(seed=902, size=SIZE):
    """Packed earth with gravel worked into it.

    The pebbles are the reason this layer needs a real height channel: they are
    what the height blend pushes up through the grass along a road edge, which
    is the difference between a path that was worn and a path that was painted.
    """
    peb_tone, peb_h = discs(size, 950, seed + 61,
                            size * 0.0030, size * 0.012, dome=1.0)
    grit_tone, grit_h = discs(size, 4200, seed + 67,
                              size * 0.0014, size * 0.0034, dome=0.8)

    # bed of compacted earth
    bed = pfbm(9, seed + 3, 5) - 0.5
    fine = pfbm(70, seed + 5, 3) - 0.5
    # granular crust between the stones. Without it the earth between the
    # gravel is glassy smooth, and a smooth road with pebbles sitting on it
    # reads as pebbles on a floor rather than as gravel worked into a surface.
    crust = pridge(30, seed + 9, 3) - 0.42

    # drag marks: many directions, low contrast, so nothing aligns to an axis
    scuff = splat(size, 1100, seed + 23, size * 0.070, size * 0.009)

    # Shrinkage cracks. Thin, and only where the noise mask says the surface
    # dried out -- a crack network running edge to edge over the whole map is a
    # texture of cracked mud, not of a road that happens to crack in places.
    c1, c2, ccid, _, _ = pvoronoi(7, seed + 41, jitter=1.0, size=size)
    crack = np.clip(1.0 - (c2 - c1) * 18.0, 0, 1) ** 1.4
    crack = crack * np.clip(pfbm(9, seed + 43, 3) * 2.6 - 1.45, 0, 1)

    # Pebble albedo stays quiet on purpose. A stone 2 cm across is three screen
    # pixels; give it real contrast and a road turns into static. Its presence
    # is carried by the height channel, where the shading resolves it as relief
    # and the layer blend uses it to push gravel up through the grass edge.
    # Crucially its *tone* varies while its *lift* barely does: driving albedo
    # off the dome instead makes every stone light, which is a field of pale
    # eggs rather than gravel.
    value = (bed * 0.42
             + fine * 0.13
             + crust * 0.26
             + scuff * 0.22
             + peb_tone * 0.15
             + peb_h * 0.08
             + grit_tone * 0.09
             + grit_h * 0.05
             - crack * 0.30)

    height = (bed * 0.14
              + peb_h * 0.95
              + grit_h * 0.42
              + crust * 0.20
              + scuff * 0.14
              + fine * 0.10
              - crack * 0.70)

    grey = level(damp_lowfreq(value, 2.5, 0.45), 0.86, 0.38)
    rgb = tint(grey, bed, 0.026)
    hgt = level(height, TARGET_HEIGHT["Dirt"], 0.80)
    return rgb, hgt, 2.8


# ---------------------------------------------------------------------------
# layer 2 -- sand, planar XZ, 1.67 m per tile. The lake beach.
# ---------------------------------------------------------------------------

def build_sand(seed=903, size=SIZE):
    """Beach sand.

    Ripples are the whole character of sand and also the easiest way to put a
    corduroy grid on a shoreline, so they are held to a low amplitude, warped
    hard by two octaves of noise, and broken by scattered shell grit. The period
    count is a whole number of cycles per tile, which is what keeps them
    continuous across the wrap.
    """
    # Phases are counted in whole cycles per tile. Both the number of ripples
    # down the tile and the number the crests drift across it have to be whole
    # numbers or the ripples step at the wrap, which is exactly the artefact
    # that makes a beach read as corduroy.
    warp_a = (pfbm(6, seed + 71, 4) - 0.5) * 1.7
    warp_b = (pfbm(6, seed + 73, 4) - 0.5) * 1.1

    yn = np.arange(size)[:, None] / size + np.zeros((size, size))
    xn = np.arange(size)[None, :] / size + np.zeros((size, size))

    ripple = np.sin(2.0 * math.pi * (yn * 9.0 + xn * 2.0 + warp_a))
    # sand ripples have a sharp crest and a long lee slope
    ripple = np.sign(ripple) * np.abs(ripple) ** 0.7

    # a second, much longer swell running across the first
    swell = np.sin(2.0 * math.pi * (xn * 3.0 - yn * 1.0 + warp_b)) * 0.5

    # Where the ripples exist at all. A shoreline ripples in patches -- between
    # them the sand is smooth, or churned by feet. Without this mask the whole
    # beach is one continuous corduroy, which is the single loudest way to make
    # a tiling ground texture announce itself.
    combed = np.clip(pfbm(5, seed + 77, 3) * 2.2 - 0.65, 0, 1)
    ripple = ripple * combed
    swell = swell * (0.35 + 0.65 * combed)

    grain = pfbm(200, seed + 5, 2) - 0.5
    drift = pfbm(8, seed + 7, 4) - 0.5

    shell_tone, shell_h = discs(size, 1100, seed + 91,
                                size * 0.0018, size * 0.0050, dome=0.9)

    value = (ripple * 0.12
             + swell * 0.08
             + drift * 0.34
             + grain * 0.16
             + shell_tone * 0.06
             + shell_h * 0.14)

    height = (ripple * 0.60
              + swell * 0.24
              + drift * 0.22
              + shell_h * 0.34
              + grain * 0.10)

    grey = level(damp_lowfreq(value, 2.5, 0.50), 0.90, 0.26)
    rgb = tint(grey, drift, 0.020)
    hgt = level(height, TARGET_HEIGHT["Sand"], 0.66)
    return rgb, hgt, 2.2


# ---------------------------------------------------------------------------
# layer 3 -- rock, triplanar, ~3.8 m per tile. The cliffs.
# ---------------------------------------------------------------------------

def build_rock(seed=904, size=SIZE):
    """Stratified, fractured cliff stone.

    This layer is sampled triplanar, so the same image lands on the ZY, XZ and
    XY planes. On a cliff face the image's vertical axis is world vertical, so
    bands that vary down the image become horizontal bedding planes -- correct.
    On the flat XZ plane those same bands would become stripes, so the bedding
    is only ever a modulation: the structure that carries the layer is the
    isotropic block fracture underneath it, which reads the same in any
    projection.

    Each Worley cell is shaded as a tilted facet rather than a flat tone (that
    is what the returned site offset is for). A field of flat tones reads as
    camouflage; a field of tilted facets reads as broken stone, and it does so
    at every distance, which matters because these faces are visible from right
    across the map.
    """
    # Two scales of warp. The coarse one pulls the lattice off its grid; the
    # fine one makes the fracture lines wander instead of running dead straight
    # between two sites, which is most of the difference between "broken stone"
    # and "polygon mesh".
    wx = ((pfbm(4, seed + 501, 3, size=size) - 0.5) * size * 0.055 +
          (pfbm(22, seed + 505, 2, size=size) - 0.5) * size * 0.012)
    wy = ((pfbm(4, seed + 503, 3, size=size) - 0.5) * size * 0.055 +
          (pfbm(22, seed + 507, 2, size=size) - 0.5) * size * 0.012)

    # --- big blocks. 4 x 7 sites, so a block is about 0.95 m wide and 0.55 m
    # tall: a slab, which is how bedded rock breaks.
    b1, b2, bcid, box, boy = pvoronoi((4, 7), seed, jitter=0.9, size=size)
    btone = cell_random(bcid, seed + 11)
    r = _rng(seed + 12)
    tilt = r.rand(int(bcid.max()) + 2, 2) * 2.0 - 1.0
    bfacet = tilt[bcid, 0] * box + tilt[bcid, 1] * boy
    # The joint has to be a crack, not a grout line. A multiplier of 9 in cell
    # units puts the dark line at about a tenth of a block, roughly 10 cm of
    # shadowed fissure. The first pass used 3.4, which spread it over half a
    # metre and turned the cliff into a wall of cobbles set in mortar.
    bjoint = np.clip(1.0 - (b2 - b1) * 9.0, 0, 1) ** 1.3
    bfacet, btone, bjoint = (warp(f, wx, wy) for f in (bfacet, btone, bjoint))

    # --- smaller chips broken off the blocks ----------------------------
    s1, s2, scid, sox, soy = pvoronoi((11, 17), seed + 400, jitter=1.0, size=size)
    stone = cell_random(scid, seed + 411)
    r2 = _rng(seed + 412)
    tilt2 = r2.rand(int(scid.max()) + 2, 2) * 2.0 - 1.0
    sfacet = tilt2[scid, 0] * sox + tilt2[scid, 1] * soy
    sjoint = np.clip(1.0 - (s2 - s1) * 11.0, 0, 1) ** 1.3
    sfacet, stone, sjoint = (warp(f, wx, wy) for f in (sfacet, stone, sjoint))

    # Fractures terminate. A joint network where every single cell is fully
    # outlined is the signature of cracked mud, and it is what the eye uses to
    # tell dried clay from stone. Masking both networks with noise breaks some
    # of the outlines so blocks merge into larger masses in places, which is
    # what a cliff actually does.
    bjoint = bjoint * np.clip(pfbm(11, seed + 421, 4, size=size) * 2.1 - 0.55, 0, 1)
    sjoint = sjoint * np.clip(pfbm(17, seed + 423, 4, size=size) * 2.3 - 0.75, 0, 1)

    # --- bedding planes, varying down the image = horizontal on a face ---
    bed_warp = (pfbm(5, seed + 21, 5) - 0.5) * 1.5
    yy = np.arange(size)[:, None] / size + np.zeros((size, size))
    # A whole number of beds per tile, no linear tilt term, and the per-bed tone
    # indexed modulo that same whole number. All three are required together.
    # Bands are indexed, so anything that shears the index -- a fractional
    # cycle count, or a linear x tilt -- means the bed arriving at one edge is
    # not the bed leaving the other, and no amount of blending hides that. The
    # apparent tilt comes from the warp instead, which is periodic and so costs
    # nothing at the wrap.
    bands = 7
    bphase = yy * bands + bed_warp
    bidx = np.floor(bphase).astype(int)
    bedtone = cell_random(bidx % bands, seed + 27, count=bands)
    bfrac = bphase - np.floor(bphase)
    # A recessed seam *at* the bedding plane, plus a lit lip just above it. The
    # pair is what makes a band read as a layer of stone with a top face, rather
    # than as a stripe of paint. The first pass put the seam in the middle of
    # the band by mistake, which is precisely the stripe-of-paint result.
    edge = np.minimum(bfrac, 1.0 - bfrac)
    bseam = np.clip(1.0 - edge * 11.0, 0, 1) ** 1.2
    blip = np.clip(1.0 - bfrac * 16.0, 0, 1) ** 1.5

    # --- weathering ------------------------------------------------------
    # Rain-worn channels run down a face. On the triplanar XZ projection they
    # become streaks across flat ground instead, which is why they are held to
    # low contrast: this map has to survive being projected three ways.
    runnel = splat(size, 320, seed + 33, size * 0.11, size * 0.010,
                   angle=math.pi * 0.5, spread=0.28)
    fract = pridge(7, seed + 37, 5) - 0.35
    grit = pfbm(130, seed + 39, 3) - 0.5
    lichen = np.clip(pfbm(9, seed + 55, 4) * 2.2 - 1.15, 0, 1)

    value = (bfacet * 0.34
             + (btone - 0.5) * 0.20
             - bjoint * 0.85
             + sfacet * 0.28
             + (stone - 0.5) * 0.20
             - sjoint * 0.36
             + (bedtone - 0.5) * 0.16
             - bseam * 0.55
             + blip * 0.30
             + fract * 0.26
             + runnel * 0.20
             + grit * 0.10
             - lichen * 0.09)

    height = (bfacet * 0.50
              + (btone - 0.5) * 0.45
              - bjoint * 1.30
              + sfacet * 0.38
              + (stone - 0.5) * 0.24
              - sjoint * 0.65
              - bseam * 0.95
              + blip * 0.35
              + (bedtone - 0.5) * 0.30
              + fract * 0.30
              + grit * 0.08)

    grey = level(damp_lowfreq(value, 3.0, 0.35), 0.76, 0.68)
    rgb = tint(grey, lichen - bedtone * 0.4, 0.030)
    hgt = level(height, TARGET_HEIGHT["Rock"], 0.92)
    # The strongest relief of the four. This layer is seen edge-on on faces up
    # to 85 degrees rather than from above, so its normal map is doing most of
    # the work, and it is the one the player asked to read as rock.
    return rgb, hgt, 3.6


BUILDERS = {
    "Grass": build_grass,
    "Dirt": build_dirt,
    "Sand": build_sand,
    "Rock": build_rock,
}


# ---------------------------------------------------------------------------
# verification
# ---------------------------------------------------------------------------

def seam_report(name, channel, arr, margin=16):
    """Compare the step across the wrap against its immediate neighbours.

    Comparing it against the *average* interior step is misleading here. These
    maps are built on lattices whose cell boundaries fall on x = 0 and y = 0 by
    construction, so the wrap always lands on a line where the image genuinely
    is discontinuous -- as are the eight other cell boundaries across the tile.
    Measured against the whole-image mean that reads as a seam when it is only
    a cell edge, and the eight interior twins prove it is not.

    So the comparison is local: the wrap step against the mean step over the
    `margin` columns either side of it. Those neighbours sit in the same
    structure, so a ratio near 1 means the wrap is doing nothing the texture is
    not already doing 16 pixels away, which is the property that survives being
    tiled ten times across the screen. Above about 2 is a real seam.
    """
    out = []
    for axis, tag in ((1, "x"), (0, "y")):
        steps = np.abs(np.diff(arr, axis=axis)).mean(axis=1 - axis)
        local = np.concatenate([steps[:margin], steps[-margin:]]).mean()
        wrap = np.abs(np.take(arr, 0, axis=axis) -
                      np.take(arr, -1, axis=axis)).mean()
        out.append((tag, wrap / max(local, 1e-9)))
    log("  seam %-6s %-7s  %s" % (name, channel, "  ".join(
        "%s %.2fx" % (t, r) for t, r in out)))
    return max(r for _, r in out)


def tile_sheet(path, rgb, reps=3, out=768):
    """3x3 tiling of the map, downsampled to roughly what a screenful shows.

    Three repeats is not arbitrary: it is about what one screen of ground holds
    at the shipped scales, so a grid that only appears under repetition appears
    here too, at the same size the player would see it.
    """
    size = rgb.shape[0]
    big = np.tile(rgb, (reps, reps, 1))
    step = max(1, (size * reps) // out)
    trim = (size * reps // step) * step
    small = big[:trim, :trim].reshape(trim // step, step,
                                      trim // step, step, 3).mean(axis=(1, 3))
    return write_rgb(path, u8(small))


# The material's per-layer tints, so previews are judged as the ground the
# player sees rather than as a grey plate. Keep in step with
# M_Ground_TerrainBlend.mat.
LAYER_TINT = {
    "Grass": (0.45, 0.62, 0.28),
    "Dirt": (0.42, 0.31, 0.21),
    "Sand": (0.82, 0.74, 0.55),
    "Rock": (0.46, 0.45, 0.48),
}
SHADE_COLOR = (0.42, 0.50, 0.68)


def lit_preview(rgb, nrm, name, elev=38.0):
    """Approximate what PokeLab/TerrainBlend makes of this map.

    Tint, then the same three-band toon ramp with the same 0.2 light wrap and
    the same shadow tint the shader uses, then a flat ambient. It is not the
    real thing -- no shadows, no macro variation, no wetness -- but it answers
    the only question a raw albedo plate cannot: does the relief in the height
    channel actually read as relief once something lights it. A map can look
    perfectly good flat and still shade like linoleum.
    """
    n = nrm * 2.0 - 1.0
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    el = math.radians(elev)
    light = np.array([math.cos(el) * 0.7, math.cos(el) * 0.7, math.sin(el)])
    light /= np.linalg.norm(light)

    ndl = np.clip((n @ light + 0.2) / 1.2, 0.0, 1.0)
    band = np.round(ndl * 2.0) / 2.0

    albedo = rgb * np.array(LAYER_TINT[name])[None, None, :]
    shaded = albedo * np.array(SHADE_COLOR)[None, None, :]
    colour = shaded + (albedo - shaded) * band[..., None]
    colour += albedo * 0.28
    return np.clip(colour, 0.0, 1.0) ** (1.0 / 2.2)


# ---------------------------------------------------------------------------

def build(names=None, verify=False, size=SIZE):
    os.makedirs(OUT_DIR, exist_ok=True)
    names = names or list(BUILDERS.keys())
    worst = 0.0
    for name in names:
        log("%s  (%.2f m per tile, %.0f px/m)" %
            (name, TILE_METRES[name], size / TILE_METRES[name]))
        rgb, hgt, nstrength = BUILDERS[name](size=size)

        rgba = np.concatenate([u8(rgb), u8(hgt)[..., None]], axis=-1)
        base = os.path.join(OUT_DIR, "Env_Terrain_%s_BaseColor.png" % name)
        write_rgba(base, rgba)

        nrm = height_to_normal(hgt, nstrength)
        npath = os.path.join(OUT_DIR, "Env_Terrain_%s_Normal.png" % name)
        write_rgb(npath, u8(nrm))

        log("  albedo mean %.3f  height mean %.3f  range %.2f-%.2f" %
            (rgb.mean(), hgt.mean(), hgt.min(), hgt.max()))

        if verify:
            worst = max(worst, seam_report(name, "albedo", rgb.mean(axis=-1)))
            worst = max(worst, seam_report(name, "height", hgt))
            worst = max(worst, seam_report(name, "normal", nrm[..., 0]))
            os.makedirs(PREVIEW_DIR, exist_ok=True)
            lit = lit_preview(rgb, nrm, name)
            key = name.lower()
            # tiled and lit: the grid check, at roughly the on-screen size
            tile_sheet(os.path.join(PREVIEW_DIR,
                                    "terrain_layer_%s_tile3x3.png" % key), lit)
            # one tile at 1:1, which is close to the near-field pixel size
            half = size // 2
            write_rgb(os.path.join(PREVIEW_DIR,
                                   "terrain_layer_%s_detail.png" % key),
                      u8(lit[:half, :half]))
            tile_sheet(os.path.join(PREVIEW_DIR,
                                    "terrain_layer_%s_height3x3.png" % key),
                       np.repeat(hgt[..., None], 3, axis=-1))
    if verify:
        log("worst local seam ratio %.2fx "
            "(1.0 is a wrap indistinguishable from its neighbours; >2 is a seam)"
            % worst)
    log("wrote %d map pair(s) to %s" % (len(names), OUT_DIR))


def main():
    argv = sys.argv[1:]
    if "--" in argv:
        argv = argv[argv.index("--") + 1:]
    verify = "--verify" in argv
    names = [a for a in argv if not a.startswith("--")]
    names = [n.capitalize() for n in names] or None
    build(names, verify=verify)


if __name__ == "__main__":
    main()
