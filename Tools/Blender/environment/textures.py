"""
Procedural painterly atlas generator for the environment kit.

One 2048x2048 base-colour atlas + one 2048x2048 tangent-space normal map per
family, laid out as a 4x4 grid of 512px cells.  Cell index maps 1:1 onto the
material's `atlas_cell` custom property, and envlib.uv_pack_into_cells() drops
each material's UV islands into its cell.

The look target is stylised/painterly, not photoreal: saturated bases, visible
brush mottling, hand-drawn-ish pattern structure, soft normal relief.
"""

import os
import math
import numpy as np

import envlib as E

CELL = 512
GRID = 4
ATLAS = CELL * GRID

TEX_DIR_NAME = "Textures"


# --------------------------------------------------------------------------
# noise primitives
# --------------------------------------------------------------------------

def _rng(seed):
    return np.random.RandomState(seed & 0x7FFFFFFF)


def _bilinear(grid, out_h, out_w):
    gh, gw = grid.shape
    ys = np.linspace(0, gh, out_h, endpoint=False)
    xs = np.linspace(0, gw, out_w, endpoint=False)
    y0 = np.floor(ys).astype(int) % gh
    x0 = np.floor(xs).astype(int) % gw
    y1 = (y0 + 1) % gh
    x1 = (x0 + 1) % gw
    fy = (ys - np.floor(ys))[:, None]
    fx = (xs - np.floor(xs))[None, :]
    fy = fy * fy * (3 - 2 * fy)
    fx = fx * fx * (3 - 2 * fx)
    a = grid[np.ix_(y0, x0)]
    b = grid[np.ix_(y0, x1)]
    c = grid[np.ix_(y1, x0)]
    d = grid[np.ix_(y1, x1)]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy


def vnoise(freq, seed, h=CELL, w=CELL):
    r = _rng(seed)
    g = r.rand(max(2, int(freq)), max(2, int(freq)))
    return _bilinear(g, h, w)


def fbm2(freq, seed, octaves=5, gain=0.5, h=CELL, w=CELL):
    total = np.zeros((h, w))
    amp = 1.0
    norm = 0.0
    f = freq
    for i in range(octaves):
        total += vnoise(f, seed + i * 977, h, w) * amp
        norm += amp
        amp *= gain
        f *= 2.0
    return total / norm


def voronoi(npts, seed, h=CELL, w=CELL, jitter=1.0, metric='e'):
    """Returns (d1, d2, cell_id) with distances normalised roughly to 0..1."""
    r = _rng(seed)
    n = int(math.sqrt(npts)) + 1
    pts = []
    for gy in range(-1, n + 1):
        for gx in range(-1, n + 1):
            px = (gx + 0.5 + (r.rand() - 0.5) * jitter) / n
            py = (gy + 0.5 + (r.rand() - 0.5) * jitter) / n
            pts.append((px, py))
    pts = np.array(pts)
    xs = (np.arange(w) + 0.5) / w
    ys = (np.arange(h) + 0.5) / h
    X = xs[None, :]
    Y = ys[:, None]
    d1 = np.full((h, w), 1e9)
    d2 = np.full((h, w), 1e9)
    cid = np.zeros((h, w), dtype=np.int32)
    for i, (px, py) in enumerate(pts):
        dx = X - px
        dy = Y - py
        if metric == 'm':
            d = np.abs(dx) + np.abs(dy)
        else:
            d = np.sqrt(dx * dx + dy * dy)
        closer = d < d1
        d2 = np.where(closer, d1, np.minimum(d2, d))
        cid = np.where(closer, i, cid)
        d1 = np.where(closer, d, d1)
    return d1 * n, d2 * n, cid


def cell_random(cid, seed):
    r = _rng(seed)
    lut = r.rand(int(cid.max()) + 2)
    return lut[cid]


def brush(seed, count=260, length=0.22, width=0.012, h=CELL, w=CELL):
    """Soft directional brush-stroke field in -1..1, painterly."""
    r = _rng(seed)
    acc = np.zeros((h, w))
    xs = (np.arange(w) + 0.5) / w
    ys = (np.arange(h) + 0.5) / h
    X = xs[None, :]
    Y = ys[:, None]
    for _ in range(count):
        cx, cy = r.rand(), r.rand()
        ang = r.rand() * math.pi * 2.0
        ln = length * (0.4 + r.rand())
        wd = width * (0.5 + r.rand())
        ca, sa = math.cos(ang), math.sin(ang)
        dx = X - cx
        dy = Y - cy
        # wrap
        dx = dx - np.round(dx)
        dy = dy - np.round(dy)
        u = dx * ca + dy * sa
        v = -dx * sa + dy * ca
        m = np.exp(-(u / ln) ** 2 * 3.0 - (v / wd) ** 2 * 3.0)
        acc += m * (r.rand() * 2.0 - 1.0)
    mx = np.abs(acc).max()
    return acc / max(mx, 1e-6)


def hsv_shift(rgb, dh=0.0, ds=1.0, dv=1.0):
    import colorsys
    h, s, v = colorsys.rgb_to_hsv(*rgb)
    h = (h + dh) % 1.0
    return colorsys.hsv_to_rgb(h, min(1.0, s * ds), min(1.0, v * dv))


def mix(c0, c1, t):
    """c0,c1: rgb tuples; t: HxW array -> HxWx3"""
    t = np.clip(t, 0.0, 1.0)[..., None]
    a = np.array(c0, dtype=np.float64)[None, None, :]
    b = np.array(c1, dtype=np.float64)[None, None, :]
    return a * (1 - t) + b * t


def edge_darken(img, amt=0.18, pow_=2.4):
    h, w, _ = img.shape
    ys = np.abs((np.arange(h) + 0.5) / h * 2 - 1)[:, None]
    xs = np.abs((np.arange(w) + 0.5) / w * 2 - 1)[None, :]
    d = np.maximum(ys, xs) ** pow_
    return img * (1.0 - amt * d[..., None])


# --------------------------------------------------------------------------
# pattern painters -- each returns (rgb float HxWx3 in 0..1, height HxW 0..1)
# --------------------------------------------------------------------------

def p_painterly(seed, base, accent, freq=6, strokes=300, contrast=0.55):
    n = fbm2(freq, seed, 5)
    b = brush(seed + 11, strokes, 0.2, 0.014)
    t = np.clip(n * contrast + b * 0.32 + 0.28, 0, 1)
    img = mix(base, accent, t)
    img += (fbm2(28, seed + 5, 3) - 0.5)[..., None] * 0.05
    hgt = np.clip(n * 0.7 + b * 0.3 * 0.5 + 0.3, 0, 1)
    return img, hgt


def p_bark(seed, base, dark, light, ridge=26, crack=0.55):
    y = (np.arange(CELL)[:, None] / CELL)
    warp = fbm2(5, seed, 4) * 0.22
    x = (np.arange(CELL)[None, :] / CELL) + warp + y * 0.06
    ridges = np.abs(np.sin(x * math.pi * ridge + fbm2(3, seed + 3, 3) * 4.0))
    ridges = ridges ** 0.6
    grain = fbm2(60, seed + 7, 3)
    cracks = np.clip(1.0 - ridges * 1.5, 0, 1) ** 2
    t = np.clip(ridges * 0.8 + grain * 0.25 - cracks * crack, 0, 1)
    img = mix(dark, light, t)
    img = img * (1.0 - cracks[..., None] * 0.45)
    # lichen patches
    lich = np.clip(fbm2(7, seed + 21, 4) * 1.9 - 1.05, 0, 1)
    lich_col = np.array((0.42, 0.52, 0.30))[None, None, :]
    tint = np.array(base)[None, None, :]
    img = img + (tint - img) * 0.12
    img = img + (lich_col - img) * (lich[..., None] * 0.5)
    hgt = np.clip(ridges * 0.85 - cracks * 0.6 + 0.35, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_leaf(seed, base, dark, light, clumps=90, vein=(0.55, 0.72, 0.30)):
    """Two scales on purpose.  The coarse voronoi (~1 clump per UV island)
    gives every leaf puff in a canopy its own tint; the fine layer gives the
    individual faces inside one puff something to vary against, which is what
    stops a canopy reading as flat plastic."""
    d1, d2, cid = voronoi(clumps, seed, jitter=1.0)
    blob = np.clip(1.0 - d1 * 1.15, 0, 1) ** 0.55
    tone = cell_random(cid, seed + 4)
    t = np.clip(blob * 0.75 + tone * 0.42 - 0.1, 0, 1)
    img = mix(dark, light, t)
    # leaf edge rim light
    rim = np.clip((d2 - d1) * 2.4, 0, 1)
    img += (np.array(light)[None, None, :] - img) * ((1 - rim)[..., None] * 0.28)
    # fine leafy detail -- one leaf per few pixels
    f1, f2, fcid = voronoi(clumps * 9, seed + 601, jitter=1.0)
    fine_tone = cell_random(fcid, seed + 607)
    fine_edge = np.clip((f2 - f1) * 5.0, 0, 1)
    img *= (0.80 + 0.30 * fine_tone)[..., None]
    img *= (0.74 + 0.26 * fine_edge)[..., None]
    # veins
    v = np.abs(np.sin((np.arange(CELL)[None, :] / CELL) * math.pi * 34 +
                      fbm2(6, seed + 9, 3) * 7.0))
    veins = np.clip(1 - v * 6.0, 0, 1) * blob
    img += (np.array(vein)[None, None, :] - img) * (veins[..., None] * 0.28)
    img += (fbm2(60, seed + 13, 3) - 0.5)[..., None] * 0.05
    hgt = np.clip(blob * 0.55 + fine_edge * 0.45, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_blades(seed, root, tip, count=70, tilt=0.10):
    y = (np.arange(CELL)[:, None] / CELL)          # 0 top .. 1 bottom
    x = (np.arange(CELL)[None, :] / CELL)
    warp = (fbm2(4, seed, 3) - 0.5) * tilt
    b = np.abs(np.sin((x + warp + y * 0.12) * math.pi * count))
    blade = np.clip(1.0 - b * 2.2, 0, 1) ** 0.6
    grad = 1.0 - y                                  # tip lighter at top
    t = np.clip(grad * 0.85 + blade * 0.35 + fbm2(9, seed + 2, 3) * 0.2 - 0.15, 0, 1)
    img = mix(root, tip, t)
    img *= (0.72 + 0.28 * blade)[..., None]
    hgt = np.clip(blade * 0.7 + grad * 0.3, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_rock(seed, base, dark, light, plates=48, speck=0.10, erosion=0.5):
    d1, d2, cid = voronoi(plates, seed, jitter=0.95)
    plate = cell_random(cid, seed + 17)
    crack = np.clip((d2 - d1) * 3.2, 0, 1)
    n = fbm2(9, seed + 3, 6)
    # erosion: smear downward
    er = fbm2(4, seed + 31, 3)
    t = np.clip(n * 0.55 + plate * 0.32 + er * erosion * 0.25 - 0.12, 0, 1)
    img = mix(dark, light, t)
    img *= (0.55 + 0.45 * np.clip(crack * 1.3, 0, 1))[..., None]
    sp = (fbm2(120, seed + 41, 2) > (1.0 - speck)).astype(np.float64)
    img += (np.array(light)[None, None, :] - img) * (sp[..., None] * 0.45)
    img += (mix((0, 0, 0), base, np.ones((CELL, CELL))) - img) * 0.18
    hgt = np.clip(t * 0.7 + crack * 0.3, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_strata(seed, cols, bands=11, tilt=0.06, weather=0.45):
    y = (np.arange(CELL)[:, None] / CELL)
    x = (np.arange(CELL)[None, :] / CELL)
    warp = (fbm2(5, seed, 5) - 0.5) * 0.26
    yy = y + x * tilt + warp
    idx = np.floor(yy * bands).astype(int) % len(cols)
    img = np.zeros((CELL, CELL, 3))
    for i, c in enumerate(cols):
        m = (idx == i)
        img[m] = c
    band_edge = np.abs(((yy * bands) % 1.0) - 0.5) * 2.0
    # a dark seam right at each bedding plane reads as stone, not as a stripe
    seam = np.clip(1.0 - band_edge * 4.0, 0, 1)
    img *= (0.62 + 0.38 * band_edge)[..., None]
    img *= (1.0 - seam * 0.42)[..., None]
    # broken blocky joints across the beds
    d1, d2, cid = voronoi(150, seed + 91, jitter=0.85, metric='m')
    joint = np.clip((d2 - d1) * 5.0, 0, 1)
    img *= (0.66 + 0.34 * joint)[..., None]
    img *= (0.86 + 0.28 * cell_random(cid, seed + 97))[..., None]
    img += (fbm2(40, seed + 23, 5) - 0.5)[..., None] * 0.08
    hgt = np.clip(0.5 + (band_edge - 0.5) * 0.5 + joint * 0.35 +
                  fbm2(30, seed + 5, 3) * 0.2, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_tiles(seed, base, dark, light, rows=11, cols=9, scallop=True, mortar=0.035):
    y = (np.arange(CELL)[:, None] / CELL) * rows
    x = (np.arange(CELL)[None, :] / CELL) * cols
    row = np.floor(y).astype(int)
    x = x + (row % 2) * 0.5
    fy = y - np.floor(y)
    fx = x - np.floor(x)
    if scallop:
        dxc = (fx - 0.5) * 2.0
        curve = 1.0 - np.sqrt(np.clip(1 - dxc * dxc, 0, 1)) * 0.55
        edge = np.clip((fy - curve) * 6.0, 0, 1)
    else:
        edge = np.clip(np.minimum(fy, 1 - fy) * 8.0, 0, 1)
    side = np.clip(np.minimum(fx, 1 - fx) / mortar, 0, 1)
    m = np.minimum(edge, side)
    tone = cell_random((row * 37 + np.floor(x).astype(int)) % 500, seed + 3)
    t = np.clip(fy * 0.75 + tone * 0.4, 0, 1)
    img = mix(dark, light, t)
    img *= (0.42 + 0.58 * m)[..., None]
    img += (fbm2(30, seed + 11, 3) - 0.5)[..., None] * 0.05
    hgt = np.clip(m * 0.65 + (1 - fy) * 0.35, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_planks(seed, base, dark, light, count=6, vertical=False, nails=True):
    a = (np.arange(CELL)[:, None] / CELL) * count
    b = (np.arange(CELL)[None, :] / CELL)
    if vertical:
        a = (np.arange(CELL)[None, :] / CELL) * count
        b = (np.arange(CELL)[:, None] / CELL)
    a = a + np.zeros((CELL, CELL))
    b = b + np.zeros((CELL, CELL))
    pi = np.floor(a).astype(int)
    fa = a - pi
    gap = np.clip(np.minimum(fa, 1 - fa) * 26.0, 0, 1)
    tone = cell_random(pi % 400, seed + 5)
    grain_f = 24 + tone * 20
    grain = np.abs(np.sin((fa * 3.0 + b * 1.2) * math.pi * 6 +
                          fbm2(8, seed + 9, 4) * 9.0))
    t = np.clip(tone * 0.45 + grain * 0.42 + fbm2(16, seed + 2, 4) * 0.3 - 0.1, 0, 1)
    img = mix(dark, light, t)
    img *= (0.35 + 0.65 * gap)[..., None]
    if nails:
        nx = ((b * 6) % 1.0)
        nsp = ((np.abs(nx - 0.5) < 0.02) & (np.abs(fa - 0.18) < 0.02)) | \
              ((np.abs(nx - 0.5) < 0.02) & (np.abs(fa - 0.82) < 0.02))
        img[nsp] = np.array(dark) * 0.6
    hgt = np.clip(gap * 0.6 + grain * 0.25 + 0.15, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_cobble(seed, stone_lo, stone_hi, mortar, count=64, worn=0.4):
    d1, d2, cid = voronoi(count, seed, jitter=0.85)
    edge = np.clip((d2 - d1) * 4.2, 0, 1)
    tone = cell_random(cid, seed + 13)
    dome = np.clip(1.0 - d1 * 0.9, 0, 1)
    img = mix(stone_lo, stone_hi, np.clip(tone * 0.8 + fbm2(20, seed + 3, 4) * 0.3, 0, 1))
    mort = 1.0 - edge
    img = img * (1 - mort[..., None]) + np.array(mortar)[None, None, :] * mort[..., None]
    img *= (0.78 + 0.22 * dome)[..., None]
    # worn centre path
    y = (np.arange(CELL)[:, None] / CELL)
    x = (np.arange(CELL)[None, :] / CELL)
    wear = np.exp(-((x - 0.5) ** 2) / 0.05) * worn
    img += (np.array(stone_hi)[None, None, :] - img) * (wear[..., None] * 0.35)
    img += (fbm2(40, seed + 27, 3) - 0.5)[..., None] * 0.045
    hgt = np.clip(edge * 0.7 + dome * 0.3, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_plaster(seed, base, grime, freq=8):
    n = fbm2(freq, seed, 6)
    b = brush(seed + 3, 200, 0.26, 0.03)
    t = np.clip(n * 0.4 + b * 0.25 + 0.5, 0, 1)
    img = mix(hsv_shift(base, 0, 1.0, 0.86), hsv_shift(base, 0, 0.9, 1.08), t)
    y = (np.arange(CELL)[:, None] / CELL) + np.zeros((CELL, CELL))
    g = np.clip((y - 0.62) * 2.5, 0, 1) * np.clip(fbm2(12, seed + 8, 4) * 1.6 - 0.3, 0, 1)
    img += (np.array(grime)[None, None, :] - img) * (g[..., None] * 0.5)
    hgt = np.clip(n * 0.6 + 0.35 + b * 0.12, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_metal(seed, base, hi, streaks=1.0, scuff=0.35):
    x = (np.arange(CELL)[None, :] / CELL) + np.zeros((CELL, CELL))
    s = fbm2(3, seed, 3, h=CELL, w=8)
    s = np.repeat(s[:, :1], CELL, axis=1)
    fine = vnoise(CELL // 2, seed + 5)
    fine = np.repeat(fine[:, :1], CELL, axis=1)
    t = np.clip(0.45 + (fine - 0.5) * 0.5 * streaks + (s - 0.5) * 0.4, 0, 1)
    img = mix(base, hi, t)
    sc = np.clip(fbm2(18, seed + 11, 4) * 1.7 - 0.85, 0, 1) * scuff
    img *= (1 - sc[..., None] * 0.4)
    hgt = np.clip(0.5 + (fine - 0.5) * 0.25, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_grad(seed, a, b, vertical=True, glow=0.0, sheen=0.25):
    t = (np.arange(CELL)[:, None] / CELL) if vertical else (np.arange(CELL)[None, :] / CELL)
    t = t + np.zeros((CELL, CELL))
    img = mix(a, b, t)
    # diagonal sheen band
    d = (np.arange(CELL)[:, None] + np.arange(CELL)[None, :]) / (2.0 * CELL)
    band = np.exp(-((d - 0.34) ** 2) / 0.004) + np.exp(-((d - 0.46) ** 2) / 0.0012)
    img += np.clip(band, 0, 1)[..., None] * sheen
    if glow > 0:
        img += glow * 0.15
    hgt = np.full((CELL, CELL), 0.5)
    return np.clip(img, 0, 1), hgt


def p_fabric(seed, base, dark, weave=110):
    x = (np.arange(CELL)[None, :] / CELL) * weave
    y = (np.arange(CELL)[:, None] / CELL) * weave
    w = (np.sin(x * math.pi * 2) * np.sin(y * math.pi * 2) + 1) * 0.5
    w = w + np.zeros((CELL, CELL))
    n = fbm2(10, seed, 4)
    t = np.clip(w * 0.4 + n * 0.5 + 0.15, 0, 1)
    img = mix(dark, base, t)
    hgt = np.clip(w * 0.5 + 0.3, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_stripe(seed, a, b, count=8, vertical=True):
    t = (np.arange(CELL)[None, :] / CELL) if vertical else (np.arange(CELL)[:, None] / CELL)
    t = t + np.zeros((CELL, CELL))
    band = (np.floor(t * count).astype(int) % 2).astype(np.float64)
    n = fbm2(12, seed, 4)
    img = mix(a, b, band)
    img *= (0.88 + 0.12 * n)[..., None]
    hgt = np.full((CELL, CELL), 0.5) + (n - 0.5) * 0.2
    return np.clip(img, 0, 1), np.clip(hgt, 0, 1)


def p_petals(seed, colours):
    """Multi-hue flower swatch: horizontal bands of petal colours with a
    soft radial petal shading, so a petal card can pick a band by UV."""
    n = len(colours)
    y = (np.arange(CELL)[:, None] / CELL) * n
    band = np.clip(np.floor(y).astype(int), 0, n - 1) + np.zeros((CELL, CELL), dtype=int)
    fy = (y - np.floor(y)) + np.zeros((CELL, CELL))
    img = np.zeros((CELL, CELL, 3))
    for i, c in enumerate(colours):
        m = (band == i)
        lightc = hsv_shift(c, 0.01, 0.72, 1.22)
        darkc = hsv_shift(c, -0.02, 1.0, 0.7)
        grad = np.clip(1.0 - np.abs(fy - 0.5) * 2.0, 0, 1) ** 0.7
        layer = mix(darkc, lightc, grad * 0.75 + fbm2(14, seed + i * 31, 4) * 0.35)
        img[m] = layer[m]
    x = (np.arange(CELL)[None, :] / CELL) + np.zeros((CELL, CELL))
    ribs = np.abs(np.sin(x * math.pi * 9))
    img *= (0.82 + 0.18 * ribs)[..., None]
    hgt = np.clip(ribs * 0.4 + 0.4, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_moss(seed, base, dark, light):
    d1, d2, cid = voronoi(180, seed, jitter=1.0)
    blob = np.clip(1.0 - d1 * 1.05, 0, 1) ** 0.4
    t = np.clip(blob * 0.7 + fbm2(30, seed + 3, 4) * 0.5 - 0.1, 0, 1)
    img = mix(dark, light, t)
    img += (np.array(base)[None, None, :] - img) * 0.2
    hgt = np.clip(blob * 0.8 + 0.2, 0, 1)
    return np.clip(img, 0, 1), hgt


def p_water_stone(seed, base, dark, light, wet=0.6):
    img, hgt = p_rock(seed, base, dark, light, plates=40, speck=0.06)
    y = (np.arange(CELL)[:, None] / CELL) + np.zeros((CELL, CELL))
    darkening = np.clip((y - 0.35) * 1.6, 0, 1) * wet
    img *= (1.0 - darkening[..., None] * 0.42)
    algae = np.clip(fbm2(9, seed + 55, 4) * 1.8 - 0.9, 0, 1) * np.clip(y * 1.4, 0, 1)
    img += (np.array((0.24, 0.42, 0.24))[None, None, :] - img) * (algae[..., None] * 0.55)
    return np.clip(img, 0, 1), hgt


# --------------------------------------------------------------------------
# atlas assembly
# --------------------------------------------------------------------------

def height_to_normal(hgt, strength=2.6):
    h = hgt.astype(np.float64)
    gx = np.zeros_like(h)
    gy = np.zeros_like(h)
    gx[:, 1:-1] = (h[:, 2:] - h[:, :-2]) * 0.5
    gy[1:-1, :] = (h[2:, :] - h[:-2, :]) * 0.5
    nx = -gx * strength
    ny = -gy * strength
    nz = np.ones_like(h)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    nx, ny, nz = nx / ln, ny / ln, nz / ln
    out = np.stack([nx * 0.5 + 0.5, ny * 0.5 + 0.5, nz * 0.5 + 0.5], axis=-1)
    return out


def build_atlas(family, cells, out_dir, normal_strength=2.6):
    """cells: list of 16 (name, painter_callable) -- painter returns (rgb,hgt)."""
    base = np.zeros((ATLAS, ATLAS, 3))
    hgt = np.zeros((ATLAS, ATLAS))
    means = {}
    for i in range(GRID * GRID):
        if i < len(cells):
            name, fn = cells[i]
            rgb, hh = fn()
            means[name] = [float(x) for x in rgb.reshape(-1, 3).mean(axis=0)]
        else:
            rgb = np.zeros((CELL, CELL, 3)) + 0.5
            hh = np.full((CELL, CELL), 0.5)
        rgb = edge_darken(rgb, 0.10, 3.0)
        col = i % GRID
        row = i // GRID
        # atlas rows: cell row 0 is the BOTTOM of UV space, image row 0 is top
        y0 = (GRID - 1 - row) * CELL
        x0 = col * CELL
        base[y0:y0 + CELL, x0:x0 + CELL, :] = rgb
        hgt[y0:y0 + CELL, x0:x0 + CELL] = hh
    nrm = height_to_normal(hgt, normal_strength)
    os.makedirs(out_dir, exist_ok=True)
    bp = os.path.join(out_dir, "Env_%s_Atlas_BaseColor.png" % family)
    npth = os.path.join(out_dir, "Env_%s_Atlas_Normal.png" % family)
    E.write_png(bp, (np.clip(base, 0, 1) * 255.0 + 0.5).astype(np.uint8))
    E.write_png(npth, (np.clip(nrm, 0, 1) * 255.0 + 0.5).astype(np.uint8))
    import json
    with open(os.path.join(out_dir, "Env_%s_Atlas_Colors.json" % family), "w",
              encoding="utf-8") as f:
        json.dump(means, f, indent=2)
    return {"base": bp, "normal": npth}


# --------------------------------------------------------------------------
# per-family cell tables
# --------------------------------------------------------------------------

def _c(fn, *a, **kw):
    return lambda: fn(*a, **kw)


FOLIAGE_CELLS = [
    ("bark_broadleaf", _c(p_bark, 101, (0.40, 0.29, 0.20), (0.18, 0.12, 0.09), (0.58, 0.44, 0.30), 40)),
    ("bark_conifer",   _c(p_bark, 102, (0.34, 0.22, 0.16), (0.13, 0.09, 0.07), (0.50, 0.33, 0.22), 54, 0.7)),
    ("bark_birch",     _c(p_bark, 103, (0.80, 0.79, 0.74), (0.22, 0.22, 0.22), (0.94, 0.93, 0.88), 16, 0.9)),
    ("bark_palm",      _c(p_bark, 104, (0.47, 0.38, 0.25), (0.24, 0.18, 0.12), (0.66, 0.55, 0.36), 26, 0.5)),
    ("leaf_a",         _c(p_leaf, 111, (0.34, 0.60, 0.18), (0.16, 0.36, 0.10), (0.60, 0.86, 0.30), 80)),
    ("leaf_b",         _c(p_leaf, 112, (0.12, 0.36, 0.26), (0.04, 0.16, 0.13), (0.24, 0.54, 0.36), 110)),
    ("leaf_autumn",    _c(p_leaf, 113, (0.72, 0.38, 0.10), (0.42, 0.14, 0.05), (0.94, 0.66, 0.20), 90, (0.80, 0.44, 0.16))),
    ("needle",         _c(p_blades, 114, (0.06, 0.20, 0.14), (0.30, 0.56, 0.28), 210, 0.05)),
    ("grass",          _c(p_blades, 121, (0.17, 0.34, 0.11), (0.66, 0.82, 0.30), 105, 0.14)),
    ("bush_leaf",      _c(p_leaf, 122, (0.24, 0.50, 0.18), (0.09, 0.24, 0.09), (0.50, 0.78, 0.26), 120)),
    ("fern",           _c(p_blades, 123, (0.09, 0.27, 0.13), (0.38, 0.68, 0.26), 88, 0.22)),
    ("reed",           _c(p_blades, 124, (0.30, 0.38, 0.16), (0.78, 0.78, 0.36), 78, 0.08)),
    ("flower",         _c(p_petals, 131, [(0.90, 0.26, 0.34), (0.96, 0.74, 0.22),
                                          (0.62, 0.42, 0.86), (0.98, 0.94, 0.90)])),
    ("lilypad",        _c(p_leaf, 132, (0.20, 0.46, 0.24), (0.10, 0.30, 0.16), (0.40, 0.66, 0.30), 26)),
    ("moss",           _c(p_moss, 133, (0.22, 0.40, 0.20), (0.08, 0.20, 0.10), (0.44, 0.66, 0.28))),
    ("vine",           _c(p_leaf, 134, (0.18, 0.42, 0.20), (0.06, 0.20, 0.10), (0.44, 0.72, 0.30), 55)),
]

TERRAIN_CELLS = [
    ("rock_grey",   _c(p_rock, 201, (0.44, 0.45, 0.48), (0.22, 0.23, 0.27), (0.68, 0.69, 0.72), 40)),
    ("rock_warm",   _c(p_rock, 202, (0.52, 0.44, 0.36), (0.28, 0.22, 0.17), (0.76, 0.66, 0.52), 36)),
    ("cliff_strata", _c(p_strata, 203, [(0.50, 0.44, 0.36), (0.42, 0.38, 0.33),
                                        (0.58, 0.50, 0.39), (0.36, 0.32, 0.29),
                                        (0.54, 0.47, 0.37)], 13, 0.08)),
    ("cave_rock",   _c(p_rock, 204, (0.30, 0.31, 0.36), (0.12, 0.13, 0.17), (0.48, 0.50, 0.56), 34)),
    ("dirt",        _c(p_painterly, 205, (0.30, 0.22, 0.14), (0.50, 0.39, 0.24), 9, 340)),
    ("sand",        _c(p_painterly, 206, (0.66, 0.58, 0.42), (0.84, 0.78, 0.60), 12, 260)),
    ("gravel",      _c(p_rock, 207, (0.46, 0.44, 0.40), (0.24, 0.23, 0.21), (0.70, 0.67, 0.60), 110, 0.16)),
    ("stalactite",  _c(p_strata, 208, [(0.46, 0.44, 0.46), (0.56, 0.53, 0.52),
                                       (0.38, 0.37, 0.40), (0.62, 0.58, 0.55)], 17, 0.02, 0.25)),
    ("moss_rock",   _c(p_moss, 209, (0.26, 0.40, 0.22), (0.10, 0.18, 0.10), (0.46, 0.62, 0.28))),
    ("wet_rock",    _c(p_water_stone, 210, (0.34, 0.36, 0.40), (0.14, 0.16, 0.20), (0.54, 0.57, 0.62))),
    ("wood_bridge", _c(p_planks, 211, (0.46, 0.32, 0.19), (0.24, 0.16, 0.10), (0.62, 0.46, 0.28), 7)),
    ("rope",        _c(p_blades, 212, (0.44, 0.34, 0.18), (0.72, 0.60, 0.36), 90, 0.02)),
    ("waterfall_stone", _c(p_water_stone, 213, (0.38, 0.42, 0.44), (0.16, 0.20, 0.24), (0.60, 0.66, 0.68), 0.8)),
    ("stepping",    _c(p_cobble, 214, (0.40, 0.41, 0.42), (0.62, 0.62, 0.60), (0.26, 0.26, 0.25), 22, 0.5)),
    ("rubble",      _c(p_rock, 215, (0.40, 0.39, 0.40), (0.18, 0.18, 0.20), (0.62, 0.61, 0.60), 90, 0.2)),
    ("earth_accent", _c(p_painterly, 216, (0.24, 0.28, 0.16), (0.44, 0.48, 0.24), 8, 300)),
]

TOWN_CELLS = [
    ("plaster_cream", _c(p_plaster, 301, (0.86, 0.79, 0.64), (0.48, 0.42, 0.32))),
    ("plaster_blue",  _c(p_plaster, 302, (0.62, 0.74, 0.84), (0.34, 0.40, 0.48))),
    ("plaster_red",   _c(p_plaster, 303, (0.84, 0.56, 0.46), (0.44, 0.28, 0.24))),
    ("wood_beam",     _c(p_planks, 304, (0.40, 0.26, 0.16), (0.20, 0.12, 0.07), (0.54, 0.37, 0.22), 8, False, False)),
    ("roof_red",      _c(p_tiles, 305, (0.72, 0.28, 0.22), (0.40, 0.12, 0.10), (0.90, 0.46, 0.30), 26, 22)),
    ("roof_blue",     _c(p_tiles, 306, (0.26, 0.40, 0.62), (0.10, 0.18, 0.34), (0.44, 0.62, 0.84), 26, 22)),
    ("roof_green",    _c(p_tiles, 307, (0.24, 0.46, 0.34), (0.09, 0.22, 0.16), (0.42, 0.68, 0.46), 30, 18, False)),
    ("metal_roof",    _c(p_stripe, 308, (0.66, 0.72, 0.77), (0.44, 0.52, 0.60), 30)),
    ("window_glass",  _c(p_grad, 309, (0.05, 0.11, 0.17), (0.20, 0.36, 0.46), True, 0.0, 0.16)),
    ("door_wood",     _c(p_planks, 310, (0.44, 0.28, 0.16), (0.22, 0.13, 0.07), (0.60, 0.40, 0.22), 10, True, True)),
    ("paving",        _c(p_cobble, 311, (0.50, 0.48, 0.45), (0.72, 0.70, 0.65), (0.34, 0.32, 0.30), 130, 0.55)),
    ("stone_wall",    _c(p_cobble, 312, (0.46, 0.45, 0.43), (0.66, 0.64, 0.60), (0.30, 0.29, 0.28), 90, 0.1)),
    ("fence_wood",    _c(p_planks, 313, (0.50, 0.38, 0.24), (0.26, 0.18, 0.11), (0.68, 0.54, 0.34), 9, True, True)),
    ("lamp_metal",    _c(p_metal, 314, (0.16, 0.17, 0.19), (0.42, 0.44, 0.48), 0.6, 0.5)),
    ("awning",        _c(p_stripe, 315, (0.86, 0.36, 0.32), (0.95, 0.93, 0.88), 7, False)),
    ("trim_white",    _c(p_plaster, 316, (0.86, 0.85, 0.81), (0.54, 0.52, 0.49))),
]

PROPS_CELLS = [
    ("ball_red",      _c(p_grad, 401, (0.74, 0.10, 0.10), (0.96, 0.30, 0.22), True, 0.0, 0.22)),
    ("ball_white",    _c(p_grad, 402, (0.80, 0.82, 0.86), (0.99, 0.99, 1.00), True, 0.0, 0.26)),
    ("ball_black",    _c(p_grad, 403, (0.05, 0.05, 0.07), (0.20, 0.21, 0.24), True, 0.0, 0.18)),
    ("ball_button",   _c(p_grad, 404, (0.86, 0.88, 0.92), (1.00, 1.00, 1.00), False, 0.0, 0.42)),
    ("metal",         _c(p_metal, 405, (0.34, 0.36, 0.40), (0.72, 0.76, 0.82), 1.0, 0.3)),
    ("plastic_white", _c(p_plaster, 406, (0.90, 0.91, 0.93), (0.62, 0.64, 0.68))),
    ("screen",        _c(p_grad, 407, (0.02, 0.16, 0.24), (0.10, 0.62, 0.74), True, 0.6, 0.35)),
    ("rubber",        _c(p_fabric, 408, (0.16, 0.17, 0.20), (0.06, 0.07, 0.09), 150)),
    ("machine_teal",  _c(p_plaster, 409, (0.20, 0.62, 0.62), (0.10, 0.32, 0.34))),
    ("machine_yellow", _c(p_plaster, 410, (0.94, 0.76, 0.22), (0.56, 0.42, 0.10))),
    ("wood_desk",     _c(p_planks, 411, (0.44, 0.30, 0.19), (0.24, 0.15, 0.09), (0.60, 0.44, 0.28), 4, False, False)),
    ("glass_tube",    _c(p_grad, 412, (0.30, 0.62, 0.70), (0.80, 0.96, 0.98), True, 0.25, 0.45)),
    ("lab_panel",     _c(p_stripe, 413, (0.82, 0.84, 0.87), (0.66, 0.70, 0.75), 14, False)),
    ("accent_orange", _c(p_plaster, 414, (0.95, 0.52, 0.14), (0.55, 0.28, 0.06))),
    ("dark_grey",     _c(p_plaster, 415, (0.20, 0.21, 0.24), (0.10, 0.10, 0.12))),
    ("emissive_cyan", _c(p_grad, 416, (0.20, 0.86, 0.92), (0.62, 0.99, 1.00), True, 0.8, 0.5)),
]

CHAR_CELLS = [
    ("skin_light",  _c(p_plaster, 501, (0.96, 0.80, 0.68), (0.72, 0.52, 0.42))),
    ("skin_mid",    _c(p_plaster, 502, (0.82, 0.62, 0.46), (0.54, 0.36, 0.26))),
    ("skin_dark",   _c(p_plaster, 503, (0.56, 0.38, 0.26), (0.32, 0.20, 0.14))),
    ("hair_brown",  _c(p_blades, 504, (0.18, 0.10, 0.06), (0.48, 0.30, 0.16), 34, 0.2)),
    ("hair_black",  _c(p_blades, 505, (0.05, 0.05, 0.08), (0.26, 0.26, 0.32), 34, 0.2)),
    ("hair_blonde", _c(p_blades, 506, (0.52, 0.36, 0.12), (0.94, 0.82, 0.44), 34, 0.2)),
    ("shirt_red",   _c(p_fabric, 507, (0.82, 0.24, 0.22), (0.48, 0.12, 0.12), 120)),
    ("shirt_blue",  _c(p_fabric, 508, (0.24, 0.44, 0.76), (0.12, 0.22, 0.44), 120)),
    ("jacket_green", _c(p_fabric, 509, (0.22, 0.48, 0.34), (0.10, 0.24, 0.18), 110)),
    ("denim",       _c(p_fabric, 510, (0.28, 0.36, 0.54), (0.16, 0.21, 0.34), 160)),
    ("shoes",       _c(p_fabric, 511, (0.90, 0.92, 0.94), (0.20, 0.22, 0.26), 90)),
    ("cap_red",     _c(p_fabric, 512, (0.86, 0.22, 0.20), (0.94, 0.94, 0.96), 100)),
    ("backpack",    _c(p_fabric, 513, (0.86, 0.62, 0.20), (0.44, 0.30, 0.10), 100)),
    ("belt",        _c(p_planks, 514, (0.34, 0.22, 0.14), (0.16, 0.10, 0.06), (0.48, 0.32, 0.20), 2, False, False)),
    ("face",        _c(p_plaster, 515, (0.98, 0.86, 0.76), (0.80, 0.60, 0.50))),
    ("accent",      _c(p_plaster, 516, (0.16, 0.18, 0.22), (0.08, 0.08, 0.10))),
]

FAMILY_CELLS = {
    "Foliage": FOLIAGE_CELLS,
    "Terrain": TERRAIN_CELLS,
    "Town": TOWN_CELLS,
    "Props": PROPS_CELLS,
    "Characters": CHAR_CELLS,
}


def cell_index(family, surface):
    for i, (n, _) in enumerate(FAMILY_CELLS[family]):
        if n == surface:
            return i
    raise KeyError("%s/%s" % (family, surface))


def atlas_dir(family):
    d = E.FAMILY_DIR[family]
    return os.path.join(d, TEX_DIR_NAME)


def atlas_paths(family):
    d = atlas_dir(family)
    return {"base": os.path.join(d, "Env_%s_Atlas_BaseColor.png" % family),
            "normal": os.path.join(d, "Env_%s_Atlas_Normal.png" % family)}


def ensure_atlas(family, force=False):
    p = atlas_paths(family)
    if not force and os.path.exists(p["base"]) and os.path.exists(p["normal"]):
        return p
    E.log("building %s atlas ..." % family)
    return build_atlas(family, FAMILY_CELLS[family], atlas_dir(family))


_COLOR_CACHE = {}


def load_colors(family):
    if family in _COLOR_CACHE:
        return _COLOR_CACHE[family]
    import json
    p = os.path.join(atlas_dir(family), "Env_%s_Atlas_Colors.json" % family)
    cols = {}
    if os.path.exists(p):
        with open(p, "r", encoding="utf-8") as f:
            cols = json.load(f)
    _COLOR_CACHE[family] = cols
    return cols


# roughness per surface keyword -- stylised, mostly matte
_ROUGH = {
    "glass": 0.12, "window_glass": 0.10, "metal": 0.34, "lamp_metal": 0.38,
    "metal_roof": 0.42, "screen": 0.15, "glass_tube": 0.12,
    "ball_red": 0.30, "ball_white": 0.30, "ball_black": 0.34,
    "ball_button": 0.18, "plastic_white": 0.42, "emissive_cyan": 0.20,
    "wet_rock": 0.36, "waterfall_stone": 0.38, "shoes": 0.55,
}
_EMISSIVE = {"screen": 1.4, "emissive_cyan": 2.4, "glass_tube": 0.9}


def full_matset(family, extra=None):
    """MatSet with all 16 atlas surfaces in cell order, so material_index ==
    atlas cell index for every generator in this family."""
    ms = E.MatSet(family, atlas_paths(family))
    cols = load_colors(family)
    for i, (name, _) in enumerate(FAMILY_CELLS[family]):
        rgb = cols.get(name, (0.5, 0.5, 0.5))
        ms.add(name, i, rgb, _ROUGH.get(name, 0.74),
               emission=_EMISSIVE.get(name, 0.0))
    for spec in (extra or []):
        name, cell, sub = spec[0], spec[1], spec[2]
        base = FAMILY_CELLS[family][cell][0]
        rgb = cols.get(base, (0.5, 0.5, 0.5))
        ms.add(name, cell, rgb, _ROUGH.get(base, 0.74), sub=sub)
    return ms
