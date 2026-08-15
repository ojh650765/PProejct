"""Material-aware palette construction.

A plain median-cut over the whole creature is the wrong tool.  Pikachu's cheek
is 0.7% of the artwork, so median cut spends no slots on it and the cheek
collapses into the body ramp -- which is exactly why the naive attempt turned a
solid red disc into a hollow ring.

Instead the artwork is segmented into *materials* (body yellow, ear black,
cheek red, mouth maroon, ...) in Lab, where clustering is dominated by chroma
rather than by area.  Each material then gets its own small luminance ramp
sized to how much of the sprite it covers.  At pixel scale a pixel is first
assigned to a material by chroma -- so a washed-out cheek pixel stays *red* --
and only then snapped to a step of that material's ramp.
"""

from __future__ import annotations

import numpy as np

# --------------------------------------------------------------------------
# sRGB <-> CIE Lab (D65).  Written out rather than pulled from skimage so the
# tool tree stays dependency-light (numpy + PIL only).
# --------------------------------------------------------------------------

_M = np.array([[0.4124564, 0.3575761, 0.1804375],
               [0.2126729, 0.7151522, 0.0721750],
               [0.0193339, 0.1191920, 0.9503041]], np.float32)
_WHITE = np.array([0.95047, 1.0, 1.08883], np.float32)


def _linear(c: np.ndarray) -> np.ndarray:
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def rgb_to_lab(rgb: np.ndarray) -> np.ndarray:
    shape = rgb.shape
    flat = _linear(np.clip(rgb.reshape(-1, 3), 0, 1))
    xyz = flat @ _M.T / _WHITE
    eps, kappa = 216 / 24389, 24389 / 27
    f = np.where(xyz > eps, np.cbrt(xyz), (kappa * xyz + 16) / 116)
    lab = np.stack([116 * f[:, 1] - 16,
                    500 * (f[:, 0] - f[:, 1]),
                    200 * (f[:, 1] - f[:, 2])], 1)
    return lab.reshape(shape)


# chroma matters far more than lightness when deciding *what a pixel is made
# of*; lightness is what the ramp encodes afterwards.
_L_WEIGHT = 0.28


def _mat_dist(lab_a: np.ndarray, lab_b: np.ndarray) -> np.ndarray:
    d = lab_a[:, None, :] - lab_b[None, :, :]
    return (_L_WEIGHT * d[..., 0]) ** 2 + d[..., 1] ** 2 + d[..., 2] ** 2


# --------------------------------------------------------------------------
# clustering
# --------------------------------------------------------------------------


def _kmeans(x: np.ndarray, k: int, iters: int = 40, seed: int = 7) -> np.ndarray:
    rng = np.random.default_rng(seed)
    # k-means++ init, so a small material is not systematically missed
    centres = [x[rng.integers(len(x))]]
    for _ in range(k - 1):
        d = np.min(((x[:, None, :] - np.stack(centres)[None]) ** 2).sum(2), 1)
        total = d.sum()
        if total <= 0:
            centres.append(x[rng.integers(len(x))])
            continue
        centres.append(x[rng.choice(len(x), p=d / total)])
    c = np.stack(centres)
    for _ in range(iters):
        lbl = _mat_dist(x, c).argmin(1)
        new = np.stack([x[lbl == i].mean(0) if (lbl == i).any() else c[i]
                        for i in range(k)])
        if np.allclose(new, c, atol=1e-3):
            break
        c = new
    return c


class Material:
    __slots__ = ("name", "lab", "rgb", "ramp", "weight", "is_dark")

    def __init__(self, name, lab, rgb, ramp, weight, is_dark):
        self.name = name
        self.lab = lab
        self.rgb = rgb
        self.ramp = ramp          # (n,3) float rgb, dark -> light
        self.weight = weight
        self.is_dark = is_dark


def segment_materials(src_rgb: np.ndarray, src_mask: np.ndarray, *,
                      n_clusters: int = 8, sample: int = 60000,
                      seed: int = 7,
                      forced: list[tuple[int, int, int]] | None = None
                      ) -> list[Material]:
    """Cluster the HIGH-RES artwork into materials.

    High-res on purpose: the cheek is thousands of pixels there and a handful
    after downsampling, so the material set has to be learned before the
    information is gone.
    """
    px = src_rgb[src_mask]
    rng = np.random.default_rng(seed)
    if len(px) > sample:
        px = px[rng.choice(len(px), sample, replace=False)]
    lab = rgb_to_lab(px)
    centres = _kmeans(lab, n_clusters, seed=seed)

    # Merge clusters that are one material rather than two.
    #
    # The distinction that matters: a *highlight* is the same pigment washed
    # out by light -- same hue, lighter, LESS chroma -- and must merge, or the
    # soft sheen on Pikachu's tail quantises into a hard cream blob that reads
    # as damage.  A different *pigment* at a similar hue -- the brown back
    # stripes against the yellow body -- is darker at comparable chroma and
    # must stay separate, or the stripes dissolve into body shading.
    def hue(c):
        return np.degrees(np.arctan2(c[2], c[1]))

    def chroma(c):
        return float(np.hypot(c[1], c[2]))

    def same_material(a, b):
        ca, cb = chroma(a), chroma(b)
        dh = abs((hue(a) - hue(b) + 180) % 360 - 180)
        if ca < 8 and cb < 8:                       # two neutrals
            return abs(a[0] - b[0]) < 26
        if dh > 30:
            return False
        lighter, darker = (a, b) if a[0] > b[0] else (b, a)
        if chroma(lighter) < chroma(darker) - 4:    # specular wash-out
            return True
        return abs(a[0] - b[0]) < 20 and abs(ca - cb) < 10

    merged: set[int] = set()
    groups: list[list[int]] = []
    for i in range(len(centres)):
        if i in merged:
            continue
        g = [i]
        merged.add(i)
        for j in range(i + 1, len(centres)):
            if j not in merged and same_material(centres[i], centres[j]):
                g.append(j)
                merged.add(j)
        groups.append(g)

    lbl = _mat_dist(lab, centres).argmin(1)
    mats: list[Material] = []
    for gi, g in enumerate(groups):
        sel = np.isin(lbl, g)
        if sel.sum() < 8:
            continue
        mpx = px[sel]
        mlab = lab[sel]
        weight = float(sel.mean())
        centre = mlab.mean(0)
        chroma = float(np.hypot(centre[1], centre[2]))
        # ramp length: broad materials carry the form and need steps; a tiny
        # accent (an eye, a nostril) reads better as one flat colour.
        if weight > 0.30:
            n = 5
        elif weight > 0.10:
            n = 4
        elif weight > 0.025:
            n = 3
        else:
            n = 2
        lo, hi = np.percentile(mlab[:, 0], [6, 94])
        if hi - lo < 6:
            n = 1
        ramp = []
        for t in (np.linspace(0, 1, n) if n > 1 else np.array([0.5])):
            target = lo + (hi - lo) * t
            k = np.argsort(np.abs(mlab[:, 0] - target))[:max(24, len(mlab) // 60)]
            ramp.append(mpx[k].mean(0))
        mats.append(Material(f"m{gi}", centre, mpx.mean(0),
                             np.stack(ramp), weight,
                             centre[0] < 34 and chroma < 22))
    mats.sort(key=lambda m: -m.weight)

    # Colours that must exist whatever the clustering decides.  An eye
    # catchlight is a handful of pixels in a 152k-pixel artwork, so no
    # area-driven method will ever spend a palette slot on it -- and a Pokemon
    # with flat black eyes reads dead, which is precisely the failure this
    # whole pipeline exists to avoid.
    for i, c in enumerate(forced or []):
        rgb = np.asarray(c, np.float32) / 255.0
        lab = rgb_to_lab(rgb.reshape(1, 3))[0]
        mats.append(Material(f"forced{i}", lab, rgb, rgb.reshape(1, 3),
                             0.0, bool(lab[0] < 34)))
    return mats


def palette_of(mats: list[Material]) -> np.ndarray:
    return np.concatenate([m.ramp for m in mats], 0)


# --------------------------------------------------------------------------
# applying it at pixel scale
# --------------------------------------------------------------------------


def classify(img_rgb: np.ndarray, mask: np.ndarray,
             mats: list[Material]) -> np.ndarray:
    """Per-pixel material index (-1 outside the mask)."""
    out = np.full(mask.shape, -1, np.int32)
    px = img_rgb[mask]
    lab = rgb_to_lab(px)
    cent = np.stack([m.lab for m in mats])
    out[mask] = _mat_dist(lab, cent).argmin(1)
    return out


def quantise(img_rgb: np.ndarray, mask: np.ndarray, mats: list[Material],
             matidx: np.ndarray | None = None) -> tuple[np.ndarray, np.ndarray]:
    """Snap to (material, ramp step).  Returns (rgb, material index map)."""
    if matidx is None:
        matidx = classify(img_rgb, mask, mats)
    out = np.zeros(img_rgb.shape, np.float32)
    lab_all = rgb_to_lab(img_rgb)
    for i, m in enumerate(mats):
        sel = matidx == i
        if not sel.any():
            continue
        ramp_l = rgb_to_lab(m.ramp)[:, 0]
        step = np.abs(lab_all[sel][:, 0][:, None] - ramp_l[None, :]).argmin(1)
        out[sel] = m.ramp[step]
    return out, matidx
