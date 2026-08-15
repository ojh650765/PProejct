"""Core raster pipeline: official artwork -> clean pixel-art sprite.

Everything here operates in two stages deliberately:

  1. HIGH-RES stage (the 390x390 official art).  All posing, warping and
     view-construction happens here, where there are enough pixels to push
     things around without destroying them.
  2. PIXEL stage (~30..170 px tall).  One single downsample + quantise +
     cleanup, applied identically to every frame of a creature so the whole
     set shares an exact palette and an exact rendering style.

Never deform a sprite after step 2 -- that is what makes derived pixel art
look like mush.
"""

from __future__ import annotations

import numpy as np
from PIL import Image

# --------------------------------------------------------------------------
# alpha-correct resampling
# --------------------------------------------------------------------------


def load_rgba(path: str) -> np.ndarray:
    """float32 RGBA in 0..1, straight (non-premultiplied) alpha."""
    return np.asarray(Image.open(path).convert("RGBA"), dtype=np.float32) / 255.0


def save_rgba(arr: np.ndarray, path: str) -> None:
    a = np.clip(arr, 0.0, 1.0)
    Image.fromarray((a * 255.0 + 0.5).astype(np.uint8), "RGBA").save(path)


def alpha_bbox(img: np.ndarray, thresh: float = 0.03) -> tuple[int, int, int, int]:
    ys, xs = np.nonzero(img[..., 3] > thresh)
    if len(xs) == 0:
        raise ValueError("empty alpha")
    return int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1


def resize_premultiplied(img: np.ndarray, w: int, h: int,
                         filt=Image.LANCZOS) -> np.ndarray:
    """Resize without the dark-halo bleed a naive RGBA resize produces.

    Colour is premultiplied by alpha before filtering so fully transparent
    source pixels (which carry garbage RGB) cannot contaminate the edge, then
    un-premultiplied afterwards.
    """
    a = img[..., 3:4]
    pm = np.concatenate([img[..., :3] * a, a], axis=2)
    pil = Image.fromarray(np.clip(pm * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGBA")
    pil = pil.resize((w, h), filt)
    out = np.asarray(pil, dtype=np.float32) / 255.0
    oa = out[..., 3:4]
    rgb = np.where(oa > 1e-4, out[..., :3] / np.maximum(oa, 1e-4), 0.0)
    return np.concatenate([np.clip(rgb, 0, 1), oa], axis=2)


# --------------------------------------------------------------------------
# silhouette cleanup -- the part that stops a downsample looking like soup
# --------------------------------------------------------------------------


def _neighbours4(mask: np.ndarray) -> np.ndarray:
    n = np.zeros(mask.shape, np.int32)
    n[1:, :] += mask[:-1, :]
    n[:-1, :] += mask[1:, :]
    n[:, 1:] += mask[:, :-1]
    n[:, :-1] += mask[:, 1:]
    return n


def _neighbours8(mask: np.ndarray) -> np.ndarray:
    n = _neighbours4(mask)
    n[1:, 1:] += mask[:-1, :-1]
    n[1:, :-1] += mask[:-1, 1:]
    n[:-1, 1:] += mask[1:, :-1]
    n[:-1, :-1] += mask[1:, 1:]
    return n


def threshold_alpha(alpha: np.ndarray, main: float = 0.50,
                    thin: float = 0.22) -> np.ndarray:
    """Two-level alpha threshold.

    A flat 0.5 cut gives a clean body but amputates every thin feature -- ear
    tips, tail points, toes, antennae -- because a 1px-wide high-res detail
    lands at ~0.3 coverage after an 8x reduction.  So: keep everything above
    `main`, then additionally keep anything above `thin` that is adjacent to
    the kept mass.  Thin features survive; stray filter ringing in open space
    does not, because it has no neighbour to attach to.
    """
    core = alpha >= main
    cand = alpha >= thin
    kept = core.copy()
    for _ in range(4):  # grow along thin structures, a pixel per pass
        grow = cand & (_neighbours8(kept.astype(np.int32)) > 0)
        if not (grow & ~kept).any():
            break
        kept |= grow
    return kept


def despeckle(mask: np.ndarray, min_neighbours: int = 2) -> np.ndarray:
    """Drop lone pixels that read as dirt rather than as form."""
    m = mask.copy()
    n = _neighbours8(m.astype(np.int32))
    m &= ~(mask & (n < min_neighbours))
    return m


def fill_holes(mask: np.ndarray) -> np.ndarray:
    """Close single-pixel pinholes inside the body."""
    n = _neighbours4((~mask).astype(np.int32))
    return mask | (~mask & (n == 0))


# --------------------------------------------------------------------------
# palette
# --------------------------------------------------------------------------


def build_palette(img: np.ndarray, mask: np.ndarray, n_colours: int,
                  seeds: list[tuple[int, int, int]] | None = None) -> np.ndarray:
    """Median-cut palette over the visible pixels, with optional forced seeds.

    Seeds exist so signature colours (Pikachu's cheek red, the eye black) are
    guaranteed a slot even though they occupy a fraction of a percent of the
    artwork and median cut would otherwise merge them into the body tone.
    """
    px = img[mask][:, :3]
    quantised = Image.fromarray(
        np.clip(px.reshape(1, -1, 3) * 255.0 + 0.5, 0, 255).astype(np.uint8), "RGB"
    ).quantize(colors=n_colours, method=Image.MEDIANCUT, dither=Image.NONE)
    pal = np.asarray(quantised.getpalette()[: n_colours * 3],
                     dtype=np.float32).reshape(-1, 3) / 255.0
    if seeds:
        pal = np.concatenate([pal, np.asarray(seeds, np.float32) / 255.0], axis=0)
    # merge near-duplicates so the reported palette size is honest
    keep: list[np.ndarray] = []
    for c in pal:
        if all(np.abs(c - k).max() > 5 / 255.0 for k in keep):
            keep.append(c)
    return np.stack(keep)


def apply_palette(img: np.ndarray, mask: np.ndarray, pal: np.ndarray) -> np.ndarray:
    out = img.copy()
    px = img[..., :3].reshape(-1, 3)
    d = ((px[:, None, :] - pal[None, :, :]) ** 2).sum(2)
    idx = d.argmin(1)
    out[..., :3] = pal[idx].reshape(img.shape[0], img.shape[1], 3)
    out[..., 3] = mask.astype(np.float32)
    out[~mask] = 0.0
    return out


# --------------------------------------------------------------------------
# outline
# --------------------------------------------------------------------------


def add_outline(rgb: np.ndarray, mask: np.ndarray, matidx: np.ndarray, mats,
                darken: float = 0.66,
                tint: tuple[float, float, float] = (0.26, 0.13, 0.11),
                tint_mix: float = 0.22) -> np.ndarray:
    """Re-assert the dark keyline every Pokemon sprite has.

    The official art's keyline is ~4px at 390px, i.e. well under one pixel
    after reduction, so it washes out entirely and the sprite goes soft.
    Gen 4/5 sprites use a *coloured* outline -- a darkened version of the fill
    it borders, never flat black -- and that is what gets rebuilt here, from
    each pixel's own material ramp.  Two rules keep it from turning to mud:

      * only body that is locally at least 3px thick gets outlined, so ear
        tips, toes and the tail point are not consumed by their own outline;
      * an already-dark material (the black ear tips) is left alone, because
        outlining black with darker black just thickens a blob.
    """
    empty = (~mask).astype(np.int32)
    edge = mask & (_neighbours4(empty) > 0)
    interior = mask & (_neighbours4(mask.astype(np.int32)) == 4)
    thick = _neighbours8(interior.astype(np.int32)) > 0
    tint_a = np.asarray(tint, np.float32)
    out = rgb.copy()
    sel = edge & thick
    for i, m in enumerate(mats):
        s = sel & (matidx == i)
        if not s.any():
            continue
        base = m.ramp[0]
        if m.is_dark:
            c = base * 0.86
        else:
            c = base * darken
            c = c * (1 - tint_mix) + tint_a * tint_mix
        out[s] = np.clip(c, 0, 1)
    return out


# --------------------------------------------------------------------------
# top-level
# --------------------------------------------------------------------------


def repair_edge_colour(rgb: np.ndarray, mask: np.ndarray, coverage: np.ndarray,
                       solid: float = 0.62) -> np.ndarray:
    """Discard the colour of partially-covered edge pixels.

    Un-premultiplying a pixel that was only 25% covered amplifies whatever
    Lanczos ringing landed there, which is why the first pass grew a pale
    cream halo along every thin edge -- very visible against a dark
    background.  The silhouette decision for those pixels is still good; only
    their colour is untrustworthy, so it gets replaced by the nearest
    confidently-covered colour instead.
    """
    out = rgb.copy()
    trust = mask & (coverage >= solid)
    if not trust.any():
        return out
    todo = mask & ~trust
    for _ in range(6):
        if not todo.any():
            break
        tf = trust.astype(np.float32)[..., None]
        acc = np.zeros_like(out)
        cnt = np.zeros(out.shape[:2] + (1,), np.float32)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0),
                       (1, 1), (1, -1), (-1, 1), (-1, -1)):
            acc += np.roll(out * tf, (dy, dx), (0, 1))
            cnt += np.roll(tf, (dy, dx), (0, 1))
        grow = todo & (cnt[..., 0] > 0)
        if not grow.any():
            break
        out[grow] = (acc / np.maximum(cnt, 1e-6))[grow]
        trust |= grow
        todo &= ~grow
    return out


def bleed_alpha(rgba: np.ndarray, rounds: int = 4) -> np.ndarray:
    """Push edge colour outward into the transparent border, keeping alpha 0.

    The sprites render opaque-queue with alpha clip and point filtering, so
    this is not needed for the sampling itself -- but atlas packing, any
    future mip, and Unity's own bilinear preview all pull from texels just
    outside the silhouette.  If those are RGB 0 the sprite gains a black
    fringe.  Bleeding costs nothing and removes the whole class of bug.
    """
    out = rgba.copy()
    known = out[..., 3] > 0.5
    for _ in range(rounds):
        kf = known.astype(np.float32)[..., None]
        acc = np.zeros(out.shape[:2] + (3,), np.float32)
        cnt = np.zeros(out.shape[:2] + (1,), np.float32)
        for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0),
                       (1, 1), (1, -1), (-1, 1), (-1, -1)):
            acc += np.roll(out[..., :3] * kf, (dy, dx), (0, 1))
            cnt += np.roll(kf, (dy, dx), (0, 1))
        grow = (~known) & (cnt[..., 0] > 0)
        if not grow.any():
            break
        out[..., :3][grow] = (acc / np.maximum(cnt, 1e-6))[grow]
        known |= grow
    return out


def pixelize(src: np.ndarray, target_h: int, *, pad: int = 0,
             main: float = 0.52, thin: float = 0.30,
             bbox: tuple[int, int, int, int] | None = None,
             filt=Image.LANCZOS) -> tuple[np.ndarray, np.ndarray]:
    """Downsample + clean silhouette.  Returns (rgba_float, mask).

    The returned alpha is *binary*: the sprites render on the opaque queue
    with alpha clip, so any surviving antialiased fringe would clip into a
    ragged edge rather than fading.  Contrast comes from the baked keyline
    instead (see add_outline).
    """
    if bbox is None:
        bbox = alpha_bbox(src)
    x0, y0, x1, y1 = bbox
    # the crop rect may legitimately extend past the artwork; pad rather than
    # clamp so the creature stays where the caller placed it in the cell
    ph, pw = src.shape[:2]
    px0, py0 = max(0, -x0), max(0, -y0)
    px1, py1 = max(0, x1 - pw), max(0, y1 - ph)
    if px0 or py0 or px1 or py1:
        src = np.pad(src, ((py0, py1), (px0, px1), (0, 0)))
        x0, x1 = x0 + px0, x1 + px0
        y0, y1 = y0 + py0, y1 + py0
    crop = src[y0:y1, x0:x1]
    sh, sw = crop.shape[:2]
    th = int(target_h)
    tw = max(1, int(round(sw * th / sh)))
    small = resize_premultiplied(crop, tw, th, filt)
    mask = threshold_alpha(small[..., 3], main, thin)
    mask = despeckle(mask)
    mask = fill_holes(mask)
    small[..., :3] = repair_edge_colour(small[..., :3], mask, small[..., 3])
    if pad:
        small = np.pad(small, ((pad, pad), (pad, pad), (0, 0)))
        mask = np.pad(mask, ((pad, pad), (pad, pad)))
    return small, mask
